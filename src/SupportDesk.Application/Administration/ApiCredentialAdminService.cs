using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Identity;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Administration;
using SupportDesk.Domain;
using SupportDesk.Domain.Integration;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Administration;

/// <summary>
/// API credentials (admin.integrations): static API keys ("sdk_{prefix}_{secret}", shown once, hashed at rest) and OAuth
/// client-credentials clients, both scope-limited. Nobody can create a credential whose scopes grant permissions they do
/// not hold. Creation reuses the identity services (<see cref="ApiKeyService"/>, <see cref="OAuthClientService"/>), so
/// the existing X-Api-Key and /oauth/token handlers authenticate them unchanged.
/// </summary>
public sealed class ApiCredentialAdminService(
    DeskDbContext db,
    RequestContext ctx,
    IAuditWriter audit,
    ApiKeyService apiKeys,
    OAuthClientService oauthClients)
{
    private static readonly IReadOnlyDictionary<string, string> ScopeDescriptions = new Dictionary<string, string>
    {
        ["tickets:read"] = "Read tickets", ["tickets:write"] = "Create and change tickets", ["customers:read"] = "Read customers and contacts",
        ["customers:write"] = "Create and change customers and contacts", ["products:read"] = "Read products, installations and assets",
        ["products:write"] = "Manage products, installations and assets", ["contracts:read"] = "Read contracts and entitlements",
        ["contracts:write"] = "Manage contracts and entitlements", ["kb:read"] = "Read internal knowledge", ["kb:write"] = "Author and publish knowledge",
        ["analytics:read"] = "Read analytics", ["admin:read"] = "Read administration data", ["admin:write"] = "Change administration settings",
        ["integrations:write"] = "Integration callbacks", ["ai:write"] = "Use AI features", [ApiScopes.All] = "Everything (all current and future permissions)"
    };

    public List<ApiScopeDto> Scopes()
    {
        ctx.Require(Permissions.AdminIntegrations);
        return ApiScopes.Catalog.Append(ApiScopes.All).Select(s =>
        {
            var perms = ApiScopes.PermissionsFor([s]).OrderBy(p => p, StringComparer.Ordinal).ToList();
            return new ApiScopeDto(s, ScopeDescriptions.GetValueOrDefault(s, s), perms, RoleRules.NotHeld(ctx.Permissions, perms).Count == 0);
        }).ToList();
    }

    // ───────────────────────────── API keys ─────────────────────────────

    public async Task<List<AdminApiKeyDto>> ListKeysAsync(CancellationToken ct)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var keys = await db.ApiKeys.AsNoTracking().OrderByDescending(k => k.CreatedAt).ToListAsync(ct);
        var names = await UserNamesAsync(keys.Select(k => k.ActAsUserId).Concat(keys.Select(k => k.CreatedById)), ct);
        return keys.Select(k => MapKey(k, names)).ToList();
    }

    public async Task<ApiKeySecretDto> CreateKeyAsync(CreateApiKeyRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var scopes = await ValidateAsync(r.Name, r.Scopes, r.ExpiresAt, r.ActAsUserId, ct);
        var created = await apiKeys.CreateAsync(r.Name.Trim(), scopes, r.ActAsUserId, r.ExpiresAt, ct);
        var key = await db.ApiKeys.AsNoTracking().FirstAsync(k => k.Id == created.Id, ct);
        var names = await UserNamesAsync([key.ActAsUserId, key.CreatedById], ct);
        return new ApiKeySecretDto(MapKey(key, names), created.Key);
    }

    public async Task RevokeKeyAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var key = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == id, ct) ?? throw new NotFoundException("API key", id);
        if (!key.IsActive) return;
        await apiKeys.RevokeAsync(id, ct);
    }

    // ───────────────────────────── OAuth clients ─────────────────────────────

    public async Task<List<AdminOAuthClientDto>> ListClientsAsync(CancellationToken ct)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var clients = await db.ApiClients.AsNoTracking().OrderByDescending(c => c.CreatedAt).ToListAsync(ct);
        var names = await UserNamesAsync(clients.Select(c => c.CreatedById), ct);
        return clients.Select(c => MapClient(c, names)).ToList();
    }

    public async Task<OAuthClientSecretDto> CreateClientAsync(CreateOAuthClientRequest r, string baseUrl, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var scopes = await ValidateAsync(r.Name, r.Scopes, r.ExpiresAt, null, ct);
        var created = await oauthClients.CreateAsync(r.Name.Trim(), scopes, ct);
        if (r.ExpiresAt is { } exp)
        {
            var tracked = await db.ApiClients.FirstAsync(c => c.Id == created.Id, ct);
            tracked.ExpiresAt = exp;
            await db.SaveChangesAsync(ct);
        }
        var client = await db.ApiClients.AsNoTracking().FirstAsync(c => c.Id == created.Id, ct);
        return new OAuthClientSecretDto(MapClient(client, await UserNamesAsync([client.CreatedById], ct)), created.ClientSecret, TokenEndpoint(baseUrl));
    }

    public async Task<OAuthClientSecretDto> RotateClientSecretAsync(Guid id, string baseUrl, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var client = await db.ApiClients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("OAuth client", id);
        if (!client.IsActive) throw new ConflictException("client_revoked", "This client is revoked. Create a new one instead.");
        EnsureGrantable(Json.StringList(client.ScopesJson));
        var secret = await oauthClients.RotateSecretAsync(id, ct);
        var fresh = await db.ApiClients.AsNoTracking().FirstAsync(c => c.Id == id, ct);
        return new OAuthClientSecretDto(MapClient(fresh, await UserNamesAsync([fresh.CreatedById], ct)), secret, TokenEndpoint(baseUrl));
    }

    /// <summary>Deactivates the client: new tokens are refused and tokens already issued stop working on their next request.</summary>
    public async Task RevokeClientAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var client = await db.ApiClients.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("OAuth client", id);
        if (!client.IsActive) return;
        client.IsActive = false;
        audit.Write(AdminAuditActions.OAuthClientRevoked, "ApiClient", id, $"OAuth client '{client.Name}' revoked ({client.ClientId})");
        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────────── Internals ─────────────────────────────

    private async Task<List<string>> ValidateAsync(string? name, IReadOnlyList<string>? scopes, DateTime? expiresAt, Guid? actAsUserId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        if ((name ?? "").Trim().Length is < 2 or > 100) errors["name"] = "Enter a name of 2–100 characters.";
        var list = (scopes ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();
        if (list.Count == 0) errors["scopes"] = "Choose at least one scope.";
        var unknown = list.Where(s => s != ApiScopes.All && !ApiScopes.Catalog.Contains(s)).ToList();
        if (unknown.Count > 0) errors["scopes"] = $"Unknown scopes: {string.Join(", ", unknown)}.";
        var now = db.Clock.UtcNow;
        if (expiresAt is { } exp && (exp <= now.AddMinutes(5) || exp > now.AddYears(3))) errors["expiresAt"] = "Choose an expiry between now and three years ahead.";
        if (actAsUserId is { } uid && !await db.Users.AnyAsync(u => u.Id == uid && u.IsActive, ct)) errors["actAsUserId"] = "Choose an active user.";
        if (errors.Count > 0) throw new ValidationException(errors);
        EnsureGrantable(list);
        return list;
    }

    /// <summary>No escalation through credentials: the scopes may only grant permissions the caller holds.</summary>
    private void EnsureGrantable(IReadOnlyList<string> scopes)
    {
        var notHeld = RoleRules.NotHeld(ctx.Permissions, ApiScopes.PermissionsFor(scopes));
        if (notHeld.Count > 0)
            throw new DomainException("privilege_escalation", $"These scopes grant permissions you do not have ({string.Join(", ", notHeld.Take(8))}{(notHeld.Count > 8 ? ", …" : "")}).", 403);
    }

    private async Task<Dictionary<Guid, string>> UserNamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
        return list.Count == 0 ? [] : await db.Users.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(u => list.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    private string Status(bool active, DateTime? expires) => !active ? "Revoked" : expires is { } e && e <= db.Clock.UtcNow ? "Expired" : "Active";

    private AdminApiKeyDto MapKey(ApiKey k, IReadOnlyDictionary<Guid, string> names) => new(k.Id, k.Name, k.Prefix, Json.StringList(k.ScopesJson), k.ActAsUserId,
        k.ActAsUserId is { } a ? names.GetValueOrDefault(a) : null, Status(k.IsActive, k.ExpiresAt), k.ExpiresAt, k.LastUsedAt, k.CreatedAt,
        k.CreatedById is { } c ? names.GetValueOrDefault(c) : null, k.RateLimitPerMinute);

    private AdminOAuthClientDto MapClient(ApiClient c, IReadOnlyDictionary<Guid, string> names) => new(c.Id, c.Name, c.ClientId, Json.StringList(c.ScopesJson),
        Status(c.IsActive, c.ExpiresAt), c.ExpiresAt, c.LastUsedAt, c.CreatedAt, c.CreatedById is { } u ? names.GetValueOrDefault(u) : null, c.RateLimitPerMinute);

    private static string TokenEndpoint(string baseUrl) => $"{baseUrl.TrimEnd('/')}/oauth/token";
}
