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
/// Durable job execution. Jobs are claimed with a 5-minute lease; a job whose lease expired (worker crash or restart)
/// is picked up again, so handlers must be idempotent. Failures retry with exponential backoff, then dead-letter.
/// </summary>
public sealed class JobProcessor(IServiceScopeFactory scopes, WorkerOptions options, IClock clock, ILogger<JobProcessor> logger)
{
    public static readonly ActivitySource Activity = new("SupportDesk.Jobs");
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<Guid>> ClaimAsync(int take, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
        var now = clock.UtcNow;
        return await db.Database.SqlQueryRaw<Guid>("""
            WITH due AS (
                SELECT TOP (@take) * FROM dbo.BackgroundJobs WITH (ROWLOCK, UPDLOCK, READPAST)
                WHERE ((Status IN ('Queued','Failed') AND RunAt <= @now) OR (Status = 'Running' AND LockedUntil < @now))
                ORDER BY Priority DESC, RunAt, Id)
            UPDATE due SET Status = 'Running', LockedUntil = @lease, LockedBy = @owner, StartedAt = @now, Attempts = Attempts + 1
            OUTPUT inserted.Id AS Value;
            """,
            new SqlParameter("@take", take), new SqlParameter("@now", now), new SqlParameter("@lease", now.Add(Lease)),
            new SqlParameter("@owner", options.InstanceName)).ToListAsync(ct);
    }

    public async Task ExecuteAsync(Guid jobId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<DeskDbContext>();
        var job = await db.BackgroundJobs.AsNoTracking().FirstAsync(j => j.Id == jobId, ct);
        using var activity = Activity.StartActivity($"job {job.Type}");
        activity?.SetTag("job.id", job.Id);
        var ctx = sp.GetRequiredService<RequestContext>();
        if (job.TenantId is { } tenant) ctx.SetSystem(tenant, "Background job");
        ctx.CorrelationId = job.CorrelationId ?? job.Id.ToString("N");
        var handler = sp.GetServices<IJobHandler>().FirstOrDefault(h => h.Type == job.Type);
        string? error = null;
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
        db.ChangeTracker.Clear();
        var now = clock.UtcNow;
        if (error is null)
        {
            await db.BackgroundJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(u => u
                .SetProperty(j => j.Status, JobStatus.Succeeded).SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.LockedUntil, (DateTime?)null).SetProperty(j => j.Error, (string?)null), ct);
            return;
        }
        var dead = job.Attempts >= job.MaxAttempts || handler is null;
        var delay = TimeSpan.FromSeconds(Math.Min(900, 5 * Math.Pow(4, Math.Max(0, job.Attempts - 1))));
        await db.BackgroundJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(u => u
            .SetProperty(j => j.Status, dead ? JobStatus.DeadLetter : JobStatus.Failed).SetProperty(j => j.RunAt, now.Add(delay))
            .SetProperty(j => j.LockedUntil, (DateTime?)null).SetProperty(j => j.Error, error.Length > 4000 ? error[..4000] : error), ct);
        if (dead) logger.LogError("Job {Job} ({Type}) dead-lettered: {Error}", job.Id, job.Type, error);
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
