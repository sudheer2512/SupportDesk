using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Automation;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Automation;

/// <param name="Trigger">Rule trigger being evaluated.</param>
/// <param name="EventId">Triggering event (outbox event id, scheduled time-bucket id or inbound webhook id) — the idempotency key with the rule version and ticket.</param>
/// <param name="CausationEventId">Event that caused the triggering event (detects events caused by a rule's own actions).</param>
/// <param name="ChainDepth">Automation hop count of the action scope (events produced by actions carry it).</param>
/// <param name="RecordNotMatched">Persist "not matched" evaluations (event rules); scheduled sweeps only record matches.</param>
public sealed record RuleRunInput(
    string Trigger,
    Guid EventId,
    Guid TicketId,
    IReadOnlyList<FieldChange>? Changes,
    Guid? CausationEventId,
    int ChainDepth,
    IReadOnlyDictionary<string, object?>? Extra = null,
    bool RecordNotMatched = true);

/// <summary>
/// Evaluates published rule versions for a trigger against a ticket and executes the actions of matching rules, in
/// execution order, each in its own DI scope as the system actor "Automation: {rule}". Safeguards: idempotency per
/// (rule version, event, ticket) via a unique RuleExecution row claimed before acting; chain-depth limit; a rule never
/// fires on events its own actions produced; an hourly per-rule per-ticket cap; StopProcessing. Every evaluation is
/// recorded with its condition trace and per-action results; transient action failures are retried automatically.
/// </summary>
public sealed class RuleEngine(
    DeskDbContext db,
    RequestContext ctx,
    TenantSettingsService settings,
    IServiceScopeFactory scopes,
    IJobQueue jobs,
    ILogger<RuleEngine> logger)
{
    public const int MaxAutomaticAttempts = 3;
    public const int MaxScheduledTicketsPerRun = 500;

    // ───────────────────────────── Event-triggered rules ─────────────────────────────

    public async Task<List<RuleExecution>> RunTriggerAsync(RuleRunInput input, CancellationToken ct)
    {
        var rules = await db.AutomationRules.AsNoTracking()
            .Where(r => r.Trigger == input.Trigger && r.IsActive && r.PublishedVersionId != null)
            .OrderBy(r => r.ExecutionOrder).ThenBy(r => r.Name).ToListAsync(ct);
        return rules.Count == 0 ? [] : await RunRulesAsync(input, rules, ct);
    }

    public async Task<List<RuleExecution>> RunRulesAsync(RuleRunInput input, IReadOnlyList<AutomationRule> rules, CancellationToken ct)
    {
        var versionIds = rules.Where(r => r.PublishedVersionId.HasValue).Select(r => r.PublishedVersionId!.Value).ToList();
        var versions = await db.RuleVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var existing = await db.RuleExecutions.AsNoTracking()
            .Where(e => e.EventId == input.EventId && e.EntityId == input.TicketId && versionIds.Contains(e.RuleVersionId))
            .ToDictionaryAsync(e => e.RuleVersionId, ct);
        var cfg = await settings.AutomationAsync(ct);
        var selfCaused = await SelfCausingRulesAsync(input, rules, ct);
        var results = new List<RuleExecution>();
        Ticket? ticket = null;
        IConditionContext? conditions = null;
        var dirty = true;

        foreach (var rule in rules)
        {
            if (rule.PublishedVersionId is not { } versionId || !versions.TryGetValue(versionId, out var version)) continue;
            if (existing.TryGetValue(versionId, out var prior))
            {
                // Idempotency: a re-delivered event never re-runs a rule that already handled it.
                results.Add(prior);
                if (rule.StopProcessing && prior.Status is RuleExecutionStatus.Succeeded or RuleExecutionStatus.PartiallyFailed or RuleExecutionStatus.Failed) break;
                continue;
            }
            if (dirty)
            {
                ticket = await LoadTicketAsync(input.TicketId, ct);
                if (ticket is null) break;
                conditions = await AutomationConditions.BuildAsync(db, ticket, input.Changes, input.Extra, ct);
                dirty = false;
            }

            var definition = RuleDefinition.Parse(version.DefinitionJson);
            var started = db.Clock.UtcNow;
            var trace = new List<ConditionTrace>();
            var matched = TriggerFilterMatches(definition, input, trace) && ConditionEvaluator.Evaluate(definition.Conditions, conditions!, trace);
            var execution = new RuleExecution
            {
                RuleId = rule.Id, RuleVersionId = versionId, EventId = input.EventId, Trigger = input.Trigger, EntityType = "Ticket", EntityId = input.TicketId,
                ChainDepth = input.ChainDepth, StartedAt = started, CompletedAt = started, CorrelationId = ctx.CorrelationId, ConditionTraceJson = Json.Serialize(trace)
            };
            if (!matched)
            {
                execution.Status = RuleExecutionStatus.NotMatched;
                if (input.RecordNotMatched && await TryInsertAsync(execution, ct)) results.Add(execution);
                continue;
            }
            var skipReason = await LoopGuardAsync(rule, input, cfg, selfCaused, started, ct);
            if (skipReason is not null)
            {
                execution.Status = RuleExecutionStatus.SkippedLoop;
                execution.Error = skipReason;
                if (await TryInsertAsync(execution, ct)) results.Add(execution);
                logger.LogInformation("Rule {Rule} skipped on ticket {Ticket}: {Reason}", rule.Name, input.TicketId, skipReason);
                continue;
            }

            // Claim the (version, event, ticket) slot before acting: a concurrent or later delivery sees the row and skips.
            execution.Status = RuleExecutionStatus.Failed;
            execution.Error = "Execution was interrupted before completion.";
            if (!await TryInsertAsync(execution, ct)) continue;
            await ExecuteActionsAsync(rule, definition, execution, input.Extra, null, ct);
            results.Add(execution);
            dirty = true;
            if (rule.StopProcessing) break;
        }
        return results;
    }

    private async Task<Ticket?> LoadTicketAsync(Guid id, CancellationToken ct) =>
        await db.Tickets.AsNoTracking().Include(t => t.Tags).ThenInclude(t => t.Tag).FirstOrDefaultAsync(t => t.Id == id && t.MergedIntoTicketId == null, ct);

    private static bool TriggerFilterMatches(RuleDefinition def, RuleRunInput input, List<ConditionTrace> trace)
    {
        if (input.Trigger != RuleTriggers.TicketUpdated || def.Trigger?.Fields is not { Count: > 0 } fields) return true;
        var changed = (input.Changes ?? []).Select(c => c.Field switch
        {
            "status" => "statusId", "assignee" => "assigneeId", "department" => "departmentId", _ => c.Field
        }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = fields.Any(f => changed.Contains(f) || changed.Contains(f.Replace("Id", "", StringComparison.Ordinal)) ||
                                     (f.StartsWith("cf:", StringComparison.OrdinalIgnoreCase) && changed.Contains("customFields")));
        trace.Add(new ConditionTrace("trigger.fields", ConditionOps.Changed, string.Join(",", fields), string.Join(",", changed), result));
        return result;
    }

    private async Task<HashSet<Guid>> SelfCausingRulesAsync(RuleRunInput input, IReadOnlyList<AutomationRule> rules, CancellationToken ct)
    {
        var result = new HashSet<Guid>();
        if (input.CausationEventId is not { } cause) return result;
        var ruleIds = rules.Select(r => r.Id).ToList();
        var prior = await db.RuleExecutions.AsNoTracking()
            .Where(e => e.EventId == cause && e.EntityId == input.TicketId && ruleIds.Contains(e.RuleId) && e.ActionResultsJson != null)
            .Select(e => new { e.RuleId, e.ActionResultsJson }).ToListAsync(ct);
        foreach (var p in prior.Where(p => ExecutionResults.Parse(p.ActionResultsJson).EmittedEventIds.Contains(input.EventId)))
            result.Add(p.RuleId);
        return result;
    }

    private async Task<string?> LoopGuardAsync(AutomationRule rule, RuleRunInput input, AutomationSettings cfg, HashSet<Guid> selfCaused, DateTime now,
        CancellationToken ct)
    {
        if (input.ChainDepth > cfg.MaxChainDepth)
            return $"Automation chain depth {input.ChainDepth} exceeds the limit of {cfg.MaxChainDepth} (loop protection).";
        if (selfCaused.Contains(rule.Id))
            return "The triggering event was produced by this rule's own actions on this ticket.";
        var since = now.AddHours(-1);
        var recent = await db.RuleExecutions.CountAsync(e => e.RuleId == rule.Id && e.EntityId == input.TicketId && e.StartedAt >= since &&
            (e.Status == RuleExecutionStatus.Succeeded || e.Status == RuleExecutionStatus.PartiallyFailed || e.Status == RuleExecutionStatus.Failed), ct);
        return recent >= cfg.MaxExecutionsPerTicketPerHour
            ? $"The rule already ran {recent} times on this ticket in the last hour (limit {cfg.MaxExecutionsPerTicketPerHour})."
            : null;
    }

    private async Task<bool> TryInsertAsync(RuleExecution execution, CancellationToken ct)
    {
        db.RuleExecutions.Add(execution);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.Entry(execution).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>Runs the actions (all, or <paramref name="onlyIndexes"/> on retry) in an automation scope and completes the execution record.</summary>
    private async Task ExecuteActionsAsync(AutomationRule rule, RuleDefinition definition, RuleExecution execution, IReadOnlyDictionary<string, object?>? extra,
        IReadOnlySet<int>? onlyIndexes, CancellationToken ct)
    {
        var previous = ExecutionResults.Parse(execution.ActionResultsJson);
        ActionRunResult run;
        using (var scope = scopes.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var actx = sp.GetRequiredService<RequestContext>();
            actx.SetSystem(ctx.TenantId, $"Automation: {rule.Name}", ActorType.Automation);
            actx.ChainDepth = execution.ChainDepth;
            actx.CausationEventId = execution.EventId;
            actx.CorrelationId = ctx.CorrelationId ?? execution.CorrelationId;
            actx.ExecutingRuleId = rule.Id;
            var executor = sp.GetRequiredService<ActionExecutor>();
            try
            {
                run = await executor.ExecuteAsync(new ActionRunContext(execution.EntityId, rule.Name, rule.Id, execution.Id, execution.Attempt, extra, execution.Trigger),
                    definition.Actions, onlyIndexes, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Only preparation can fail here (each action's own failure is captured in its outcome).
                logger.LogWarning(ex, "Rule {Rule} failed on ticket {Ticket}", rule.Name, execution.EntityId);
                run = new ActionRunResult([], []);
                execution.Error = $"{ex.GetType().Name}: {ex.Message}";
            }
            if (run.Outcomes.Count > 0)
            {
                try
                {
                    var summary = string.Join("; ", run.Outcomes.Select(o => o.Status == ActionStatus.Failed ? $"{o.Type} failed: {o.Error}" : o.Message ?? o.Type));
                    await executor.RecordHistoryAsync(execution.EntityId, TicketEventTypes.RuleApplied,
                        $"{(onlyIndexes is null ? "Rule" : "Retried rule")} '{rule.Name}' applied: {summary}",
                        new { executionId = execution.Id, ruleId = rule.Id, attempt = execution.Attempt, actions = run.Outcomes.Select(o => new { o.Type, o.Status, o.Message, o.Error }) }, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Could not record the history of rule {Rule} on ticket {Ticket}", rule.Name, execution.EntityId);
                }
            }
        }

        // Merge with earlier attempts (retries replace the outcomes they re-ran).
        var merged = previous.Actions.ToDictionary(a => a.Index);
        foreach (var o in run.Outcomes)
        {
            if (merged.TryGetValue(o.Index, out var old)) o.Attempts = old.Attempts + 1;
            merged[o.Index] = o;
        }
        previous.Actions = merged.Values.OrderBy(a => a.Index).ToList();
        previous.EmittedEventIds = previous.EmittedEventIds.Union(run.EmittedEventIds).ToList();
        execution.ActionResultsJson = previous.ToJson();
        execution.Status = StatusOf(previous.Actions, definition.Actions.Count);
        var failures = previous.Actions.Where(a => a.Status == ActionStatus.Failed).ToList();
        if (run.Outcomes.Count > 0 || failures.Count > 0)
            execution.Error = failures.Count == 0 ? null : string.Join(" | ", failures.Select(f => $"#{f.Index + 1} {f.Type}: {f.Error}"));
        execution.CompletedAt = db.Clock.UtcNow;

        var now = db.Clock.UtcNow;
        if (failures.Any(f => f.Transient) && execution.Attempt < MaxAutomaticAttempts)
        {
            var delay = TimeSpan.FromMinutes(Math.Pow(4, execution.Attempt - 1));
            jobs.Enqueue(RetryExecutionJobHandler.JobType, new RetryExecutionJob(execution.Id), now.Add(delay), $"automation-retry:{execution.Id:N}:{execution.Attempt}");
        }
        await db.SaveChangesAsync(ct);
        await db.AutomationRules.Where(r => r.Id == rule.Id).ExecuteUpdateAsync(u => u
            .SetProperty(r => r.ExecutionCount, r => r.ExecutionCount + 1).SetProperty(r => r.LastExecutedAt, now), ct);
    }

    public static RuleExecutionStatus StatusOf(IReadOnlyList<ActionOutcome> outcomes, int actionCount)
    {
        if (outcomes.Count == 0) return actionCount == 0 ? RuleExecutionStatus.Succeeded : RuleExecutionStatus.Failed;
        var failed = outcomes.Count(o => o.Status == ActionStatus.Failed);
        if (failed == 0) return RuleExecutionStatus.Succeeded;
        return failed == outcomes.Count ? RuleExecutionStatus.Failed : RuleExecutionStatus.PartiallyFailed;
    }

    // ───────────────────────────── Retry ─────────────────────────────

    /// <summary>Re-runs the failed actions of an execution (manual: all failed actions; automatic: transient failures only).</summary>
    public async Task<RuleExecution> RetryAsync(Guid executionId, bool automatic, CancellationToken ct)
    {
        var execution = await db.RuleExecutions.FirstOrDefaultAsync(e => e.Id == executionId, ct) ?? throw new NotFoundException("Rule execution", executionId);
        if (execution.Status is not (RuleExecutionStatus.Failed or RuleExecutionStatus.PartiallyFailed))
        {
            if (automatic) return execution;
            throw new ConflictException("not_retryable", "Only failed or partially failed executions can be retried.");
        }
        var rule = await db.AutomationRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == execution.RuleId, ct)
                   ?? throw new DomainException("not_retryable", "The rule no longer exists.", 409);
        var version = await db.RuleVersions.AsNoTracking().FirstAsync(v => v.Id == execution.RuleVersionId, ct);
        if (!await db.Tickets.AnyAsync(t => t.Id == execution.EntityId && t.MergedIntoTicketId == null, ct))
            throw new DomainException("not_retryable", "The ticket no longer exists or was merged.", 409);
        var definition = RuleDefinition.Parse(version.DefinitionJson);
        var previous = ExecutionResults.Parse(execution.ActionResultsJson);
        var ran = previous.Actions.Select(a => a.Index).ToHashSet();
        var indexes = previous.Actions.Where(a => a.Status == ActionStatus.Failed && (!automatic || a.Transient)).Select(a => a.Index)
            .Concat(Enumerable.Range(0, definition.Actions.Count).Where(i => !ran.Contains(i)))
            .ToHashSet();
        if (indexes.Count == 0) return execution;
        execution.Attempt++;
        await ExecuteActionsAsync(rule, definition, execution, null, indexes, ct);
        return execution;
    }

    // ───────────────────────────── Scheduled rules ─────────────────────────────

    /// <summary>Evaluates due scheduled rules of the current tenant. Each (rule, ticket, time bucket) executes at most once.</summary>
    public async Task<int> RunScheduledAsync(CancellationToken ct)
    {
        var now = db.Clock.UtcNow;
        var due = await db.AutomationRules
            .Where(r => r.Trigger == RuleTriggers.Scheduled && r.IsActive && r.PublishedVersionId != null && (r.NextScheduledRunAt == null || r.NextScheduledRunAt <= now))
            .OrderBy(r => r.ExecutionOrder).ThenBy(r => r.Name).ToListAsync(ct);
        var executed = 0;
        foreach (var rule in due)
        {
            var interval = Math.Clamp(rule.ScheduleIntervalMinutes ?? 60, 1, 7 * 24 * 60);
            var bucket = (long)Math.Floor((now - DateTime.UnixEpoch).TotalMinutes / interval);
            rule.NextScheduledRunAt = DateTime.SpecifyKind(DateTime.UnixEpoch.AddMinutes((bucket + 1) * (double)interval), DateTimeKind.Utc);
            await db.SaveChangesAsync(ct); // advance first so a failing rule cannot run in a tight loop

            var version = await db.RuleVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == rule.PublishedVersionId, ct);
            if (version is null) continue;
            var definition = RuleDefinition.Parse(version.DefinitionJson);
            var eventId = DeterministicGuid.From("automation-scheduled", rule.Id, bucket);
            var candidates = await CandidatesAsync(definition.Conditions, now, ct);
            var snapshot = new AutomationRule
            {
                Id = rule.Id, Name = rule.Name, Trigger = rule.Trigger, IsActive = true, PublishedVersionId = rule.PublishedVersionId,
                StopProcessing = rule.StopProcessing, ExecutionOrder = rule.ExecutionOrder
            };
            foreach (var ticketId in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var results = await RunRulesAsync(new RuleRunInput(RuleTriggers.Scheduled, eventId, ticketId, null, null, ChainDepth: 1, RecordNotMatched: false), [snapshot], ct);
                executed += results.Count(r => r.Status is RuleExecutionStatus.Succeeded or RuleExecutionStatus.PartiallyFailed or RuleExecutionStatus.Failed);
            }
        }
        return executed;
    }

    /// <summary>Open tickets pre-filtered in SQL by the translatable part of the conditions (exact evaluation happens afterwards).</summary>
    private async Task<List<Guid>> CandidatesAsync(ConditionGroup conditions, DateTime now, CancellationToken ct)
    {
        IQueryable<Ticket> q = db.Tickets.AsNoTracking().Where(t => t.MergedIntoTicketId == null && !t.IsArchived && t.StatusCategory != StatusCategory.Closed);
        var isAnd = !string.Equals(conditions.Logic, "or", StringComparison.OrdinalIgnoreCase) || conditions.Conditions.Count + conditions.Groups.Count == 1;
        if (isAnd)
        {
            foreach (var c in conditions.Conditions) q = TryPushDown(q, new ConditionGroup { Conditions = [c] }, now);
            foreach (var g in conditions.Groups) q = TryPushDown(q, g, now);
        }
        else q = TryPushDown(q, conditions, now);
        return await q.OrderBy(t => t.LastActivityAt).Select(t => t.Id).Take(MaxScheduledTicketsPerRun).ToListAsync(ct);
    }

    private static IQueryable<Ticket> TryPushDown(IQueryable<Ticket> q, ConditionGroup g, DateTime now)
    {
        if (g.IsEmpty) return q;
        if (g.Conditions.Count == 1 && g.Groups.Count == 0 && g.Conditions[0] is { Field: "isUnassigned" } u &&
            ConditionEvaluator.ExpectedList(u.Value).FirstOrDefault() is { } expected && bool.TryParse(expected, out var unassigned))
        {
            var wantUnassigned = u.Op == ConditionOps.NotEquals ? !unassigned : unassigned;
            return wantUnassigned ? q.Where(t => t.AssigneeId == null) : q.Where(t => t.AssigneeId != null);
        }
        try
        {
            return q.Where(TicketFilterTranslator.Translate(g, now));
        }
        catch (Exception ex) when (ex is DomainException or FormatException or ArgumentException or InvalidOperationException or OverflowException)
        {
            return q; // not translatable — evaluated in memory only
        }
    }

    // ───────────────────────────── Inbound webhook ─────────────────────────────

    public async Task<RuleExecution?> RunWebhookAsync(AutomationRule rule, Guid ticketId, Guid eventId, IReadOnlyDictionary<string, object?> payload,
        CancellationToken ct) =>
        (await RunRulesAsync(new RuleRunInput(RuleTriggers.Webhook, eventId, ticketId, null, null, ChainDepth: 1, payload), [rule], ct)).FirstOrDefault();
}
