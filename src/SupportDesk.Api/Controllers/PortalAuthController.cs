using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Application.Common;
using SupportDesk.Application.Identity;
using SupportDesk.Contracts.Auth;
using SupportDesk.Contracts.Portal;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;

namespace SupportDesk.Api.Controllers;

/// <summary>Customer portal configuration and contact authentication. The brand slug is resolved server-side to its tenant.</summary>
[Route("api/v1/portal/{brand}")]
[AllowAnonymous]
[EnableRateLimiting(HostingSetup.AuthLimiter)]
public sealed class PortalAuthController(PortalAuthService portal, AuthService auth) : DeskControllerBase
{
    public const string RefreshCookie = "sd_prt";
    public const string RefreshPath = "/api/v1/portal";

    [HttpGet("config"), EnableRateLimiting(HostingSetup.PublicLimiter)]
    public async Task<ActionResult<PortalConfigDto>> Config(string brand, CancellationToken ct)
    {
        var b = await portal.ResolveBrandAsync(brand, ct) ?? throw new NotFoundException("Help center", brand);
        return new PortalConfigDto(b.Slug, b.Name, b.LogoUrl, b.PrimaryColor, b.AccentColor, b.PortalTitle, b.PortalWelcome, b.FooterHtml, b.SupportEmail,
            b.SupportPhone, Json.StringList(b.SupportedLocalesJson),
            new PortalFeaturesDto(b.KnowledgeBaseEnabled, b.CommunityEnabled, b.ChatEnabled, b.AiAssistantEnabled, b.AllowAnonymousTickets, b.AllowSelfRegistration),
            b.PortalAuthMode.ToString());
    }

    [HttpPost("auth/login")]
    public async Task<ActionResult<PortalLoginResponse>> Login(string brand, PortalLoginRequest request, CancellationToken ct) =>
        ToResponse(await portal.LoginAsync(brand, request, Client, ct));

    [HttpPost("auth/otp/request")]
    public async Task<ActionResult<PortalLoginResponse>> RequestOtp(string brand, PortalOtpRequest request, CancellationToken ct)
    {
        var r = await portal.RequestOtpAsync(brand, request, ct);
        return Ok(new PortalLoginResponse(r.Status, null, null, null, null, r.DemoCode, r.Message));
    }

    [HttpPost("auth/otp/verify")]
    public async Task<ActionResult<PortalLoginResponse>> VerifyOtp(string brand, PortalOtpVerifyRequest request, CancellationToken ct) =>
        ToResponse(await portal.VerifyOtpAsync(brand, request, Client, ct));

    [HttpPost("auth/register")]
    public async Task<ActionResult<PortalLoginResponse>> Register(string brand, PortalRegisterRequest request, CancellationToken ct) =>
        ToResponse(await portal.RegisterAsync(brand, request, Client, ct));

    [HttpPost("auth/refresh")]
    public async Task<ActionResult<PortalLoginResponse>> Refresh(string brand, [FromBody] RefreshRequest? request, CancellationToken ct)
    {
        var token = request?.RefreshToken;
        if (token is null)
        {
            if (!HasAntiForgeryHeader) return Problem(statusCode: 400, title: "Missing X-Requested-With header.");
            token = Request.Cookies[RefreshCookie];
        }
        if (string.IsNullOrEmpty(token)) return Unauthorized(new PortalLoginResponse("invalid", null, null, null, null, Message: "Sign in again."));
        return ToResponse(await portal.RefreshAsync(brand, token, ct));
    }

    [HttpPost("auth/logout")]
    public async Task<IActionResult> Logout(string brand, [FromBody] RefreshRequest? request, CancellationToken ct)
    {
        // Cookie-borne logout needs the anti-forgery header (as refresh does): no CSRF logout.
        if (request?.RefreshToken is null && Request.Cookies.ContainsKey(RefreshCookie) && !HasAntiForgeryHeader)
            return Problem(statusCode: 400, title: "Missing X-Requested-With header.");
        var token = request?.RefreshToken ?? Request.Cookies[RefreshCookie];
        if (token is not null && await portal.ResolveBrandAsync(brand, ct) is not null) await auth.LogoutAsync(null, token, ct);
        ClearRefreshCookie(RefreshCookie, RefreshPath);
        return NoContent();
    }

    private ActionResult<PortalLoginResponse> ToResponse(PortalAuthResult r)
    {
        if (r.Status == "ok" && r.Tokens is { } t)
        {
            if (!IsMobileClient) SetRefreshCookie(RefreshCookie, RefreshPath, t.RefreshToken, t.RefreshExpiresAt);
            return Ok(new PortalLoginResponse("ok", t.AccessToken, t.AccessExpiresAt, IsMobileClient ? t.RefreshToken : null, r.Contact));
        }
        if (r.Status is "sent" or "exists") return Ok(new PortalLoginResponse(r.Status, null, null, null, null, r.DemoCode, r.Message));
        return Unauthorized(new PortalLoginResponse(r.Status, null, null, null, null, Message: r.Message));
    }
}
