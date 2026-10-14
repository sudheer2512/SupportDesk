using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Application.Identity;
using SupportDesk.Contracts.Auth;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Api.Controllers;

/// <summary>Agent/administrator authentication, MFA, sessions, password management and single sign-on.</summary>
[Route("api/v1/auth")]
public sealed class AuthController(AuthService auth, SsoService sso, RequestContext ctx) : DeskControllerBase
{
    public const string RefreshCookie = "sd_rt";
    public const string RefreshPath = "/api/v1/auth";

    [HttpPost("login"), AllowAnonymous, EnableRateLimiting(HostingSetup.AuthLimiter)]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken ct) =>
        ToResponse(await auth.LoginAsync(request, Client, ct));

    [HttpPost("mfa"), AllowAnonymous, EnableRateLimiting(HostingSetup.AuthLimiter)]
    public async Task<ActionResult<LoginResponse>> Mfa(MfaVerifyRequest request, CancellationToken ct) =>
        ToResponse(await auth.VerifyMfaAsync(request, Client, ct));

    [HttpPost("refresh"), AllowAnonymous, EnableRateLimiting(HostingSetup.AuthLimiter)]
    public async Task<ActionResult<LoginResponse>> Refresh([FromBody] RefreshRequest? request, CancellationToken ct)
    {
        var token = request?.RefreshToken;
        if (token is null)
        {
            if (!HasAntiForgeryHeader) return Problem(statusCode: 400, title: "Missing X-Requested-With header.");
            token = Request.Cookies[RefreshCookie];
        }
        if (string.IsNullOrEmpty(token)) return Unauthorized(new LoginResponse("invalid", Message: "Sign in again."));
        return ToResponse(await auth.RefreshAsync(token, ct));
    }

    [HttpPost("logout"), AllowAnonymous]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest? request, CancellationToken ct)
    {
        Guid? sid = Guid.TryParse(User.FindFirst(DeskClaims.Session)?.Value, out var s) ? s : null;
        // Cookie-only logout (no bearer, no body token) needs the same anti-forgery header as refresh: no CSRF logout.
        if (sid is null && request?.RefreshToken is null && Request.Cookies.ContainsKey(RefreshCookie) && !HasAntiForgeryHeader)
            return Problem(statusCode: 400, title: "Missing X-Requested-With header.");
        if (ctx.HasTenant || sid is not null || request?.RefreshToken is not null || Request.Cookies.ContainsKey(RefreshCookie))
            await auth.LogoutAsync(sid, request?.RefreshToken ?? Request.Cookies[RefreshCookie], ct);
        if (sid is { } id) RequestContextMiddleware.Forget(id);
        ClearRefreshCookie(RefreshCookie, RefreshPath);
        return NoContent();
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        await auth.ChangePasswordAsync(ctx.UserId!.Value, request, ct);
        return NoContent();
    }

    public sealed record ExpiredPasswordRequest(string Tenant, string Email, string CurrentPassword, string NewPassword);

    [HttpPost("change-expired-password"), AllowAnonymous, EnableRateLimiting(HostingSetup.AuthLimiter)]
    public async Task<ActionResult<LoginResponse>> ChangeExpired(ExpiredPasswordRequest r, CancellationToken ct) =>
        ToResponse(await auth.ChangeExpiredPasswordAsync(r.Tenant, r.Email, r.CurrentPassword, r.NewPassword, Client, ct));

    [HttpPost("password-reset"), AllowAnonymous, EnableRateLimiting(HostingSetup.AuthLimiter)]
    public async Task<IActionResult> RequestReset(PasswordResetRequest request, CancellationToken ct)
    {
        var demo = await auth.RequestPasswordResetAsync(request, ct);
        return Ok(new { status = "sent", message = "If the account exists, a reset code was e-mailed.", demoCode = demo });
    }

    [HttpPost("password-reset/confirm"), AllowAnonymous, EnableRateLimiting(HostingSetup.AuthLimiter)]
    public async Task<IActionResult> ConfirmReset(PasswordResetConfirmRequest request, CancellationToken ct)
    {
        await auth.ConfirmPasswordResetAsync(request, ct);
        return NoContent();
    }

    [HttpPost("mfa/setup")]
    public Task<MfaSetupDto> MfaSetup(CancellationToken ct) => auth.BeginMfaSetupAsync(ctx.UserId!.Value, ct);

    [HttpPost("mfa/enable")]
    public async Task<IActionResult> MfaEnable(MfaEnableRequest request, CancellationToken ct)
    {
        await auth.EnableMfaAsync(ctx.UserId!.Value, request.Code, ct);
        return NoContent();
    }

    [HttpPost("mfa/disable")]
    public async Task<IActionResult> MfaDisable(CancellationToken ct)
    {
        await auth.DisableMfaAsync(ctx.UserId!.Value, ct);
        return NoContent();
    }

    // ───── Single sign-on (OpenID Connect) ─────

    [HttpGet("sso/{tenant}/start"), AllowAnonymous, EnableRateLimiting(HostingSetup.AuthLimiter)]
    public async Task<IActionResult> SsoStart(string tenant, [FromQuery] string? returnUrl, CancellationToken ct)
    {
        var callback = $"{Request.Scheme}://{Request.Host}/api/v1/auth/sso/callback";
        return Redirect(await sso.StartAsync(tenant, callback, returnUrl ?? "/login", ct));
    }

    [HttpGet("sso/callback"), AllowAnonymous]
    public async Task<IActionResult> SsoCallback([FromQuery] string code, [FromQuery] string state, CancellationToken ct) =>
        Redirect(await sso.CallbackAsync(code, state, Client, ct));

    public sealed record SsoCompleteRequest(string Code);

    [HttpPost("sso/complete"), AllowAnonymous, EnableRateLimiting(HostingSetup.AuthLimiter)]
    public async Task<ActionResult<LoginResponse>> SsoComplete(SsoCompleteRequest request, CancellationToken ct) =>
        ToResponse(await sso.CompleteAsync(request.Code, ct));

    private ActionResult<LoginResponse> ToResponse(AuthResult r)
    {
        if (r.Status == LoginStatus.Ok && r.Tokens is { } t)
        {
            if (!IsMobileClient) SetRefreshCookie(RefreshCookie, RefreshPath, t.RefreshToken, t.RefreshExpiresAt);
            return Ok(new LoginResponse(LoginStatus.Ok, t.AccessToken, t.AccessExpiresAt, IsMobileClient ? t.RefreshToken : null, r.Me));
        }
        if (r.Status == LoginStatus.MfaRequired)
            return Ok(new LoginResponse(r.Status, ChallengeId: r.ChallengeId, Methods: r.Methods, DemoCode: r.DemoCode, Message: r.Message));
        if (r.Status == LoginStatus.PasswordExpired) return Ok(new LoginResponse(r.Status, Message: r.Message));
        if (r.Status == "locked") return StatusCode(423, new LoginResponse(r.Status, Message: r.Message));
        if (r.Status is LoginStatus.IpNotAllowed or LoginStatus.SsoRequired) return StatusCode(403, new LoginResponse(r.Status, Message: r.Message));
        ClearRefreshCookie(RefreshCookie, RefreshPath);
        return Unauthorized(new LoginResponse(r.Status, Message: r.Message));
    }
}

/// <summary>OAuth 2.0 client-credentials token endpoint and the public signing keys (JWKS / discovery).</summary>
[ApiController]
public sealed class OAuthController(OAuthClientService clients, SigningKeyProvider keys) : ControllerBase
{
    [HttpPost("/oauth/token"), AllowAnonymous, EnableRateLimiting(HostingSetup.AuthLimiter), Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Token([FromForm(Name = "grant_type")] string grantType, [FromForm(Name = "client_id")] string? clientId,
        [FromForm(Name = "client_secret")] string? clientSecret, [FromForm(Name = "scope")] string? scope, CancellationToken ct)
    {
        if (grantType != "client_credentials") return BadRequest(new { error = "unsupported_grant_type" });
        if (clientId is null || clientSecret is null)
        {
            // Also accept HTTP Basic client authentication.
            var auth = Request.Headers.Authorization.ToString();
            if (auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..])).Split(':', 2);
                clientId = Uri.UnescapeDataString(decoded[0]);
                clientSecret = decoded.Length > 1 ? Uri.UnescapeDataString(decoded[1]) : null;
            }
        }
        if (clientId is null || clientSecret is null) return BadRequest(new { error = "invalid_request" });
        var token = await clients.IssueTokenAsync(clientId, clientSecret, scope, ct);
        return token is null ? Unauthorized(new { error = "invalid_client" }) : Ok(token);
    }

    [HttpGet("/.well-known/jwks.json"), AllowAnonymous]
    public IActionResult Jwks() => Ok(keys.Jwks());

    [HttpGet("/.well-known/openid-configuration"), AllowAnonymous]
    public IActionResult Discovery()
    {
        var issuerBase = $"{Request.Scheme}://{Request.Host}";
        return Ok(new
        {
            issuer = SigningKeyProvider.Issuer,
            token_endpoint = $"{issuerBase}/oauth/token",
            jwks_uri = $"{issuerBase}/.well-known/jwks.json",
            grant_types_supported = new[] { "client_credentials" },
            token_endpoint_auth_methods_supported = new[] { "client_secret_post", "client_secret_basic" },
            scopes_supported = ApiScopes.Catalog
        });
    }
}

/// <summary>Platform operator sign-in (SaaS administration console).</summary>
[ApiController, Route("api/v1/platform/auth")]
public sealed class PlatformAuthController(PlatformAuthService platform) : ControllerBase
{
    [HttpPost("login"), AllowAnonymous, EnableRateLimiting(HostingSetup.AuthLimiter)]
    public async Task<ActionResult<PlatformLoginResponse>> Login(PlatformLoginRequest request, CancellationToken ct)
    {
        var result = await platform.LoginAsync(request, ct);
        return result.Status is "ok" or "mfa_required" ? Ok(result) : Unauthorized(result);
    }
}
