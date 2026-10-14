using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using SupportDesk.Application.Common;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Platform;

/// <summary>Binary storage for attachments, exports and recordings. Keys always start with the tenant id.</summary>
public interface IFileStorage
{
    Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
}

public sealed record ScanResult(ScanStatus Status, string? Detail = null);

public interface IMalwareScanner
{
    Task<ScanResult> ScanAsync(Stream content, string fileName, CancellationToken ct = default);
}

/// <summary>Encrypts secrets at rest (channel passwords, API tokens, MFA secrets).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}

/// <summary>Pushes realtime events to connected clients (SignalR in the API; no-op elsewhere).</summary>
public interface IRealtimePublisher
{
    Task ToTenantAsync(Guid tenantId, string eventName, object payload, CancellationToken ct = default);
    Task ToUsersAsync(Guid tenantId, IEnumerable<Guid> userIds, string eventName, object payload, CancellationToken ct = default);
    Task ToTicketAsync(Guid tenantId, Guid ticketId, string eventName, object payload, CancellationToken ct = default);
    Task ToDepartmentsAsync(Guid tenantId, IEnumerable<Guid> departmentIds, string eventName, object payload, CancellationToken ct = default);
    Task ToContactAsync(Guid tenantId, Guid contactId, string eventName, object payload, CancellationToken ct = default);
    Task ToConversationAsync(Guid tenantId, Guid conversationId, string eventName, object payload, CancellationToken ct = default);
}

public sealed class NullRealtimePublisher : IRealtimePublisher
{
    public Task ToTenantAsync(Guid tenantId, string eventName, object payload, CancellationToken ct = default) => Task.CompletedTask;
    public Task ToUsersAsync(Guid tenantId, IEnumerable<Guid> userIds, string eventName, object payload, CancellationToken ct = default) => Task.CompletedTask;
    public Task ToTicketAsync(Guid tenantId, Guid ticketId, string eventName, object payload, CancellationToken ct = default) => Task.CompletedTask;
    public Task ToDepartmentsAsync(Guid tenantId, IEnumerable<Guid> departmentIds, string eventName, object payload, CancellationToken ct = default) => Task.CompletedTask;
    public Task ToContactAsync(Guid tenantId, Guid contactId, string eventName, object payload, CancellationToken ct = default) => Task.CompletedTask;
    public Task ToConversationAsync(Guid tenantId, Guid conversationId, string eventName, object payload, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Transactional system e-mail (OTP codes, password resets, digests, scheduled reports, CSAT surveys).
/// Sent through the tenant's default outbound e-mail channel when configured, otherwise the platform mail transport.
/// </summary>
public interface ISystemMailer
{
    Task SendAsync(Guid? tenantId, string to, string subject, string html, string? text = null,
        IReadOnlyList<MailAttachment>? attachments = null, CancellationToken ct = default);
}

public sealed record MailAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>Tenant-scoped cache over IDistributedCache (Redis in production, memory in development). Keys are prefixed with the tenant id.</summary>
public interface ITenantCache
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
    Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan ttl, CancellationToken ct = default);
}

public sealed class TenantCache(IDistributedCache cache, RequestContext ctx) : ITenantCache
{
    private string Key(string key)
    {
        if (!ctx.HasTenant) throw new InvalidOperationException("Tenant cache used without a tenant.");
        return $"sd:{ctx.TenantId:N}:{key}";
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var bytes = await cache.GetAsync(Key(key), ct);
        return bytes is null ? default : JsonSerializer.Deserialize<T>(bytes, Json.Options);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) =>
        cache.SetAsync(Key(key), JsonSerializer.SerializeToUtf8Bytes(value, Json.Options),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }, ct);

    public Task RemoveAsync(string key, CancellationToken ct = default) => cache.RemoveAsync(Key(key), ct);

    public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan ttl, CancellationToken ct = default)
    {
        var existing = await cache.GetAsync(Key(key), ct);
        if (existing is not null) return JsonSerializer.Deserialize<T>(existing, Json.Options)!;
        var value = await factory();
        await SetAsync(key, value, ttl, ct);
        return value;
    }
}
