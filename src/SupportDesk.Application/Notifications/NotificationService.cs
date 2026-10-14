using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Notifications;

public sealed record NotificationDto(Guid Id, string Type, string Title, string? Body, string? Link, string Severity, bool RequiresAck,
    DateTime? ReadAt, DateTime? AcknowledgedAt, DateTime CreatedAt, string? EntityType, Guid? EntityId);

public sealed record NotifyRequest(
    IReadOnlyCollection<Guid> UserIds,
    string Type,
    string Title,
    string? Body,
    string? Link,
    NotificationSeverity Severity = NotificationSeverity.Info,
    bool RequiresAck = false,
    string? EntityType = null,
    Guid? EntityId = null,
    /// <summary>Unique per event; combined with the user id to make delivery idempotent.</summary>
    string? DedupKey = null);

/// <summary>Default delivery channels per notification type (overridden by user preferences).</summary>
public static class NotificationTypes
{
    public const string TicketAssigned = "ticket.assigned";
    public const string TicketMentioned = "ticket.mentioned";
    public const string CustomerReplied = "ticket.customer_replied";
    public const string TicketEscalated = "ticket.escalated";
    public const string SlaWarning = "sla.warning";
    public const string SlaBreached = "sla.breached";
    public const string ApprovalRequested = "approval.requested";
    public const string ApprovalDecided = "approval.decided";
    public const string FollowUpDue = "followup.due";
    public const string AiProposal = "ai.proposal";
    public const string AnomalyDetected = "anomaly.detected";
    public const string ChatWaiting = "chat.waiting";
    public const string ChatTransferred = "chat.transferred";
    public const string System = "system";

    public static readonly string[] All =
        [TicketAssigned, TicketMentioned, CustomerReplied, TicketEscalated, SlaWarning, SlaBreached, ApprovalRequested, ApprovalDecided, FollowUpDue, AiProposal, AnomalyDetected, ChatWaiting, ChatTransferred, System];

    public static (bool Email, bool Push, bool Teams) Defaults(string type) => type switch
    {
        TicketAssigned or TicketMentioned or ApprovalRequested => (true, true, false),
        SlaBreached or TicketEscalated => (true, true, true),
        SlaWarning or CustomerReplied or FollowUpDue or ChatWaiting or ChatTransferred => (false, true, false),
        _ => (false, false, false)
    };
}

/// <summary>
/// Creates in-app notifications (deduplicated), pushes them in real time, and schedules external deliveries
/// (e-mail, push, Teams, digests) honouring each user's preferences, quiet hours and digest settings.
/// Critical notifications ignore quiet hours and can require acknowledgement.
/// </summary>
public sealed class NotificationService(DeskDbContext db, RequestContext ctx, IRealtimePublisher realtime)
{
    public async Task<int> NotifyAsync(NotifyRequest r, CancellationToken ct = default)
    {
        if (r.UserIds.Count == 0) return 0;
        var now = db.Clock.UtcNow;
        var users = await db.Users.AsNoTracking().Where(u => r.UserIds.Contains(u.Id) && u.IsActive).Select(u => u.Id).ToListAsync(ct);
        var prefs = await db.NotificationPreferences.AsNoTracking().Where(p => users.Contains(p.UserId) && p.EventType == r.Type).ToDictionaryAsync(p => p.UserId, ct);
        var settings = await db.NotificationSettings.AsNoTracking().Where(s => users.Contains(s.UserId)).ToDictionaryAsync(s => s.UserId, ct);
        // Tenant defaults (administration → notifications) apply where a user has no preference / settings of their own.
        var policy = await Administration.NotificationPolicySettings.LoadAsync(db, ct);
        var tenantDefault = policy.For(r.Type);
        var created = new List<Notification>();
        foreach (var userId in users)
        {
            var dedup = $"{r.DedupKey ?? Guid.NewGuid().ToString("N")}:{userId:N}";
            if (dedup.Length > 440) dedup = TextNormalization.Sha256Hex(dedup);
            if (await db.Notifications.AnyAsync(n => n.DedupKey == dedup, ct)) continue;
            prefs.TryGetValue(userId, out var pref);
            if (!settings.TryGetValue(userId, out var s)) s = policy.AsUserDefaults(userId);
            if (s is { Enabled: false }) continue;
            var inApp = pref?.InApp ?? tenantDefault.InApp;
            var n = new Notification
            {
                UserId = userId, Type = r.Type, Title = Trim(r.Title, 300), Body = r.Body is null ? null : Trim(r.Body, 2000), Link = r.Link,
                Severity = r.Severity, RequiresAck = r.RequiresAck, EntityType = r.EntityType, EntityId = r.EntityId, DedupKey = dedup, CreatedAt = now,
                ReadAt = inApp ? null : now
            };
            db.Notifications.Add(n);
            created.Add(n);

            var email = pref?.Email ?? (tenantDefault.Email || tenantDefault.Digest);
            var push = pref?.Push ?? tenantDefault.Push;
            var teams = pref?.Teams ?? tenantDefault.Teams;
            var digest = pref?.Digest ?? (tenantDefault.Digest ? policy.DigestMode : DigestMode.None);
            var deliverAt = r.Severity == NotificationSeverity.Critical ? now : AfterQuietHours(now, s);
            if (email)
                db.NotificationDeliveries.Add(new NotificationDelivery
                {
                    NotificationId = n.Id, Channel = digest == DigestMode.None ? NotificationChannel.Email : NotificationChannel.Digest,
                    ScheduledFor = digest == DigestMode.None ? deliverAt : NextDigest(now, digest, s)
                });
            if (push) db.NotificationDeliveries.Add(new NotificationDelivery { NotificationId = n.Id, Channel = NotificationChannel.Push, ScheduledFor = deliverAt });
            if (teams) db.NotificationDeliveries.Add(new NotificationDelivery { NotificationId = n.Id, Channel = NotificationChannel.Teams, ScheduledFor = deliverAt });
        }
        if (created.Count == 0) return 0;
        await db.SaveChangesAsync(ct);
        foreach (var n in created.Where(n => n.ReadAt is null))
            await realtime.ToUsersAsync(ctx.TenantId, [n.UserId!.Value], "notification", Map(n), ct);
        return created.Count;
    }

    public static DateTime AfterQuietHours(DateTime nowUtc, NotificationSettings? s)
    {
        if (s?.QuietHoursStart is not { } start || s.QuietHoursEnd is not { } end) return nowUtc;
        var tz = WorkingCalendar.ResolveTimeZone(s.TimeZone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var t = TimeOnly.FromDateTime(local);
        var inQuiet = start <= end ? t >= start && t < end : t >= start || t < end;
        if (!inQuiet) return nowUtc;
        var endLocal = local.Date.Add(end.ToTimeSpan());
        if (endLocal <= local) endLocal = endLocal.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(endLocal, DateTimeKind.Unspecified), tz);
    }

    private static DateTime NextDigest(DateTime nowUtc, DigestMode mode, NotificationSettings? s)
    {
        if (mode == DigestMode.Hourly) return new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
        var tz = WorkingCalendar.ResolveTimeZone(s?.TimeZone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var at = local.Date.Add((s?.DailyDigestAt ?? new TimeOnly(8, 0)).ToTimeSpan());
        if (at <= local) at = at.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(at, DateTimeKind.Unspecified), tz);
    }

    public async Task<List<NotificationDto>> ListAsync(bool unreadOnly, int limit, CancellationToken ct = default)
    {
        var q = db.Notifications.AsNoTracking().Where(n => n.UserId == ctx.UserId);
        if (unreadOnly) q = q.Where(n => n.ReadAt == null);
        return (await q.OrderByDescending(n => n.CreatedAt).Take(Math.Clamp(limit, 1, 200)).ToListAsync(ct)).Select(Map).ToList();
    }

    public Task<int> UnreadCountAsync(CancellationToken ct = default) => db.Notifications.CountAsync(n => n.UserId == ctx.UserId && n.ReadAt == null, ct);

    public async Task MarkReadAsync(Guid id, CancellationToken ct = default) =>
        await db.Notifications.Where(n => n.Id == id && n.UserId == ctx.UserId && n.ReadAt == null).ExecuteUpdateAsync(u => u.SetProperty(n => n.ReadAt, db.Clock.UtcNow), ct);

    public async Task MarkAllReadAsync(CancellationToken ct = default) =>
        await db.Notifications.Where(n => n.UserId == ctx.UserId && n.ReadAt == null && !n.RequiresAck).ExecuteUpdateAsync(u => u.SetProperty(n => n.ReadAt, db.Clock.UtcNow), ct);

    public async Task AcknowledgeAsync(Guid id, CancellationToken ct = default)
    {
        var now = db.Clock.UtcNow;
        await db.Notifications.Where(n => n.Id == id && n.UserId == ctx.UserId)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.AcknowledgedAt, now).SetProperty(n => n.ReadAt, n => n.ReadAt ?? now), ct);
        // An acknowledged critical alert no longer needs to be escalated by other channels.
        await db.NotificationDeliveries.Where(d => d.NotificationId == id && d.Status == DeliveryStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.Status, DeliveryStatus.Succeeded).SetProperty(d => d.Error, "acknowledged in app"), ct);
    }

    public static NotificationDto Map(Notification n) => new(n.Id, n.Type, n.Title, n.Body, n.Link, n.Severity.ToString(), n.RequiresAck, n.ReadAt,
        n.AcknowledgedAt, n.CreatedAt, n.EntityType, n.EntityId);

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
