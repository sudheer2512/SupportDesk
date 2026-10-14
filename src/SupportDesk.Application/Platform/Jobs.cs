using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Platform;

/// <summary>Durable job queue backed by the BackgroundJobs table (lease-based, restart-safe, idempotency keys).</summary>
public interface IJobQueue
{
    /// <summary>Adds a job to the current unit of work (committed with the caller's SaveChanges).</summary>
    BackgroundJob Enqueue(string type, object payload, DateTime? runAt = null, string? idempotencyKey = null, int priority = 0, int maxAttempts = 6);

    /// <summary>Enqueues and saves immediately; returns false if a job with the same idempotency key already exists.</summary>
    Task<bool> EnqueueIfAbsentAsync(string type, object payload, string idempotencyKey, DateTime? runAt = null, CancellationToken ct = default);
}

public sealed class JobQueue(DeskDbContext db, RequestContext ctx) : IJobQueue
{
    public BackgroundJob Enqueue(string type, object payload, DateTime? runAt = null, string? idempotencyKey = null, int priority = 0, int maxAttempts = 6)
    {
        var job = new BackgroundJob
        {
            TenantId = ctx.HasTenant ? ctx.TenantId : null,
            Type = type,
            PayloadJson = Json.Serialize(payload),
            RunAt = runAt ?? db.Clock.UtcNow,
            IdempotencyKey = idempotencyKey,
            Priority = priority,
            MaxAttempts = maxAttempts,
            CorrelationId = ctx.CorrelationId,
            CreatedAt = db.Clock.UtcNow
        };
        db.BackgroundJobs.Add(job);
        return job;
    }

    public async Task<bool> EnqueueIfAbsentAsync(string type, object payload, string idempotencyKey, DateTime? runAt = null, CancellationToken ct = default)
    {
        if (await db.BackgroundJobs.AnyAsync(j => j.IdempotencyKey == idempotencyKey, ct)) return false;
        var job = Enqueue(type, payload, runAt, idempotencyKey);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.Entry(job).State = EntityState.Detached;
            return false;
        }
    }
}

/// <summary>Executes one job type. Must be idempotent: a job may run again after a crash before completion.</summary>
public interface IJobHandler
{
    string Type { get; }
    Task ExecuteAsync(BackgroundJob job, CancellationToken ct);
}

public abstract class JobHandler<TPayload> : IJobHandler
{
    public abstract string Type { get; }
    public Task ExecuteAsync(BackgroundJob job, CancellationToken ct) =>
        ExecuteAsync(Json.Deserialize<TPayload>(job.PayloadJson) ?? throw new InvalidOperationException($"Empty payload for job {job.Id}"), job, ct);
    protected abstract Task ExecuteAsync(TPayload payload, BackgroundJob job, CancellationToken ct);
}

/// <summary>
/// Periodic task run by the RecurringTaskRunner under a database lease (ScheduledTaskState) so that only one host
/// runs a given task at a time, even with several API/worker instances. Tasks run without a tenant; use
/// <see cref="TenantScopeRunner"/> to fan out per tenant.
/// </summary>
public interface IRecurringTask
{
    string Name { get; }
    TimeSpan Interval { get; }
    Task RunAsync(CancellationToken ct);
}

/// <summary>Runs work in a fresh DI scope per tenant with a system request context.</summary>
public sealed class TenantScopeRunner(IServiceScopeFactory scopes)
{
    public async Task ForEachActiveTenantAsync(string actorName, Func<IServiceProvider, Tenant, CancellationToken, Task> work, CancellationToken ct)
    {
        List<Tenant> tenants;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
            tenants = await db.Tenants.AsNoTracking()
                .Where(t => t.Status == TenantStatus.Active || t.Status == TenantStatus.Trial)
                .ToListAsync(ct);
        }
        foreach (var tenant in tenants)
        {
            ct.ThrowIfCancellationRequested();
            await RunForTenantAsync(tenant.Id, actorName, sp => work(sp, tenant, ct), ct);
        }
    }

    public async Task RunForTenantAsync(Guid tenantId, string actorName, Func<IServiceProvider, Task> work, CancellationToken ct = default,
        ActorType actor = ActorType.System)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<RequestContext>().SetSystem(tenantId, actorName, actor);
        await work(scope.ServiceProvider);
    }
}
