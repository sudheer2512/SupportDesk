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
/// (crashed instance) are reclaimed automatically.
/// </summary>
public sealed class OutboxProcessor(IServiceScopeFactory scopes, WorkerOptions options, IClock clock, ILogger<OutboxProcessor> logger)
{
    public const int MaxAttempts = 8;
    public static readonly ActivitySource Activity = new("SupportDesk.Outbox");

    /// <summary>A registered handler: its name/order, and a cached constructor so only that handler is built per run.</summary>
    private sealed record HandlerInfo(string Name, int Order, Type Type, ObjectFactory Create);

    private List<HandlerInfo>? _handlerCatalog;
    /// <summary>Interested handlers per event type (CanHandle depends on the type only).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HandlerInfo[]> _handlersByType = new(StringComparer.Ordinal);

    /// <summary>Processes one batch. Returns the number of events handled.</summary>
    public async Task<int> ProcessBatchAsync(int batchSize, CancellationToken ct)
    {
        List<ClaimedEvent> claimed;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
            var now = clock.UtcNow;
            claimed = await db.Database.SqlQueryRaw<ClaimedEvent>("""
                WITH due AS (
                    SELECT TOP (@take) * FROM dbo.OutboxEvents WITH (ROWLOCK, UPDLOCK, READPAST)
                    WHERE ((Status IN ('Pending','Failed') AND NextAttemptAt <= @now) OR (Status = 'Processing' AND LockedUntil < @now))
                    ORDER BY OccurredAt, Id)
                UPDATE due SET Status = 'Processing', LockedUntil = @lease, LockedBy = @owner
                OUTPUT inserted.Id, inserted.OccurredAt;
                """,
                new SqlParameter("@take", batchSize), new SqlParameter("@now", now), new SqlParameter("@lease", now.AddMinutes(2)),
                new SqlParameter("@owner", options.InstanceName)).ToListAsync(ct);
        }
        // OUTPUT does not guarantee row order: process in event order.
        foreach (var e in claimed.OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)) await ProcessEventAsync(e.Id, ct);
        return claimed.Count;
    }

    private sealed class ClaimedEvent
    {
        public Guid Id { get; set; }
        public DateTime OccurredAt { get; set; }
    }

    private async Task ProcessEventAsync(Guid id, CancellationToken ct)
    {
        OutboxEvent ev;
        HashSet<string> done;
        HashSet<string> attempted;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
            ev = await db.OutboxEvents.AsNoTracking().FirstAsync(e => e.Id == id, ct);
            var deliveries = await db.OutboxDeliveries.AsNoTracking().Where(d => d.EventId == id).Select(d => new { d.Handler, d.Succeeded }).ToListAsync(ct);
            done = deliveries.Where(d => d.Succeeded).Select(d => d.Handler).ToHashSet();
            attempted = deliveries.Select(d => d.Handler).ToHashSet();
        }
        using var activity = Activity.StartActivity($"outbox {ev.Type}");
        activity?.SetTag("outbox.event_id", ev.Id);
        activity?.SetTag("tenant.id", ev.TenantId);
        var failures = new List<string>();
        foreach (var info in HandlersFor(ev.Type))
        {
            var name = info.Name;
            if (done.Contains(name)) continue;
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            // Build just this handler (resolving IEnumerable<IOutboxHandler> here constructed every handler, per handler).
            var handler = (IOutboxHandler)info.Create(sp, null);
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
            finally
            {
                // Built by the cached factory, so the scope does not track the handler itself.
                if (handler is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
                else if (handler is IDisposable disposable) disposable.Dispose();
            }
            await RecordDeliveryAsync(ev.Id, name, error, attempted.Contains(name), ct);
        }
        await CompleteAsync(ev, failures, ct);
    }

    private async Task RecordDeliveryAsync(Guid eventId, string handler, string? error, bool existing, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
        // First attempt (the common case): no row yet, insert without looking it up.
        var d = existing ? await db.OutboxDeliveries.FirstOrDefaultAsync(x => x.EventId == eventId && x.Handler == handler, ct) : null;
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

    private async Task CompleteAsync(OutboxEvent ev, List<string> failures, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
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

    private List<HandlerInfo> HandlerCatalog()
    {
        if (_handlerCatalog is not null) return _handlerCatalog;
        using var scope = scopes.CreateScope();
        _handlerCatalog = scope.ServiceProvider.GetServices<IOutboxHandler>()
            .Select(h => new HandlerInfo(h.Name, h.Order, h.GetType(), ActivatorUtilities.CreateFactory(h.GetType(), Type.EmptyTypes)))
            .OrderBy(h => h.Order).ThenBy(h => h.Name).ToList();
        return _handlerCatalog;
    }

    /// <summary>The handlers interested in an event type, in order (computed once per type).</summary>
    private HandlerInfo[] HandlersFor(string eventType) => _handlersByType.GetOrAdd(eventType, type =>
    {
        var catalog = HandlerCatalog();
        using var scope = scopes.CreateScope();
        var instances = scope.ServiceProvider.GetServices<IOutboxHandler>().GroupBy(h => h.Name).ToDictionary(g => g.Key, g => g.First());
        return catalog.Where(h => instances[h.Name].CanHandle(type)).ToArray();
    });

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
