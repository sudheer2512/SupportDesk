using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SupportDesk.Application.Platform;
using SupportDesk.Domain;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Identity;

/// <summary>
/// Per-tenant OpenID Connect single sign-on (Authorization Code + PKCE, confidential client).
/// Flow: /auth/sso/{tenant}/start → IdP → /auth/sso/callback → our session → SPA receives a one-time handoff code
/// (60 s) that it exchanges for tokens at /auth/sso/complete. The id_token is validated against the IdP's JWKS.
/// </summary>
public sealed class SsoService(
    DeskDbContext db,
    RequestContext ctx,
    TenantSettingsService settings,
    ISecretProtector secrets,
    IDistributedCache cache,
    IHttpClientFactory httpFactory,
    AuthService auth,
    PrincipalLoader principals,
    TokenService tokens,
    IAuditWriter audit)
{
    private sealed record PendingLogin(Guid TenantId, string Verifier, string Nonce, string RedirectUri, string ReturnUrl);
    private sealed record Handoff(Guid TenantId, Guid UserId, Guid SessionId, string RefreshToken);

    public async Task<string> StartAsync(string tenantSlug, string callbackUrl, string? returnUrl, CancellationToken ct)
    {
        var tenant = await auth.ResolveTenantAsync(tenantSlug, ct) ?? throw new NotFoundException("Organisation");
        ctx.SetTenant(tenant.Id, tenant.Slug);
        var sso = await settings.GetAsync<SsoSettings>(SettingKeys.Sso, ct);
        if (!sso.Enabled || sso.Authority is null || sso.ClientId is null)
            throw new DomainException("sso_not_configured", "Single sign-on is not configured for this organisation.");
        var config = await DiscoverAsync(sso.Authority, ct);
        var verifier = TextNormalization.RandomToken(32);
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = TextNormalization.RandomToken(24);
        var nonce = TextNormalization.RandomToken(16);
        await cache.SetStringAsync($"sso:state:{state}", JsonSerializer.Serialize(new PendingLogin(tenant.Id, verifier, nonce, callbackUrl, SafeReturn(returnUrl))),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) }, ct);
        var q = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = sso.ClientId, ["redirect_uri"] = callbackUrl, ["scope"] = sso.Scopes,
            ["state"] = state, ["nonce"] = nonce, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256"
        };
        return config.AuthorizationEndpoint + "?" + string.Join('&', q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    /// <summary>Handles the IdP callback; returns the SPA URL carrying a one-time handoff code.</summary>
    public async Task<string> CallbackAsync(string code, string state, ClientInfo client, CancellationToken ct)
    {
        var raw = await cache.GetStringAsync($"sso:state:{state}", ct) ?? throw new DomainException("sso_state_invalid", "The sign-in request expired. Try again.");
        await cache.RemoveAsync($"sso:state:{state}", ct);
        var pending = JsonSerializer.Deserialize<PendingLogin>(raw)!;
        ctx.SetTenant(pending.TenantId);
        var sso = await settings.GetAsync<SsoSettings>(SettingKeys.Sso, ct);
        var config = await DiscoverAsync(sso.Authority!, ct);

        using var http = httpFactory.CreateClient("sso");
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = pending.RedirectUri, ["client_id"] = sso.ClientId!,
            ["code_verifier"] = pending.Verifier
        };
        if (sso.ClientSecretProtected is not null) form["client_secret"] = secrets.Unprotect(sso.ClientSecretProtected);
        var response = await http.PostAsync(config.TokenEndpoint, new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode) throw new DomainException("sso_token_failed", "The identity provider rejected the sign-in.");
        var tokenResponse = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var idToken = tokenResponse.GetProperty("id_token").GetString()!;

        var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(idToken, new TokenValidationParameters
        {
            ValidIssuer = config.Issuer,
            ValidAudience = sso.ClientId,
            IssuerSigningKeys = config.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        }, out _);
        if (principal.FindFirst("nonce")?.Value != pending.Nonce) throw new DomainException("sso_nonce", "Sign-in validation failed.");
        var email = TextNormalization.NormalizeEmail(principal.FindFirst(sso.EmailClaim)?.Value ?? principal.FindFirst("preferred_username")?.Value)
                    ?? throw new DomainException("sso_email_missing", "The identity provider did not return an e-mail address.");
        var subject = principal.FindFirst("sub")?.Value ?? email;

        var user = await db.Users.FirstOrDefaultAsync(u => u.ExternalIssuer == config.Issuer && u.ExternalSubject == subject, ct)
                   ?? await db.Users.FirstOrDefaultAsync(u => u.EmailNormalized == email, ct);
        if (user is null)
        {
            if (!sso.AutoProvision) throw new DomainException("sso_user_unknown", "Your account has not been set up in SupportDesk. Ask an administrator.");
            user = new User
            {
                Email = email, EmailNormalized = email, DisplayName = principal.FindFirst(sso.NameClaim)?.Value ?? principal.FindFirst("name")?.Value ?? email, Kind = UserKind.Agent,
                ExternalIssuer = config.Issuer, ExternalSubject = subject
            };
            db.Users.Add(user);
            db.Agents.Add(new Agent { UserId = user.Id });
            if (sso.DefaultRoleId is { } roleId) db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = roleId });
            audit.Write(AuditActions.UserCreated, "User", user.Id, $"User {email} provisioned by single sign-on");
        }
        if (!user.IsActive) throw new DomainException("account_disabled", "Your account is disabled.");
        user.ExternalIssuer ??= config.Issuer;
        user.ExternalSubject ??= subject;
        user.LastLoginAt = db.Clock.UtcNow;
        var (session, refresh) = await auth.CreateSessionAsync(PrincipalKind.User, user.Id, pending.TenantId, client, mfa: true, ct);
        audit.Write(AuditActions.LoginSucceeded, "User", user.Id, $"{user.DisplayName} signed in with SSO", new { client.IpAddress })
            .AttributeTo(ActorType.User, user.Id, user.DisplayName);
        await db.SaveChangesAsync(ct);

        var handoff = TextNormalization.RandomToken(24);
        await cache.SetStringAsync($"sso:handoff:{handoff}", JsonSerializer.Serialize(new Handoff(pending.TenantId, user.Id, session.Id, refresh)),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60) }, ct);
        var sep = pending.ReturnUrl.Contains('?') ? '&' : '?';
        return $"{pending.ReturnUrl}{sep}sso={Uri.EscapeDataString(handoff)}";
    }

    public async Task<AuthResult> CompleteAsync(string handoffCode, CancellationToken ct)
    {
        var raw = await cache.GetStringAsync($"sso:handoff:{handoffCode}", ct);
        if (raw is null) return new AuthResult("invalid", Message: "The sign-in link expired.");
        await cache.RemoveAsync($"sso:handoff:{handoffCode}", ct);
        var h = JsonSerializer.Deserialize<Handoff>(raw)!;
        ctx.SetTenant(h.TenantId);
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == h.UserId, ct);
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == h.TenantId, ct);
        var policy = (await settings.SecurityAsync(ct)).Session;
        var session = await db.UserSessions.AsNoTracking().FirstAsync(s => s.Id == h.SessionId, ct);
        var access = tokens.Issue(DeskClaims.TypeAgent, user.Id, tenant.Id, tenant.Slug, user.DisplayName, TimeSpan.FromMinutes(policy.AccessTokenMinutes),
            h.SessionId, user.Email, mfa: true);
        return new AuthResult("ok", new SessionTokens(access.Token, access.ExpiresAt, h.RefreshToken, session.ExpiresAt, h.SessionId),
            await principals.BuildMeAsync(user.Id, ct));
    }

    private static async Task<OpenIdConnectConfiguration> DiscoverAsync(string authority, CancellationToken ct)
    {
        var manager = new ConfigurationManager<OpenIdConnectConfiguration>(
            authority.TrimEnd('/') + "/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever { RequireHttps = true });
        return await manager.GetConfigurationAsync(ct);
    }

    private static string SafeReturn(string? returnUrl) =>
        string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//") ? "/login" : returnUrl;
}
