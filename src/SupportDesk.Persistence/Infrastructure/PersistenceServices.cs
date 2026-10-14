using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SupportDesk.Domain;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Persistence.Infrastructure;

public static class PersistenceServices
{
    public const string ConnectionName = "SupportDesk";

    /// <summary>Registers the DbContext (SQL Server, tenant connection routing), request context, clock and counters.</summary>
    public static IServiceCollection AddDeskPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var shared = configuration.GetConnectionString(ConnectionName)
                     ?? throw new InvalidOperationException($"Connection string '{ConnectionName}' is not configured.");

        services.TryAddSingleton<IClock, SystemClock>();
        services.AddScoped<RequestContext>();
        services.AddSingleton(new SharedConnection(shared));
        services.AddSingleton<TenantDatabaseDirectory>();
        services.AddScoped<TenantCounterService>();
        services.AddScoped<DatabaseInitializer>();

        services.AddDbContext<DeskDbContext>((sp, o) =>
        {
            o.UseSqlServer(shared, sql =>
            {
                sql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), null);
                sql.CommandTimeout(60);
                sql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });
            o.AddInterceptors(new TenantConnectionInterceptor(sp.GetRequiredService<RequestContext>(), sp.GetRequiredService<TenantDatabaseDirectory>()));
            o.ConfigureWarnings(w => w.Ignore(RelationalEventId.MultipleCollectionIncludeWarning));
        });
        return services;
    }
}

public sealed record SharedConnection(string ConnectionString);

/// <summary>
/// Maps tenants that run on a dedicated database (enterprise isolation) to their connection strings. The tenant
/// catalog always lives in the shared database. Connection strings come from configuration by name — never from user input.
/// </summary>
public sealed class TenantDatabaseDirectory(SharedConnection shared, IConfiguration configuration, ILogger<TenantDatabaseDirectory> logger)
{
    private readonly ConcurrentDictionary<Guid, string> _dedicated = new();
    private DateTime _loadedAt = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string? DedicatedConnectionFor(Guid tenantId)
    {
        EnsureFresh();
        return _dedicated.TryGetValue(tenantId, out var cs) ? cs : null;
    }

    public void Invalidate() => _loadedAt = DateTime.MinValue;

    public IReadOnlyDictionary<Guid, string> All()
    {
        EnsureFresh();
        return _dedicated;
    }

    private void EnsureFresh()
    {
        if (DateTime.UtcNow - _loadedAt < TimeSpan.FromSeconds(60)) return;
        if (!_lock.Wait(0)) return;
        try
        {
            using var conn = new SqlConnection(shared.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "IF OBJECT_ID(N'dbo.Tenants') IS NOT NULL SELECT Id, DedicatedConnectionName FROM dbo.Tenants WHERE DatabaseMode = 'Dedicated' AND DedicatedConnectionName IS NOT NULL";
            using var reader = cmd.ExecuteReader();
            var fresh = new Dictionary<Guid, string>();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                var cs = configuration.GetConnectionString(name);
                if (cs is null) logger.LogError("Dedicated connection string {Name} is not configured", name);
                else fresh[reader.GetGuid(0)] = cs;
            }
            _dedicated.Clear();
            foreach (var kv in fresh) _dedicated[kv.Key] = kv.Value;
            _loadedAt = DateTime.UtcNow;
        }
        catch (SqlException ex)
        {
            logger.LogWarning(ex, "Could not refresh the tenant database directory");
            _loadedAt = DateTime.UtcNow.AddSeconds(-45);
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>Routes a DbContext's connection to the tenant's dedicated database (if any) just before it opens.</summary>
internal sealed class TenantConnectionInterceptor(RequestContext request, TenantDatabaseDirectory directory) : DbConnectionInterceptor
{
    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        Route(connection);
        return result;
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
        InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Route(connection);
        return ValueTask.FromResult(result);
    }

    private void Route(DbConnection connection)
    {
        if (!request.HasTenant || connection.State != ConnectionState.Closed) return;
        var dedicated = directory.DedicatedConnectionFor(request.TenantId);
        if (dedicated is not null && connection.ConnectionString != dedicated) connection.ConnectionString = dedicated;
    }
}

/// <summary>Atomic per-tenant counters (ticket numbers, invoice numbers) using UPDLOCK/HOLDLOCK — gap-free under concurrency.</summary>
public sealed class TenantCounterService(DeskDbContext db)
{
    public async Task<long> NextAsync(string name, long start = 1, CancellationToken ct = default)
    {
        var tenant = db.Request.TenantId;
        if (tenant == Guid.Empty) throw new InvalidOperationException("Counters require a tenant.");
        const string sql = """
            SET NOCOUNT ON;
            DECLARE @v bigint;
            UPDATE dbo.TenantCounters WITH (UPDLOCK, HOLDLOCK) SET @v = NextValue, NextValue = NextValue + 1 WHERE TenantId = @t AND Name = @n;
            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO dbo.TenantCounters (Id, TenantId, Name, NextValue) VALUES (@id, @t, @n, @start + 1);
                SET @v = @start;
            END
            SELECT @v;
            """;
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State == ConnectionState.Closed;
        if (wasClosed) await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            cmd.Parameters.Add(new SqlParameter("@t", tenant));
            cmd.Parameters.Add(new SqlParameter("@n", name));
            cmd.Parameters.Add(new SqlParameter("@id", SequentialGuid.New()));
            cmd.Parameters.Add(new SqlParameter("@start", start));
            var value = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt64(value);
        }
        finally
        {
            if (wasClosed) await db.Database.CloseConnectionAsync();
        }
    }
}

public enum DatabaseInitializeMode { Migrate, EnsureCreated, None }

/// <summary>Applies migrations to the shared database and every dedicated tenant database.</summary>
public sealed class DatabaseInitializer(DeskDbContext db, TenantDatabaseDirectory directory, IConfiguration configuration, ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var mode = configuration.GetValue("Database:InitializeMode", DatabaseInitializeMode.Migrate);
        if (mode == DatabaseInitializeMode.None) return;
        if (mode == DatabaseInitializeMode.EnsureCreated)
        {
            await db.Database.EnsureCreatedAsync(ct);
            return;
        }
        logger.LogInformation("Applying database migrations (shared database)");
        await db.Database.MigrateAsync(ct);
        foreach (var (tenantId, cs) in directory.All())
            await MigrateDedicatedAsync(tenantId, cs, ct);
    }

    public async Task MigrateDedicatedAsync(Guid tenantId, string connectionString, CancellationToken ct = default)
    {
        logger.LogInformation("Applying migrations to dedicated database of tenant {Tenant}", tenantId);
        var options = new DbContextOptionsBuilder<DeskDbContext>().UseSqlServer(connectionString).Options;
        await using var tenantDb = new DeskDbContext(options, new RequestContext(), db.Clock);
        await tenantDb.Database.MigrateAsync(ct);
    }
}

/// <summary>Used by `dotnet ef` (migrations). Connection from SUPPORTDESK_DESIGN_CONNECTION or LocalDB.</summary>
public sealed class DeskDbContextDesignFactory : IDesignTimeDbContextFactory<DeskDbContext>
{
    public DeskDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("SUPPORTDESK_DESIGN_CONNECTION")
                 ?? "Server=(localdb)\\MSSQLLocalDB;Database=SupportDesk;Trusted_Connection=True;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<DeskDbContext>()
            .UseSqlServer(cs, sql => sql.MigrationsAssembly(typeof(DeskDbContext).Assembly.FullName))
            .Options;
        return new DeskDbContext(options, new RequestContext(), new SystemClock());
    }
}
