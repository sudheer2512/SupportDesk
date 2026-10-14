using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Platform;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Communication;

/// <summary>Delivers scheduled notification deliveries (e-mail, push, Teams, hourly/daily digests) for every tenant every 30 s.</summary>
public sealed class NotificationDeliveryTask(TenantScopeRunner tenants) : IRecurringTask
{
    public string Name => "notification-delivery";
    public TimeSpan Interval => TimeSpan.FromSeconds(30);

    public Task RunAsync(CancellationToken ct) =>
        tenants.ForEachActiveTenantAsync(Name, (sp, _, token) => sp.GetRequiredService<NotificationDeliveryProcessor>().ProcessDueAsync(token), ct);
}

/// <summary>
/// Sends due <see cref="NotificationDelivery"/> rows: e-mail through the system mailer, push to every active device of
/// the user (FCM, or log-only when not configured), Teams via the user's or tenant's incoming webhook, and digests
/// aggregated per user (one e-mail listing every pending item). Failures retry with backoff and dead-letter after 5 attempts.
/// </summary>
public sealed class NotificationDeliveryProcessor(
    DeskDbContext db,
    ISystemMailer mailer,
    IPushSender push,
    ITeamsNotifier teams,
    ISecretProtector protector,
    TenantSettingsService settings,
    CommunicationKeys keys,
    ILogger<NotificationDeliveryProcessor> logger,
    Administration.EmailTemplateService templates)
{
    public const int MaxAttempts = 5;

    public async Task<int> ProcessDueAsync(CancellationToken ct)
    {
        var now = db.Clock.UtcNow;
        var due = await db.NotificationDeliveries
            .Where(d => (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.Failed) && d.ScheduledFor <= now)
            .OrderBy(d => d.ScheduledFor).Take(200).ToListAsync(ct);
        if (due.Count == 0) return 0;
        var notificationIds = due.Select(d => d.NotificationId).Distinct().ToList();
        var notifications = await db.Notifications.AsNoTracking().Where(n => notificationIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, ct);
        var userIds = notifications.Values.Where(n => n.UserId != null).Select(n => n.UserId!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        var userSettings = await db.NotificationSettings.AsNoTracking().Where(s => userIds.Contains(s.UserId)).ToDictionaryAsync(s => s.UserId, ct);
        var defaults = await settings.GetAsync<NotificationDefaults>(SettingKeys.Notifications, ct);
        var baseUrl = keys.PublicBaseUrl;
        var processed = 0;

        // Digests: one e-mail per user with every due digest item.
        foreach (var group in due.Where(d => d.Channel == NotificationChannel.Digest).GroupBy(d => notifications.GetValueOrDefault(d.NotificationId)?.UserId))
        {
            var items = group.ToList();
            if (group.Key is not { } uid || !users.TryGetValue(uid, out var user) || !user.IsActive)
            {
                Complete(items, DeliveryStatus.Succeeded, "recipient inactive");
                continue;
            }
            var rows = items.Select(d => notifications[d.NotificationId]).OrderByDescending(n => n.CreatedAt).ToList();
            var list = new StringBuilder("<ul>");
            foreach (var n in rows)
                list.Append("<li><strong>").Append(WebUtility.HtmlEncode(n.Title)).Append("</strong>")
                    .Append(n.Body is null ? "" : " — " + WebUtility.HtmlEncode(n.Body))
                    .Append(n.Link is null ? "" : $" (<a href=\"{WebUtility.HtmlEncode(baseUrl + n.Link)}\">open</a>)").Append("</li>");
            list.Append("</ul>");
            // Tenant template (administration → notifications → e-mail templates) or the built-in default.
            var (digestSubject, digestHtml) = await templates.RenderAsync(Administration.EmailTemplateCatalog.AgentDigest, new Dictionary<string, string?>
            {
                ["user.name"] = user.DisplayName, ["digest.count"] = rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), ["digest.items"] = list.ToString()
            }, ct);
            await SendAsync(items, async () =>
            {
                await mailer.SendAsync(db.Request.TenantId, user.Email, digestSubject, digestHtml, ct: ct);
                return SendResult.Ok(null);
            }, ct);
            processed += items.Count;
        }

        foreach (var d in due.Where(d => d.Channel != NotificationChannel.Digest))
        {
            processed++;
            if (!notifications.TryGetValue(d.NotificationId, out var n) || n.UserId is not { } uid || !users.TryGetValue(uid, out var user) || !user.IsActive)
            {
                Complete([d], DeliveryStatus.Succeeded, "recipient inactive");
                continue;
            }
            if (n.RequiresAck && n.AcknowledgedAt is not null)
            {
                Complete([d], DeliveryStatus.Succeeded, "acknowledged in app");
                continue;
            }
            var link = n.Link is null ? null : $"{baseUrl}{n.Link}";
            switch (d.Channel)
            {
                case NotificationChannel.Email:
                {
                    var (subject, body) = await templates.RenderAsync(Administration.EmailTemplateCatalog.AgentNotification, new Dictionary<string, string?>
                    {
                        ["user.name"] = user.DisplayName, ["notification.title"] = n.Title, ["notification.body"] = n.Body ?? n.Title, ["notification.link"] = link ?? baseUrl
                    }, ct);
                    await SendAsync([d], async () =>
                    {
                        await mailer.SendAsync(db.Request.TenantId, user.Email, subject, body, ct: ct);
                        return SendResult.Ok(null);
                    }, ct);
                    break;
                }
                case NotificationChannel.Push:
                {
                    var devices = await db.PushDevices.AsNoTracking().Where(p => p.UserId == uid && p.IsActive).Select(p => p.Token).ToListAsync(ct);
                    if (devices.Count == 0)
                    {
                        Complete([d], DeliveryStatus.Succeeded, "no registered devices");
                        break;
                    }
                    await SendAsync([d], async () =>
                    {
                        SendResult? last = null;
                        foreach (var token in devices)
                        {
                            last = await push.SendAsync(token, n.Title, n.Body ?? "", new Dictionary<string, string>
                            {
                                ["type"] = n.Type, ["link"] = n.Link ?? "", ["notificationId"] = n.Id.ToString(), ["entityId"] = n.EntityId?.ToString() ?? ""
                            }, ct);
                            if (last.Success) return last;
                        }
                        return last ?? SendResult.Fail("no device accepted the push");
                    }, ct);
                    break;
                }
                case NotificationChannel.Teams:
                {
                    var protectedUrl = userSettings.GetValueOrDefault(uid)?.TeamsWebhookUrlProtected ?? defaults.TeamsWebhookUrlProtected;
                    if (protectedUrl is null)
                    {
                        Complete([d], DeliveryStatus.DeadLetter, "No Microsoft Teams webhook is configured.");
                        break;
                    }
                    string url;
                    try { url = protector.Unprotect(protectedUrl); }
                    catch (System.Security.Cryptography.CryptographicException)
                    {
                        Complete([d], DeliveryStatus.DeadLetter, "The Teams webhook URL cannot be decrypted.");
                        break;
                    }
                    await SendAsync([d], () => teams.SendAsync(url, n.Title, $"{n.Body}\n\nFor {user.DisplayName}", link, ct), ct);
                    break;
                }
                default:
                    Complete([d], DeliveryStatus.DeadLetter, $"{d.Channel} notifications are not supported.");
                    break;
            }
        }
        await db.SaveChangesAsync(ct);
        return processed;
    }

    private async Task SendAsync(IReadOnlyList<NotificationDelivery> rows, Func<Task<SendResult>> send, CancellationToken ct)
    {
        SendResult result;
        try { result = await send(); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            result = SendResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        if (result.Success)
        {
            Complete(rows, DeliveryStatus.Succeeded, null);
            return;
        }
        var now = db.Clock.UtcNow;
        foreach (var d in rows)
        {
            d.Attempts++;
            d.Error = TextNormalization.Truncate(result.Error, 2000);
            if (result.Permanent || d.Attempts >= MaxAttempts)
            {
                d.Status = DeliveryStatus.DeadLetter;
                logger.LogWarning("Notification delivery {Delivery} ({Channel}) dead-lettered: {Error}", d.Id, d.Channel, d.Error);
            }
            else
            {
                d.Status = DeliveryStatus.Failed;
                d.ScheduledFor = now.AddSeconds(Math.Min(3600, 60 * Math.Pow(3, d.Attempts - 1)));
            }
        }
    }

    private void Complete(IEnumerable<NotificationDelivery> rows, DeliveryStatus status, string? note)
    {
        foreach (var d in rows)
        {
            d.Status = status;
            d.Attempts++;
            d.Error = note;
            if (status == DeliveryStatus.Succeeded) d.SentAt = db.Clock.UtcNow;
        }
    }
}
