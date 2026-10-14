using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Automation;
using SupportDesk.Contracts.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Automation;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Automation;

public static class AutomationAuditActions
{
    public const string RuleSaved = "automation.rule_saved";
    public const string RulePublished = "automation.rule_published";
    public const string RuleDeleted = "automation.rule_deleted";
    public const string RuleToggled = "automation.rule_toggled";
    public const string RuleSecretRotated = "automation.rule_secret_rotated";
    public const string BlueprintSaved = "automation.blueprint_saved";
    public const string BlueprintPublished = "automation.blueprint_published";
    public const string BlueprintDeleted = "automation.blueprint_deleted";
    public const string TicketRemovedFromBlueprint = "automation.ticket_removed_from_blueprint";
}

/// <summary>
/// Rule administration (admin.automation): CRUD, immutable versions (every save with a definition creates a draft;
/// publish supersedes the previous version; roll back republishes an old definition as a new version),
/// activation and ordering, simulation (dry run with condition trace, no side effects), conflict detection,
/// execution history and retry, and the designer catalog.
/// </summary>
public sealed class RuleService(
    DeskDbContext db,
    RequestContext ctx,
    IAuditWriter audit,
    TicketLookups lookups,
    AutomationReferenceLoader references,
    ISecretProtector secrets,
    RuleEngine engine)
{
    public const string InboundWebhookPath = "/api/v1/automation/webhooks/";

    public AutomationCatalogDto Catalog()
    {
        ctx.RequireAny(Permissions.AdminAutomation, Permissions.AdminIntegrations);
        return AutomationCatalog.ToDto();
    }

    // ───────────────────────────── Queries ─────────────────────────────

    public async Task<List<RuleSummaryDto>> ListAsync(string? trigger, bool? active, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        var q = db.AutomationRules.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(trigger)) q = q.Where(r => r.Trigger == trigger);
        if (active is { } a) q = q.Where(r => r.IsActive == a);
        var rules = await q.OrderBy(r => r.Trigger).ThenBy(r => r.ExecutionOrder).ThenBy(r => r.Name).ToListAsync(ct);
        return await SummariesAsync(rules, ct);
    }

    public async Task<RuleDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        var rule = await db.AutomationRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Rule", id);
        return await DetailAsync(rule, null, ct);
    }

    public async Task<RuleVersionDto> GetVersionAsync(Guid id, Guid versionId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        var v = await db.RuleVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == versionId && x.RuleId == id, ct) ?? throw new NotFoundException("Rule version", versionId);
        await lookups.LoadAsync(ct);
        return MapVersion(v);
    }

    // ───────────────────────────── Commands ─────────────────────────────

    public async Task<RuleDetailDto> CreateAsync(SaveRuleRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        ValidateMetadata(r);
        var definition = ParseDefinition(r.Definition);
        var rule = new AutomationRule
        {
            Name = r.Name.Trim(), Description = r.Description?.Trim(), Trigger = r.Trigger, IsActive = r.IsActive ?? true, StopProcessing = r.StopProcessing,
            ExecutionOrder = r.ExecutionOrder ?? (await db.AutomationRules.Where(x => x.Trigger == r.Trigger).MaxAsync(x => (int?)x.ExecutionOrder, ct) ?? 0) + 10,
            ScheduleIntervalMinutes = r.Trigger == RuleTriggers.Scheduled ? r.ScheduleIntervalMinutes ?? 60 : null
        };
        string? secret = null;
        if (rule.Trigger == RuleTriggers.Webhook) secret = IssueWebhookCredentials(rule);
        db.AutomationRules.Add(rule);
        var version = AddVersion(rule, definition, r.ChangeNote ?? "Created");
        if (r.Publish) await PublishCoreAsync(rule, version, ct);
        audit.Write(AutomationAuditActions.RuleSaved, "AutomationRule", rule.Id, $"Rule '{rule.Name}' created ({rule.Trigger})");
        await db.SaveChangesAsync(ct);
        return await DetailAsync(rule, secret, ct);
    }

    public async Task<RuleDetailDto> UpdateAsync(Guid id, SaveRuleRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        ValidateMetadata(r);
        var rule = await LoadAsync(id, r.RowVersion, ct);
        rule.Name = r.Name.Trim();
        rule.Description = r.Description?.Trim();
        rule.StopProcessing = r.StopProcessing;
        if (r.ExecutionOrder is { } order) rule.ExecutionOrder = order;
        if (r.IsActive is { } active) rule.IsActive = active;
        string? secret = null;
        if (rule.Trigger != r.Trigger)
        {
            rule.Trigger = r.Trigger;
            rule.NextScheduledRunAt = null;
            if (rule.Trigger == RuleTriggers.Webhook && rule.WebhookKey is null) secret = IssueWebhookCredentials(rule);
        }
        rule.ScheduleIntervalMinutes = rule.Trigger == RuleTriggers.Scheduled ? r.ScheduleIntervalMinutes ?? rule.ScheduleIntervalMinutes ?? 60 : null;
        RuleVersion? version = null;
        if (r.Definition is not null) version = AddVersion(rule, ParseDefinition(r.Definition), r.ChangeNote);
        if (r.Publish)
        {
            version ??= await db.RuleVersions.Where(v => v.RuleId == id).OrderByDescending(v => v.VersionNumber).FirstAsync(ct);
            await PublishCoreAsync(rule, version, ct);
        }
        else if (version is null && rule.PublishedVersionId is { } pub)
        {
            // A trigger change must still be valid for the published definition (e.g. change operators on scheduled rules).
            var published = await db.RuleVersions.AsNoTracking().FirstAsync(v => v.Id == pub, ct);
            ThrowIfInvalid(AutomationValidator.ValidateRule(rule.Trigger, RuleDefinition.Parse(published.DefinitionJson), await references.LoadAsync(ct)));
        }
        audit.Write(AutomationAuditActions.RuleSaved, "AutomationRule", rule.Id, $"Rule '{rule.Name}' updated{(version is null ? "" : $" (v{version.VersionNumber})")}");
        await SaveAsync(ct);
        return await DetailAsync(rule, secret, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        var rule = await db.AutomationRules.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Rule", id);
        db.AutomationRules.Remove(rule);
        audit.Write(AutomationAuditActions.RuleDeleted, "AutomationRule", rule.Id, $"Rule '{rule.Name}' deleted");
        await db.SaveChangesAsync(ct);
    }

    public async Task<RuleDetailDto> PublishAsync(Guid id, Guid versionId, string? note, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        var rule = await LoadAsync(id, null, ct);
        var version = await db.RuleVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.RuleId == id, ct) ?? throw new NotFoundException("Rule version", versionId);
        if (version.State == VersionState.Superseded)
            throw new DomainException(ErrorCodes.Validation, "This version was superseded. Use roll back to publish it again as a new version.");
        if (note is not null) version.ChangeNote = note;
        await PublishCoreAsync(rule, version, ct);
        await SaveAsync(ct);
        return await DetailAsync(rule, null, ct);
    }

    /// <summary>Republishes an older definition as a new version (history stays immutable).</summary>
    public async Task<RuleDetailDto> RollbackAsync(Guid id, Guid versionId, string? note, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        var rule = await LoadAsync(id, null, ct);
        var source = await db.RuleVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId && v.RuleId == id, ct) ?? throw new NotFoundException("Rule version", versionId);
        var version = AddVersion(rule, RuleDefinition.Parse(source.DefinitionJson), note ?? $"Rolled back to v{source.VersionNumber}");
        await PublishCoreAsync(rule, version, ct);
        await SaveAsync(ct);
        return await DetailAsync(rule, null, ct);
    }

    public async Task<RuleDetailDto> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        var rule = await LoadAsync(id, null, ct);
        if (active && rule.PublishedVersionId is null) throw new DomainException(ErrorCodes.Validation, "Publish a version before activating the rule.");
        rule.IsActive = active;
        if (active && rule.Trigger == RuleTriggers.Scheduled) rule.NextScheduledRunAt ??= db.Clock.UtcNow;
        audit.Write(AutomationAuditActions.RuleToggled, "AutomationRule", rule.Id, $"Rule '{rule.Name}' {(active ? "activated" : "deactivated")}");
        await SaveAsync(ct);
        return await DetailAsync(rule, null, ct);
    }

    /// <summary>Sets the execution order of the given rules (in list order); other rules keep their order.</summary>
    public async Task<List<RuleSummaryDto>> ReorderAsync(ReorderRulesRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        var ids = r.RuleIds.Distinct().ToList();
        var rules = await db.AutomationRules.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        if (rules.Count != ids.Count) throw new NotFoundException("Rule");
        for (var i = 0; i < ids.Count; i++) rules.First(x => x.Id == ids[i]).ExecutionOrder = (i + 1) * 10;
        await SaveAsync(ct);
        return await SummariesAsync(rules.OrderBy(x => x.ExecutionOrder).ToList(), ct);
    }

    public async Task<RuleDetailDto> RotateWebhookSecretAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        var rule = await LoadAsync(id, null, ct);
        if (rule.Trigger != RuleTriggers.Webhook) throw new DomainException(ErrorCodes.Validation, "Only webhook-triggered rules have a webhook secret.");
        var secret = TextNormalization.RandomToken(24);
        rule.WebhookKey ??= TextNormalization.RandomToken(18);
        rule.WebhookSecretHash = TextNormalization.Sha256Hex(secret);
        audit.Write(AutomationAuditActions.RuleSecretRotated, "AutomationRule", rule.Id, $"Webhook secret of rule '{rule.Name}' rotated");
        await SaveAsync(ct);
        return await DetailAsync(rule, secret, ct);
    }

    public async Task<ValidationResultDto> ValidateAsync(ValidateRuleRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        var issues = AutomationValidator.ValidateRule(r.Trigger, ParseDefinition(r.Definition), await references.LoadAsync(ct));
        return ToResult(issues);
    }

    // ───────────────────────────── Simulation ─────────────────────────────

    /// <summary>Dry run: evaluates conditions and lists the actions that would run. Nothing is changed; a "Simulated" execution is recorded.</summary>
    public async Task<SimulationResultDto> SimulateAsync(Guid? ruleId, SimulateRuleRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        AutomationRule? rule = null;
        if (ruleId is { } rid) rule = await db.AutomationRules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == rid, ct) ?? throw new NotFoundException("Rule", rid);
        RuleVersion? version = null;
        RuleDefinition definition;
        if (r.Definition is { } d) definition = ParseDefinition(d);
        else if (rule is not null)
        {
            version = r.VersionId is { } vid
                ? await db.RuleVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vid && v.RuleId == rule.Id, ct) ?? throw new NotFoundException("Rule version", vid)
                : await db.RuleVersions.AsNoTracking().Where(v => v.RuleId == rule.Id).OrderByDescending(v => v.VersionNumber).FirstAsync(ct);
            definition = RuleDefinition.Parse(version.DefinitionJson);
        }
        else throw new ValidationException(new Dictionary<string, string> { ["definition"] = "Give a definition to simulate or simulate a saved rule." });
        var trigger = r.Trigger ?? rule?.Trigger ?? RuleTriggers.TicketCreated;

        List<Ticket> candidates;
        if (r.TicketId is { } tid)
            candidates = [await db.Tickets.AsNoTracking().Include(t => t.Tags).ThenInclude(t => t.Tag).FirstOrDefaultAsync(t => t.Id == tid, ct) ?? throw new NotFoundException("Ticket", tid)];
        else
            candidates = await db.Tickets.AsNoTracking().Include(t => t.Tags).ThenInclude(t => t.Tag).Where(t => t.MergedIntoTicketId == null)
                .OrderByDescending(t => t.CreatedAt).Take(Math.Clamp(r.LastTickets ?? 10, 1, 50)).ToListAsync(ct);

        await lookups.LoadAsync(ct);
        var simulationId = Guid.NewGuid();
        var now = db.Clock.UtcNow;
        var results = new List<SimulationTicketResultDto>();
        foreach (var t in candidates)
        {
            var trace = new List<ConditionTrace>();
            var cond = await TicketConditionContext.BuildAsync(db, t, null, ct);
            var matched = ConditionEvaluator.Evaluate(definition.Conditions, cond, trace);
            string? note = null;
            if (trigger == RuleTriggers.TicketUpdated && definition.Trigger?.Fields is { Count: > 0 } fields)
                note = $"Runs only when {string.Join(", ", fields)} change (not simulated).";
            if (definition.Conditions.AllConditions().Any(c => c.Op is ConditionOps.Changed or ConditionOps.ChangedTo or ConditionOps.ChangedFrom))
                note = (note is null ? "" : note + " ") + "'changed' conditions need a triggering change and evaluate to false in a simulation.";
            var values = matched ? await TemplateValues.BuildAsync(db, lookups, t.Id, rule?.Name ?? "Simulation", null, ct) : null;
            var actions = definition.Actions.Select((a, i) => Describe(a, i, t, values, matched)).ToList();
            results.Add(new SimulationTicketResultDto(t.Id, t.Number, t.Subject, matched, trace.Select(MapTrace).ToList(), actions, note));
            if (rule is not null)
                db.RuleExecutions.Add(new RuleExecution
                {
                    RuleId = rule.Id, RuleVersionId = version?.Id ?? Guid.Empty, EventId = simulationId, Trigger = trigger, EntityType = "Ticket", EntityId = t.Id,
                    Status = RuleExecutionStatus.Simulated, ConditionTraceJson = Json.Serialize(trace), StartedAt = now, CompletedAt = now, CorrelationId = ctx.CorrelationId,
                    ActionResultsJson = new ExecutionResults
                    {
                        Actions = actions.Select(a => new ActionOutcome { Index = a.Index, Type = a.Type, Status = a.WouldRun ? "would_run" : ActionStatus.Skipped, Message = a.Description })
                            .ToList()
                    }.ToJson()
                });
        }
        if (rule is not null) await db.SaveChangesAsync(ct);
        return new SimulationResultDto(rule?.Id, version?.Id, version?.VersionNumber, results.Count, results.Count(x => x.Matched), results);
    }

    private SimulatedActionDto Describe(RuleAction a, int index, Ticket t, IReadOnlyDictionary<string, string?>? values, bool matched)
    {
        var p = a.P;
        string Text(string? s) => values is null ? s ?? "" : TemplateRenderer.Render(s, values, TemplateEncoding.Text);
        string Short(string? s) { var x = TextNormalization.HtmlToText(Text(s)); return x.Length > 120 ? x[..119] + "…" : x; }
        string? note = null;
        var description = a.Type switch
        {
            ActionTypes.Assign => p.Guid("userId") is { } u ? $"Assign to {lookups.User(u) ?? u.ToString()}"
                : p.Bool("route") ? $"Assign using routing{(p.Guid("teamId") is { } rt ? $" in {lookups.Team(rt)}" : "")}"
                : $"Assign to team {lookups.Team(p.Guid("teamId")) ?? p.Str("teamId")}",
            ActionTypes.TransferDepartment => $"Move to {lookups.Department(p.Guid("departmentId")) ?? p.Str("departmentId")}",
            ActionTypes.SetPriority => $"Set priority to {p.Str("priority")} (now {t.Priority})",
            ActionTypes.SetSeverity => $"Set severity to {p.Str("severity")}",
            ActionTypes.SetStatus => $"Set status to {(p.Guid("statusId") is { } s ? lookups.Status(s) : "?")}{(p.Str("substatus") is { } sub ? $" ({sub})" : "")}",
            ActionTypes.SetSubstatus => $"Set substatus to {p.Str("substatus")}",
            ActionTypes.SetCategory => $"Set category to {lookups.Category(p.Guid("subcategoryId") ?? p.Guid("categoryId"))}",
            ActionTypes.SetIssueType => $"Set issue type to {lookups.IssueType(p.Guid("issueTypeId"))}",
            ActionTypes.SetCustomField => $"Set custom field '{p.Str("key")}' to {Text(p.Str("value"))}",
            ActionTypes.AddTags => $"Add tags: {string.Join(", ", p.StrList("tags"))}",
            ActionTypes.RemoveTags => $"Remove tags: {string.Join(", ", p.StrList("tags"))}",
            ActionTypes.AddNote => $"Add internal note: \"{Short(p.Str("bodyHtml"))}\"",
            ActionTypes.ReplyWithTemplate => "Send a template reply to the customer",
            ActionTypes.AddWatcher => $"Add followers: {string.Join(", ", p.GuidList("userIds").Select(u => lookups.User(u) ?? u.ToString()))}",
            ActionTypes.NotifyAgents => $"Notify {string.Join(", ", p.StrList("recipients").Concat(p.GuidList("userIds").Select(u => lookups.User(u) ?? u.ToString())))}: \"{Short(p.Str("title"))}\"",
            ActionTypes.SendMessage => $"Send {p.Str("channel")} to {p.Str("to")}: \"{Short(p.Str("subject") ?? p.Str("bodyHtml"))}\"",
            ActionTypes.CreateTask => $"Create task \"{Short(p.Str("title"))}\"",
            ActionTypes.CreateChildTicket => $"Create child ticket \"{Short(p.Str("subject"))}\"",
            ActionTypes.InvokeWebhook => $"Call {p.Str("method")} {ActionExecutor.RedactUrl(values is null ? p.Str("url") ?? "" : TemplateRenderer.Render(p.Str("url"), values, TemplateEncoding.Url))}",
            ActionTypes.RequestApproval => $"Request approval: \"{Short(p.Str("reason"))}\"",
            ActionTypes.Escalate => $"Escalate{(p.Bool("raisePriority") ? " and raise priority" : "")}: \"{Short(p.Str("reason"))}\"",
            ActionTypes.AiClassify => p.Bool("apply") ? "Classify with AI and apply" : "Suggest a classification with AI",
            ActionTypes.AiSummarize => "Summarise the conversation with AI",
            ActionTypes.UpdateErpRecord => $"Update {p.Str("system")} {p.Str("recordType")} {Text(p.Str("recordNumber"))}".TrimEnd(),
            _ => $"Unknown action '{a.Type}'"
        };
        if (matched && a.Type == ActionTypes.SetStatus && t.WorkflowId is not null) note = "Would fail: the ticket follows a blueprint.";
        if (matched && a.Type is ActionTypes.InvokeWebhook or ActionTypes.SendMessage or ActionTypes.UpdateErpRecord) note = "External call — not performed in a simulation.";
        return new SimulatedActionDto(index, a.Type, description, matched, note);
    }

    // ───────────────────────────── Conflicts ─────────────────────────────

    public async Task<List<RuleConflictDto>> ConflictsAsync(string? trigger, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        var rules = await ActiveForAnalysisAsync(trigger, ct);
        return ConflictDetector.Detect(rules).Select(MapConflict).ToList();
    }

    public async Task<List<RuleConflictDto>> ConflictsForRuleAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        var rule = await db.AutomationRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Rule", id);
        return await ConflictsForAsync(rule, ct);
    }

    private async Task<List<RuleConflictDto>> ConflictsForAsync(AutomationRule rule, CancellationToken ct)
    {
        var latest = await db.RuleVersions.AsNoTracking().Where(v => v.RuleId == rule.Id).OrderByDescending(v => v.VersionNumber).FirstOrDefaultAsync(ct);
        if (latest is null) return [];
        var self = new RuleForAnalysis(rule.Id, rule.Name, rule.Trigger, rule.ExecutionOrder, rule.StopProcessing, RuleDefinition.Parse(latest.DefinitionJson));
        var others = await ActiveForAnalysisAsync(rule.Trigger, ct);
        return ConflictDetector.DetectFor(self, others).Select(MapConflict).ToList();
    }

    private async Task<List<RuleForAnalysis>> ActiveForAnalysisAsync(string? trigger, CancellationToken ct)
    {
        var q = db.AutomationRules.AsNoTracking().Where(r => r.IsActive && r.PublishedVersionId != null);
        if (!string.IsNullOrWhiteSpace(trigger)) q = q.Where(r => r.Trigger == trigger);
        var rules = await q.ToListAsync(ct);
        var versionIds = rules.Select(r => r.PublishedVersionId!.Value).ToList();
        var defs = await db.RuleVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.DefinitionJson, ct);
        return rules.Where(r => defs.ContainsKey(r.PublishedVersionId!.Value))
            .Select(r => new RuleForAnalysis(r.Id, r.Name, r.Trigger, r.ExecutionOrder, r.StopProcessing, RuleDefinition.Parse(defs[r.PublishedVersionId!.Value])))
            .ToList();
    }

    private static RuleConflictDto MapConflict(RuleConflict c) => new(c.Kind, c.Trigger, c.RuleId, c.RuleName, c.OtherRuleId, c.OtherRuleName, c.Field, c.Explanation);

    // ───────────────────────────── Executions ─────────────────────────────

    public async Task<PageResult<RuleExecutionDto>> ExecutionsAsync(ExecutionQuery query, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        var q = db.RuleExecutions.AsNoTracking();
        if (query.RuleId is { } rid) q = q.Where(e => e.RuleId == rid);
        if (query.TicketId is { } tid) q = q.Where(e => e.EntityId == tid);
        if (!string.IsNullOrWhiteSpace(query.Trigger)) q = q.Where(e => e.Trigger == query.Trigger);
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!Enum.TryParse<RuleExecutionStatus>(query.Status, true, out var status))
                throw new ValidationException(new Dictionary<string, string> { ["status"] = $"Unknown status '{query.Status}'." });
            q = q.Where(e => e.Status == status);
        }
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 200);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(e => e.StartedAt).ThenByDescending(e => e.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PageResult<RuleExecutionDto>(await MapExecutionsAsync(rows, ct), page, size, total);
    }

    public async Task<RuleExecutionDto> ExecutionAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        var e = await db.RuleExecutions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Rule execution", id);
        return (await MapExecutionsAsync([e], ct))[0];
    }

    public async Task<RuleExecutionDto> RetryAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminAutomation);
        var e = await engine.RetryAsync(id, automatic: false, ct);
        return (await MapExecutionsAsync([e], ct))[0];
    }

    private async Task<List<RuleExecutionDto>> MapExecutionsAsync(IReadOnlyList<RuleExecution> rows, CancellationToken ct)
    {
        var ruleIds = rows.Select(r => r.RuleId).Distinct().ToList();
        var versionIds = rows.Select(r => r.RuleVersionId).Distinct().ToList();
        var ticketIds = rows.Select(r => r.EntityId).Distinct().ToList();
        var names = await db.AutomationRules.AsNoTracking().Where(r => ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var numbers = await db.RuleVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.VersionNumber, ct);
        var tickets = await db.Tickets.AsNoTracking().IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(t => ticketIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Number, ct);
        return rows.Select(e =>
        {
            var results = ExecutionResults.Parse(e.ActionResultsJson);
            var trace = Json.DeserializeOr(e.ConditionTraceJson, new List<ConditionTrace>());
            return new RuleExecutionDto(e.Id, e.RuleId, names.GetValueOrDefault(e.RuleId), e.RuleVersionId, numbers.TryGetValue(e.RuleVersionId, out var n) ? n : null,
                e.EventId, e.Trigger, e.EntityType, e.EntityId, tickets.TryGetValue(e.EntityId, out var num) ? num : null, e.Status.ToString(),
                trace.Select(MapTrace).ToList(),
                results.Actions.Select(a => new ActionResultDto(a.Index, a.Type, a.Status, a.Message, a.Error, a.Transient, a.Attempts)).ToList(),
                e.Error, e.ChainDepth, e.Attempt, e.StartedAt, e.CompletedAt, e.CorrelationId);
        }).ToList();
    }

    public static ConditionTraceDto MapTrace(ConditionTrace t) => new(t.Field, t.Op, t.Expected, t.Actual, t.Result);

    // ───────────────────────────── Helpers ─────────────────────────────

    private static void ValidateMetadata(SaveRuleRequest r)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(r.Name)) errors["name"] = "A rule name is required.";
        else if (r.Name.Trim().Length > 200) errors["name"] = "At most 200 characters.";
        if (!RuleTriggers.All.Contains(r.Trigger)) errors["trigger"] = $"Unknown trigger '{r.Trigger}'.";
        if (r.ScheduleIntervalMinutes is { } i && (i < 5 || i > 7 * 24 * 60)) errors["scheduleIntervalMinutes"] = "Enter between 5 minutes and 7 days.";
        if (r.Description is { Length: > 2000 }) errors["description"] = "At most 2000 characters.";
        if (errors.Count > 0) throw new ValidationException(errors);
    }

    private RuleDefinition ParseDefinition(JsonElement? json)
    {
        var def = json is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } d ? RuleDefinition.Parse(d.GetRawText()) : new RuleDefinition();
        DefinitionSecrets.Protect(def.Actions, secrets);
        return def;
    }

    private RuleVersion AddVersion(AutomationRule rule, RuleDefinition definition, string? note)
    {
        rule.LatestVersionNumber++;
        var version = new RuleVersion
        {
            RuleId = rule.Id, VersionNumber = rule.LatestVersionNumber, DefinitionJson = definition.ToJson(), State = VersionState.Draft, ChangeNote = note,
            CreatedAt = db.Clock.UtcNow, CreatedById = ctx.UserId
        };
        db.RuleVersions.Add(version);
        return version;
    }

    private async Task PublishCoreAsync(AutomationRule rule, RuleVersion version, CancellationToken ct)
    {
        ThrowIfInvalid(AutomationValidator.ValidateRule(rule.Trigger, RuleDefinition.Parse(version.DefinitionJson), await references.LoadAsync(ct)));
        if (rule.PublishedVersionId is { } current && current != version.Id &&
            await db.RuleVersions.FirstOrDefaultAsync(v => v.Id == current, ct) is { } previous)
            previous.State = VersionState.Superseded;
        version.State = VersionState.Published;
        version.PublishedAt = db.Clock.UtcNow;
        version.PublishedById = ctx.UserId;
        rule.PublishedVersionId = version.Id;
        if (rule.Trigger == RuleTriggers.Scheduled) rule.NextScheduledRunAt ??= db.Clock.UtcNow;
        audit.Write(AutomationAuditActions.RulePublished, "AutomationRule", rule.Id, $"Rule '{rule.Name}' v{version.VersionNumber} published",
            new { version = version.VersionNumber, version.ChangeNote });
    }

    public static void ThrowIfInvalid(IReadOnlyList<ValidationIssue> issues)
    {
        var errors = issues.Where(i => !i.IsWarning).GroupBy(i => i.Path).ToDictionary(g => g.Key, g => string.Join(" ", g.Select(i => i.Message).Distinct()));
        if (errors.Count > 0) throw new ValidationException(errors);
    }

    public static ValidationResultDto ToResult(IReadOnlyList<ValidationIssue> issues) => new(
        issues.All(i => i.IsWarning),
        issues.Where(i => !i.IsWarning).Select(i => new ValidationIssueDto(i.Path, i.Message)).ToList(),
        issues.Where(i => i.IsWarning).Select(i => new ValidationIssueDto(i.Path, i.Message)).ToList());

    private static string IssueWebhookCredentials(AutomationRule rule)
    {
        var secret = TextNormalization.RandomToken(24);
        rule.WebhookKey = TextNormalization.RandomToken(18);
        rule.WebhookSecretHash = TextNormalization.Sha256Hex(secret);
        return secret;
    }

    private async Task<AutomationRule> LoadAsync(Guid id, string? rowVersion, CancellationToken ct)
    {
        var rule = await db.AutomationRules.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Rule", id);
        if (!string.IsNullOrWhiteSpace(rowVersion))
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(rowVersion); }
            catch (FormatException) { throw new ValidationException(new Dictionary<string, string> { ["rowVersion"] = "Invalid rowVersion." }); }
            if (!bytes.SequenceEqual(rule.RowVersion)) throw new ConflictException(ErrorCodes.ConcurrencyConflict, "The rule was changed by someone else. Reload and try again.");
            db.Entry(rule).Property(x => x.RowVersion).OriginalValue = bytes;
        }
        return rule;
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException(ErrorCodes.ConcurrencyConflict, "The rule was changed by someone else. Reload and try again."); }
    }

    private async Task<List<RuleSummaryDto>> SummariesAsync(IReadOnlyList<AutomationRule> rules, CancellationToken ct)
    {
        var ids = rules.Select(r => r.Id).ToList();
        var published = await db.RuleVersions.AsNoTracking().Where(v => ids.Contains(v.RuleId) && v.State == VersionState.Published)
            .Select(v => new { v.RuleId, v.VersionNumber }).ToListAsync(ct);
        return rules.Select(r => Summary(r, published.FirstOrDefault(p => p.RuleId == r.Id)?.VersionNumber)).ToList();
    }

    private static RuleSummaryDto Summary(AutomationRule r, int? publishedNumber) => new(
        r.Id, r.Name, r.Description, r.Trigger, r.IsActive, r.ExecutionOrder, r.StopProcessing, r.PublishedVersionId, publishedNumber, r.LatestVersionNumber,
        publishedNumber is null ? r.LatestVersionNumber > 0 : r.LatestVersionNumber > publishedNumber, r.ScheduleIntervalMinutes, r.NextScheduledRunAt,
        r.WebhookKey is null ? null : InboundWebhookPath + r.WebhookKey, r.ExecutionCount, r.LastExecutedAt, r.UpdatedAt, Convert.ToBase64String(r.RowVersion));

    private async Task<RuleDetailDto> DetailAsync(AutomationRule rule, string? secret, CancellationToken ct)
    {
        await lookups.LoadAsync(ct);
        var versions = await db.RuleVersions.AsNoTracking().Where(v => v.RuleId == rule.Id).OrderByDescending(v => v.VersionNumber).ToListAsync(ct);
        var fresh = await db.AutomationRules.AsNoTracking().FirstAsync(r => r.Id == rule.Id, ct);
        var published = versions.FirstOrDefault(v => v.Id == fresh.PublishedVersionId);
        var latest = versions.FirstOrDefault();
        return new RuleDetailDto(Summary(fresh, published?.VersionNumber), published is null ? null : MapVersion(published), latest is null ? null : MapVersion(latest),
            versions.Select(v => new RuleVersionSummaryDto(v.Id, v.VersionNumber, v.State.ToString(), v.ChangeNote, v.CreatedAt, lookups.User(v.CreatedById), v.PublishedAt,
                lookups.User(v.PublishedById))).ToList(),
            await ConflictsForAsync(fresh, ct), secret);
    }

    private RuleVersionDto MapVersion(RuleVersion v) => new(v.Id, v.VersionNumber, v.State.ToString(), Json.Element(v.DefinitionJson), v.ChangeNote, v.CreatedAt,
        lookups.User(v.CreatedById), v.PublishedAt, lookups.User(v.PublishedById));
}
