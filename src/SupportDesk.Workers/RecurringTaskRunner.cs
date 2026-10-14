using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Platform;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;

namespace SupportDesk.Workers;

/// <summary>
/// Runs every registered <see cref="IRecurringTask"/> on its interval. A database lease per task (ScheduledTasks)
/// guarantees a single runner across all API/worker instances; a crashed runner's lease simply expires.
/// </summary>
public sealed class RecurringTaskProcessor(IServiceScopeFactory scopes, WorkerOptions options, IClock clock, ILogger<RecurringTaskProcessor> logger)
{
    private List<(string Name, TimeSpan Interval)>? _catalog;

    public List<(string Name, TimeSpan Interval)> Catalog()
    {
        if (_catalog is not null) return _catalog;
        using var scope = scopes.CreateScope();
        _catalog = scope.ServiceProvider.GetServices<IRecurringTask>().Select(t => (t.Name, t.Interval)).ToList();
        return _catalog;
    }

    public async Task RunDueAsync(CancellationToken ct)
    {
        foreach (var (name, interval) in Catalog())
        {
            if (ct.IsCancellationRequested) return;
            if (!await TryClaimAsync(name, interval, ct)) continue;
            var started = clock.UtcNow;
            string? error = null;
            try
            {
                using var scope = scopes.CreateScope();
                var task = scope.ServiceProvider.GetServices<IRecurringTask>().First(t => t.Name == name);
                await task.RunAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                logger.LogError(ex, "Recurring task {Task} failed", name);
            }
            await ReleaseAsync(name, interval, started, error, ct);
        }
    }

    /// <summary>Runs one task immediately (tests, admin "run now").</summary>
    public async Task RunNowAsync(string name, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetServices<IRecurringTask>().First(t => t.Name == name).RunAsync(ct);
    }

    private async Task<bool> TryClaimAsync(string name, TimeSpan interval, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
        var now = clock.UtcNow;
        if (!await db.ScheduledTasks.AnyAsync(t => t.Name == name, ct))
        {
            db.ScheduledTasks.Add(new ScheduledTaskState { Name = name, NextRunAt = now });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { /* another instance created it */ }
        }
        var lease = now.Add(interval < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(2) : interval + TimeSpan.FromMinutes(5));
        var claimed = await db.Database.ExecuteSqlRawAsync("""
            UPDATE dbo.ScheduledTasks SET LockedBy = @owner, LockedUntil = @lease
            WHERE Name = @name AND NextRunAt <= @now AND (LockedUntil IS NULL OR LockedUntil < @now)
            """, [new SqlParameter("@owner", options.InstanceName), new SqlParameter("@lease", lease), new SqlParameter("@name", name), new SqlParameter("@now", now)], ct);
        return claimed == 1;
    }

    private async Task ReleaseAsync(string name, TimeSpan interval, DateTime started, string? error, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
        var now = clock.UtcNow;
        await db.ScheduledTasks.Where(t => t.Name == name).ExecuteUpdateAsync(u => u
            .SetProperty(t => t.LastRunAt, started).SetProperty(t => t.NextRunAt, now.Add(interval)).SetProperty(t => t.LockedUntil, (DateTime?)null)
            .SetProperty(t => t.LastError, error).SetProperty(t => t.RunCount, t => t.RunCount + 1), ct);
    }
}

public sealed class RecurringTaskRunner(RecurringTaskProcessor processor, ILogger<RecurringTaskRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await processor.RunDueAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Recurring task loop error"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
