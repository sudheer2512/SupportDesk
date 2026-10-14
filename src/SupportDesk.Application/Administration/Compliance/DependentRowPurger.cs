using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Administration.Compliance;

/// <summary>
/// Hard-deletes rows together with everything that references them, discovering references from the database's
/// foreign keys (so tables added by other modules are handled without code changes): a nullable referencing column is
/// set to NULL, a required one has its rows deleted recursively first. Callers pass ids they selected through the
/// tenant-filtered DbContext; children reached through foreign keys belong to the same tenant by construction.
/// Runs on the DbContext's connection and joins its current transaction.
/// </summary>
public sealed class DependentRowPurger(DeskDbContext db)
{
    private sealed record ForeignKey(string ChildSchema, string ChildTable, string ChildColumn, bool Nullable, string ParentColumn);

    private Dictionary<string, List<ForeignKey>>? _graph;

    public async Task<int> DeleteAsync(string table, IReadOnlyCollection<Guid> ids, CancellationToken ct, string schema = "dbo")
    {
        if (ids.Count == 0) return 0;
        _graph ??= await LoadGraphAsync(ct);
        var total = 0;
        foreach (var chunk in ids.Chunk(400))
            total += await DeleteChunkAsync(schema, table, chunk, 0, ct);
        return total;
    }

    private async Task<int> DeleteChunkAsync(string schema, string table, IReadOnlyCollection<Guid> ids, int depth, CancellationToken ct)
    {
        if (depth > 10) throw new InvalidOperationException($"Foreign-key chain too deep while purging {table}.");
        var list = string.Join(",", ids.Select(id => $"'{id}'"));
        foreach (var fk in _graph!.GetValueOrDefault(Key(schema, table)) ?? [])
        {
            if (fk.ChildTable == table && fk.ChildSchema == schema && fk.Nullable)
            {
                await ExecAsync($"UPDATE {Q(fk.ChildSchema)}.{Q(fk.ChildTable)} SET {Q(fk.ChildColumn)} = NULL WHERE {Q(fk.ChildColumn)} IN ({list})", ct);
                continue;
            }
            if (fk.Nullable)
            {
                await ExecAsync($"UPDATE {Q(fk.ChildSchema)}.{Q(fk.ChildTable)} SET {Q(fk.ChildColumn)} = NULL WHERE {Q(fk.ChildColumn)} IN ({list})", ct);
                continue;
            }
            var childIds = await QueryIdsAsync($"SELECT [Id] FROM {Q(fk.ChildSchema)}.{Q(fk.ChildTable)} WHERE {Q(fk.ChildColumn)} IN ({list})", ct);
            if (childIds.Count > 0)
                foreach (var chunk in childIds.Chunk(400))
                    await DeleteChunkAsync(fk.ChildSchema, fk.ChildTable, chunk, depth + 1, ct);
        }
        return await ExecAsync($"DELETE FROM {Q(schema)}.{Q(table)} WHERE [Id] IN ({list})", ct);
    }

    private async Task<Dictionary<string, List<ForeignKey>>> LoadGraphAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT ps.name AS ParentSchema, pt.name AS ParentTable, cs.name AS ChildSchema, ct.name AS ChildTable,
                   cc.name AS ChildColumn, cc.is_nullable AS Nullable, pc.name AS ParentColumn
            FROM sys.foreign_key_columns fkc
            JOIN sys.tables pt ON pt.object_id = fkc.referenced_object_id
            JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
            JOIN sys.tables ct ON ct.object_id = fkc.parent_object_id
            JOIN sys.schemas cs ON cs.schema_id = ct.schema_id
            JOIN sys.columns cc ON cc.object_id = fkc.parent_object_id AND cc.column_id = fkc.parent_column_id
            JOIN sys.columns pc ON pc.object_id = fkc.referenced_object_id AND pc.column_id = fkc.referenced_column_id
            """;
        var graph = new Dictionary<string, List<ForeignKey>>(StringComparer.OrdinalIgnoreCase);
        await WithCommandAsync(sql, async cmd =>
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var parent = Key(reader.GetString(0), reader.GetString(1));
                if (!graph.TryGetValue(parent, out var list)) graph[parent] = list = [];
                // Only references to the primary key column "Id" matter for row purges.
                if (!string.Equals(reader.GetString(6), "Id", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new ForeignKey(reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5), reader.GetString(6)));
            }
        }, ct);
        return graph;
    }

    private async Task<List<Guid>> QueryIdsAsync(string sql, CancellationToken ct)
    {
        var ids = new List<Guid>();
        await WithCommandAsync(sql, async cmd =>
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        }, ct);
        return ids;
    }

    private async Task<int> ExecAsync(string sql, CancellationToken ct)
    {
        var n = 0;
        await WithCommandAsync(sql, async cmd => n = await cmd.ExecuteNonQueryAsync(ct), ct);
        return n;
    }

    private async Task WithCommandAsync(string sql, Func<DbCommand, Task> work, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State == ConnectionState.Closed;
        if (wasClosed) await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 300;
            cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            await work(cmd);
        }
        finally
        {
            if (wasClosed) await db.Database.CloseConnectionAsync();
        }
    }

    private static string Key(string schema, string table) => $"{schema}.{table}";

    private static string Q(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
