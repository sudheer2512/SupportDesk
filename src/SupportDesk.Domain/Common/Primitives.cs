using System.Security.Cryptography;
using System.Text;

namespace SupportDesk.Domain;

/// <summary>Base type for every persisted entity. Ids are assigned in code (never by the database).</summary>
public abstract class Entity
{
    public Guid Id { get; set; } = SequentialGuid.New();
}

/// <summary>Rows that belong to exactly one tenant. The persistence layer filters and stamps these automatically.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

public interface IHasTimestamps
{
    DateTime CreatedAt { get; set; }
    DateTime UpdatedAt { get; set; }
}

public interface IHasAuthors
{
    Guid? CreatedById { get; set; }
    Guid? UpdatedById { get; set; }
}

public interface ISoftDeletable
{
    bool IsDeleted { get; set; }
    DateTime? DeletedAt { get; set; }
}

/// <summary>Optimistic concurrency via SQL Server rowversion.</summary>
public interface IConcurrencyVersioned
{
    byte[] RowVersion { get; set; }
}

/// <summary>Rows that may only be inserted (audit trails, event histories). Updates and deletes are rejected by the DbContext.</summary>
public interface IAppendOnly;

public abstract class TenantEntity : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
}

public abstract class AuditedEntity : TenantEntity, IHasTimestamps, IHasAuthors
{
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? CreatedById { get; set; }
    public Guid? UpdatedById { get; set; }
}

/// <summary>
/// GUIDs whose SQL Server sort order follows creation order (same byte layout as EF Core's
/// SequentialGuidValueGenerator). Keeps clustered indexes append-mostly instead of random inserts.
/// </summary>
public static class SequentialGuid
{
    private static long _counter = DateTime.UtcNow.Ticks;

    public static Guid New()
    {
        var guidBytes = Guid.NewGuid().ToByteArray();
        var counterBytes = BitConverter.GetBytes(Interlocked.Increment(ref _counter));
        if (!BitConverter.IsLittleEndian) Array.Reverse(counterBytes);
        guidBytes[08] = counterBytes[1];
        guidBytes[09] = counterBytes[0];
        guidBytes[10] = counterBytes[7];
        guidBytes[11] = counterBytes[6];
        guidBytes[12] = counterBytes[5];
        guidBytes[13] = counterBytes[4];
        guidBytes[14] = counterBytes[3];
        guidBytes[15] = counterBytes[2];
        return new Guid(guidBytes);
    }
}

/// <summary>Name-based (RFC 4122 v5 style) GUIDs: the same inputs always give the same id. Used for idempotency keys and seed data.</summary>
public static class DeterministicGuid
{
    private static readonly Guid Namespace = new("5b0c6a2e-7f7d-4b8e-9a51-0d6f3c1e2a47");

    public static Guid From(params object?[] parts) => From(Namespace, string.Join("|", parts.Select(p => p?.ToString() ?? "")));

    public static Guid From(Guid ns, string name)
    {
        var nsBytes = ns.ToByteArray();
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var data = new byte[nsBytes.Length + nameBytes.Length];
        nsBytes.CopyTo(data, 0);
        nameBytes.CopyTo(data, nsBytes.Length);
        var hash = SHA1.HashData(data);
        var result = new byte[16];
        Array.Copy(hash, result, 16);
        result[6] = (byte)((result[6] & 0x0F) | 0x50);
        result[8] = (byte)((result[8] & 0x3F) | 0x80);
        return new Guid(result);
    }
}

/// <summary>Time source. Always UTC. Tests and the demo seeder substitute a controllable clock.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>Clock that can be pinned or advanced (tests, seeding historical demo data).</summary>
public sealed class MutableClock : IClock
{
    private readonly AsyncLocal<DateTime?> _pinned = new();
    private TimeSpan _offset;

    public DateTime UtcNow => _pinned.Value ?? DateTime.UtcNow.Add(_offset);

    public void Pin(DateTime utc) => _pinned.Value = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
    public void Unpin() => _pinned.Value = null;
    public void Advance(TimeSpan by)
    {
        if (_pinned.Value is { } p) _pinned.Value = p.Add(by);
        else _offset = _offset.Add(by);
    }
}

/// <summary>A business rule was violated. Mapped to HTTP 4xx problem details by the API.</summary>
public class DomainException(string code, string message, int statusCode = 422) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class NotFoundException(string entity, object? id = null)
    : DomainException("not_found", id is null ? $"{entity} was not found." : $"{entity} '{id}' was not found.", 404);

public sealed class ForbiddenException(string message = "You do not have permission to perform this action.")
    : DomainException("forbidden", message, 403);

public sealed class ConflictException(string code, string message) : DomainException(code, message, 409);

public sealed class LimitExceededException(string limit, string message) : DomainException("limit_exceeded", message, 402)
{
    public string Limit { get; } = limit;
}
