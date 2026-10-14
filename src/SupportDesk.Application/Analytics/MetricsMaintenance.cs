using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Analytics;
using SupportDesk.Domain;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Analytics;

/// <summary>
/// Keeps <see cref="Domain.Analytics.TicketMetric"/> current: every committed ticket, SLA and satisfaction event
/// recomputes the ticket's row (idempotent upsert; runs first among outbox handlers).
/// </summary>
public sealed class TicketMetricsOutboxHandler(TicketMetricBuilder builder) : IOutboxHandler
{
    public string Name => "analytics.ticket-metrics";
    public int Order => HandlerOrder.Metrics;

    public bool CanHandle(string eventType) =>
        (eventType.StartsWith("ticket.", StringComparison.Ordinal) && eventType != EventTypes.TicketViewed) ||
        eventType is EventTypes.SlaBreached or EventTypes.SlaMet or EventTypes.SlaEscalated or EventTypes.SatisfactionReceived;

    public async Task HandleAsync(OutboxEvent ev, CancellationToken ct)
    {
        if (ev.AggregateType != "Ticket" || ev.AggregateId is not { } ticketId) return;
        await builder.RebuildAsync([ticketId], ct);
    }
}

public sealed record MetricsBackfillPayload(int Days);

/// <summary>
/// Metrics maintenance for one tenant: catch-up of missing/stale ticket rows, the daily agent and backlog roll-ups, the
/// on-demand backfill and the role-default dashboards.
/// </summary>
public sealed class MetricsMaintenanceService(
    DeskDbContext db,
    RequestContext ctx,
    TicketMetricBuilder builder,
    AnalyticsRollupService rollups,
    DashboardService dashboards,
    IJobQueue jobs,
    IAuditWriter audit)
{
    public const string BackfillJob = "analytics.metrics-backfill";
    public const int CatchUpBatch = 2000;

    /// <summary>One maintenance tick: catch up stale rows, roll up yesterday and today, ensure default dashboards.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var stale = await builder.StaleTicketIdsAsync(CatchUpBatch, ct);
        if (stale.Count > 0) await builder.RebuildAsync(stale, ct);
        var (yesterday, today) = await rollups.RecentDaysAsync(ct);
        // First run for a tenant with history: backfill the roll-ups instead of only the last two days.
        if (!await db.AgentMetrics.AnyAsync(ct) && await db.TicketMetrics.AnyAsync(m => m.CreatedDay < yesterday, ct))
            await EnqueueBackfillAsync(120, ct);
        await rollups.RebuildDaysAsync(yesterday, today, ct);
        await dashboards.EnsureDefaultsAsync(ct);
    }

    /// <summary>Recomputes every ticket row and the roll-ups of the last <paramref name="days"/> days. Safe to re-run.</summary>
    public async Task BackfillAsync(int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 400);
        var all = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().OrderBy(t => t.Id).Select(t => t.Id).ToListAsync(ct);
        foreach (var chunk in all.Chunk(TicketMetricBuilder.BatchSize * 5))
        {
            await builder.RebuildAsync(chunk, ct);
            db.ChangeTracker.Clear();
        }
        var (_, today) = await rollups.RecentDaysAsync(ct);
        await rollups.RebuildDaysAsync(today.AddDays(-days + 1), today, ct);
        await dashboards.EnsureDefaultsAsync(ct);
    }

    public async Task<bool> EnqueueBackfillAsync(int days, CancellationToken ct)
    {
        if (await BackfillPendingAsync(ct)) return false;
        jobs.Enqueue(BackfillJob, new MetricsBackfillPayload(Math.Clamp(days, 1, 400)), maxAttempts: 3);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private Task<bool> BackfillPendingAsync(CancellationToken ct) =>
        db.BackgroundJobs.AsNoTracking().AnyAsync(j => j.TenantId == ctx.TenantId && j.Type == BackfillJob &&
                                                       (j.Status == JobStatus.Queued || j.Status == JobStatus.Running || j.Status == JobStatus.Failed), ct);

    /// <summary>POST /analytics/metrics/rebuild — queue a full recomputation (analytics.team).</summary>
    public async Task<MetricsStatusDto> RequestRebuildAsync(RebuildMetricsRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsTeam);
        if (await EnqueueBackfillAsync(r.Days ?? 120, ct))
        {
            audit.Write("analytics.metrics_rebuild", "Analytics", null, $"Queued a rebuild of ticket metrics and {r.Days ?? 120} days of roll-ups");
            await db.SaveChangesAsync(ct);
        }
        return await StatusAsync(ct);
    }

    public async Task<MetricsStatusDto> StatusAsync(CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsTeam);
        var tickets = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).CountAsync(ct);
        var metrics = await db.TicketMetrics.CountAsync(ct);
        var missing = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).CountAsync(t => !db.TicketMetrics.Any(m => m.TicketId == t.Id), ct);
        var stale = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter])
            .CountAsync(t => db.TicketMetrics.Any(m => m.TicketId == t.Id && m.SourceVersion != t.RowVersion), ct);
        var last = await db.TicketMetrics.MaxAsync(m => (DateTime?)m.UpdatedAt, ct);
        var agentDay = await db.AgentMetrics.MaxAsync(m => (DateOnly?)m.Day, ct);
        var backlogDay = await db.BacklogSnapshots.MaxAsync(m => (DateOnly?)m.Day, ct);
        return new MetricsStatusDto(tickets, metrics, missing, stale, last, agentDay, backlogDay, await BackfillPendingAsync(ct));
    }
}

/// <summary>Analytics maintenance every 15 minutes for every active tenant (see <see cref="MetricsMaintenanceService.TickAsync"/>).</summary>
public sealed class AnalyticsRollupTask(TenantScopeRunner runner, ILogger<AnalyticsRollupTask> logger) : IRecurringTask
{
    public const string TaskName = "analytics.rollup";
    public string Name => TaskName;
    public TimeSpan Interval => TimeSpan.FromMinutes(15);

    public Task RunAsync(CancellationToken ct) => runner.ForEachActiveTenantAsync("Analytics roll-up", async (sp, tenant, token) =>
    {
        try
        {
            await sp.GetRequiredService<MetricsMaintenanceService>().TickAsync(token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Analytics roll-up failed for tenant {Tenant}", tenant.Slug);
        }
    }, ct);
}

/// <summary>Durable backfill job (on demand or when roll-ups are missing). Idempotent: it only rewrites derived rows.</summary>
public sealed class MetricsBackfillJobHandler(MetricsMaintenanceService maintenance) : JobHandler<MetricsBackfillPayload>
{
    public override string Type => MetricsMaintenanceService.BackfillJob;

    protected override Task ExecuteAsync(MetricsBackfillPayload payload, BackgroundJob job, CancellationToken ct) =>
        maintenance.BackfillAsync(payload.Days <= 0 ? 120 : payload.Days, ct);
}
