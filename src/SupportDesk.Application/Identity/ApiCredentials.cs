using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Auth;
using SupportDesk.Domain;
using SupportDesk.Domain.Integration;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Identity;

public static class ApiScopes
{
    public const string All = "*";
    public static readonly string[] Catalog =
    [
        "tickets:read", "tickets:write", "customers:read", "customers:write", "products:read", "products:write", "contracts:read",
        "contracts:write", "kb:read", "kb:write", "analytics:read", "admin:read", "admin:write", "integrations:write", "ai:write"
    ];

    /// <summary>Permissions granted to a client by its scopes (see <see cref="ApiScopeRules"/>).</summary>
    public static HashSet<string> PermissionsFor(IEnumerable<string> scopes)
    {
        var set = scopes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Permissions.Catalog.Select(p => p.Code).Where(code => ApiScopeRules.Allows(set, code)).ToHashSet();
    }
}

public sealed record ApiKeyCreated(Guid Id, string Name, string Key, string Prefix, IReadOnlyList<string> Scopes, DateTime? ExpiresAt);

public sealed record ValidatedClient(Guid TenantId, Guid ClientId, string Name, IReadOnlyList<string> Scopes, Guid? ActAsUserId, int RateLimitPerMinute);

/// <summary>Static API keys: "sdk_{prefix}_{secret}". Only SHA-256 of the full key is stored.</summary>
public sealed class ApiKeyService(DeskDbContext db, RequestContext ctx, IAuditWriter audit)
{
    public async Task<ApiKeyCreated> CreateAsync(string name, IReadOnlyList<string> scopes, Guid? actAsUserId, DateTime? expiresAt, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var invalid = scopes.Where(s => s != ApiScopes.All && !ApiScopes.Catalog.Contains(s)).ToList();
        if (invalid.Count > 0) throw new DomainException("validation_failed", $"Unknown scopes: {string.Join(", ", invalid)}");
        var prefix = TextNormalization.RandomAlphanumeric(10);
        var key = $"sdk_{prefix}_{TextNormalization.RandomToken(24)}";
        var entity = new ApiKey
        {
            Name = name, Prefix = prefix, KeyHash = TokenHasher.Hash(key), ScopesJson = Json.Serialize(scopes), ActAsUserId = actAsUserId, ExpiresAt = expiresAt
        };
        db.ApiKeys.Add(entity);
        audit.Write(AuditActions.ApiKeyCreated, "ApiKey", entity.Id, $"API key '{name}' created ({prefix})", new { scopes });
        await db.SaveChangesAsync(ct);
        return new ApiKeyCreated(entity.Id, name, key, prefix, scopes, expiresAt);
    }

    public async Task RevokeAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct) ?? throw new NotFoundException("API key", id);
        key.IsActive = false;
        audit.Write(AuditActions.ApiKeyRevoked, "ApiKey", id, $"API key '{key.Name}' revoked");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Validates a presented key (any tenant). Returns null for unknown, revoked or expired keys.</summary>
    public async Task<ValidatedClient?> ValidateAsync(string presented, CancellationToken ct)
    {
        var parts = presented.Split('_');
        if (parts.Length < 3 || parts[0] != "sdk") return null;
        var prefix = parts[1];
        var key = await db.AllTenants<ApiKey>().FirstOrDefaultAsync(k => k.Prefix == prefix, ct);
        if (key is null || !key.IsActive || key.ExpiresAt < db.Clock.UtcNow) return null;
        if (!TokenHasher.FixedEquals(key.KeyHash, TokenHasher.Hash(presented))) return null;
        if (key.LastUsedAt is null || key.LastUsedAt < db.Clock.UtcNow.AddMinutes(-5))
            await db.AllTenants<ApiKey>().Where(k => k.Id == key.Id).ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, db.Clock.UtcNow), ct);
        return new ValidatedClient(key.TenantId, key.Id, key.Name, Json.StringList(key.ScopesJson), key.ActAsUserId, key.RateLimitPerMinute);
    }
}

public sealed record OAuthClientCreated(Guid Id, string Name, string ClientId, string ClientSecret, IReadOnlyList<string> Scopes);

/// <summary>OAuth 2.0 client-credentials grant for system integrations (ERP, CRM, telephony).</summary>
public sealed class OAuthClientService(DeskDbContext db, RequestContext ctx, PasswordService passwords, TokenService tokens, IAuditWriter audit)
{
    public async Task<OAuthClientCreated> CreateAsync(string name, IReadOnlyList<string> scopes, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var clientId = $"{ctx.TenantSlug ?? "t"}-{TextNormalization.RandomAlphanumeric(12)}";
        var secret = TextNormalization.RandomToken(32);
        var client = new ApiClient { Name = name, ClientId = clientId, SecretHash = passwords.Hash(secret), ScopesJson = Json.Serialize(scopes) };
        db.ApiClients.Add(client);
        audit.Write(AuditActions.ApiKeyCreated, "ApiClient", client.Id, $"OAuth client '{name}' created ({clientId})", new { scopes });
        await db.SaveChangesAsync(ct);
        return new OAuthClientCreated(client.Id, name, clientId, secret, scopes);
    }

    public async Task<string> RotateSecretAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var client = await db.ApiClients.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("OAuth client", id);
        var secret = TextNormalization.RandomToken(32);
        client.SecretHash = passwords.Hash(secret);
        audit.Write(AuditActions.IntegrationChanged, "ApiClient", id, $"OAuth client secret rotated ({client.ClientId})");
        await db.SaveChangesAsync(ct);
        return secret;
    }

    /// <summary>Issues an access token for valid client credentials. Requested scopes are intersected with the granted ones.</summary>
    public async Task<OAuthTokenResponse?> IssueTokenAsync(string clientId, string clientSecret, string? requestedScope, CancellationToken ct)
    {
        var client = await db.AllTenants<ApiClient>().FirstOrDefaultAsync(c => c.ClientId == clientId, ct);
        if (client is null || !client.IsActive || client.ExpiresAt < db.Clock.UtcNow || !passwords.Verify(client.SecretHash, clientSecret)) return null;
        var granted = Json.StringList(client.ScopesJson);
        var scopes = string.IsNullOrWhiteSpace(requestedScope)
            ? granted
            : requestedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(s => granted.Contains(s) || granted.Contains(ApiScopes.All)).ToList();
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == client.TenantId, ct);
        await db.AllTenants<ApiClient>().Where(c => c.Id == client.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastUsedAt, db.Clock.UtcNow), ct);
        var lifetime = TimeSpan.FromHours(1);
        var token = tokens.Issue(DeskClaims.TypeClient, client.Id, client.TenantId, tenant.Slug, client.Name, lifetime, scopes: scopes,
            rateLimitPerMinute: client.RateLimitPerMinute);
        return new OAuthTokenResponse(token.Token, "Bearer", (int)lifetime.TotalSeconds, string.Join(' ', scopes));
    }

    public async Task<ValidatedClient?> LoadAsync(Guid clientId, Guid tenantId, IReadOnlyList<string> scopes, CancellationToken ct)
    {
        var client = await db.AllTenants<ApiClient>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId && c.TenantId == tenantId, ct);
        if (client is null || !client.IsActive) return null;
        return new ValidatedClient(client.TenantId, client.Id, client.Name, scopes, null, client.RateLimitPerMinute);
    }
}
