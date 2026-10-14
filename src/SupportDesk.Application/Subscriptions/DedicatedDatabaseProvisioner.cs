using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tenancy;
using SupportDesk.Contracts.Platform;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;
using SupportDesk.Persistence.Infrastructure;

namespace SupportDesk.Application.Subscriptions;

/// <summary>
/// Moves a tenant to a dedicated database (enterprise isolation). Steps:
/// 1. resolve the connection by name (<c>ConnectionStrings:{name}</c> or the <c>ConnectionStrings:DedicatedTemplate</c>);
/// 2. create the database on the same SQL Server instance; 3. apply the model (migrations, or EnsureCreated in
/// development) and the audit trigger; 4. copy the tenant's rows of every tenant-owned table plus the global reference
/// catalogs (plans, permissions) and the tenant row, with constraints disabled during the copy; 5. switch the tenant to
/// <c>DatabaseMode.Dedicated</c> with the connection name (never a raw connection string) and refresh the routing directory.
/// The shared-database rows stay as a frozen copy until an operator purges them after verification (see docs/DEPLOYMENT.md).
/// </summary>
public sealed class DedicatedDatabaseProvisioner(
    DeskDbContext db,
    RequestContext ctx,
    SharedConnection shared,
    IConfiguration configuration,
    TenantDatabaseDirectoryAccessor directory,
    DedicatedCatalogMirror mirror,
    IAuditWriter audit,
    ILogger<DedicatedDatabaseProvisioner> logger)
{
    /// <summary>Global (not tenant-owned) tables copied in full so foreign keys resolve inside the dedicated database.</summary>
    private static readonly HashSet<Type> GlobalReferenceTypes = [typeof(Plan), typeof(SupportDesk.Domain.Identity.Permission)];

    public async Task<DedicatedDatabaseResultDto> ProvisionAsync(Guid tenantId, ProvisionDedicatedRequest r, CancellationToken ct = default)
    {
        PlatformAccess.Enter(ctx);
        var steps = new List<string>();
        var name = (r.ConnectionName ?? "").Trim();
        if (!DedicatedConnections.IsValidName(name))
            throw new Tickets.ValidationException(new Dictionary<string, string> { ["connectionName"] = "Use 3–40 lower-case letters, digits or underscores." });
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct) ?? throw new NotFoundException("Tenant", tenantId);
        if (tenant.DatabaseMode == DatabaseMode.Dedicated) throw new ConflictException("already_dedicated", "The tenant already runs on a dedicated database.");
        var sub = await db.AllTenants<Subscription>().AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        var plan = sub is null ? null : await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == sub.PlanId, ct);
        if (plan is null || !Json.StringList(plan.FeaturesJson).Contains(PlanFeatures.DedicatedDatabase))
            throw new DomainException("feature_not_in_plan", "Dedicated databases require a plan with the 'dedicated_database' feature (Enterprise).", 409);
        if (await db.Tenants.AnyAsync(t => t.DedicatedConnectionName == name, ct))
            throw new ConflictException("connection_in_use", "Another tenant already uses this connection name.");

        var cs = DedicatedConnections.Resolve(configuration, name)
                 ?? throw new DomainException("connection_not_configured",
                     $"No connection string named '{name}' is configured and no ConnectionStrings:DedicatedTemplate is set.", 422);
        var target = new SqlConnectionStringBuilder(cs);
        var source = new SqlConnectionStringBuilder(shared.ConnectionString);
        var database = target.InitialCatalog;
        if (string.IsNullOrWhiteSpace(database) || !database.All(c => char.IsLetterOrDigit(c) || c is '_' or '-'))
            throw new DomainException("invalid_database", "The dedicated connection must name a database (letters, digits, '_' or '-').", 422);
        if (string.Equals(database, source.InitialCatalog, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("invalid_database", "The dedicated database must differ from the shared database.", 422);
        if (!string.Equals(target.DataSource, source.DataSource, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("different_server_unsupported",
                "Online copy works within one SQL Server instance. For another server, restore a copy of the tenant's data there first (see docs/DEPLOYMENT.md).", 422);

        // 1–2. Create the database.
        var master = new SqlConnectionStringBuilder(cs) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"IF DB_ID(@db) IS NULL CREATE DATABASE {Quote(database)}";
            cmd.Parameters.AddWithValue("@db", database);
            cmd.CommandTimeout = 180;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        steps.Add($"Database {database} created (or already present) on {target.DataSource}.");

        // 3. Apply the model.
        var mode = configuration.GetValue("Database:InitializeMode", DatabaseInitializeMode.Migrate);
        var options = new DbContextOptionsBuilder<DeskDbContext>().UseSqlServer(cs, sql => sql.CommandTimeout(300)).Options;
        await using (var tenantDb = new DeskDbContext(options, new RequestContext(), db.Clock))
        {
            if (mode == DatabaseInitializeMode.EnsureCreated) await tenantDb.Database.EnsureCreatedAsync(ct);
            else await tenantDb.Database.MigrateAsync(ct);
            await DatabaseInitializer.EnsureSqlObjectsAsync(tenantDb, ct);
            if (await tenantDb.Tenants.IgnoreQueryFilters().AnyAsync(ct))
                throw new ConflictException("database_not_empty", $"Database {database} already contains tenant data. Choose a new connection name.");
        }
        steps.Add(mode == DatabaseInitializeMode.EnsureCreated ? "Schema created from the model (EnsureCreated)." : "Migrations applied.");

        // 4. Copy the tenant's data (same instance: cross-database INSERT … SELECT).
        var tables = 0;
        long rows = 0;
        await using (var conn = new SqlConnection(shared.ConnectionString))
        {
            await conn.OpenAsync(ct);
            var entityTypes = db.Model.GetEntityTypes().Where(e => e.GetTableName() is not null && !e.IsOwned() && e.BaseType is null).ToList();
            await ExecAsync(conn, string.Join('\n', entityTypes.Select(e => $"ALTER TABLE {Quote(database)}.{Table(e)} NOCHECK CONSTRAINT ALL;")), ct);
            try
            {
                foreach (var entity in entityTypes)
                {
                    string filter;
                    if (entity.ClrType == typeof(Tenant)) filter = "s.[Id] = @t";
                    else if (entity.FindProperty("TenantId") is not null) filter = "s.[TenantId] = @t";
                    else if (GlobalReferenceTypes.Contains(entity.ClrType))
                        filter = $"NOT EXISTS (SELECT 1 FROM {Quote(database)}.{Table(entity)} x WHERE x.[{entity.FindPrimaryKey()!.Properties[0].GetColumnName()}] = s.[{entity.FindPrimaryKey()!.Properties[0].GetColumnName()}])";
                    else continue;
                    var columns = Columns(entity);
                    if (columns.Count == 0) continue;
                    var list = string.Join(", ", columns.Select(Quote));
                    var sql = $"INSERT INTO {Quote(database)}.{Table(entity)} ({list}) SELECT {string.Join(", ", columns.Select(c => "s." + Quote(c)))} FROM {Table(entity)} s WHERE {filter};";
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 600;
                    cmd.Parameters.AddWithValue("@t", tenantId);
                    var n = await cmd.ExecuteNonQueryAsync(ct);
                    tables++;
                    rows += Math.Max(0, n);
                }
            }
            finally
            {
                // Re-enable constraints for new writes (existing rows are trusted: they come from a consistent source).
                await ExecAsync(conn, string.Join('\n', entityTypes.Select(e => $"ALTER TABLE {Quote(database)}.{Table(e)} CHECK CONSTRAINT ALL;")), ct);
            }
        }
        steps.Add($"Copied {rows:N0} rows from {tables} tables (tenant-owned data, the tenant record, plans and permissions).");
        steps.Add("Sessions are not copied: users sign in again after the move.");

        // 5. Switch the tenant.
        tenant.DatabaseMode = DatabaseMode.Dedicated;
        tenant.DedicatedConnectionName = name;
        audit.Write("platform.dedicated_database", "Tenant", tenant.Id, $"Moved {tenant.Slug} to dedicated database {database} (connection '{name}')",
            new { name, database, tables, rows });
        CatalogAudit.Write(db, tenant.Id, ActorType.Platform, ctx.UserId, ctx.ActorName, "platform.dedicated_database", "Tenant", tenant.Id,
            $"Workspace moved to a dedicated database (connection '{name}')", new { name, tables, rows }, db.Clock.UtcNow);
        await db.SaveChangesAsync(ct);
        directory.Invalidate();
        await mirror.SyncAsync(tenantId, ct);
        steps.Add($"Tenant switched to DatabaseMode=Dedicated with connection name '{name}'; routing directory refreshed.");
        logger.LogInformation("Tenant {Tenant} moved to dedicated database {Database}", tenantId, database);
        return new DedicatedDatabaseResultDto(name, database, tables, rows, steps);
    }

    private static List<string> Columns(IEntityType entity)
    {
        var store = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        return entity.GetProperties()
            .Where(p => !(p.IsConcurrencyToken && p.ValueGenerated == ValueGenerated.OnAddOrUpdate) && p.GetComputedColumnSql() is null)
            .Select(p => p.GetColumnName(store))
            .Where(c => c is not null)
            .Select(c => c!)
            .Distinct()
            .ToList();
    }

    private static string Table(IEntityType e) => $"{Quote(e.GetSchema() ?? "dbo")}.{Quote(e.GetTableName()!)}";

    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static async Task ExecAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sql)) return;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
