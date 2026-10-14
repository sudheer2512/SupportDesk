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

/// <summary>A claimed job and the database its row lives in (<see cref="Guid.Empty"/> = the shared database).</summary>
public readonly record struct ClaimedJob(Guid Database, Guid Id);

/// <summary>
/// Durable job execution. Jobs are claimed with a 5-minute lease; a job whose lease expired (worker crash or restart)
/// is picked up again, so handlers must be idempotent. Failures retry with exponential backoff, then dead-letter.
/// Jobs are claimed from every database (shared and dedicated); bookkeeping stays pinned to the job's database while the
/// handler runs in a scope routed by the job's tenant.
/// </summary>
public sealed class JobProcessor(IServiceScopeFactory scopes, TenantScopeRunner runner, WorkerOptions options, IClock clock, ILogger<JobProcessor> logger)
{
    public static readonly ActivitySource Activity = new("SupportDesk.Jobs");
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<ClaimedJob>> ClaimAsync(int take, CancellationToken ct)
    {
        var claimed = new List<ClaimedJob>();
        foreach (var partition in runner.Partitions())
        {
            if (claimed.Count >= take) break;
            var database = partition.DedicatedTenantId ?? Guid.Empty;
            try
            {
                using var scope = PinnedScope(database);
                var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
                var now = clock.UtcNow;
                var ids = await db.Database.SqlQueryRaw<Guid>("""
                    WITH due AS (
                        SELECT TOP (@take) * FROM dbo.BackgroundJobs WITH (ROWLOCK, UPDLOCK, READPAST)
                        WHERE ((Status IN ('Queued','Failed') AND RunAt <= @now) OR (Status = 'Running' AND LockedUntil < @now))
                        ORDER BY Priority DESC, RunAt, Id)
                    UPDATE due SET Status = 'Running', LockedUntil = @lease, LockedBy = @owner, StartedAt = @now, Attempts = Attempts + 1
                    OUTPUT inserted.Id AS Value;
                    """,
                    new SqlParameter("@take", take - claimed.Count), new SqlParameter("@now", now), new SqlParameter("@lease", now.Add(Lease)),
                    new SqlParameter("@owner", options.InstanceName)).ToListAsync(ct);
                claimed.AddRange(ids.Select(id => new ClaimedJob(database, id)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && partition.DedicatedTenantId is not null)
            {
                // One unreachable dedicated database must not stop job processing for everyone else.
                logger.LogError(ex, "Job claim failed for the dedicated database of tenant {Tenant}", partition.DedicatedTenantId);
            }
        }
        return claimed;
    }

    public async Task ExecuteAsync(ClaimedJob claimed, CancellationToken ct)
    {
        BackgroundJob job;
        using (var scope = PinnedScope(claimed.Database))
            job = await scope.ServiceProvider.GetRequiredService<DeskDbContext>().BackgroundJobs.AsNoTracking().FirstAsync(j => j.Id == claimed.Id, ct);
        using var activity = Activity.StartActivity($"job {job.Type}");
        activity?.SetTag("job.id", job.Id);
        string? error = null;
        bool hasHandler;
        using (var scope = scopes.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var ctx = sp.GetRequiredService<RequestContext>();
            if (job.TenantId is { } tenant) ctx.SetSystem(tenant, "Background job");
            ctx.CorrelationId = job.CorrelationId ?? job.Id.ToString("N");
            var handler = sp.GetServices<IJobHandler>().FirstOrDefault(h => h.Type == job.Type);
            hasHandler = handler is not null;
            if (handler is null) error = $"No handler registered for job type '{job.Type}'.";
            else
            {
                try
                {
                    await handler.ExecuteAsync(job, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    error = $"{ex.GetType().Name}: {ex.Message}";
                    logger.LogWarning(ex, "Job {Job} ({Type}) attempt {Attempt} failed", job.Id, job.Type, job.Attempts);
                }
            }
        }
        using var bookkeeping = PinnedScope(claimed.Database);
        var db = bookkeeping.ServiceProvider.GetRequiredService<DeskDbContext>();
        var now = clock.UtcNow;
        if (error is null)
        {
            await db.BackgroundJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(u => u
                .SetProperty(j => j.Status, JobStatus.Succeeded).SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.LockedUntil, (DateTime?)null).SetProperty(j => j.Error, (string?)null), ct);
            return;
        }
        var dead = job.Attempts >= job.MaxAttempts || !hasHandler;
        var delay = TimeSpan.FromSeconds(Math.Min(900, 5 * Math.Pow(4, Math.Max(0, job.Attempts - 1))));
        await db.BackgroundJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(u => u
            .SetProperty(j => j.Status, dead ? JobStatus.DeadLetter : JobStatus.Failed).SetProperty(j => j.RunAt, now.Add(delay))
            .SetProperty(j => j.LockedUntil, (DateTime?)null).SetProperty(j => j.Error, error.Length > 4000 ? error[..4000] : error), ct);
        if (dead) logger.LogError("Job {Job} ({Type}) dead-lettered: {Error}", job.Id, job.Type, error);
    }

    /// <summary>A scope whose connections stay on one database (where the job row lives), whatever tenant it touches.</summary>
    private IServiceScope PinnedScope(Guid database)
    {
        var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<RequestContext>().DatabasePin = database;
        return scope;
    }

    /// <summary>Claims and runs due jobs until none are left (used by tests and the runner loop).</summary>
    public async Task<int> DrainAsync(CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var ids = await ClaimAsync(20, ct);
            if (ids.Count == 0) return total;
            foreach (var id in ids) await ExecuteAsync(id, ct);
            total += ids.Count;
        }
    }
}

public sealed class JobRunner(JobProcessor processor, WorkerOptions options, ILogger<JobRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        using var throttle = new SemaphoreSlim(options.JobConcurrency);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var free = throttle.CurrentCount;
                var ids = free == 0 ? [] : await processor.ClaimAsync(free, stoppingToken);
                foreach (var id in ids)
                {
                    await throttle.WaitAsync(stoppingToken);
                    _ = Task.Run(async () =>
                    {
                        try { await processor.ExecuteAsync(id, stoppingToken); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Job {Job} crashed", id); }
                        finally { throttle.Release(); }
                    }, stoppingToken);
                }
                if (ids.Count == 0) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job runner loop error");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
