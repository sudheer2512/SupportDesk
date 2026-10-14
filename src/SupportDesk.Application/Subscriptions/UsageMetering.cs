using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Subscriptions;
using SupportDesk.Domain;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Subscriptions;

/// <summary>
/// Per-instance buffer of integration API calls (API keys / OAuth clients). Counting in memory keeps the hot path free of
/// database writes; <see cref="UsageMeteringTask"/> flushes the buffer into the monthly UsageRecords counter.
/// </summary>
public sealed class ApiCallCounter
{
    private readonly ConcurrentDictionary<Guid, long> _pending = new();

    public void Increment(Guid tenantId) => _pending.AddOrUpdate(tenantId, 1, (_, n) => n + 1);

    public long Pending(Guid tenantId) => _pending.TryGetValue(tenantId, out var n) ? n : 0;

    /// <summary>Takes the buffered count for a tenant (atomically resets it).</summary>
    public long Take(Guid tenantId)
    {
        while (_pending.TryGetValue(tenantId, out var n))
        {
            if (_pending.TryUpdate(tenantId, 0, n)) return n;
        }
        return 0;
    }

    public IReadOnlyList<Guid> Tenants => _pending.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
}

/// <summary>
/// MVC filter that meters integration API calls and enforces the plan's monthly API-call limit (402 limit_exceeded).
/// Interactive agent and portal traffic is not metered. Limits are cached per tenant for a minute.
/// </summary>
public sealed class ApiUsageFilter(ApiCallCounter counter, RequestContext ctx, IServiceScopeFactory scopes) : IAsyncActionFilter
{
    private static readonly ConcurrentDictionary<Guid, (long? Limit, long Used, DateTime At)> Cache = new();

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (ctx.ActorType == ActorType.ApiClient && ctx.HasTenant)
        {
            var (limit, used) = await LookupAsync(ctx.TenantId);
            if (limit is { } max && used + counter.Pending(ctx.TenantId) >= max)
            {
                context.Result = new ObjectResult(new
                {
                    type = "about:blank", title = $"The monthly API call allowance of your plan ({max:N0}) is used up.", status = 402,
                    code = "limit_exceeded", limit = UsageMetrics.ApiCalls
                }) { StatusCode = 402 };
                return;
            }
            counter.Increment(ctx.TenantId);
        }
        await next();
    }

    private async Task<(long? Limit, long Used)> LookupAsync(Guid tenantId)
    {
        if (Cache.TryGetValue(tenantId, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(1)) return (c.Limit, c.Used);
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<RequestContext>().SetSystem(tenantId, "API metering");
        var limits = await scope.ServiceProvider.GetRequiredService<LimitGuard>().GetLimitsAsync();
        var used = await scope.ServiceProvider.GetRequiredService<UsageMeter>().GetAsync(UsageMetrics.ApiCalls);
        Cache[tenantId] = (limits.ApiCallsMonthly, used, DateTime.UtcNow);
        return (limits.ApiCallsMonthly, used);
    }

    public static void Forget(Guid tenantId) => Cache.TryRemove(tenantId, out _);
}

/// <summary>Recomputes usage gauges from the tenant's data and returns every meter's current value.</summary>
public sealed class UsageCollector(DeskDbContext db, UsageMeter meter, ApiCallCounter apiCalls)
{
    public async Task<Dictionary<string, long>> RecomputeAsync(CancellationToken ct)
    {
        var tenantId = db.Request.TenantId;
        var flushed = apiCalls.Take(tenantId);
        if (flushed > 0) await meter.IncrementAsync(UsageMetrics.ApiCalls, flushed, ct);

        var agents = await db.Users.CountAsync(u => u.IsActive && (u.Kind == UserKind.Agent || u.Kind == UserKind.Administrator), ct);
        var lightAgents = await db.Users.CountAsync(u => u.IsActive && u.Kind == UserKind.LightAgent, ct);
        var portalUsers = await db.Contacts.CountAsync(c => c.Status == ContactStatus.Active && c.PortalPasswordHash != null, ct);
        var storage = await db.StoredFiles.SumAsync(f => (long?)f.SizeBytes, ct) ?? 0;
        var channels = await db.ChannelAccounts.CountAsync(ct);
        var attachments = await db.TicketAttachments.CountAsync(ct);

        await meter.SetGaugeAsync(UsageMetrics.Agents, agents, ct);
        await meter.SetGaugeAsync(UsageMetrics.LightAgents, lightAgents, ct);
        await meter.SetGaugeAsync(UsageMetrics.PortalUsers, portalUsers, ct);
        await meter.SetGaugeAsync(UsageMetrics.StorageBytes, storage, ct);
        await meter.SetGaugeAsync(UsageMetrics.Channels, channels, ct);
        await meter.SetGaugeAsync(UsageMetrics.Attachments, attachments, ct);

        var snapshot = await meter.SnapshotAsync(ct);
        var result = new Dictionary<string, long>();
        foreach (var (metric, _, _, _) in PlanCatalog.Meters) result[metric] = snapshot.TryGetValue(metric, out var v) ? v : 0;
        return result;
    }

    /// <summary>Current meter values without recomputing (reads the counters).</summary>
    public async Task<Dictionary<string, long>> CurrentAsync(CancellationToken ct)
    {
        var snapshot = await meter.SnapshotAsync(ct);
        var result = new Dictionary<string, long>();
        foreach (var (metric, _, _, _) in PlanCatalog.Meters)
            result[metric] = (snapshot.TryGetValue(metric, out var v) ? v : 0) + (metric == UsageMetrics.ApiCalls ? apiCalls.Pending(db.Request.TenantId) : 0);
        return result;
    }
}

/// <summary>
/// Usage metering (every 15 minutes): per tenant, flushes buffered API calls, recomputes gauges (agents, light agents,
/// portal users, storage, channels, attachments) in the tenant's own database, and writes the day's usage history into
/// the shared catalog (<see cref="UsageSnapshot"/>) for history charts and the platform console.
/// </summary>
public sealed class UsageMeteringTask(DeskDbContext db, TenantScopeRunner runner, UsageSnapshotWriter writer, ILogger<UsageMeteringTask> logger) : IRecurringTask
{
    public const string TaskName = "usage-metering";
    public string Name => TaskName;
    public TimeSpan Interval => TimeSpan.FromMinutes(15);

    public async Task RunAsync(CancellationToken ct)
    {
        var tenants = await db.Tenants.AsNoTracking().Where(t => t.Status != TenantStatus.PendingDeletion).Select(t => t.Id).ToListAsync(ct);
        foreach (var tenantId in tenants)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Dictionary<string, long> values = [];
                await runner.RunForTenantAsync(tenantId, "Usage metering",
                    async sp => values = await sp.GetRequiredService<UsageCollector>().RecomputeAsync(ct), ct);
                await writer.WriteAsync(tenantId, values, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Usage metering failed for tenant {Tenant}", tenantId);
            }
        }
    }
}

/// <summary>Upserts today's usage history rows for a tenant in the shared catalog.</summary>
public sealed class UsageSnapshotWriter(CatalogStore catalog, IClock clock)
{
    public async Task WriteAsync(Guid tenantId, IReadOnlyDictionary<string, long> values, CancellationToken ct)
    {
        var day = clock.UtcNow.Date;
        var cdb = catalog.Db;
        var existing = await cdb.AllTenants<UsageSnapshot>().Where(s => s.TenantId == tenantId && s.Day == day).ToListAsync(ct);
        foreach (var (metric, quantity) in values)
        {
            var row = existing.FirstOrDefault(s => s.Metric == metric);
            if (row is null) cdb.Add(new UsageSnapshot { TenantId = tenantId, Day = day, Metric = metric, Quantity = quantity, UpdatedAt = clock.UtcNow });
            else if (row.Quantity != quantity)
            {
                row.Quantity = quantity;
                row.UpdatedAt = clock.UtcNow;
            }
        }
        await cdb.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<UsageHistoryDto>> HistoryAsync(Guid tenantId, int days, CancellationToken ct)
    {
        var from = clock.UtcNow.Date.AddDays(-Math.Clamp(days, 1, 400) + 1);
        var rows = await catalog.Db.AllTenants<UsageSnapshot>().AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Day >= from)
            .OrderBy(s => s.Day)
            .Select(s => new { s.Metric, s.Day, s.Quantity })
            .ToListAsync(ct);
        return PlanCatalog.Meters
            .Select(m => new UsageHistoryDto(m.Metric, m.Label, m.Unit,
                rows.Where(r => r.Metric == m.Metric).Select(r => new UsagePointDto(r.Day, r.Quantity)).ToList()))
            .ToList();
    }
}
