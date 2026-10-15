using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Application.Identity;

namespace SupportDesk.Api.Controllers;

/// <summary>Base for agent-facing API controllers: agent policy, API rate limiting, JSON in/out.</summary>
[ApiController]
[Authorize(Policy = Policies.Agent)]
[EnableRateLimiting(HostingSetup.ApiLimiter)]
[Produces("application/json")]
public abstract class DeskControllerBase : ControllerBase
{
    protected ClientInfo Client => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(),
        Request.Headers.TryGetValue("X-Client", out var c) ? c.ToString() : "web");

    protected bool IsMobileClient => string.Equals(Request.Headers["X-Client"].ToString(), "mobile", StringComparison.OrdinalIgnoreCase);

    protected void SetRefreshCookie(string name, string path, string token, DateTime expires) =>
        Response.Cookies.Append(name, token, new CookieOptions
        {
            HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Strict, Path = AppPath(path), Expires = expires, IsEssential = true
        });

    protected void ClearRefreshCookie(string name, string path) =>
        Response.Cookies.Delete(name, new CookieOptions { Path = AppPath(path), Secure = Request.IsHttps, SameSite = SameSiteMode.Strict, HttpOnly = true });

    /// <summary>An app-relative path ("/api/v1/auth") under the request's path base (IIS virtual directory, proxy sub-path).</summary>
    protected string AppPath(string path) => Request.PathBase.Add(new PathString(path)).Value ?? path;

    /// <summary>Redirect target: root-relative URLs ("/login?…") are resolved under the path base; absolute URLs pass through.</summary>
    protected string AppUrl(string url) => url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) ? AppPath(url) : url;

    /// <summary>Cookie-borne refresh requests must carry a custom header that cross-site forms cannot set (CSRF defence).</summary>
    protected bool HasAntiForgeryHeader => string.Equals(Request.Headers["X-Requested-With"].ToString(), "SupportDesk", StringComparison.Ordinal);
}
