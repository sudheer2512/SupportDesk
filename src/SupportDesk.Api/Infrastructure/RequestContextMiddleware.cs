using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Identity;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Api.Infrastructure;

/// <summary>
/// Populates the scoped <see cref="RequestContext"/> from the authenticated principal. The tenant always comes from the
/// validated token/API key — never from a request header or body. Revoked sessions and disabled users are rejected,
/// suspended tenants are read-only (writes return 402), and API usage is metered per tenant.
/// </summary>
public sealed class RequestContextMiddleware(RequestDelegate next, ILogger<RequestContextMiddleware> logger)
{
    private static readonly ConcurrentDictionary<Guid, (bool Active, DateTime CheckedAt)> SessionCache = new();

    public async Task InvokeAsync(HttpContext http, RequestContext ctx, PrincipalLoader principals, DeskDbContext db, OAuthClientService clients)
    {
        ctx.CorrelationId = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier;
        ctx.IpAddress = http.Connection.RemoteIpAddress?.ToString();
        ctx.UserAgent = http.Request.Headers.UserAgent.ToString();
        http.Response.Headers["X-Correlation-Id"] = ctx.CorrelationId;

        var user = http.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var type = user.FindFirst(DeskClaims.Type)?.Value;
            Guid.TryParse(user.FindFirst(DeskClaims.Subject)?.Value, out var subject);
            Guid.TryParse(user.FindFirst(DeskClaims.Tenant)?.Value, out var tenantId);
            Guid.TryParse(user.FindFirst(DeskClaims.Session)?.Value, out var sessionId);
            if (tenantId != Guid.Empty) ctx.SetTenant(tenantId, user.FindFirst(DeskClaims.TenantSlug)?.Value);

            if (sessionId != Guid.Empty && !await SessionActiveAsync(db, sessionId, http.RequestAborted))
            {
                await Reject(http, 401, "session_revoked", "Your session has ended. Sign in again.");
                return;
            }

            switch (type)
            {
                case DeskClaims.TypeAgent:
                    if (!await principals.ApplyAgentAsync(tenantId, subject, http.RequestAborted))
                    {
                        await Reject(http, 401, "account_disabled", "Your account is disabled.");
                        return;
                    }
                    break;
                case DeskClaims.TypeContact:
                    ctx.SetContact(tenantId, subject, user.FindFirst(DeskClaims.Name)?.Value ?? "Customer");
                    break;
                case DeskClaims.TypeClient:
                {
                    var scopes = (user.FindFirst(DeskClaims.Scope)?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                    var permissions = ApiScopes.PermissionsFor(scopes);
                    Guid? actAs = Guid.TryParse(user.FindFirst("act_as")?.Value, out var a) ? a : null;
                    if (user.FindFirst("api_key") is null && await clients.LoadAsync(subject, tenantId, scopes.ToList(), http.RequestAborted) is null)
                    {
                        await Reject(http, 401, "client_disabled", "This API client is disabled.");
                        return;
                    }
                    if (actAs is { } actor && await principals.LoadAgentAsync(actor, http.RequestAborted) is { IsActive: true } p)
                    {
                        ctx.SetTenant(tenantId);
                        permissions.IntersectWith(p.Permissions);
                    }
                    ctx.SetApiClient(tenantId, subject, user.FindFirst(DeskClaims.Name)?.Value ?? "API client", scopes, permissions, actAs);
                    break;
                }
                case DeskClaims.TypePlatform:
                    ctx.SetPlatformOperator(subject, user.FindFirst(DeskClaims.Name)?.Value ?? "Platform operator");
                    break;
            }

            if (ctx.HasTenant && !HttpMethods.IsGet(http.Request.Method) && !IsBillingOrAuth(http.Request.Path))
            {
                var status = await db.Tenants.AsNoTracking().Where(t => t.Id == ctx.TenantId).Select(t => t.Status).FirstOrDefaultAsync(http.RequestAborted);
                if (status is TenantStatus.Suspended or TenantStatus.Cancelled or TenantStatus.PendingDeletion)
                {
                    await Reject(http, 402, "tenant_suspended", "This account is suspended. Changes are disabled until the subscription is restored.");
                    return;
                }
            }
        }

        using (logger.BeginScope(new Dictionary<string, object?> { ["TenantId"] = ctx.TenantId, ["UserId"] = ctx.UserId ?? ctx.ContactId, ["CorrelationId"] = ctx.CorrelationId }))
        {
            Activity.Current?.SetTag("tenant.id", ctx.TenantId.ToString());
            await next(http);
        }
    }

    private static bool IsBillingOrAuth(PathString path) =>
        path.StartsWithSegments("/api/v1/subscription") || path.StartsWithSegments("/api/v1/auth") || path.StartsWithSegments("/api/v1/portal");

    private static async Task<bool> SessionActiveAsync(DeskDbContext db, Guid sessionId, CancellationToken ct)
    {
        if (SessionCache.TryGetValue(sessionId, out var c) && DateTime.UtcNow - c.CheckedAt < TimeSpan.FromSeconds(30)) return c.Active;
        var now = DateTime.UtcNow;
        var family = await db.UserSessions.AsNoTracking().Where(s => s.Id == sessionId).Select(s => (Guid?)s.FamilyId).FirstOrDefaultAsync(ct);
        var active = family is not null && await db.UserSessions.AsNoTracking().AnyAsync(s => s.FamilyId == family && s.RevokedAt == null && s.ExpiresAt > now, ct);
        SessionCache[sessionId] = (active, DateTime.UtcNow);
        return active;
    }

    /// <summary>Forgets cached session state (logout on this instance takes effect immediately).</summary>
    public static void Forget(Guid sessionId) => SessionCache.TryRemove(sessionId, out _);

    private static async Task Reject(HttpContext http, int status, string code, string message)
    {
        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(new { type = "about:blank", title = message, status, code }, http.RequestAborted);
    }
}
