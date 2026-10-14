using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Platform;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Workers;

/// <summary>
/// Claims due outbox events with a lease (UPDLOCK/READPAST, safe across instances), runs every interested handler in its
/// own scope and tenant context, and records per-handler success so retries never repeat completed handlers.
/// Failed events back off exponentially and dead-letter after <see cref="MaxAttempts"/>. Events whose lease expired
/// (crashed instance) are reclaimed automatically. Every database is processed: the shared one and each tenant's dedicated
/// database — event bookkeeping stays pinned to the database the event lives in, handlers route by the event's tenant.
/// </summary>
public sealed class OutboxProcessor(IServiceScopeFactory scopes, TenantScopeRunner runner, WorkerOptions options, IClock clock, ILogger<OutboxProcessor> logger)
{
    public const int MaxAttempts = 8;
    public static readonly ActivitySource Activity = new("SupportDesk.Outbox");
    private List<(string Name, int Order)>? _handlerCatalog;

    /// <summary>Processes one batch per database. Returns the number of events handled.</summary>
    public async Task<int> ProcessBatchAsync(int batchSize, CancellationToken ct)
    {
        var total = 0;
        foreach (var partition in runner.Partitions())
        {
            var database = partition.DedicatedTenantId ?? Guid.Empty;
            try
            {
                total += await ProcessBatchAsync(database, batchSize, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && partition.DedicatedTenantId is not null)
            {
                // One unreachable dedicated database must not stop event processing for everyone else.
                logger.LogError(ex, "Outbox processing failed for the dedicated database of tenant {Tenant}", partition.DedicatedTenantId);
            }
        }
        return total;
    }

    private async Task<int> ProcessBatchAsync(Guid database, int batchSize, CancellationToken ct)
    {
        List<Guid> ids;
        using (var scope = PinnedScope(database))
        {
            var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
            var now = clock.UtcNow;
            ids = await db.Database.SqlQueryRaw<Guid>("""
                WITH due AS (
                    SELECT TOP (@take) * FROM dbo.OutboxEvents WITH (ROWLOCK, UPDLOCK, READPAST)
                    WHERE ((Status IN ('Pending','Failed') AND NextAttemptAt <= @now) OR (Status = 'Processing' AND LockedUntil < @now))
                    ORDER BY OccurredAt, Id)
                UPDATE due SET Status = 'Processing', LockedUntil = @lease, LockedBy = @owner
                OUTPUT inserted.Id AS Value;
                """,
                new SqlParameter("@take", batchSize), new SqlParameter("@now", now), new SqlParameter("@lease", now.AddMinutes(2)),
                new SqlParameter("@owner", options.InstanceName)).ToListAsync(ct);
        }
        foreach (var id in ids) await ProcessEventAsync(database, id, ct);
        return ids.Count;
    }

    /// <summary>A scope whose connections stay on one database (where the outbox rows live), whatever tenant it touches.</summary>
    private IServiceScope PinnedScope(Guid database)
    {
        var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<RequestContext>().DatabasePin = database;
        return scope;
    }

    private async Task ProcessEventAsync(Guid database, Guid id, CancellationToken ct)
    {
        OutboxEvent ev;
        HashSet<string> done;
        using (var scope = PinnedScope(database))
        {
            var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
            ev = await db.OutboxEvents.AsNoTracking().FirstAsync(e => e.Id == id, ct);
            done = (await db.OutboxDeliveries.AsNoTracking().Where(d => d.EventId == id && d.Succeeded).Select(d => d.Handler).ToListAsync(ct)).ToHashSet();
        }
        using var activity = Activity.StartActivity($"outbox {ev.Type}");
        activity?.SetTag("outbox.event_id", ev.Id);
        activity?.SetTag("tenant.id", ev.TenantId);
        var catalog = HandlerCatalog();
        var failures = new List<string>();
        foreach (var (name, _) in catalog)
        {
            if (done.Contains(name)) continue;
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var handler = sp.GetServices<IOutboxHandler>().First(h => h.Name == name);
            if (!handler.CanHandle(ev.Type)) continue;
            var ctx = sp.GetRequiredService<RequestContext>();
            if (ev.TenantId is { } tenant) ctx.SetSystem(tenant, "Automation", ActorType.System);
            ctx.ChainDepth = ev.ChainDepth + 1;
            ctx.CausationEventId = ev.Id;
            ctx.CorrelationId = ev.CorrelationId ?? ev.Id.ToString("N");
            string? error = null;
            try
            {
                await handler.HandleAsync(ev, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                failures.Add($"{name}: {error}");
                logger.LogWarning(ex, "Outbox handler {Handler} failed for event {Event} ({Type})", name, ev.Id, ev.Type);
            }
            await RecordDeliveryAsync(database, ev.Id, name, error, ct);
        }
        await CompleteAsync(database, ev, failures, ct);
    }

    private async Task RecordDeliveryAsync(Guid database, Guid eventId, string handler, string? error, CancellationToken ct)
    {
        using var scope = PinnedScope(database);
        var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
        var d = await db.OutboxDeliveries.FirstOrDefaultAsync(x => x.EventId == eventId && x.Handler == handler, ct);
        if (d is null)
        {
            d = new OutboxDelivery { EventId = eventId, Handler = handler };
            db.OutboxDeliveries.Add(d);
        }
        d.Attempts++;
        d.Succeeded = error is null;
        d.Error = error is null ? null : Truncate(error, 2000);
        d.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task CompleteAsync(Guid database, OutboxEvent ev, List<string> failures, CancellationToken ct)
    {
        using var scope = PinnedScope(database);
        var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
        var now = clock.UtcNow;
        if (failures.Count == 0)
        {
            await db.OutboxEvents.Where(e => e.Id == ev.Id).ExecuteUpdateAsync(u => u
                .SetProperty(e => e.Status, OutboxStatus.Processed).SetProperty(e => e.ProcessedAt, now)
                .SetProperty(e => e.LockedUntil, (DateTime?)null).SetProperty(e => e.Error, (string?)null), ct);
            return;
        }
        var attempts = ev.Attempts + 1;
        var status = attempts >= MaxAttempts ? OutboxStatus.DeadLetter : OutboxStatus.Failed;
        var delay = TimeSpan.FromSeconds(Math.Min(900, 5 * Math.Pow(4, attempts - 1)));
        var error = Truncate(string.Join(" | ", failures), 4000);
        await db.OutboxEvents.Where(e => e.Id == ev.Id).ExecuteUpdateAsync(u => u
            .SetProperty(e => e.Status, status).SetProperty(e => e.Attempts, attempts).SetProperty(e => e.NextAttemptAt, now.Add(delay))
            .SetProperty(e => e.LockedUntil, (DateTime?)null).SetProperty(e => e.Error, error), ct);
        if (status == OutboxStatus.DeadLetter) logger.LogError("Outbox event {Event} ({Type}) dead-lettered: {Error}", ev.Id, ev.Type, error);
    }

    private List<(string Name, int Order)> HandlerCatalog()
    {
        if (_handlerCatalog is not null) return _handlerCatalog;
        using var scope = scopes.CreateScope();
        _handlerCatalog = scope.ServiceProvider.GetServices<IOutboxHandler>().Select(h => (h.Name, h.Order)).OrderBy(h => h.Order).ThenBy(h => h.Name).ToList();
        return _handlerCatalog;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

public sealed class OutboxDispatcher(OutboxProcessor processor, OutboxSignal signal, ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var handled = await processor.ProcessBatchAsync(50, stoppingToken);
                if (handled > 0) continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outbox dispatch loop error");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            try { await signal.WaitAsync(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
