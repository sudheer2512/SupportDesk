using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Approvals;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Automation;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Automation;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Automation;

/// <summary>What an approved blueprint transition applies (ApprovalRequest.PayloadJson).</summary>
public sealed record TransitionApprovalPayload(Guid WorkflowId, Guid VersionId, string TransitionKey, string FromState, string? Comment,
    Dictionary<string, JsonElement>? Fields, Guid? RequestedById);

/// <summary>
/// Blueprint (state-transition) workflows: administration with versions and publish-time validation, assignment of new
/// tickets to the first matching blueprint, the per-ticket view of available transitions, and transition execution
/// (role check, mandatory fields, validation conditions, mandatory comment, approval, after-actions, status change,
/// history and events). While a ticket follows a blueprint, manual status changes are refused by the ticket service.
/// </summary>
public sealed class BlueprintService(
    DeskDbContext db,
    RequestContext ctx,
    TicketService tickets,
    TicketHistory history,
    IEventPublisher events,
    IAuditWriter audit,
    TicketLookups lookups,
    AutomationReferenceLoader references,
    ISecretProtector secrets,
    IServiceScopeFactory scopes)
{
    public const string ApprovalType = "workflow_transition";

    // ───────────────────────────── Administration ─────────────────────────────

    public async Task<List<BlueprintSummaryDto>> ListAsync(CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        var rows = await db.Workflows.AsNoTracking().OrderBy(w => w.Name).ToListAsync(ct);
        return await SummariesAsync(rows, ct);
    }

    public async Task<BlueprintDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        var wf = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw new NotFoundException("Blueprint", id);
        return await DetailAsync(wf.Id, ct);
    }

    public async Task<BlueprintDetailDto> CreateAsync(SaveBlueprintRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        await ValidateMetadataAsync(r, ct);
        var wf = new Workflow { Name = r.Name.Trim(), Description = r.Description?.Trim(), DepartmentId = r.DepartmentId, CategoryId = r.CategoryId, IsActive = r.IsActive ?? true };
        db.Workflows.Add(wf);
        var version = AddVersion(wf, ParseDefinition(r.Definition), r.ChangeNote ?? "Created");
        if (r.Publish) await PublishCoreAsync(wf, version, ct);
        audit.Write(AutomationAuditActions.BlueprintSaved, "Workflow", wf.Id, $"Blueprint '{wf.Name}' created");
        await db.SaveChangesAsync(ct);
        return await DetailAsync(wf.Id, ct);
    }

    public async Task<BlueprintDetailDto> UpdateAsync(Guid id, SaveBlueprintRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        await ValidateMetadataAsync(r, ct);
        var wf = await LoadAsync(id, r.RowVersion, ct);
        wf.Name = r.Name.Trim();
        wf.Description = r.Description?.Trim();
        wf.DepartmentId = r.DepartmentId;
        wf.CategoryId = r.CategoryId;
        if (r.IsActive is { } active) wf.IsActive = active;
        WorkflowVersion? version = null;
        if (r.Definition is not null) version = AddVersion(wf, ParseDefinition(r.Definition), r.ChangeNote);
        if (r.Publish)
        {
            version ??= await db.WorkflowVersions.Where(v => v.WorkflowId == id).OrderByDescending(v => v.VersionNumber).FirstAsync(ct);
            await PublishCoreAsync(wf, version, ct);
        }
        audit.Write(AutomationAuditActions.BlueprintSaved, "Workflow", wf.Id, $"Blueprint '{wf.Name}' updated{(version is null ? "" : $" (v{version.VersionNumber})")}");
        await SaveAsync(ct);
        return await DetailAsync(wf.Id, ct);
    }

    public async Task<BlueprintDetailDto> PublishAsync(Guid id, Guid versionId, string? note, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        var wf = await LoadAsync(id, null, ct);
        var version = await db.WorkflowVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.WorkflowId == id, ct) ?? throw new NotFoundException("Blueprint version", versionId);
        if (note is not null) version.ChangeNote = note;
        await PublishCoreAsync(wf, version, ct);
        await SaveAsync(ct);
        return await DetailAsync(wf.Id, ct);
    }

    public async Task<BlueprintDetailDto> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        var wf = await LoadAsync(id, null, ct);
        if (active && wf.PublishedVersionId is null) throw new DomainException(ErrorCodes.Validation, "Publish a version before activating the blueprint.");
        wf.IsActive = active;
        audit.Write(AutomationAuditActions.BlueprintSaved, "Workflow", wf.Id, $"Blueprint '{wf.Name}' {(active ? "activated" : "deactivated")}");
        await SaveAsync(ct);
        return await DetailAsync(wf.Id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        var wf = await db.Workflows.FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw new NotFoundException("Blueprint", id);
        var inUse = await db.Tickets.CountAsync(t => t.WorkflowId == id, ct);
        if (inUse > 0)
            throw new ConflictException("blueprint_in_use", $"{inUse} ticket(s) follow this blueprint. Deactivate it instead, or remove the tickets from the blueprint first.");
        db.Workflows.Remove(wf);
        audit.Write(AutomationAuditActions.BlueprintDeleted, "Workflow", wf.Id, $"Blueprint '{wf.Name}' deleted");
        await db.SaveChangesAsync(ct);
    }

    public async Task<ValidationResultDto> ValidateAsync(ValidateBlueprintRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        return RuleService.ToResult(AutomationValidator.ValidateBlueprint(ParseDefinition(r.Definition), await references.LoadAsync(ct)));
    }

    private async Task ValidateMetadataAsync(SaveBlueprintRequest r, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(r.Name)) errors["name"] = "A blueprint name is required.";
        else if (r.Name.Trim().Length > 200) errors["name"] = "At most 200 characters.";
        if (r.DepartmentId is { } d && !await db.Departments.AnyAsync(x => x.Id == d && x.IsActive, ct)) errors["departmentId"] = "Unknown or inactive department.";
        if (r.CategoryId is { } c && !await db.TicketCategories.AnyAsync(x => x.Id == c, ct)) errors["categoryId"] = "Unknown category.";
        if (errors.Count > 0) throw new ValidationException(errors);
    }

    private BlueprintDefinition ParseDefinition(JsonElement? json)
    {
        var def = json is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } d ? BlueprintDefinition.Parse(d.GetRawText()) : new BlueprintDefinition();
        foreach (var t in def.Transitions) DefinitionSecrets.Protect(t.Actions, secrets);
        return def;
    }

    private WorkflowVersion AddVersion(Workflow wf, BlueprintDefinition def, string? note)
    {
        wf.LatestVersionNumber++;
        var v = new WorkflowVersion
        {
            WorkflowId = wf.Id, VersionNumber = wf.LatestVersionNumber, DefinitionJson = def.ToJson(), State = VersionState.Draft, ChangeNote = note,
            CreatedAt = db.Clock.UtcNow, CreatedById = ctx.UserId
        };
        db.WorkflowVersions.Add(v);
        return v;
    }

    private async Task PublishCoreAsync(Workflow wf, WorkflowVersion version, CancellationToken ct)
    {
        RuleService.ThrowIfInvalid(AutomationValidator.ValidateBlueprint(BlueprintDefinition.Parse(version.DefinitionJson), await references.LoadAsync(ct)));
        if (wf.PublishedVersionId is { } current && current != version.Id && await db.WorkflowVersions.FirstOrDefaultAsync(v => v.Id == current, ct) is { } previous)
            previous.State = VersionState.Superseded;
        version.State = VersionState.Published;
        version.PublishedAt = db.Clock.UtcNow;
        version.PublishedById = ctx.UserId;
        wf.PublishedVersionId = version.Id;
        audit.Write(AutomationAuditActions.BlueprintPublished, "Workflow", wf.Id, $"Blueprint '{wf.Name}' v{version.VersionNumber} published");
    }

    private async Task<Workflow> LoadAsync(Guid id, string? rowVersion, CancellationToken ct)
    {
        var wf = await db.Workflows.FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw new NotFoundException("Blueprint", id);
        if (!string.IsNullOrWhiteSpace(rowVersion))
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(rowVersion); }
            catch (FormatException) { throw new ValidationException(new Dictionary<string, string> { ["rowVersion"] = "Invalid rowVersion." }); }
            if (!bytes.SequenceEqual(wf.RowVersion)) throw new ConflictException(ErrorCodes.ConcurrencyConflict, "The blueprint was changed by someone else. Reload and try again.");
            db.Entry(wf).Property(x => x.RowVersion).OriginalValue = bytes;
        }
        return wf;
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException(ErrorCodes.ConcurrencyConflict, "The blueprint was changed by someone else. Reload and try again."); }
    }

    private async Task<List<BlueprintSummaryDto>> SummariesAsync(IReadOnlyList<Workflow> rows, CancellationToken ct)
    {
        await lookups.LoadAsync(ct);
        var ids = rows.Select(w => w.Id).ToList();
        var published = await db.WorkflowVersions.AsNoTracking().Where(v => ids.Contains(v.WorkflowId) && v.State == VersionState.Published)
            .Select(v => new { v.WorkflowId, v.VersionNumber }).ToListAsync(ct);
        var counts = await db.Tickets.AsNoTracking().Where(t => t.WorkflowId != null && ids.Contains(t.WorkflowId.Value) && t.StatusCategory != StatusCategory.Closed)
            .GroupBy(t => t.WorkflowId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return rows.Select(w =>
        {
            var pub = published.FirstOrDefault(p => p.WorkflowId == w.Id)?.VersionNumber;
            return new BlueprintSummaryDto(w.Id, w.Name, w.Description, w.DepartmentId, lookups.Department(w.DepartmentId), w.CategoryId, lookups.Category(w.CategoryId),
                w.IsActive, w.PublishedVersionId, pub, w.LatestVersionNumber, pub is null ? w.LatestVersionNumber > 0 : w.LatestVersionNumber > pub,
                counts.GetValueOrDefault(w.Id), w.UpdatedAt, Convert.ToBase64String(w.RowVersion));
        }).ToList();
    }

    private async Task<BlueprintDetailDto> DetailAsync(Guid id, CancellationToken ct)
    {
        var wf = await db.Workflows.AsNoTracking().FirstAsync(w => w.Id == id, ct);
        var summary = (await SummariesAsync([wf], ct))[0];
        var versions = await db.WorkflowVersions.AsNoTracking().Where(v => v.WorkflowId == id).OrderByDescending(v => v.VersionNumber).ToListAsync(ct);
        RuleVersionDto Map(WorkflowVersion v) => new(v.Id, v.VersionNumber, v.State.ToString(), Json.Element(v.DefinitionJson), v.ChangeNote, v.CreatedAt,
            lookups.User(v.CreatedById), v.PublishedAt, lookups.User(v.PublishedById));
        var published = versions.FirstOrDefault(v => v.Id == wf.PublishedVersionId);
        var latest = versions.FirstOrDefault();
        return new BlueprintDetailDto(summary, published is null ? null : Map(published), latest is null ? null : Map(latest),
            versions.Select(v => new RuleVersionSummaryDto(v.Id, v.VersionNumber, v.State.ToString(), v.ChangeNote, v.CreatedAt, lookups.User(v.CreatedById),
                v.PublishedAt, lookups.User(v.PublishedById))).ToList());
    }

    // ───────────────────────────── Assignment on creation ─────────────────────────────

    /// <summary>Puts a new ticket on the most specific active blueprint (department + category, category, department, any).</summary>
    public async Task AssignOnCreateAsync(Guid ticketId, CancellationToken ct = default)
    {
        var t = await db.Tickets.Include(x => x.Tags).FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (t is null || t.WorkflowId is not null || t.MergedIntoTicketId is not null) return;
        var candidates = await db.Workflows.AsNoTracking()
            .Where(w => w.IsActive && w.PublishedVersionId != null && (w.DepartmentId == null || w.DepartmentId == t.DepartmentId) &&
                        (w.CategoryId == null || w.CategoryId == t.CategoryId || w.CategoryId == t.SubcategoryId))
            .ToListAsync(ct);
        var wf = candidates.OrderByDescending(w => (w.CategoryId is null ? 0 : 2) + (w.DepartmentId is null ? 0 : 1)).ThenBy(w => w.CreatedAt).FirstOrDefault();
        if (wf is null) return;
        var version = await db.WorkflowVersions.AsNoTracking().FirstAsync(v => v.Id == wf.PublishedVersionId, ct);
        var def = BlueprintDefinition.Parse(version.DefinitionJson);
        if (def.Initial is not { } initial) return;
        t.WorkflowId = wf.Id;
        t.WorkflowVersionId = version.Id;
        t.WorkflowStateKey = initial.Key;
        await EnterStateAsync(t, initial, $"Blueprint '{wf.Name}' started", ct);
        history.Record(t, TicketEventTypes.WorkflowTransition, $"Blueprint '{wf.Name}' applied — state: {initial.Name}",
            [new FieldChange("workflow", null, wf.Name), new FieldChange("workflowState", null, initial.Name)],
            new { workflowId = wf.Id, versionId = version.Id, version = version.VersionNumber, state = initial.Key });
        events.Publish(EventTypes.TicketUpdated, "Ticket", t.Id, new TicketEventPayload(t.Id, t.Number, [new FieldChange("workflowState", null, initial.Key)]));
        await tickets.SaveAsync(t, ct);
    }

    private async Task EnterStateAsync(Ticket t, BlueprintState state, string comment, CancellationToken ct)
    {
        if (state.StatusId is not { } statusId) return;
        if (statusId == t.StatusId && (state.Substatus ?? t.Substatus) == t.Substatus) return;
        var status = await db.TicketStatuses.AsNoTracking().FirstOrDefaultAsync(s => s.Id == statusId && s.IsActive, ct)
                     ?? throw new DomainException("configuration_error", $"The status of blueprint state '{state.Name}' no longer exists or is inactive.");
        await tickets.ApplyStatusAsync(t, status, state.Substatus, comment, bypassRules: true, ct);
    }

    // ───────────────────────────── Ticket view ─────────────────────────────

    public async Task<TicketBlueprintDto?> GetTicketBlueprintAsync(Guid ticketId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsView);
        var t = await db.Tickets.AsNoTracking().Include(x => x.Tags).ThenInclude(x => x.Tag).FirstOrDefaultAsync(x => x.Id == ticketId, ct)
                ?? throw new NotFoundException("Ticket", ticketId);
        await tickets.EnsureVisibleAsync(t, ct);
        if (await LoadTicketWorkflowAsync(t, ct) is not { } loaded) return null;
        var (wf, version, def) = loaded;
        var state = def.State(t.WorkflowStateKey) ?? def.Initial!;
        var statuses = await db.TicketStatuses.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        BlueprintStateDto MapState(BlueprintState s) => new(s.Key, s.Name, s.StatusId, s.StatusId is { } sid ? statuses.GetValueOrDefault(sid) : null, s.Substatus,
            s.IsInitial, s.IsFinal, s.X, s.Y);

        var pending = await PendingApprovalAsync(t.Id, ct);
        var roles = await CallerRolesAsync(ct);
        var labels = await FieldLabelsAsync(ct);
        var cond = await AutomationConditions.BuildAsync(db, t, null, null, ct);
        var transitions = new List<BlueprintTransitionDto>();
        if (!state.IsFinal)
            foreach (var tr in def.From(state.Key).Where(tr => RoleAllowed(tr, roles)))
            {
                var missing = MissingFields(t, tr, null);
                var validation = tr.Validation is not { IsEmpty: false } v || ConditionEvaluator.Evaluate(v, cond);
                var blocked = pending is not null ? "Another transition is awaiting approval."
                    : !validation ? tr.ValidationMessage ?? "The ticket does not meet the conditions for this transition." : null;
                var to = def.State(tr.To);
                transitions.Add(new BlueprintTransitionDto(tr.Key, tr.Name, tr.To, to?.Name ?? tr.To, tr.RequireComment, tr.Approval is not null,
                    tr.RequiredFields.Select(f => new RequiredFieldDto(f, labels.GetValueOrDefault(f, f), missing.Contains(f))).ToList(), missing,
                    validation, blocked is null, blocked));
            }

        PendingTransitionDto? pendingDto = null;
        if (pending is not null)
        {
            await lookups.LoadAsync(ct);
            var payload = Json.Deserialize<TransitionApprovalPayload>(pending.PayloadJson);
            var tr = def.Transitions.FirstOrDefault(x => x.Key == payload?.TransitionKey);
            pendingDto = new PendingTransitionDto(pending.Id, payload?.TransitionKey ?? "", tr?.Name ?? payload?.TransitionKey ?? "", pending.RequestedById,
                lookups.User(pending.RequestedById), pending.CreatedAt);
        }
        return new TicketBlueprintDto(t.Id, wf.Id, wf.Name, version.Id, version.VersionNumber, MapState(state), transitions, pendingDto,
            def.States.Select(MapState).ToList());
    }

    // ───────────────────────────── Transitions ─────────────────────────────

    public async Task<TransitionResultDto> ExecuteTransitionAsync(Guid ticketId, string key, ExecuteTransitionRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsEdit);
        var t = await tickets.LoadForUpdateAsync(ticketId, ct);
        var loaded = await LoadTicketWorkflowAsync(t, ct) ?? throw new DomainException(ErrorCodes.InvalidTransition, "This ticket does not follow a blueprint.");
        var (wf, version, def) = loaded;
        var state = def.State(t.WorkflowStateKey) ?? throw new DomainException(ErrorCodes.InvalidTransition, "The ticket's blueprint state is unknown.");
        var tr = (state.IsFinal ? null : def.From(state.Key).FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)))
                 ?? throw new DomainException(ErrorCodes.InvalidTransition, $"'{key}' is not available from '{state.Name}'.");
        var roles = await CallerRolesAsync(ct);
        if (!RoleAllowed(tr, roles)) throw new ForbiddenException($"Your role cannot perform '{tr.Name}'.");
        if (await PendingApprovalAsync(t.Id, ct) is not null)
            throw new ConflictException("transition_pending_approval", "A blueprint transition on this ticket is awaiting approval.");

        var fields = NormalizeFields(r.Fields);
        var missing = MissingFields(t, tr, fields);
        if (missing.Count > 0)
        {
            var labels = await FieldLabelsAsync(ct);
            throw new DomainException(ErrorCodes.MissingFields,
                $"Fill in the mandatory fields for '{tr.Name}': {string.Join(", ", missing.Select(m => labels.GetValueOrDefault(m, m)))}.", 422);
        }
        if (tr.RequireComment && string.IsNullOrWhiteSpace(r.Comment))
            throw new ValidationException(new Dictionary<string, string> { ["comment"] = $"A comment is required for '{tr.Name}'." });
        if (!await ValidationPassesAsync(t, tr, fields, ct))
            throw new DomainException("transition_validation_failed", tr.ValidationMessage ?? $"The ticket does not meet the conditions for '{tr.Name}'.", 422);

        if (tr.Approval is { } approval && !ctx.IsSystem && !await IsApproverAsync(approval, roles, ct))
        {
            var request = new ApprovalRequest
            {
                Type = ApprovalType, EntityType = "Ticket", EntityId = t.Id, TicketId = t.Id, RequestedById = ctx.UserId ?? Guid.Empty,
                ApproverUserId = approval.UserId, ApproverRoleId = approval.RoleId, ExpiresAt = db.Clock.UtcNow.AddDays(7),
                Reason = $"{wf.Name}: {tr.Name}{(string.IsNullOrWhiteSpace(r.Comment) ? "" : $" — {r.Comment.Trim()}")}{(approval.Note is null ? "" : $" ({approval.Note})")}",
                PayloadJson = Json.Serialize(new TransitionApprovalPayload(wf.Id, version.Id, tr.Key, state.Key, r.Comment?.Trim(), fields, ctx.UserId))
            };
            db.ApprovalRequests.Add(request);
            history.Record(t, TicketEventTypes.ApprovalRequested, $"Blueprint transition '{tr.Name}' requested — awaiting approval", null,
                new { approvalId = request.Id, transition = tr.Key, from = state.Key, to = tr.To, comment = r.Comment });
            events.Publish(EventTypes.ApprovalRequested, "ApprovalRequest", request.Id, new EntityEventPayload(request.Id, ApprovalType));
            await tickets.SaveAsync(t, ct);
            return new TransitionResultDto("pending_approval", state.Key, state.Name, request.Id, []);
        }

        var results = await CompleteTransitionAsync(t, wf, version, def, state, tr, r.Comment?.Trim(), fields, null, ct);
        var target = def.State(tr.To)!;
        return new TransitionResultDto("completed", target.Key, target.Name, null, results);
    }

    /// <summary>Called by <see cref="WorkflowTransitionApprovalAction"/> in a system scope once the approver approved.</summary>
    public async Task CompleteApprovedAsync(Guid ticketId, TransitionApprovalPayload payload, string approverName, CancellationToken ct)
    {
        var t = await db.Tickets.Include(x => x.Tags).FirstOrDefaultAsync(x => x.Id == ticketId, ct) ?? throw new NotFoundException("Ticket", ticketId);
        if (t.WorkflowId != payload.WorkflowId || !string.Equals(t.WorkflowStateKey, payload.FromState, StringComparison.OrdinalIgnoreCase))
            throw new ConflictException("blueprint_state_changed", "The ticket is no longer in the blueprint state this request was made from. Reject the request instead.");
        var wf = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == payload.WorkflowId, ct) ?? throw new NotFoundException("Blueprint", payload.WorkflowId);
        var version = await db.WorkflowVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == (t.WorkflowVersionId ?? payload.VersionId), ct)
                      ?? throw new NotFoundException("Blueprint version", payload.VersionId);
        var def = BlueprintDefinition.Parse(version.DefinitionJson);
        var state = def.State(payload.FromState) ?? throw new DomainException(ErrorCodes.InvalidTransition, "The blueprint state no longer exists.");
        var tr = def.From(state.Key).FirstOrDefault(x => string.Equals(x.Key, payload.TransitionKey, StringComparison.OrdinalIgnoreCase))
                 ?? throw new DomainException(ErrorCodes.InvalidTransition, "The transition no longer exists in this blueprint version.");
        var fields = payload.Fields ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var missing = MissingFields(t, tr, fields);
        if (missing.Count > 0)
            throw new DomainException(ErrorCodes.MissingFields, $"Mandatory fields are no longer filled in: {string.Join(", ", missing)}.", 422);
        await CompleteTransitionAsync(t, wf, version, def, state, tr, payload.Comment, fields, approverName, ct);
    }

    private async Task<List<ActionResultDto>> CompleteTransitionAsync(Ticket t, Workflow wf, WorkflowVersion version, BlueprintDefinition def, BlueprintState from,
        BlueprintTransition tr, string? comment, Dictionary<string, JsonElement> fields, string? approvedBy, CancellationToken ct)
    {
        if (fields.Count > 0) await ApplyFieldsAsync(t, fields, ct);
        var to = def.State(tr.To)!;
        t.WorkflowStateKey = to.Key;
        await EnterStateAsync(t, to, $"{wf.Name}: {tr.Name}{(string.IsNullOrWhiteSpace(comment) ? "" : $" — {comment}")}", ct);
        t.LastActivityAt = db.Clock.UtcNow;
        history.Record(t, TicketEventTypes.WorkflowTransition,
            $"{wf.Name}: {from.Name} → {to.Name} ({tr.Name}){(approvedBy is null ? "" : $", approved by {approvedBy}")}{(string.IsNullOrWhiteSpace(comment) ? "" : $" — {comment}")}",
            [new FieldChange("workflowState", from.Name, to.Name)],
            new { workflowId = wf.Id, versionId = version.Id, transition = tr.Key, from = from.Key, to = to.Key, comment, approvedBy, fields = fields.Keys });
        events.Publish(EventTypes.TicketUpdated, "Ticket", t.Id, new TicketEventPayload(t.Id, t.Number, [new FieldChange("workflowState", from.Key, to.Key)], Note: comment));
        await tickets.SaveAsync(t, ct);
        if (tr.Actions.Count == 0) return [];
        return await RunAfterActionsAsync(t.Id, wf.Name, tr, ct);
    }

    private async Task<List<ActionResultDto>> RunAfterActionsAsync(Guid ticketId, string workflowName, BlueprintTransition tr, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var actx = scope.ServiceProvider.GetRequiredService<RequestContext>();
        actx.SetSystem(ctx.TenantId, $"Blueprint: {workflowName}", ActorType.Automation);
        actx.ChainDepth = ctx.ChainDepth + 1;
        actx.CorrelationId = ctx.CorrelationId;
        var executor = scope.ServiceProvider.GetRequiredService<ActionExecutor>();
        var run = await executor.ExecuteAsync(new ActionRunContext(ticketId, $"Blueprint: {workflowName}"), tr.Actions, null, ct);
        var summary = string.Join("; ", run.Outcomes.Select(o => o.Status == ActionStatus.Failed ? $"{o.Type} failed: {o.Error}" : o.Message ?? o.Type));
        await executor.RecordHistoryAsync(ticketId, TicketEventTypes.WorkflowTransition, $"Transition '{tr.Name}' actions: {summary}",
            new { transition = tr.Key, actions = run.Outcomes.Select(o => new { o.Type, o.Status, o.Message, o.Error }) }, ct);
        return run.Outcomes.Select(o => new ActionResultDto(o.Index, o.Type, o.Status, o.Message, o.Error, o.Transient, o.Attempts)).ToList();
    }

    public async Task RemoveFromBlueprintAsync(Guid ticketId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        var t = await tickets.LoadForUpdateAsync(ticketId, ct);
        if (t.WorkflowId is not { } workflowId) return;
        var name = await db.Workflows.Where(w => w.Id == workflowId).Select(w => w.Name).FirstOrDefaultAsync(ct) ?? "blueprint";
        await db.ApprovalRequests.Where(a => a.TicketId == t.Id && a.Type == ApprovalType && a.Status == ApprovalStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, ApprovalStatus.Cancelled).SetProperty(a => a.DecisionNote, "Ticket removed from the blueprint"), ct);
        var state = t.WorkflowStateKey;
        t.WorkflowId = null;
        t.WorkflowVersionId = null;
        t.WorkflowStateKey = null;
        t.LastActivityAt = db.Clock.UtcNow;
        history.Record(t, TicketEventTypes.WorkflowTransition, $"Removed from blueprint '{name}'", [new FieldChange("workflow", name, null)], new { workflowId, state });
        audit.Write(AutomationAuditActions.TicketRemovedFromBlueprint, "Ticket", t.Id, $"Ticket #{t.Number} removed from blueprint '{name}'");
        events.Publish(EventTypes.TicketUpdated, "Ticket", t.Id, new TicketEventPayload(t.Id, t.Number, [new FieldChange("workflow", name, null)]));
        await tickets.SaveAsync(t, ct);
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    private async Task<(Workflow Workflow, WorkflowVersion Version, BlueprintDefinition Definition)?> LoadTicketWorkflowAsync(Ticket t, CancellationToken ct)
    {
        if (t.WorkflowId is not { } workflowId) return null;
        var wf = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workflowId, ct);
        if (wf is null) return null;
        var versionId = t.WorkflowVersionId ?? wf.PublishedVersionId;
        var version = versionId is null ? null : await db.WorkflowVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (version is null) return null;
        var def = BlueprintDefinition.Parse(version.DefinitionJson);
        return def.Initial is null ? null : (wf, version, def);
    }

    private async Task<ApprovalRequest?> PendingApprovalAsync(Guid ticketId, CancellationToken ct)
    {
        var now = db.Clock.UtcNow;
        return await db.ApprovalRequests.AsNoTracking()
            .Where(a => a.TicketId == ticketId && a.Type == ApprovalType && a.Status == ApprovalStatus.Pending && (a.ExpiresAt == null || a.ExpiresAt > now))
            .OrderByDescending(a => a.CreatedAt).FirstOrDefaultAsync(ct);
    }

    private async Task<HashSet<Guid>> CallerRolesAsync(CancellationToken ct) =>
        ctx.UserId is { } uid ? (await db.UserRoles.AsNoTracking().Where(r => r.UserId == uid).Select(r => r.RoleId).ToListAsync(ct)).ToHashSet() : [];

    /// <summary>Allowed roles (empty = anyone who may edit the ticket); blueprint administrators may perform any transition.</summary>
    private bool RoleAllowed(BlueprintTransition tr, HashSet<Guid> roles) =>
        tr.AllowedRoleIds.Count == 0 || ctx.IsSystem || ctx.Has(Permissions.AdminAutomation) || tr.AllowedRoleIds.Any(roles.Contains);

    /// <summary>The designated approver performing the transition approves it implicitly.</summary>
    private Task<bool> IsApproverAsync(BlueprintApproval approval, HashSet<Guid> roles, CancellationToken ct) =>
        Task.FromResult((approval.UserId is { } u && u == ctx.UserId) || (approval.RoleId is { } r && roles.Contains(r)));

    private static Dictionary<string, JsonElement> NormalizeFields(Dictionary<string, JsonElement>? input)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (input is null) return result;
        var errors = new Dictionary<string, string>();
        foreach (var (key, value) in input)
        {
            if (key.StartsWith("cf:", StringComparison.OrdinalIgnoreCase) || AutomationCatalog.RequirableFields.ContainsKey(key)) result[key] = value;
            else errors[$"fields.{key}"] = "This field cannot be set during a transition.";
        }
        if (errors.Count > 0) throw new ValidationException(errors);
        return result;
    }

    private static List<string> MissingFields(Ticket t, BlueprintTransition tr, IReadOnlyDictionary<string, JsonElement>? provided) =>
        tr.RequiredFields.Where(f => !(provided is not null && provided.TryGetValue(f, out var v) && !IsEmpty(v)) && !IsSet(t, f)).ToList();

    private static bool IsEmpty(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()),
        JsonValueKind.Array => v.GetArrayLength() == 0,
        _ => false
    };

    private static bool IsSet(Ticket t, string field)
    {
        if (field.StartsWith("cf:", StringComparison.OrdinalIgnoreCase))
            return Json.Object(t.CustomFieldsJson).TryGetValue(field[3..], out var v) && !IsEmpty(v);
        return field.ToLowerInvariant() switch
        {
            "priority" => true,
            "severity" => t.Severity is not null,
            "categoryid" => t.CategoryId is not null,
            "subcategoryid" => t.SubcategoryId is not null,
            "issuetypeid" => t.IssueTypeId is not null,
            "productid" => t.ProductId is not null,
            "moduleid" => t.ModuleId is not null,
            "versionid" => t.VersionId is not null,
            "installationid" => t.InstallationId is not null,
            "customerid" => t.CustomerId is not null,
            "contactid" => t.ContactId is not null,
            "assigneeid" => t.AssigneeId is not null,
            "teamid" => t.TeamId is not null,
            "resolutioncode" => !string.IsNullOrWhiteSpace(t.ResolutionCode),
            "rootcause" => !string.IsNullOrWhiteSpace(t.RootCause),
            _ => false
        };
    }

    private async Task<Dictionary<string, string>> FieldLabelsAsync(CancellationToken ct)
    {
        var labels = new Dictionary<string, string>(AutomationCatalog.RequirableFields, StringComparer.OrdinalIgnoreCase);
        foreach (var f in await db.CustomFields.AsNoTracking().Where(f => f.Entity == "Ticket").Select(f => new { f.Key, f.Label }).ToListAsync(ct))
            labels["cf:" + f.Key] = f.Label;
        return labels;
    }

    private async Task<bool> ValidationPassesAsync(Ticket t, BlueprintTransition tr, IReadOnlyDictionary<string, JsonElement> fields, CancellationToken ct)
    {
        if (tr.Validation is not { IsEmpty: false } validation) return true;
        var snapshot = await db.Tickets.AsNoTracking().Include(x => x.Tags).ThenInclude(x => x.Tag).FirstAsync(x => x.Id == t.Id, ct);
        var cond = await AutomationConditions.BuildAsync(db, snapshot, null,
            fields.ToDictionary(f => f.Key, f => ToValue(f.Value), StringComparer.OrdinalIgnoreCase), ct);
        return ConditionEvaluator.Evaluate(validation, cond);
    }

    private static object? ToValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => v.EnumerateArray().Select(x => x.ToString()).ToList(),
        JsonValueKind.Null => null,
        _ => v.GetRawText()
    };

    /// <summary>Applies transition input through the ticket service (validation, SLA, history) — resolution fields directly.</summary>
    private async Task ApplyFieldsAsync(Ticket t, IReadOnlyDictionary<string, JsonElement> fields, CancellationToken ct)
    {
        string? S(string key) => fields.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : fields.TryGetValue(key, out var x) && x.ValueKind != JsonValueKind.Null ? x.ToString() : null;
        Guid? G(string key) => Guid.TryParse(S(key), out var g) ? g : null;
        var custom = fields.Where(f => f.Key.StartsWith("cf:", StringComparison.OrdinalIgnoreCase)).ToDictionary(f => f.Key[3..], f => f.Value);
        var update = new UpdateTicketRequest
        {
            RowVersion = Convert.ToBase64String(t.RowVersion), Priority = S("priority"), Severity = S("severity"), CategoryId = G("categoryId"),
            SubcategoryId = G("subcategoryId"), IssueTypeId = G("issueTypeId"), ProductId = G("productId"), ModuleId = G("moduleId"), VersionId = G("versionId"),
            InstallationId = G("installationId"), CustomerId = G("customerId"), ContactId = G("contactId"), CustomFields = custom.Count > 0 ? custom : null
        };
        await tickets.UpdateAsync(t.Id, update, ct);
        var changes = new ChangeSet();
        if (S("resolutionCode") is { Length: > 0 } code) changes.Set("resolutionCode", t.ResolutionCode, code, v => t.ResolutionCode = v);
        if (S("rootCause") is { Length: > 0 } cause) changes.Set("rootCause", t.RootCause, cause, v => t.RootCause = v);
        if (changes.Any) history.Record(t, TicketEventTypes.FieldsChanged, "Resolution details updated", changes.Changes);
        if (G("assigneeId") is not null || G("teamId") is not null)
            await tickets.AssignCoreAsync(t, G("assigneeId") ?? t.AssigneeId, G("teamId"), "Blueprint transition", ct);
    }
}

/// <summary>Completes a blueprint transition once its approval request is approved.</summary>
public sealed class WorkflowTransitionApprovalAction(RequestContext ctx, IServiceScopeFactory scopes) : IApprovalAction
{
    public string Type => BlueprintService.ApprovalType;

    public async Task ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var payload = Json.Deserialize<TransitionApprovalPayload>(request.PayloadJson)
                      ?? throw new DomainException(ErrorCodes.Validation, "The approval request has no transition details.");
        var approver = ctx.ActorName ?? "approver";
        // The approver may not be allowed to edit the ticket directly; the approved transition runs as the blueprint itself.
        using var scope = scopes.CreateScope();
        var sctx = scope.ServiceProvider.GetRequiredService<RequestContext>();
        sctx.SetSystem(ctx.TenantId, $"Blueprint (approved by {approver})", ActorType.Automation);
        sctx.CorrelationId = ctx.CorrelationId;
        sctx.ChainDepth = ctx.ChainDepth;
        await scope.ServiceProvider.GetRequiredService<BlueprintService>().CompleteApprovedAsync(request.EntityId, payload, approver, ct);
    }
}
