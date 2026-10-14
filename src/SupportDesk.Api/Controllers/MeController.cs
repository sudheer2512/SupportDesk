using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Api.Realtime;
using SupportDesk.Application.Identity;
using SupportDesk.Application.Notifications;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Auth;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Organisation;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Api.Controllers;

/// <summary>The signed-in agent: profile, availability, sessions and notification preferences.</summary>
[Route("api/v1/me")]
public sealed class MeController(DeskDbContext db, RequestContext ctx, PrincipalLoader principals, IRealtimePublisher realtime, HtmlSanitization html)
    : DeskControllerBase
{
    [HttpGet]
    public Task<MeDto> Get(CancellationToken ct) => principals.BuildMeAsync(RequireUser(), ct);

    public sealed record AvailabilityRequest(string Availability);

    [HttpPut("availability")]
    public async Task<IActionResult> SetAvailability(AvailabilityRequest r, CancellationToken ct)
    {
        if (!Enum.TryParse<AgentAvailability>(r.Availability, true, out var value)) return BadRequest(new { code = "validation_failed", title = "Unknown availability." });
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.UserId == RequireUser(), ct) ?? throw new NotFoundException("Agent profile");
        agent.Availability = value;
        agent.AvailabilityChangedAt = db.Clock.UtcNow;
        await db.SaveChangesAsync(ct);
        await realtime.ToTenantAsync(ctx.TenantId, "agent.availability", new { userId = ctx.UserId, availability = value.ToString() }, ct);
        return NoContent();
    }

    public sealed record ProfileRequest(string? DisplayName, string? TimeZone, string? Locale, string? Signature, string? Phone);

    [HttpPut("profile")]
    public async Task<MeDto> UpdateProfile(ProfileRequest r, CancellationToken ct)
    {
        var user = await db.Users.FirstAsync(u => u.Id == RequireUser(), ct);
        if (!string.IsNullOrWhiteSpace(r.DisplayName)) user.DisplayName = r.DisplayName.Trim();
        if (!string.IsNullOrWhiteSpace(r.TimeZone))
        {
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(r.TimeZone, out _)) throw new DomainException("validation_failed", "Unknown time zone.");
            user.TimeZone = r.TimeZone;
        }
        if (!string.IsNullOrWhiteSpace(r.Locale)) user.Locale = r.Locale;
        if (r.Signature is not null) user.Signature = html.Sanitize(r.Signature);
        if (r.Phone is not null) user.Phone = r.Phone;
        await db.SaveChangesAsync(ct);
        await principals.InvalidateAsync(user.Id, ct);
        return await principals.BuildMeAsync(user.Id, ct);
    }

    public sealed record SessionDto(Guid Id, string? ClientKind, string? IpAddress, string? UserAgent, DateTime CreatedAt, DateTime LastSeenAt, bool Current);

    [HttpGet("sessions")]
    public async Task<List<SessionDto>> Sessions(CancellationToken ct)
    {
        var uid = RequireUser();
        var now = db.Clock.UtcNow;
        Guid.TryParse(User.FindFirst(DeskClaims.Session)?.Value, out var current);
        var currentFamily = await db.UserSessions.Where(s => s.Id == current).Select(s => s.FamilyId).FirstOrDefaultAsync(ct);
        var rows = await db.UserSessions.AsNoTracking()
            .Where(s => s.PrincipalKind == PrincipalKind.User && s.PrincipalId == uid && s.RevokedAt == null && s.ExpiresAt > now && s.ReplacedByHash == null)
            .OrderByDescending(s => s.LastSeenAt).ToListAsync(ct);
        return rows.Select(s => new SessionDto(s.Id, s.ClientKind, s.IpAddress, s.UserAgent, s.CreatedAt, s.LastSeenAt, s.FamilyId == currentFamily)).ToList();
    }

    [HttpDelete("sessions/{id:guid}")]
    public async Task<IActionResult> RevokeSession(Guid id, CancellationToken ct)
    {
        var uid = RequireUser();
        var session = await db.UserSessions.FirstOrDefaultAsync(s => s.Id == id && s.PrincipalId == uid, ct) ?? throw new NotFoundException("Session", id);
        await db.UserSessions.Where(s => s.FamilyId == session.FamilyId && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, db.Clock.UtcNow).SetProperty(s => s.RevokedReason, "revoked by user"), ct);
        return NoContent();
    }

    public sealed record PreferenceDto(string EventType, bool InApp, bool Email, bool Push, bool Teams, string Digest);
    public sealed record SettingsDto(bool Enabled, string? QuietHoursStart, string? QuietHoursEnd, string TimeZone, string DailyDigestAt, bool DesktopNotifications,
        IReadOnlyList<PreferenceDto> Preferences);

    [HttpGet("notification-settings")]
    public async Task<SettingsDto> GetNotificationSettings(CancellationToken ct)
    {
        var uid = RequireUser();
        // Without personal settings/preferences the tenant defaults apply (administration → notifications).
        var policy = await Application.Administration.NotificationPolicySettings.LoadAsync(db, ct);
        var s = await db.NotificationSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == uid, ct) ?? policy.AsUserDefaults(uid);
        var prefs = await db.NotificationPreferences.AsNoTracking().Where(p => p.UserId == uid).ToDictionaryAsync(p => p.EventType, ct);
        return new SettingsDto(s.Enabled, s.QuietHoursStart?.ToString("HH:mm"), s.QuietHoursEnd?.ToString("HH:mm"), s.TimeZone, s.DailyDigestAt.ToString("HH:mm"),
            s.DesktopNotifications,
            NotificationTypes.All.Select(t =>
            {
                var d = policy.For(t);
                return prefs.TryGetValue(t, out var p)
                    ? new PreferenceDto(t, p.InApp, p.Email, p.Push, p.Teams, p.Digest.ToString())
                    : new PreferenceDto(t, d.InApp, d.Email || d.Digest, d.Push, d.Teams, (d.Digest ? policy.DigestMode : DigestMode.None).ToString());
            }).ToList());
    }

    [HttpPut("notification-settings")]
    public async Task<SettingsDto> SaveNotificationSettings(SettingsDto r, CancellationToken ct)
    {
        var uid = RequireUser();
        var s = await db.NotificationSettings.FirstOrDefaultAsync(x => x.UserId == uid, ct);
        if (s is null)
        {
            s = new NotificationSettings { UserId = uid };
            db.NotificationSettings.Add(s);
        }
        s.Enabled = r.Enabled;
        s.QuietHoursStart = TimeOnly.TryParse(r.QuietHoursStart, out var qs) ? qs : null;
        s.QuietHoursEnd = TimeOnly.TryParse(r.QuietHoursEnd, out var qe) ? qe : null;
        s.TimeZone = TimeZoneInfo.TryFindSystemTimeZoneById(r.TimeZone, out _) ? r.TimeZone : "UTC";
        s.DailyDigestAt = TimeOnly.TryParse(r.DailyDigestAt, out var dd) ? dd : new TimeOnly(8, 0);
        s.DesktopNotifications = r.DesktopNotifications;
        var existing = await db.NotificationPreferences.Where(p => p.UserId == uid).ToListAsync(ct);
        foreach (var p in r.Preferences.Where(p => NotificationTypes.All.Contains(p.EventType)))
        {
            var row = existing.FirstOrDefault(x => x.EventType == p.EventType);
            if (row is null)
            {
                row = new NotificationPreference { UserId = uid, EventType = p.EventType };
                db.NotificationPreferences.Add(row);
            }
            row.InApp = p.InApp;
            row.Email = p.Email;
            row.Push = p.Push;
            row.Teams = p.Teams;
            row.Digest = Enum.TryParse<DigestMode>(p.Digest, true, out var dm) ? dm : DigestMode.None;
        }
        await db.SaveChangesAsync(ct);
        return await GetNotificationSettings(ct);
    }

    private Guid RequireUser() => ctx.UserId ?? throw new ForbiddenException("This endpoint requires a signed-in agent.");
}

/// <summary>In-app notifications for the signed-in agent.</summary>
[Route("api/v1/notifications")]
public sealed class NotificationsController(NotificationService notifications) : DeskControllerBase
{
    [HttpGet]
    public Task<List<NotificationDto>> List([FromQuery] bool unreadOnly = false, [FromQuery] int limit = 30, CancellationToken ct = default) =>
        notifications.ListAsync(unreadOnly, limit, ct);

    [HttpGet("unread-count")]
    public async Task<object> UnreadCount(CancellationToken ct) => new { count = await notifications.UnreadCountAsync(ct) };

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> Read(Guid id, CancellationToken ct)
    {
        await notifications.MarkReadAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/ack")]
    public async Task<IActionResult> Ack(Guid id, CancellationToken ct)
    {
        await notifications.AcknowledgeAsync(id, ct);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> ReadAll(CancellationToken ct)
    {
        await notifications.MarkAllReadAsync(ct);
        return NoContent();
    }
}
