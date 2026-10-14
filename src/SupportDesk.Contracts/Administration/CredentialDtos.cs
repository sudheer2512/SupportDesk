namespace SupportDesk.Contracts.Administration;

/// <summary>An API scope ("tickets:read", "admin:write", "*") and the permissions it grants. Grantable: the caller holds all of them.</summary>
public sealed record ApiScopeDto(string Scope, string Description, IReadOnlyList<string> Permissions, bool Grantable);

/// <summary>Static API key (X-Api-Key: sdk_{prefix}_{secret}). Status: Active, Expired or Revoked. The secret is never returned after creation.</summary>
public sealed record AdminApiKeyDto(
    Guid Id,
    string Name,
    string Prefix,
    IReadOnlyList<string> Scopes,
    Guid? ActAsUserId,
    string? ActAsUserName,
    string Status,
    DateTime? ExpiresAt,
    DateTime? LastUsedAt,
    DateTime CreatedAt,
    string? CreatedBy,
    int RateLimitPerMinute);

public sealed record CreateApiKeyRequest(string Name, IReadOnlyList<string> Scopes, Guid? ActAsUserId, DateTime? ExpiresAt);

/// <summary>Returned once at creation: <see cref="Secret"/> is the full key.</summary>
public sealed record ApiKeySecretDto(AdminApiKeyDto Key, string Secret);

/// <summary>OAuth 2.0 client (client-credentials grant at POST /oauth/token).</summary>
public sealed record AdminOAuthClientDto(
    Guid Id,
    string Name,
    string ClientId,
    IReadOnlyList<string> Scopes,
    string Status,
    DateTime? ExpiresAt,
    DateTime? LastUsedAt,
    DateTime CreatedAt,
    string? CreatedBy,
    int RateLimitPerMinute);

public sealed record CreateOAuthClientRequest(string Name, IReadOnlyList<string> Scopes, DateTime? ExpiresAt);

/// <summary>Returned once at creation and on secret rotation.</summary>
public sealed record OAuthClientSecretDto(AdminOAuthClientDto Client, string ClientSecret, string TokenEndpoint);
