using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using SupportDesk.Application.Identity;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Administration;
using SupportDesk.Contracts.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Administration;

/// <summary>Agent IP allow-list (tenant setting "security.ip_allowlist").</summary>
public sealed record IpAllowListSettings
{
    public bool Enabled { get; init; }
    public List<string> Ranges { get; init; } = [];
}

/// <summary>Pure IP range matching: single addresses or CIDR blocks, IPv4 and IPv6 (IPv4-mapped IPv6 addresses match IPv4 rules).</summary>
public static class IpRanges
{
    public static bool TryParse(string? text, out IPNetwork network)
    {
        network = default;
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return false;
        if (t.Contains('/')) return IPNetwork.TryParse(t, out network);
        if (!IPAddress.TryParse(t, out var ip)) return false;
        ip = Normalize(ip);
        network = new IPNetwork(ip, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        return true;
    }

    public static bool Matches(string? address, IEnumerable<string> ranges)
    {
        if (!IPAddress.TryParse(address, out var ip)) return false;
        ip = Normalize(ip);
        foreach (var r in ranges)
            if (TryParse(r, out var net) && net.Contains(ip)) return true;
        return false;
    }

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}

/// <summary>
/// Enforces the agent IP allow-list at sign-in and on every agent request (RequestContextMiddleware). Platform operators
/// and API clients are not subject to it. The rules are cached per tenant for 60 s and invalidated when saved.
/// </summary>
public sealed class IpAccessPolicy(DeskDbContext db, ITenantCache cache)
{
    private const string CacheKey = "admin:ip-allowlist";

    public async Task<bool> IsAllowedAsync(string? ipAddress, CancellationToken ct)
    {
        if (!db.Request.HasTenant) return true;
        var rules = await cache.GetOrCreateAsync(CacheKey, async () =>
        {
            var json = await db.TenantSettings.AsNoTracking().Where(s => s.Key == AdminSettingKeys.IpAllowList).Select(s => s.ValueJson).FirstOrDefaultAsync(ct);
            return Common.Json.DeserializeOr(json, new IpAllowListSettings());
        }, TimeSpan.FromSeconds(60), ct);
        return !rules.Enabled || rules.Ranges.Count == 0 || IpRanges.Matches(ipAddress, rules.Ranges);
    }

    public Task InvalidateAsync(CancellationToken ct) => cache.RemoveAsync(CacheKey, ct);
}

/// <summary>
/// Security administration (admin.security): MFA policy, password and session policies, agent IP allow-list (with
/// anti-lockout check), OpenID Connect SSO configuration (write-only secret, discovery test) and active sessions.
/// </summary>
public sealed class SecurityAdminService(
    DeskDbContext db,
    RequestContext ctx,
    IAuditWriter audit,
    AdminSettingsStore store,
    IpAccessPolicy ipPolicy,
    ISecretProtector secrets,
    LimitGuard limits,
    PrincipalLoader principals)
{
    public const string MfaOptional = "Optional";
    public const string MfaAdmins = "RequiredForAdmins";
    public const string MfaAll = "RequiredForAll";

    // ───────────────────────────── Policies ─────────────────────────────

    public async Task<SecurityPolicyDto> GetPolicyAsync(CancellationToken ct)
    {
        ctx.Require(Permissions.AdminSecurity);
        var (s, version) = await store.GetAsync<SecuritySettings>(SettingKeys.Security, ct);
        var (ip, ipVersion) = await store.GetAsync<IpAllowListSettings>(AdminSettingKeys.IpAllowList, ct);
        var p = s.Password;
        var se = s.Session;
        return new SecurityPolicyDto(s.RequireMfaForAll ? MfaAll : s.RequireMfaForAdmins ? MfaAdmins : MfaOptional,
            new PasswordPolicyDto(p.MinLength, p.RequireUpper, p.RequireLower, p.RequireDigit, p.RequireSymbol, p.HistoryCount, p.ExpiryDays, p.MaxFailedAttempts, p.LockoutMinutes),
            new SessionPolicyDto(se.AccessTokenMinutes, se.IdleTimeoutMinutes, se.AbsoluteLifetimeHours, se.MaxConcurrentSessions),
            new IpAllowListDto(ip.Enabled, ip.Ranges), ctx.IpAddress, version + ipVersion);
    }

    public async Task<SecurityPolicyDto> SavePolicyAsync(SaveSecurityPolicyRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminSecurity);
        var errors = new Dictionary<string, string>();
        if (r.MfaPolicy is not (MfaOptional or MfaAdmins or MfaAll)) errors["mfaPolicy"] = "Choose Optional, RequiredForAdmins or RequiredForAll.";
        var p = r.Password;
        if (p is null) errors["password"] = "The password policy is required.";
        else
        {
            if (p.MinLength is < 8 or > 128) errors["password.minLength"] = "Between 8 and 128 characters.";
            if (p.HistoryCount is < 0 or > 24) errors["password.historyCount"] = "Between 0 and 24 previous passwords.";
            if (p.ExpiryDays is < 0 or > 365) errors["password.expiryDays"] = "Between 0 (never) and 365 days.";
            if (p.MaxFailedAttempts is < 3 or > 20) errors["password.maxFailedAttempts"] = "Between 3 and 20 attempts.";
            if (p.LockoutMinutes is < 1 or > 1440) errors["password.lockoutMinutes"] = "Between 1 and 1440 minutes.";
        }
        var se = r.Session;
        if (se is null) errors["session"] = "The session policy is required.";
        else
        {
            if (se.AccessTokenMinutes is < 5 or > 60) errors["session.accessTokenMinutes"] = "Between 5 and 60 minutes.";
            if (se.IdleTimeoutMinutes is < 5 or > 10080) errors["session.idleTimeoutMinutes"] = "Between 5 minutes and 7 days.";
            if (se.AbsoluteLifetimeHours is < 1 or > 720) errors["session.absoluteLifetimeHours"] = "Between 1 and 720 hours.";
            if (se.MaxConcurrentSessions is < 1 or > 50) errors["session.maxConcurrentSessions"] = "Between 1 and 50 sessions.";
            if (se.IdleTimeoutMinutes > se.AbsoluteLifetimeHours * 60) errors["session.idleTimeoutMinutes"] = "The idle timeout cannot exceed the absolute lifetime.";
        }
        var ranges = (r.IpAllowList?.Ranges ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var bad = ranges.Where(x => !IpRanges.TryParse(x, out _)).ToList();
        if (bad.Count > 0) errors["ipAllowList"] = $"Not an IP address or CIDR range: {string.Join(", ", bad)}.";
        else if (ranges.Count > 100) errors["ipAllowList"] = "At most 100 entries.";
        var enabled = r.IpAllowList?.Enabled == true;
        if (enabled && ranges.Count == 0 && !errors.ContainsKey("ipAllowList")) errors["ipAllowList"] = "Add at least one address or range before switching the allow-list on.";
        else if (enabled && !errors.ContainsKey("ipAllowList") && !IpRanges.Matches(ctx.IpAddress, ranges))
            errors["ipAllowList"] = $"Your current address ({ctx.IpAddress ?? "unknown"}) is not in the list — saving would lock you out. Add it first.";
        if (errors.Count > 0) throw new ValidationException(errors);

        var (current, _) = await store.GetAsync<SecuritySettings>(SettingKeys.Security, ct);
        var next = current with
        {
            RequireMfaForAdmins = r.MfaPolicy is MfaAdmins or MfaAll,
            RequireMfaForAll = r.MfaPolicy == MfaAll,
            Password = new PasswordPolicy
            {
                MinLength = p!.MinLength, RequireUpper = p.RequireUpper, RequireLower = p.RequireLower, RequireDigit = p.RequireDigit, RequireSymbol = p.RequireSymbol,
                HistoryCount = p.HistoryCount, ExpiryDays = p.ExpiryDays, MaxFailedAttempts = p.MaxFailedAttempts, LockoutMinutes = p.LockoutMinutes
            },
            Session = new SessionPolicy
            {
                AccessTokenMinutes = se!.AccessTokenMinutes, IdleTimeoutMinutes = se.IdleTimeoutMinutes, AbsoluteLifetimeHours = se.AbsoluteLifetimeHours,
                MaxConcurrentSessions = se.MaxConcurrentSessions
            }
        };
        var changed = await store.StageAsync(SettingKeys.Security, next, "Security policy changed", ct);
        var ipChanged = await store.StageAsync(AdminSettingKeys.IpAllowList, new IpAllowListSettings { Enabled = enabled, Ranges = ranges }, "Agent IP allow-list changed", ct);
        if (changed || ipChanged)
        {
            audit.Write(AdminAuditActions.SecurityPolicyChanged, "Tenant", ctx.TenantId, $"Security policy saved (MFA: {r.MfaPolicy}, IP allow-list: {(enabled ? $"{ranges.Count} range(s)" : "off")})");
            await db.SaveChangesAsync(ct);
            await ipPolicy.InvalidateAsync(ct);
        }
        return await GetPolicyAsync(ct);
    }

    // ───────────────────────────── Single sign-on ─────────────────────────────

    public async Task<SsoConfigDto> GetSsoAsync(string baseUrl, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminSecurity);
        var (s, _) = await store.GetAsync<SsoSettings>(SettingKeys.Sso, ct);
        return MapSso(s, baseUrl, await limits.HasFeatureAsync(PlanFeatures.Sso, ct));
    }

    public async Task<SsoConfigDto> SaveSsoAsync(SaveSsoConfigRequest r, string baseUrl, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminSecurity);
        var errors = new Dictionary<string, string>();
        var authority = AdminRules.Clean(r.Authority, 500)?.TrimEnd('/');
        var clientId = AdminRules.Clean(r.ClientId, 300);
        if (authority is not null && (!Uri.TryCreate(authority, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps))
            errors["authority"] = "Enter the issuer's https:// address (its /.well-known/openid-configuration must be reachable).";
        if (r.Enabled)
        {
            if (authority is null) errors["authority"] = "The authority is required to switch SSO on.";
            if (clientId is null) errors["clientId"] = "The client id is required to switch SSO on.";
        }
        var scopes = string.Join(' ', (r.Scopes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct());
        if (!scopes.Split(' ').Contains("openid")) errors["scopes"] = "The scopes must include openid.";
        var emailClaim = AdminRules.Clean(r.EmailClaim, 100) ?? "email";
        var nameClaim = AdminRules.Clean(r.NameClaim, 100) ?? "name";
        if (r.EnforceSso && !r.Enabled) errors["enforceSso"] = "SSO must be on to require it.";
        if (r.DefaultRoleId is { } roleId)
        {
            var role = await db.Roles.AsNoTracking().Include(x => x.Permissions).FirstOrDefaultAsync(x => x.Id == roleId, ct);
            if (role is null) errors["defaultRoleId"] = "Unknown role.";
            else if (RoleRules.NotHeld(ctx.Permissions, role.Permissions.Select(x => x.PermissionCode)).Count > 0)
                errors["defaultRoleId"] = "You cannot give new users a role with permissions you do not have.";
        }
        if (r.AutoProvision && r.DefaultRoleId is null) errors["defaultRoleId"] = "Choose the role given to users created at their first sign-in.";
        if (r.ClientSecret is { Length: > 2000 }) errors["clientSecret"] = "The client secret is too long.";
        if (errors.Count > 0) throw new ValidationException(errors);
        if (r.Enabled) await limits.RequireFeatureAsync(PlanFeatures.Sso, ct);

        var (current, _) = await store.GetAsync<SsoSettings>(SettingKeys.Sso, ct);
        var secret = r.ClearClientSecret ? null : string.IsNullOrEmpty(r.ClientSecret) ? current.ClientSecretProtected : secrets.Protect(r.ClientSecret);
        var next = current with
        {
            Enabled = r.Enabled, Authority = authority, ClientId = clientId, ClientSecretProtected = secret, Scopes = scopes, EmailClaim = emailClaim, NameClaim = nameClaim,
            AutoProvision = r.AutoProvision, DefaultRoleId = r.DefaultRoleId, EnforceSso = r.EnforceSso
        };
        // The stored value holds the encrypted secret: audit a redacted view instead of the raw setting diff.
        if (await store.StageAsync(SettingKeys.Sso, next, "Single sign-on configuration changed", ct, auditValues: false))
        {
            audit.Write(AdminAuditActions.SsoChanged, "Tenant", ctx.TenantId, $"Single sign-on {(r.Enabled ? "enabled" : "disabled")}" + (r.ClientSecret is { Length: > 0 } ? "; client secret replaced" : r.ClearClientSecret ? "; client secret removed" : ""),
                new { authority, clientId, scopes, emailClaim, nameClaim, r.AutoProvision, r.DefaultRoleId, r.EnforceSso });
            await db.SaveChangesAsync(ct);
        }
        return MapSso(next, baseUrl, await limits.HasFeatureAsync(PlanFeatures.Sso, ct));
    }

    /// <summary>Fetches the provider's discovery document and signing keys (no user sign-in).</summary>
    public async Task<SsoTestResultDto> TestSsoAsync(string? authorityOverride, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminSecurity);
        var (s, _) = await store.GetAsync<SsoSettings>(SettingKeys.Sso, ct);
        var authority = AdminRules.Clean(authorityOverride, 500)?.TrimEnd('/') ?? s.Authority;
        if (authority is null || !Uri.TryCreate(authority, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
            return new SsoTestResultDto(false, null, null, null, 0, "Enter an https:// authority first.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var manager = new ConfigurationManager<OpenIdConnectConfiguration>(authority + "/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever { RequireHttps = true });
            var config = await manager.GetConfigurationAsync(timeout.Token);
            var ok = !string.IsNullOrEmpty(config.AuthorizationEndpoint) && !string.IsNullOrEmpty(config.TokenEndpoint) && config.SigningKeys.Count > 0;
            return new SsoTestResultDto(ok, config.Issuer, config.AuthorizationEndpoint, config.TokenEndpoint, config.SigningKeys.Count,
                ok ? "The identity provider's discovery document and signing keys were loaded." : "The discovery document is incomplete (missing endpoints or signing keys).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new SsoTestResultDto(false, null, null, null, 0, $"Could not load {authority}/.well-known/openid-configuration: {ex.GetBaseException().Message}");
        }
    }

    private SsoConfigDto MapSso(SsoSettings s, string baseUrl, bool planIncludesSso) => new(s.Enabled, s.Authority, s.ClientId, s.ClientSecretProtected is not null, s.Scopes,
        s.EmailClaim, s.NameClaim, s.AutoProvision, s.DefaultRoleId, s.EnforceSso, $"{baseUrl.TrimEnd('/')}/api/v1/auth/sso/callback",
        $"{baseUrl.TrimEnd('/')}/api/v1/auth/sso/{ctx.TenantSlug}/start", planIncludesSso);

    // ───────────────────────────── Sessions ─────────────────────────────

    /// <summary>Active agent sessions, most recently used first (keyset paging; cursor = "{lastSeenTicks}_{id}").</summary>
    public async Task<PagedResult<AdminSessionDto>> ListSessionsAsync(Guid? userId, string? cursor, int limit, Guid? currentSessionId, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminSecurity);
        var now = db.Clock.UtcNow;
        limit = Math.Clamp(limit, 1, 200);
        var tenant = ctx.TenantId;
        var q = db.UserSessions.AsNoTracking().Where(s => s.TenantId == tenant && s.PrincipalKind == PrincipalKind.User && s.RevokedAt == null
                                                          && s.ReplacedByHash == null && s.ExpiresAt > now && s.IdleExpiresAt > now);
        if (userId is { } u) q = q.Where(s => s.PrincipalId == u);
        if (!string.IsNullOrEmpty(cursor))
        {
            var parts = cursor.Split('_');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) || !Guid.TryParse(parts[1], out var lastId))
                throw new ValidationException(new Dictionary<string, string> { ["cursor"] = "Invalid cursor." });
            var at = new DateTime(ticks, DateTimeKind.Utc);
            q = q.Where(s => s.LastSeenAt < at || (s.LastSeenAt == at && s.Id.CompareTo(lastId) < 0));
        }
        var rows = await q.OrderByDescending(s => s.LastSeenAt).ThenByDescending(s => s.Id).Take(limit + 1).ToListAsync(ct);
        var page = rows.Take(limit).ToList();
        var userIds = page.Select(s => s.PrincipalId).Distinct().ToList();
        var users = await db.Users.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(x => userIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => new { x.DisplayName, x.Email }, ct);
        var currentFamily = currentSessionId is { } cs
            ? await db.UserSessions.AsNoTracking().Where(s => s.Id == cs && s.TenantId == tenant).Select(s => (Guid?)s.FamilyId).FirstOrDefaultAsync(ct)
            : null;
        var items = page.Select(s => new AdminSessionDto(s.Id, s.PrincipalId, users.GetValueOrDefault(s.PrincipalId)?.DisplayName ?? "Unknown user",
            users.GetValueOrDefault(s.PrincipalId)?.Email, s.ClientKind, s.IpAddress, s.UserAgent, s.CreatedAt, s.LastSeenAt, s.ExpiresAt, s.MfaSatisfied,
            currentFamily is not null && s.FamilyId == currentFamily)).ToList();
        var next = rows.Count > limit ? $"{page[^1].LastSeenAt.Ticks}_{page[^1].Id}" : null;
        return new PagedResult<AdminSessionDto>(items, next);
    }

    /// <summary>Revokes the session's whole refresh-token family. Returns the revoked session ids (the API forgets them immediately).</summary>
    public async Task<IReadOnlyList<Guid>> RevokeSessionAsync(Guid sessionId, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminSecurity);
        var tenant = ctx.TenantId;
        var session = await db.UserSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId && s.TenantId == tenant && s.PrincipalKind == PrincipalKind.User, ct)
                      ?? throw new NotFoundException("Session", sessionId);
        var ids = await RevokeWhereAsync(s => s.FamilyId == session.FamilyId, ct);
        var name = await db.Users.AsNoTracking().Where(u => u.Id == session.PrincipalId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
        audit.Write(AuditActions.SessionRevoked, "User", session.PrincipalId, $"Session of {name ?? "a user"} revoked by an administrator", new { sessionId, session.IpAddress, session.ClientKind });
        await db.SaveChangesAsync(ct);
        return ids;
    }

    /// <summary>Signs a user out everywhere.</summary>
    public async Task<IReadOnlyList<Guid>> RevokeUserSessionsAsync(Guid userId, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminSecurity);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException("User", userId);
        var ids = await RevokeWhereAsync(s => s.PrincipalId == userId, ct);
        audit.Write(AuditActions.SessionRevoked, "User", userId, $"All sessions of {user.DisplayName} revoked by an administrator ({ids.Count})");
        await db.SaveChangesAsync(ct);
        await principals.InvalidateAsync(userId, ct);
        return ids;
    }

    private async Task<List<Guid>> RevokeWhereAsync(System.Linq.Expressions.Expression<Func<UserSession, bool>> filter, CancellationToken ct)
    {
        var tenant = ctx.TenantId;
        var now = db.Clock.UtcNow;
        var sessions = await db.UserSessions.Where(s => s.TenantId == tenant && s.PrincipalKind == PrincipalKind.User && s.RevokedAt == null).Where(filter).ToListAsync(ct);
        foreach (var s in sessions)
        {
            s.RevokedAt = now;
            s.RevokedReason = "revoked by administrator";
        }
        return sessions.Select(s => s.Id).ToList();
    }
}
