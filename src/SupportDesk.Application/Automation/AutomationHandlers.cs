using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Domain;
using SupportDesk.Domain.Automation;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Automation;

/// <summary>
/// Maps outbox events to rule triggers (ticket.*, sla.warning/breached, customer.activity) and runs the matching rules.
/// Runs with the dispatcher's system context: ChainDepth = event depth + 1, CausationEventId = event id.
/// </summary>
public sealed class AutomationOutboxHandler(DeskDbContext db, RequestContext ctx, RuleEngine engine, ILogger<AutomationOutboxHandler> logger) : IOutboxHandler
{
    /// <summary>ticket.updated changes that are not field edits (soft locks) and never trigger rules.</summary>
    private static readonly HashSet<string> IgnoredChanges = new(["lock"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Events older than this (replayed backlogs, restored databases, imported history) do not trigger rules: acting on
    /// today's ticket state because of a days-old event would surprise agents and customers.
    /// </summary>
    public static readonly TimeSpan MaxEventAge = TimeSpan.FromHours(72);

    public string Name => "automation-rules";
    public int Order => HandlerOrder.Automation;
    public bool CanHandle(string eventType) => AutomationCatalog.EventTriggers.Contains(eventType);

    public async Task HandleAsync(OutboxEvent ev, CancellationToken ct)
    {
        if (ev.TenantId is null) return;
        if (!await db.AutomationRules.AnyAsync(r => r.Trigger == ev.Type && r.IsActive && r.PublishedVersionId != null, ct)) return;
        if (ev.OccurredAt < db.Clock.UtcNow - MaxEventAge)
        {
            logger.LogInformation("Automation skipped stale event {Event} ({Type}) from {OccurredAt:u}", ev.Id, ev.Type, ev.OccurredAt);
            return;
        }

        IReadOnlyList<FieldChange>? changes = null;
        List<Guid> ticketIds;
        if (ev.AggregateType == "Ticket" && ev.AggregateId is { } ticketId)
        {
            ticketIds = [ticketId];
            if (ev.Type.StartsWith("ticket.", StringComparison.Ordinal)) changes = TryPayload<TicketEventPayload>(ev.PayloadJson)?.Changes;
            if (ev.Type == EventTypes.TicketUpdated && changes is { Count: > 0 } && changes.All(c => IgnoredChanges.Contains(c.Field))) return;
        }
        else ticketIds = await RelatedTicketsAsync(ev, ct);

        foreach (var id in ticketIds)
            await engine.RunTriggerAsync(new RuleRunInput(ev.Type, ev.Id, id, changes, ev.CausationId, ctx.ChainDepth), ct);
    }

    /// <summary>customer.activity and other non-ticket events: the referenced ticket, else the customer's/contact's open tickets.</summary>
    private async Task<List<Guid>> RelatedTicketsAsync(OutboxEvent ev, CancellationToken ct)
    {
        var payload = Json.Object(ev.PayloadJson);
        if (payload.TryGetValue("ticketId", out var t) && Guid.TryParse(t.ToString(), out var tid)) return [tid];
        Guid? Id(string key) => payload.TryGetValue(key, out var v) && Guid.TryParse(v.ToString(), out var g) ? g : null;
        var customerId = ev.AggregateType == "Customer" ? ev.AggregateId ?? Id("id") : Id("customerId");
        var contactId = ev.AggregateType == "Contact" ? ev.AggregateId ?? Id("id") : Id("contactId");
        if (customerId is null && contactId is null) return [];
        return await db.Tickets.AsNoTracking()
            .Where(x => x.StatusCategory < StatusCategory.Resolved && x.MergedIntoTicketId == null &&
                        ((customerId != null && x.CustomerId == customerId) || (contactId != null && x.ContactId == contactId)))
            .OrderByDescending(x => x.LastActivityAt).Select(x => x.Id).Take(25).ToListAsync(ct);
    }

    private static T? TryPayload<T>(string json) where T : class
    {
        try { return Json.Deserialize<T>(json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>Puts new tickets on the first matching active blueprint (runs just before the automation rules).</summary>
public sealed class BlueprintAssignmentHandler(DeskDbContext db, BlueprintService blueprints) : IOutboxHandler
{
    public string Name => "blueprint-assignment";
    public int Order => HandlerOrder.Automation - 5;
    public bool CanHandle(string eventType) => eventType == EventTypes.TicketCreated;

    public async Task HandleAsync(OutboxEvent ev, CancellationToken ct)
    {
        if (ev.TenantId is null || ev.AggregateId is not { } ticketId) return;
        if (!await db.Workflows.AnyAsync(w => w.IsActive && w.PublishedVersionId != null, ct)) return;
        await blueprints.AssignOnCreateAsync(ticketId, ct);
    }
}

/// <summary>Evaluates due scheduled rules for every active tenant (every minute; each rule has its own interval).</summary>
public sealed class ScheduledRulesTask(TenantScopeRunner runner) : IRecurringTask
{
    public const string TaskName = "automation-scheduled-rules";
    public string Name => TaskName;
    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public Task RunAsync(CancellationToken ct) =>
        runner.ForEachActiveTenantAsync("Automation scheduler", (sp, _, token) => sp.GetRequiredService<RuleEngine>().RunScheduledAsync(token), ct);
}

/// <summary>Purges old non-acting evaluations (not matched, simulations, loop skips) so execution history stays lean.</summary>
public sealed class AutomationHousekeepingTask(TenantScopeRunner runner) : IRecurringTask
{
    public const int RetentionDays = 30;
    public string Name => "automation-housekeeping";
    public TimeSpan Interval => TimeSpan.FromHours(6);

    public Task RunAsync(CancellationToken ct) =>
        runner.ForEachActiveTenantAsync("Automation housekeeping", async (sp, _, token) =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var cutoff = db.Clock.UtcNow.AddDays(-RetentionDays);
            await db.RuleExecutions.Where(e => e.StartedAt < cutoff &&
                    (e.Status == RuleExecutionStatus.NotMatched || e.Status == RuleExecutionStatus.Simulated || e.Status == RuleExecutionStatus.SkippedLoop))
                .ExecuteDeleteAsync(token);
            await db.WebhookDeliveries.Where(d => d.CreatedAt < cutoff.AddDays(-60) && d.Status == DeliveryStatus.Succeeded).ExecuteDeleteAsync(token);
        }, ct);
}

public sealed record RetryExecutionJob(Guid ExecutionId);

/// <summary>Automatic retry of executions whose actions failed transiently.</summary>
public sealed class RetryExecutionJobHandler(RuleEngine engine) : JobHandler<RetryExecutionJob>
{
    public const string JobType = "automation.retry-execution";
    public override string Type => JobType;

    protected override Task ExecuteAsync(RetryExecutionJob payload, BackgroundJob job, CancellationToken ct) =>
        engine.RetryAsync(payload.ExecutionId, automatic: true, ct);
}

/// <summary>
/// Durable HTTP call of the invoke_webhook action. Transient failures (timeouts, 5xx, 429, network) throw so the job
/// runner retries with exponential backoff and finally dead-letters; the rule execution's action result is kept current.
/// </summary>
public sealed class HttpActionJobHandler(DeskDbContext db, OutboundHttpSender sender, ISecretProtector secrets, WebhookTransportOptions options)
    : JobHandler<HttpActionJob>
{
    public const string JobType = "automation.http-action";
    public override string Type => JobType;

    protected override async Task ExecuteAsync(HttpActionJob p, BackgroundJob job, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in p.Headers) headers[k] = Reveal(v);
        headers["User-Agent"] = options.UserAgent;
        headers["X-SupportDesk-Delivery"] = job.Id.ToString();
        if (p.ExecutionId is { } eid) headers["X-SupportDesk-Execution"] = eid.ToString();
        if (p.Secret is { Length: > 0 } secret) headers["X-SupportDesk-Signature"] = OutboundHttpSender.Signature(Reveal(secret), p.Body ?? "");

        var result = await sender.SendAsync(new OutboundHttpRequest(p.Method, p.Url, headers, p.Body, TimeSpan.FromSeconds(Math.Clamp(p.TimeoutSeconds, 1, 30)),
            "Automation", $"{p.Source}: {p.Method} webhook"), ct);
        var final = result.Success || !result.Transient || job.Attempts >= job.MaxAttempts;
        if (p.ExecutionId is { } executionId && await db.RuleExecutions.FirstOrDefaultAsync(e => e.Id == executionId, ct) is { } execution)
        {
            var results = ExecutionResults.Parse(execution.ActionResultsJson);
            var outcome = results.Actions.FirstOrDefault(a => a.Index == p.ActionIndex);
            if (outcome is null) results.Actions.Add(outcome = new ActionOutcome { Index = p.ActionIndex, Type = ActionTypes.InvokeWebhook });
            outcome.Attempts = job.Attempts;
            outcome.Status = result.Success ? ActionStatus.Succeeded : final ? ActionStatus.Failed : ActionStatus.Queued;
            outcome.Message = result.Success ? $"{p.Method} {ActionExecutor.RedactUrl(p.Url)} → HTTP {result.StatusCode}" : final ? null : $"Retrying after: {result.Error}";
            outcome.Error = result.Success || !final ? null : result.Error;
            outcome.Transient = !result.Success && result.Transient;
            results.Actions = results.Actions.OrderBy(a => a.Index).ToList();
            execution.ActionResultsJson = results.ToJson();
            execution.Status = RuleEngine.StatusOf(results.Actions, results.Actions.Count);
            var failures = results.Actions.Where(a => a.Status == ActionStatus.Failed).ToList();
            execution.Error = failures.Count == 0 ? null : string.Join(" | ", failures.Select(f => $"#{f.Index + 1} {f.Type}: {f.Error}"));
            execution.CompletedAt = db.Clock.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        if (!result.Success && result.Transient)
            throw new DomainException("transient_failure", $"Webhook call failed (attempt {job.Attempts}/{job.MaxAttempts}): {result.Error}");
    }

    private string Reveal(string value) => value.StartsWith(SecretValues.Prefix, StringComparison.Ordinal) ? secrets.Unprotect(value[SecretValues.Prefix.Length..]) : value;
}
