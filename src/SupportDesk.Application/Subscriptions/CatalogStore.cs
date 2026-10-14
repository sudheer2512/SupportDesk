using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;
using SupportDesk.Persistence.Infrastructure;

namespace SupportDesk.Application.Subscriptions;

/// <summary>
/// The SaaS catalog (tenants, plans, subscriptions, invoices, usage history, sign-ups, support access) always lives in the
/// shared database — even for tenants on a dedicated database, whose tenant-scoped DbContext is routed elsewhere. This
/// store hands out a DbContext bound to the shared database in a platform scope: every query must use
/// <c>AllTenants&lt;T&gt;()</c> with an explicit tenant predicate, and inserts must set <c>TenantId</c> explicitly.
/// </summary>
public sealed class CatalogStore(SharedConnection shared, IClock clock, RequestContext requester) : IAsyncDisposable, IDisposable
{
    private DeskDbContext? _db;

    public DeskDbContext Db => _db ??= Create();

    private DeskDbContext Create()
    {
        var context = new RequestContext { CorrelationId = requester.CorrelationId, IpAddress = requester.IpAddress, UserAgent = requester.UserAgent };
        // Authors are stamped from the private context; the operator flag here only feeds IHasAuthors, never authorisation.
        if (requester.UserId is { } user) context.SetPlatformOperator(user, requester.ActorName ?? "User");
        context.EnterPlatformScope();
        var options = new DbContextOptionsBuilder<DeskDbContext>()
            .UseSqlServer(shared.ConnectionString, sql => sql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), null))
            .Options;
        return new DeskDbContext(options, context, clock);
    }

    public void Dispose() => _db?.Dispose();

    public async ValueTask DisposeAsync()
    {
        if (_db is not null) await _db.DisposeAsync();
    }
}

/// <summary>
/// Keeps the catalog rows the kernel reads in tenant context (Tenant status, Subscription, Plans — used by
/// <c>RequestContextMiddleware</c> and <c>LimitGuard</c>) in sync inside a dedicated tenant database. No-op for shared tenants.
/// </summary>
public sealed class DedicatedCatalogMirror(SharedConnection shared, IConfiguration configuration, IClock clock, ILogger<DedicatedCatalogMirror> logger)
{
    public async Task SyncAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var source = Open(shared.ConnectionString);
        var tenant = await source.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is not { DatabaseMode: DatabaseMode.Dedicated, DedicatedConnectionName: { } name }) return;
        var cs = DedicatedConnections.Resolve(configuration, name);
        if (cs is null)
        {
            logger.LogError("Dedicated connection {Name} of tenant {Tenant} is not configured; catalog mirror skipped", name, tenantId);
            return;
        }
        var subscription = await source.AllTenants<Subscription>().AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        var plans = await source.Plans.AsNoTracking().ToListAsync(ct);

        await using var target = Open(cs);
        await UpsertAsync(target, tenant, ct);
        foreach (var plan in plans) await UpsertAsync(target, plan, ct);
        if (subscription is not null) await UpsertAsync(target, subscription, ct);
        await target.SaveChangesAsync(ct);
    }

    private DeskDbContext Open(string connectionString)
    {
        var context = new RequestContext();
        context.EnterPlatformScope();
        var options = new DbContextOptionsBuilder<DeskDbContext>().UseSqlServer(connectionString).Options;
        return new DeskDbContext(options, context, clock);
    }

    private static async Task UpsertAsync<T>(DeskDbContext target, T source, CancellationToken ct) where T : Entity
    {
        var existing = await target.Set<T>().IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == source.Id, ct);
        if (existing is null)
        {
            target.Set<T>().Add(source);
            return;
        }
        var entry = target.Entry(existing);
        foreach (var property in entry.Properties)
        {
            if (property.Metadata.IsPrimaryKey() || property.Metadata.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAddOrUpdate) continue;
            var value = property.Metadata.PropertyInfo?.GetValue(source);
            if (!Equals(property.CurrentValue, value)) property.CurrentValue = value;
        }
    }
}
