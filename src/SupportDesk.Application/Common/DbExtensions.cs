using Microsoft.EntityFrameworkCore;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Common;

public static class DbExtensions
{
    /// <summary>
    /// Runs <paramref name="action"/> in an explicit transaction under the configured execution strategy
    /// (SQL Server transient-fault retries require user transactions to be wrapped this way).
    /// Re-entrant: if a transaction is already open, the action simply joins it.
    /// </summary>
    public static async Task InTransactionAsync(this DeskDbContext db, Func<Task> action, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            await action();
            return;
        }
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await action();
            await tx.CommitAsync(ct);
        });
    }

    public static async Task<T> InTransactionAsync<T>(this DeskDbContext db, Func<Task<T>> action, CancellationToken ct = default)
    {
        T result = default!;
        await db.InTransactionAsync(async () => { result = await action(); }, ct);
        return result;
    }

    /// <summary>Opaque keyset cursor: base64url JSON {"v":"…","id":"…"}.</summary>
    public static string EncodeCursor(string value, Guid id) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Json.Serialize(new CursorData(value, id)))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static CursorData? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var s = cursor.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            return Json.Deserialize<CursorData>(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s)));
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            throw new Domain.DomainException("validation_failed", "Invalid cursor.", 400);
        }
    }
}

public sealed record CursorData(string V, Guid Id);
