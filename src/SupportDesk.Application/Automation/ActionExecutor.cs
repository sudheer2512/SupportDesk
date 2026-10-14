using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Notifications;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Automation;

public static class ActionStatus
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string Queued = "queued";
}

/// <summary>Outcome of one action of an execution (stored in RuleExecution.ActionResultsJson).</summary>
public sealed class ActionOutcome
{
    public int Index { get; set; }
    public string Type { get; set; } = "";
    public string Status { get; set; } = ActionStatus.Succeeded;
    public string? Message { get; set; }
    public string? Error { get; set; }
    public bool Transient { get; set; }
    public int Attempts { get; set; } = 1;
}

/// <summary>RuleExecution.ActionResultsJson: per-action outcomes plus the outbox events the actions emitted (loop detection).</summary>
public sealed class ExecutionResults
{
    public List<ActionOutcome> Actions { get; set; } = [];
    public List<Guid> EmittedEventIds { get; set; } = [];

    public static ExecutionResults Parse(string? json) => Json.DeserializeOr(json, new ExecutionResults());
    public string ToJson() => Json.Serialize(this);
}

/// <param name="TicketId">Ticket the actions apply to.</param>
/// <param name="SourceName">Rule or blueprint name (template value {{rule.name}}, notes).</param>
/// <param name="ExecutionId">Rule execution (idempotency keys for jobs, notifications and child tickets).</param>
/// <param name="Attempt">Execution attempt number (retries).</param>
/// <param name="Extra">Extra template/condition values (inbound webhook payload).</param>
public sealed record ActionRunContext(Guid TicketId, string SourceName, Guid? RuleId = null, Guid? ExecutionId = null, int Attempt = 1,
    IReadOnlyDictionary<string, object?>? Extra = null, string? Trigger = null);

public sealed record ActionRunResult(List<ActionOutcome> Outcomes, HashSet<Guid> EmittedEventIds);

/// <summary>Payload of the durable HTTP job created by the invoke_webhook action.</summary>
public sealed record HttpActionJob(Guid? ExecutionId, int ActionIndex, Guid? RuleId, Guid TicketId, string Method, string Url,
    Dictionary<string, string> Headers, string? Body, string? Secret, int TimeoutSeconds, string Source);

/// <summary>
/// Executes automation actions (rules, blueprint after-actions) against one ticket through the regular ticket services,
/// so history, SLA, events and validation behave exactly as for agents. Must run in a DI scope whose RequestContext is
/// a system/automation actor (set by the caller, with <see cref="RequestContext.ExecutingRuleId"/> for rules). Each
/// action commits on its own; a failing action is recorded and the next one still runs. Consequential actions
/// (delete, merge, spam) are deliberately not offered.
/// </summary>
public sealed class ActionExecutor(
    DeskDbContext db,
    TicketService tickets,
    ConversationService conversation,
    TicketCollaborationService collaboration,
    NotificationService notifications,
    IOutboundMessenger messenger,
    IAiActionGateway ai,
    IErpRecordUpdater erp,
    IJobQueue jobs,
    IEventPublisher events,
    TicketHistory history,
    IRoutingService routing,
    TicketLookups lookups,
    ILogger<ActionExecutor> logger)
{
    public async Task<ActionRunResult> ExecuteAsync(ActionRunContext run, IReadOnlyList<RuleAction> actions, IReadOnlySet<int>? onlyIndexes,
        CancellationToken ct)
    {
        var outcomes = new List<ActionOutcome>();
        var emitted = new HashSet<Guid>();
        var values = await TemplateValues.BuildAsync(db, lookups, run.TicketId, run.SourceName, run.Extra, ct);
        for (var i = 0; i < actions.Count; i++)
        {
            if (onlyIndexes is not null && !onlyIndexes.Contains(i)) continue;
            var action = actions[i];
            var outcome = new ActionOutcome { Index = i, Type = action.Type };
            try
            {
                var (status, message) = await RunAsync(run, action, i, values, ct);
                outcome.Status = status;
                outcome.Message = message;
                Capture(emitted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Capture(emitted);
                outcome.Status = ActionStatus.Failed;
                outcome.Error = ex is DomainException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                outcome.Transient = TransientErrors.IsTransient(ex);
                db.ChangeTracker.Clear();
                logger.LogInformation("Automation action {Type} ({Source}) failed on ticket {Ticket}: {Error}", action.Type, run.SourceName, run.TicketId, outcome.Error);
            }
            outcomes.Add(outcome);
        }
        return new ActionRunResult(outcomes, emitted);
    }

    /// <summary>Writes the "RuleApplied" ticket history entry (attributed to the executing rule).</summary>
    public async Task RecordHistoryAsync(Guid ticketId, string type, string summary, object details, CancellationToken ct)
    {
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (t is null) return;
        history.Record(t, type, summary.Length > 1000 ? summary[..999] + "…" : summary, null, details);
        await db.SaveChangesAsync(ct);
    }

    private void Capture(HashSet<Guid> emitted)
    {
        foreach (var e in db.ChangeTracker.Entries<OutboxEvent>().Where(e => e.State == EntityState.Unchanged)) emitted.Add(e.Entity.Id);
    }

    private static (string, string?) Ok(string message) => (ActionStatus.Succeeded, message);
    private static (string, string?) Skip(string message) => (ActionStatus.Skipped, message);

    private async Task<(string Status, string? Message)> RunAsync(ActionRunContext run, RuleAction action, int index, IReadOnlyDictionary<string, string?> values,
        CancellationToken ct)
    {
        var p = action.P;
        var id = run.TicketId;
        string T(string? template) => TemplateRenderer.Render(template, values, TemplateEncoding.Text);
        string H(string? template) => TemplateRenderer.Render(template, values, TemplateEncoding.Html);
        await lookups.LoadAsync(ct);

        switch (action.Type)
        {
            case ActionTypes.Assign: return await AssignAsync(run, p, T(p.Str("note")), ct);

            case ActionTypes.SetPriority:
            {
                var priority = p.Str("priority");
                return await UpdateFieldsAsync(id, r => r with { Priority = priority }, $"Priority set to {priority}", ct);
            }
            case ActionTypes.SetSeverity:
            {
                var severity = p.Str("severity");
                return await UpdateFieldsAsync(id, r => r with { Severity = severity }, $"Severity set to {severity}", ct);
            }
            case ActionTypes.SetCategory:
            {
                var category = p.Guid("categoryId");
                var sub = p.Guid("subcategoryId");
                return await UpdateFieldsAsync(id, r => r with { CategoryId = category, SubcategoryId = sub, Clear = sub is null ? ["subcategoryId"] : null },
                    $"Category set to {lookups.Category(sub ?? category)}", ct);
            }
            case ActionTypes.SetIssueType:
            {
                var issueType = p.Guid("issueTypeId");
                return await UpdateFieldsAsync(id, r => r with { IssueTypeId = issueType }, $"Issue type set to {lookups.IssueType(issueType)}", ct);
            }
            case ActionTypes.SetCustomField:
            {
                var key = p.Str("key")!;
                if (key.StartsWith("cf:", StringComparison.OrdinalIgnoreCase)) key = key[3..];
                p.TryGet("value", out var raw);
                var value = raw.ValueKind == JsonValueKind.String ? JsonSerializer.SerializeToElement(T(raw.GetString()))
                    : raw.ValueKind == JsonValueKind.Undefined ? JsonSerializer.SerializeToElement<string?>(null) : raw.Clone();
                return await UpdateFieldsAsync(id, r => r with { CustomFields = new Dictionary<string, JsonElement> { [key] = value } },
                    $"Custom field '{key}' set", ct);
            }
            case ActionTypes.SetStatus: return await SetStatusAsync(run, p.Guid("statusId")!.Value, p.Str("substatus"), T(p.Str("comment")), ct);
            case ActionTypes.SetSubstatus:
            {
                var t = await tickets.LoadForUpdateAsync(id, ct);
                return await SetStatusAsync(run, t.StatusId, p.Str("substatus"), null, ct);
            }
            case ActionTypes.TransferDepartment:
            {
                var department = p.Guid("departmentId")!.Value;
                var t = await tickets.LoadForUpdateAsync(id, ct);
                if (t.DepartmentId == department) return Skip("Already in that department.");
                if (!await db.Departments.AnyAsync(d => d.Id == department && d.IsActive, ct))
                    throw new DomainException(ErrorCodes.Validation, "The target department no longer exists or is inactive.");
                await tickets.TransferCoreAsync(t, department, p.Guid("teamId"), null, T(p.Str("reason")) is { Length: > 0 } reason ? reason : $"Rule: {run.SourceName}", ct);
                await tickets.SaveAsync(t, ct);
                return Ok($"Moved to {lookups.Department(department)}");
            }
            case ActionTypes.AddTags or ActionTypes.RemoveTags:
            {
                var current = await db.TicketTags.Where(x => x.TicketId == id).Select(x => x.Tag!.Name).ToListAsync(ct);
                var change = p.StrList("tags").Select(TicketService.NormalizeTag).Where(x => x.Length > 0).ToHashSet();
                var next = action.Type == ActionTypes.AddTags ? current.Union(change).ToList() : current.Where(x => !change.Contains(x)).ToList();
                if (next.Count == current.Count && next.All(current.Contains)) return Skip("Tags already up to date.");
                await tickets.SetTagsAsync(id, next, ct);
                return Ok($"{(action.Type == ActionTypes.AddTags ? "Added" : "Removed")} tag(s) {string.Join(", ", change)}");
            }
            case ActionTypes.AddNote:
            {
                var body = H(p.Str("bodyHtml"));
                var thread = await conversation.AddNoteAsync(id, new NoteRequest
                {
                    BodyHtml = body, ClientRequestId = run.ExecutionId is { } e ? $"rule:{e:N}:{index}:{run.Attempt}" : null
                }, ct);
                return Ok($"Internal note added (#{thread.Seq})");
            }
            case ActionTypes.ReplyWithTemplate: return await ReplyWithTemplateAsync(run, p.Guid("templateId")!.Value, index, values, ct);
            case ActionTypes.AddWatcher:
            {
                var users = p.GuidList("userIds");
                if (p.Guid("userId") is { } single) users.Add(single);
                var kind = p.Str("kind") ?? "Watcher";
                var existing = await db.TicketWatchers.Where(w => w.TicketId == id).Select(w => w.UserId).ToListAsync(ct);
                var added = 0;
                foreach (var u in users.Distinct().Where(u => !existing.Contains(u)))
                {
                    await collaboration.AddWatcherAsync(id, new AddWatcherRequest(u, kind), ct);
                    added++;
                }
                return added == 0 ? Skip("Already following.") : Ok($"Added {added} follower(s)");
            }
            case ActionTypes.NotifyAgents: return await NotifyAsync(run, p, index, T, ct);
            case ActionTypes.SendMessage: return await SendMessageAsync(run, p, T, H, ct);
            case ActionTypes.CreateTask:
            {
                var t = await db.Tickets.AsNoTracking().FirstAsync(x => x.Id == id, ct);
                var assignee = p.Str("assigneeId") == "assignee" ? t.AssigneeId : p.Guid("assigneeId");
                DateTime? due = p.Double("dueInHours") is { } hours ? db.Clock.UtcNow.AddHours(hours) : null;
                var task = await collaboration.SaveTaskAsync(id, null, new SaveTaskRequest(T(p.Str("title")), p.Has("description") ? T(p.Str("description")) : null,
                    false, null, assignee, due, null, null), ct);
                return Ok($"Task \"{task.Title}\" created");
            }
            case ActionTypes.CreateChildTicket: return await CreateChildAsync(run, p, index, T, H, ct);
            case ActionTypes.InvokeWebhook: return await QueueHttpAsync(run, p, index, values, ct);
            case ActionTypes.RequestApproval:
            {
                var type = p.Str("type") is { Length: > 0 } ty ? ty : "automation";
                if (await db.ApprovalRequests.AnyAsync(a => a.TicketId == id && a.Type == type && a.Status == ApprovalStatus.Pending, ct))
                    return Skip("An approval of this type is already pending.");
                var t = await db.Tickets.AsNoTracking().FirstAsync(x => x.Id == id, ct);
                var reason = T(p.Str("reason"));
                var approval = new ApprovalRequest
                {
                    Type = type, EntityType = "Ticket", EntityId = id, TicketId = id, RequestedById = Guid.Empty, ApproverUserId = p.Guid("approverUserId"),
                    ApproverRoleId = p.Guid("approverRoleId"), Reason = reason, ExpiresAt = db.Clock.UtcNow.AddDays(p.Int("expiresInDays") ?? 7),
                    PayloadJson = Json.Serialize(new { source = run.SourceName, ruleId = run.RuleId, executionId = run.ExecutionId })
                };
                db.ApprovalRequests.Add(approval);
                history.Record(t, TicketEventTypes.ApprovalRequested, $"Approval requested ({type}): {reason}", null, new { approvalId = approval.Id });
                events.Publish(EventTypes.ApprovalRequested, "ApprovalRequest", approval.Id, new EntityEventPayload(approval.Id, type));
                await db.SaveChangesAsync(ct);
                return Ok("Approval requested");
            }
            case ActionTypes.Escalate:
            {
                var t = await tickets.LoadForUpdateAsync(id, ct);
                if (!t.StatusCategory.IsOpen()) return Skip("Only open tickets can be escalated.");
                var to = p.Guid("toUserId");
                if (to is null && p.Bool("toSupervisor"))
                    to = await db.AgentDepartments.Where(d => d.DepartmentId == t.DepartmentId && d.IsSupervisor && d.UserId != t.AssigneeId)
                        .Join(db.Users.Where(u => u.IsActive), d => d.UserId, u => u.Id, (d, u) => (Guid?)u.Id).FirstOrDefaultAsync(ct);
                if (to is { } u && await db.TicketWatchers.AnyAsync(w => w.TicketId == t.Id && w.UserId == u, ct)) to = null;
                await tickets.EscalateCoreAsync(t, T(p.Str("reason")), to, p.Bool("raisePriority"), ct);
                await tickets.SaveAsync(t, ct);
                return Ok($"Escalated to level {t.EscalationLevel}{(to is null ? "" : $" ({lookups.User(to)} added)")}");
            }
            case ActionTypes.AiClassify: return Ok(await ai.ClassifyTicketAsync(id, p.Bool("apply"), ct));
            case ActionTypes.AiSummarize: return Ok(await ai.SummarizeTicketAsync(id, ct));
            case ActionTypes.UpdateErpRecord:
            {
                var system = p.Str("system")!;
                var recordType = p.Str("recordType")!;
                Guid? recordId = null;
                var number = p.Has("recordNumber") ? T(p.Str("recordNumber")) : null;
                if (p.Bool("useOriginReference") || string.IsNullOrWhiteSpace(number))
                {
                    var origin = await db.TicketExternalReferences.AsNoTracking()
                        .Where(x => x.TicketId == id && (x.IsOrigin || x.System == system))
                        .OrderByDescending(x => x.IsOrigin).ThenByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
                    if (origin is null) return Skip("The ticket has no related ERP record.");
                    recordId = origin.RecordId;
                    number = origin.RecordNumber;
                }
                p.TryGet("changes", out var changes);
                var result = await erp.UpdateRecordAsync(system, recordType, recordId, number, TemplateRenderer.RenderJson(changes, values), ct);
                return Ok(result);
            }
            default:
                throw new DomainException(ErrorCodes.Validation, $"Unknown action type '{action.Type}'.");
        }
    }

    private async Task<(string, string?)> UpdateFieldsAsync(Guid ticketId, Func<UpdateTicketRequest, UpdateTicketRequest> build, string description, CancellationToken ct)
    {
        var t = await tickets.LoadForUpdateAsync(ticketId, ct);
        var before = t.RowVersion.ToArray();
        await tickets.UpdateAsync(ticketId, build(new UpdateTicketRequest { RowVersion = Convert.ToBase64String(t.RowVersion) }), ct);
        return t.RowVersion.SequenceEqual(before) ? Skip("Already set — no change.") : Ok(description);
    }

    private async Task<(string, string?)> AssignAsync(ActionRunContext run, ActionParams p, string note, CancellationToken ct)
    {
        var t = await tickets.LoadForUpdateAsync(run.TicketId, ct);
        var userId = p.Guid("userId");
        var teamId = p.Guid("teamId");
        if (string.IsNullOrWhiteSpace(note)) note = $"Rule: {run.SourceName}";
        if (userId is null && p.Bool("route"))
        {
            var (originalAssignee, originalTeam) = (t.AssigneeId, t.TeamId);
            t.AssigneeId = null;
            if (teamId is not null) t.TeamId = teamId;
            var decision = await routing.RouteAsync(t, ct);
            var pickedTeam = decision.TeamId ?? t.TeamId;
            (t.AssigneeId, t.TeamId) = (originalAssignee, originalTeam);
            if (decision.AssigneeId is null)
            {
                if (pickedTeam is not null && pickedTeam != t.TeamId)
                {
                    await tickets.AssignCoreAsync(t, t.AssigneeId, pickedTeam, note, ct);
                    await tickets.SaveAsync(t, ct);
                }
                return Skip($"Routing found no eligible agent ({decision.Reason}).");
            }
            userId = decision.AssigneeId;
            teamId = pickedTeam;
        }
        else if (userId is null && teamId is { } team)
        {
            userId = t.AssigneeId is { } current && await db.TeamMembers.AnyAsync(m => m.TeamId == team && m.UserId == current, ct) ? current : null;
        }
        if (userId == t.AssigneeId && (teamId is null || teamId == t.TeamId)) return Skip("Already assigned.");
        await tickets.AssignCoreAsync(t, userId, teamId, note, ct);
        await tickets.SaveAsync(t, ct);
        return Ok(t.AssigneeId is null ? $"Assigned to team {lookups.Team(t.TeamId)}" : $"Assigned to {lookups.User(t.AssigneeId)}");
    }

    private async Task<(string, string?)> SetStatusAsync(ActionRunContext run, Guid statusId, string? substatus, string? comment, CancellationToken ct)
    {
        var t = await tickets.LoadForUpdateAsync(run.TicketId, ct);
        if (t.WorkflowId is not null)
            throw new DomainException(ErrorCodes.InvalidTransition, "This ticket follows a blueprint; its status only changes through blueprint transitions.");
        var to = await db.TicketStatuses.AsNoTracking().FirstOrDefaultAsync(s => s.Id == statusId && s.IsActive, ct)
                 ?? throw new DomainException(ErrorCodes.Validation, "The target status no longer exists or is inactive.");
        if (t.StatusId == to.Id && t.Substatus == substatus) return Skip($"Already '{to.Name}'.");
        var from = await db.TicketStatuses.AsNoTracking().FirstAsync(s => s.Id == t.StatusId, ct);
        var allowed = Json.GuidList(from.AllowedNextJson);
        if (from.Id != to.Id && allowed.Count > 0 && !allowed.Contains(to.Id))
            throw new DomainException(ErrorCodes.InvalidTransition, $"'{from.Name}' cannot move directly to '{to.Name}' (configured status transitions).");
        await tickets.ApplyStatusAsync(t, to, substatus, string.IsNullOrWhiteSpace(comment) ? $"Rule: {run.SourceName}" : comment, bypassRules: false, ct);
        await tickets.SaveAsync(t, ct);
        return Ok($"Status set to {to.Name}{(substatus is null ? "" : $" ({substatus})")}");
    }

    private async Task<(string, string?)> ReplyWithTemplateAsync(ActionRunContext run, Guid templateId, int index, IReadOnlyDictionary<string, string?> values,
        CancellationToken ct)
    {
        var t = await db.Tickets.AsNoTracking().FirstAsync(x => x.Id == run.TicketId, ct);
        if (t.ContactId is null) return Skip("The ticket has no customer contact to reply to.");
        if (!await db.ReplyTemplates.AnyAsync(x => x.Id == templateId && x.IsActive, ct))
            throw new DomainException(ErrorCodes.Validation, "The reply template no longer exists or is inactive.");
        var html = await collaboration.RenderTemplateAsync(templateId, run.TicketId, ct);
        html = TemplateRenderer.Render(html, values, TemplateEncoding.Html);
        var thread = await conversation.ReplyAsync(run.TicketId, new ReplyRequest
        {
            BodyHtml = html, Force = true, TemplateId = templateId,
            ClientRequestId = run.ExecutionId is { } e ? $"rule:{e:N}:{index}:{run.Attempt}" : null
        }, ct);
        return Ok($"Template reply sent (#{thread.Seq})");
    }

    private async Task<(string, string?)> NotifyAsync(ActionRunContext run, ActionParams p, int index, Func<string?, string> render, CancellationToken ct)
    {
        var t = await db.Tickets.AsNoTracking().FirstAsync(x => x.Id == run.TicketId, ct);
        var users = new HashSet<Guid>(p.GuidList("userIds"));
        foreach (var r in p.StrList("recipients").Select(r => r.ToLowerInvariant()))
        {
            switch (r)
            {
                case "assignee" when t.AssigneeId is { } a: users.Add(a); break;
                case "supervisors":
                    users.UnionWith(await db.AgentDepartments.Where(d => d.DepartmentId == t.DepartmentId && d.IsSupervisor).Select(d => d.UserId).ToListAsync(ct));
                    break;
                case "watchers":
                    users.UnionWith(await db.TicketWatchers.Where(w => w.TicketId == t.Id).Select(w => w.UserId).ToListAsync(ct));
                    break;
                case "team" when t.TeamId is { } team:
                    users.UnionWith(await db.TeamMembers.Where(m => m.TeamId == team).Select(m => m.UserId).ToListAsync(ct));
                    break;
                case "department":
                    users.UnionWith(await db.AgentDepartments.Where(d => d.DepartmentId == t.DepartmentId).Select(d => d.UserId).ToListAsync(ct));
                    break;
            }
        }
        var roles = p.GuidList("roleIds");
        if (roles.Count > 0) users.UnionWith(await db.UserRoles.Where(r => roles.Contains(r.RoleId)).Select(r => r.UserId).ToListAsync(ct));
        if (users.Count == 0) return Skip("No recipients.");
        var severity = Enum.TryParse<NotificationSeverity>(p.Str("severity"), true, out var s) ? s : NotificationSeverity.Info;
        var dedup = run.ExecutionId is { } e ? $"rule:{e:N}:{index}" : null;
        var sent = await notifications.NotifyAsync(new NotifyRequest(users, NotificationTypes.System, render(p.Str("title")),
            p.Has("message") ? render(p.Str("message")) : null, $"/tickets/{t.Id}", severity, severity == NotificationSeverity.Critical, "Ticket", t.Id, dedup), ct);
        return sent == 0 ? Skip("Recipients already notified or notifications disabled.") : Ok($"Notified {sent} agent(s)");
    }

    private async Task<(string, string?)> SendMessageAsync(ActionRunContext run, ActionParams p, Func<string?, string> text, Func<string?, string> html,
        CancellationToken ct)
    {
        var channel = Enum.Parse<Channel>(p.Str("channel")!, true);
        var t = await db.Tickets.AsNoTracking().FirstAsync(x => x.Id == run.TicketId, ct);
        string? to = null;
        Guid? contactId = null, userId = null;
        switch (p.Str("to")?.ToLowerInvariant())
        {
            case "contact":
            {
                if (t.ContactId is null) return Skip("The ticket has no contact.");
                var c = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == t.ContactId, ct);
                if (c is null) return Skip("The contact no longer exists.");
                contactId = c.Id;
                to = channel switch
                {
                    Channel.Email => c.Email,
                    Channel.WhatsApp => c.WhatsAppNumber ?? c.Mobile ?? c.Phone,
                    _ => c.Mobile ?? c.Phone
                };
                if (string.IsNullOrWhiteSpace(to)) return Skip($"The contact has no {(channel == Channel.Email ? "e-mail address" : "phone number")}.");
                break;
            }
            case "assignee" or "user":
            {
                userId = p.Str("to")!.Equals("assignee", StringComparison.OrdinalIgnoreCase) ? t.AssigneeId : p.Guid("userId");
                if (userId is null) return Skip("The ticket is unassigned.");
                var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId && x.IsActive, ct);
                if (u is null) return Skip("The agent is not active.");
                to = channel == Channel.Email ? u.Email : u.Phone;
                if (string.IsNullOrWhiteSpace(to)) return Skip("The agent has no address for this channel.");
                break;
            }
            default:
                to = text(p.Str("address"));
                break;
        }
        var result = await messenger.SendAsync(new OutboundMessageRequest(channel, to, contactId, userId, text(p.Str("subject")), html(p.Str("bodyHtml")),
            t.Id, p.Str("templateName")), ct);
        return Ok(string.IsNullOrWhiteSpace(result) ? $"{channel} message sent to {to}" : result);
    }

    private async Task<(string, string?)> CreateChildAsync(ActionRunContext run, ActionParams p, int index, Func<string?, string> text, Func<string?, string> html,
        CancellationToken ct)
    {
        var parent = await db.Tickets.AsNoTracking().Include(x => x.Tags).ThenInclude(x => x.Tag).FirstAsync(x => x.Id == run.TicketId, ct);
        var department = p.Guid("departmentId") ?? parent.DepartmentId;
        var child = await tickets.CreateAsync(new CreateTicketRequest
        {
            Subject = text(p.Str("subject")),
            DescriptionHtml = p.Has("descriptionHtml") ? html(p.Str("descriptionHtml")) : parent.DescriptionHtml,
            Channel = Channel.Internal.ToString(), BrandId = parent.BrandId, CustomerId = parent.CustomerId, ContactId = parent.ContactId,
            LocationId = parent.LocationId, DepartmentId = department, ProductId = parent.ProductId, ModuleId = parent.ModuleId, VersionId = parent.VersionId,
            InstallationId = parent.InstallationId, Priority = p.Str("priority") ?? parent.Priority.ToString(), ParentTicketId = parent.Id,
            Tags = p.StrList("tags"), SkipRouting = !p.Bool("route"),
            ClientRequestId = run.ExecutionId is { } e ? $"rule:{e:N}:{index}" : null
        }, TicketCreationSource.Agent with { EnforceRequiredFields = false }, ct);
        return Ok($"Child ticket {lookups.Display(child.Number)} created");
    }

    private async Task<(string, string?)> QueueHttpAsync(ActionRunContext run, ActionParams p, int index, IReadOnlyDictionary<string, string?> values,
        CancellationToken ct)
    {
        var method = (p.Str("method") ?? "POST").ToUpperInvariant();
        var url = TemplateRenderer.Render(p.Str("url"), values, TemplateEncoding.Url);
        if (!AutomationValidator.IsValidHttpUrl(url)) throw new DomainException(ErrorCodes.Validation, "The webhook URL is not a valid http(s) address.");
        var headers = p.StrMap("headers").ToDictionary(h => h.Key,
            h => h.Value.StartsWith(SecretValues.Prefix, StringComparison.Ordinal) ? h.Value : TemplateRenderer.Render(h.Value, values, TemplateEncoding.Text),
            StringComparer.OrdinalIgnoreCase);
        string? body = null;
        if (method is not ("GET" or "DELETE"))
            body = p.Has("body") ? TemplateRenderer.Render(p.Str("body"), values, TemplateEncoding.Json) : DefaultBody(run, values);
        var job = new HttpActionJob(run.ExecutionId, index, run.RuleId, run.TicketId, method, url, headers, body, p.Str("secret"),
            Math.Clamp(p.Int("timeoutSeconds") ?? 10, 1, 30), run.SourceName);
        jobs.Enqueue(HttpActionJobHandler.JobType, job, idempotencyKey: run.ExecutionId is { } e ? $"automation-http:{e:N}:{index}:{run.Attempt}" : null, maxAttempts: 5);
        await db.SaveChangesAsync(ct);
        return (ActionStatus.Queued, $"{method} {RedactUrl(url)} queued");
    }

    private static string DefaultBody(ActionRunContext run, IReadOnlyDictionary<string, string?> v) => Json.Serialize(new
    {
        source = run.SourceName,
        ruleId = run.RuleId,
        trigger = run.Trigger,
        ticket = new
        {
            id = run.TicketId, number = v.GetValueOrDefault("ticket.number"), subject = v.GetValueOrDefault("ticket.subject"),
            status = v.GetValueOrDefault("ticket.status"), priority = v.GetValueOrDefault("ticket.priority"), department = v.GetValueOrDefault("department.name"),
            assignee = v.GetValueOrDefault("assignee.name"), customer = v.GetValueOrDefault("customer.name"), contact = v.GetValueOrDefault("contact.name")
        }
    });

    public static string RedactUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Authority}{u.AbsolutePath}" : url;
}
