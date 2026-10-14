using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Notifications;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Automation;
using SupportDesk.Contracts.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Automation;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Automation;

/// <summary>Outbound webhook subscriptions (admin.integrations): CRUD, secret rotation, delivery log, redelivery and test ping.</summary>
public sealed class WebhookService(DeskDbContext db, RequestContext ctx, ISecretProtector secrets, IAuditWriter audit, IJobQueue jobs,
    WebhookDeliveryProcessor processor)
{
    public const string PingEvent = "webhook.ping";

    public static IReadOnlyList<string> SubscribableEvents => EventTypes.Public;

    public async Task<List<WebhookDto>> ListAsync(CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        return (await db.Webhooks.AsNoTracking().OrderBy(w => w.Name).ToListAsync(ct)).Select(w => Map(w)).ToList();
    }

    public async Task<WebhookDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        return Map(await db.Webhooks.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw new NotFoundException("Webhook", id));
    }

    public async Task<WebhookDto> CreateAsync(SaveWebhookRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        Validate(r, creating: true);
        var secret = string.IsNullOrWhiteSpace(r.Secret) ? "whsec_" + TextNormalization.RandomToken(24) : r.Secret.Trim();
        var hook = new Webhook
        {
            Name = r.Name.Trim(), Url = r.Url.Trim(), EventsJson = Json.Serialize(NormalizeEvents(r.Events)), SecretProtected = secrets.Protect(secret),
            Status = r.IsActive ? WebhookStatus.Active : WebhookStatus.Disabled
        };
        db.Webhooks.Add(hook);
        audit.Write(AuditActions.IntegrationChanged, "Webhook", hook.Id, $"Webhook '{hook.Name}' created ({ActionExecutor.RedactUrl(hook.Url)})");
        await db.SaveChangesAsync(ct);
        return Map(hook, secret);
    }

    public async Task<WebhookDto> UpdateAsync(Guid id, SaveWebhookRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        Validate(r, creating: false);
        var hook = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw new NotFoundException("Webhook", id);
        hook.Name = r.Name.Trim();
        hook.Url = r.Url.Trim();
        hook.EventsJson = Json.Serialize(NormalizeEvents(r.Events));
        string? secret = null;
        if (!string.IsNullOrWhiteSpace(r.Secret))
        {
            secret = r.Secret.Trim();
            hook.SecretProtected = secrets.Protect(secret);
        }
        if (r.IsActive && hook.Status == WebhookStatus.Disabled) { hook.Status = WebhookStatus.Active; hook.ConsecutiveFailures = 0; }
        else if (!r.IsActive) hook.Status = WebhookStatus.Disabled;
        audit.Write(AuditActions.IntegrationChanged, "Webhook", hook.Id, $"Webhook '{hook.Name}' updated");
        await db.SaveChangesAsync(ct);
        return Map(hook, secret);
    }

    public async Task<WebhookDto> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var hook = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw new NotFoundException("Webhook", id);
        hook.Status = active ? WebhookStatus.Active : WebhookStatus.Disabled;
        if (active) hook.ConsecutiveFailures = 0;
        audit.Write(AuditActions.IntegrationChanged, "Webhook", hook.Id, $"Webhook '{hook.Name}' {(active ? "enabled" : "disabled")}");
        await db.SaveChangesAsync(ct);
        return Map(hook);
    }

    public async Task<WebhookDto> RotateSecretAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var hook = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw new NotFoundException("Webhook", id);
        var secret = "whsec_" + TextNormalization.RandomToken(24);
        hook.SecretProtected = secrets.Protect(secret);
        audit.Write(AuditActions.IntegrationChanged, "Webhook", hook.Id, $"Signing secret of webhook '{hook.Name}' rotated");
        await db.SaveChangesAsync(ct);
        return Map(hook, secret);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var hook = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw new NotFoundException("Webhook", id);
        db.Webhooks.Remove(hook);
        audit.Write(AuditActions.IntegrationChanged, "Webhook", hook.Id, $"Webhook '{hook.Name}' deleted");
        await db.SaveChangesAsync(ct);
    }

    public async Task<PageResult<WebhookDeliveryDto>> DeliveriesAsync(Guid id, string? status, int page, int pageSize, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        if (!await db.Webhooks.AnyAsync(w => w.Id == id, ct)) throw new NotFoundException("Webhook", id);
        var q = db.WebhookDeliveries.AsNoTracking().Where(d => d.WebhookId == id);
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<DeliveryStatus>(status, true, out var s)) throw new Tickets.ValidationException(new Dictionary<string, string> { ["status"] = "Unknown status." });
            q = q.Where(d => d.Status == s);
        }
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PageResult<WebhookDeliveryDto>(rows.Select(d => MapDelivery(d, includePayload: false)).ToList(), page, pageSize, total);
    }

    public async Task<WebhookDeliveryDto> DeliveryAsync(Guid id, Guid deliveryId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var d = await db.WebhookDeliveries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deliveryId && x.WebhookId == id, ct) ?? throw new NotFoundException("Delivery", deliveryId);
        return MapDelivery(d, includePayload: true);
    }

    /// <summary>Queues a delivery again (any state); attempts restart and the webhook must be enabled.</summary>
    public async Task<WebhookDeliveryDto> RedeliverAsync(Guid id, Guid deliveryId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var hook = await db.Webhooks.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw new NotFoundException("Webhook", id);
        if (hook.Status == WebhookStatus.Disabled) throw new DomainException(ErrorCodes.Validation, "Enable the webhook before redelivering.");
        var d = await db.WebhookDeliveries.FirstOrDefaultAsync(x => x.Id == deliveryId && x.WebhookId == id, ct) ?? throw new NotFoundException("Delivery", deliveryId);
        d.Status = DeliveryStatus.Pending;
        d.Attempts = 0;
        d.NextAttemptAt = db.Clock.UtcNow;
        d.DeliveredAt = null;
        jobs.Enqueue(WebhookDeliveryJobHandler.JobType, new WebhookDeliveryJob(d.Id), idempotencyKey: $"webhook-delivery:{d.Id:N}:redeliver:{Guid.NewGuid():N}");
        audit.Write(AuditActions.IntegrationChanged, "Webhook", hook.Id, $"Delivery {d.Id} of '{d.EventType}' queued again");
        await db.SaveChangesAsync(ct);
        return MapDelivery(d, includePayload: false);
    }

    /// <summary>Sends a signed "webhook.ping" event immediately and returns the delivery result.</summary>
    public async Task<WebhookDeliveryDto> PingAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var hook = await db.Webhooks.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw new NotFoundException("Webhook", id);
        var eventId = Guid.NewGuid();
        var now = db.Clock.UtcNow;
        var payload = WebhookDispatchHandler.Envelope(eventId, PingEvent, now, ctx.TenantId,
            JsonSerializer.SerializeToNode(new { webhookId = hook.Id, name = hook.Name, message = "Test ping from SupportDesk" }, Json.Options)!);
        var d = new WebhookDelivery
        {
            WebhookId = hook.Id, EventId = eventId, EventType = PingEvent, PayloadJson = payload, Status = DeliveryStatus.Pending, NextAttemptAt = now, CreatedAt = now
        };
        db.WebhookDeliveries.Add(d);
        await db.SaveChangesAsync(ct);
        var result = await processor.DeliverAsync(d.Id, test: true, ct) ?? d;
        return MapDelivery(result, includePayload: true);
    }

    private void Validate(SaveWebhookRequest r, bool creating)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(r.Name)) errors["name"] = "A name is required.";
        else if (r.Name.Trim().Length > 200) errors["name"] = "At most 200 characters.";
        if (string.IsNullOrWhiteSpace(r.Url) || !AutomationValidator.IsValidHttpUrl(r.Url.Trim())) errors["url"] = "Enter an absolute http(s) URL without credentials.";
        else if (r.Url.Trim().Length > 450) errors["url"] = "At most 450 characters.";
        if (r.Events is not { Count: > 0 }) errors["events"] = "Subscribe to at least one event (or \"*\" for all).";
        else if (r.Events.FirstOrDefault(e => e != "*" && !EventTypes.Public.Contains(e)) is { } unknown) errors["events"] = $"'{unknown}' is not a public event type.";
        if (r.Secret is { Length: > 0 } s && s.Trim().Length < 16) errors["secret"] = "Use a secret of at least 16 characters.";
        _ = creating;
        if (errors.Count > 0) throw new Tickets.ValidationException(errors);
    }

    private static List<string> NormalizeEvents(IEnumerable<string> events)
    {
        var list = events.Select(e => e.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Contains("*") ? ["*"] : list;
    }

    public static WebhookDto Map(Webhook w, string? secret = null) => new(w.Id, w.Name, w.Url, Json.StringList(w.EventsJson), w.Status.ToString(), w.ConsecutiveFailures,
        w.LastDeliveryAt, w.LastStatusCode, w.CreatedAt, w.UpdatedAt, secret);

    public static WebhookDeliveryDto MapDelivery(WebhookDelivery d, bool includePayload) => new(d.Id, d.WebhookId, d.EventId, d.EventType, d.Status.ToString(), d.Attempts,
        d.NextAttemptAt, d.ResponseCode, d.ResponseExcerpt, d.DurationMs, d.CreatedAt, d.DeliveredAt, includePayload ? Json.Element(d.PayloadJson) : null);
}

/// <summary>
/// Creates a <see cref="WebhookDelivery"/> (and a durable delivery job) for every enabled subscription to a public event.
/// Envelope: {"id","type","occurredAt","tenantId","data"}; ticket events include a ticket snapshot in data.ticket.
/// </summary>
public sealed class WebhookDispatchHandler(DeskDbContext db, IJobQueue jobs) : IOutboxHandler
{
    private static readonly HashSet<string> Public = new(EventTypes.Public, StringComparer.Ordinal);

    public string Name => "webhooks";
    public int Order => HandlerOrder.Webhooks;
    public bool CanHandle(string eventType) => Public.Contains(eventType);

    public async Task HandleAsync(OutboxEvent ev, CancellationToken ct)
    {
        if (ev.TenantId is not { } tenantId) return;
        var hooks = await db.Webhooks.AsNoTracking().Where(w => w.Status != WebhookStatus.Disabled).ToListAsync(ct);
        var subscribed = hooks.Where(h => Json.StringList(h.EventsJson).Any(e => e == "*" || e == ev.Type)).ToList();
        if (subscribed.Count == 0) return;
        var existing = await db.WebhookDeliveries.AsNoTracking().Where(d => d.EventId == ev.Id).Select(d => d.WebhookId).ToListAsync(ct);
        var pending = subscribed.Where(h => !existing.Contains(h.Id)).ToList();
        if (pending.Count == 0) return;
        var envelope = Envelope(ev.Id, ev.Type, ev.OccurredAt, tenantId, await DataAsync(ev, ct));
        var now = db.Clock.UtcNow;
        foreach (var hook in pending)
        {
            var d = new WebhookDelivery
            {
                WebhookId = hook.Id, EventId = ev.Id, EventType = ev.Type, PayloadJson = envelope, Status = DeliveryStatus.Pending, NextAttemptAt = now, CreatedAt = now
            };
            db.WebhookDeliveries.Add(d);
            jobs.Enqueue(WebhookDeliveryJobHandler.JobType, new WebhookDeliveryJob(d.Id), idempotencyKey: $"webhook-delivery:{d.Id:N}:1");
        }
        await db.SaveChangesAsync(ct);
    }

    public static string Envelope(Guid id, string type, DateTime occurredAt, Guid tenantId, JsonNode data) =>
        new JsonObject
        {
            ["id"] = id.ToString(),
            ["type"] = type,
            ["occurredAt"] = DateTime.SpecifyKind(occurredAt, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
            ["tenantId"] = tenantId.ToString(),
            ["data"] = data
        }.ToJsonString();

    private async Task<JsonNode> DataAsync(OutboxEvent ev, CancellationToken ct)
    {
        JsonNode data;
        try { data = JsonNode.Parse(string.IsNullOrWhiteSpace(ev.PayloadJson) ? "{}" : ev.PayloadJson) ?? new JsonObject(); }
        catch (JsonException) { data = new JsonObject(); }
        if (data is not JsonObject obj || ev.AggregateType != "Ticket" || ev.AggregateId is not { } ticketId) return data;
        var t = await db.Tickets.AsNoTracking().IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(x => x.Id == ticketId)
            .Select(x => new
            {
                x.Id, x.Number, x.Subject, x.StatusId, x.StatusCategory, x.Priority, x.Severity, x.Channel, x.DepartmentId, x.TeamId, x.AssigneeId, x.CustomerId,
                x.ContactId, x.ProductId, x.CategoryId, x.DueAt, x.CreatedAt, x.UpdatedAt, x.IsDeleted
            }).FirstOrDefaultAsync(ct);
        if (t is null) return obj;
        var status = await db.TicketStatuses.AsNoTracking().Where(s => s.Id == t.StatusId).Select(s => s.Name).FirstOrDefaultAsync(ct);
        obj["ticket"] = JsonSerializer.SerializeToNode(new
        {
            t.Id, t.Number, t.Subject, status, statusCategory = t.StatusCategory.ToString(), priority = t.Priority.ToString(), severity = t.Severity?.ToString(),
            channel = t.Channel.ToString(), t.DepartmentId, t.TeamId, t.AssigneeId, t.CustomerId, t.ContactId, t.ProductId, t.CategoryId, t.DueAt, t.CreatedAt,
            t.UpdatedAt, deleted = t.IsDeleted
        }, Json.Options);
        return obj;
    }
}

public sealed record WebhookDeliveryJob(Guid DeliveryId);

public sealed class WebhookDeliveryJobHandler(WebhookDeliveryProcessor processor) : JobHandler<WebhookDeliveryJob>
{
    public const string JobType = "automation.webhook-delivery";
    public override string Type => JobType;

    protected override Task ExecuteAsync(WebhookDeliveryJob payload, BackgroundJob job, CancellationToken ct) => processor.DeliverAsync(payload.DeliveryId, test: false, ct);
}

/// <summary>
/// Sends one webhook delivery: POST of the stored envelope with X-SupportDesk-Event, X-SupportDesk-Delivery,
/// X-SupportDesk-Timestamp and X-SupportDesk-Signature: sha256=HMAC(secret, body). Failures retry with exponential
/// backoff (30 s … 12 h) as new durable jobs and dead-letter after <see cref="MaxAttempts"/>; a webhook is disabled
/// automatically after <see cref="AutoDisableAfter"/> consecutive failures (administrators are notified).
/// </summary>
public sealed class WebhookDeliveryProcessor(DeskDbContext db, OutboundHttpSender sender, ISecretProtector secrets, WebhookTransportOptions options,
    IJobQueue jobs, IAuditWriter audit, NotificationService notifications)
{
    public const int MaxAttempts = 8;
    public const int AutoDisableAfter = 20;

    public static TimeSpan Backoff(int attempt) => attempt switch
    {
        <= 1 => TimeSpan.FromSeconds(30),
        2 => TimeSpan.FromMinutes(2),
        3 => TimeSpan.FromMinutes(10),
        4 => TimeSpan.FromMinutes(30),
        5 => TimeSpan.FromHours(2),
        6 => TimeSpan.FromHours(6),
        _ => TimeSpan.FromHours(12)
    };

    public async Task<WebhookDelivery?> DeliverAsync(Guid deliveryId, bool test, CancellationToken ct)
    {
        var d = await db.WebhookDeliveries.FirstOrDefaultAsync(x => x.Id == deliveryId, ct);
        if (d is null || d.Status is DeliveryStatus.Succeeded or DeliveryStatus.DeadLetter) return d;
        var now = db.Clock.UtcNow;
        if (!test && d.Status == DeliveryStatus.Failed && d.NextAttemptAt > now.AddSeconds(5)) return d; // a later job owns the next attempt
        var hook = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == d.WebhookId, ct);
        if (hook is null) return d;
        if (!test && hook.Status == WebhookStatus.Disabled)
        {
            d.Status = DeliveryStatus.DeadLetter;
            d.ResponseExcerpt = "Not sent: the webhook is disabled.";
            await db.SaveChangesAsync(ct);
            return d;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = options.UserAgent,
            ["X-SupportDesk-Event"] = d.EventType,
            ["X-SupportDesk-Delivery"] = d.Id.ToString(),
            ["X-SupportDesk-Timestamp"] = new DateTimeOffset(now).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["X-SupportDesk-Signature"] = OutboundHttpSender.Signature(secrets.Unprotect(hook.SecretProtected), d.PayloadJson)
        };
        var result = await sender.SendAsync(new OutboundHttpRequest("POST", hook.Url, headers, d.PayloadJson, TimeSpan.FromSeconds(Math.Clamp(options.DefaultTimeoutSeconds, 1, 30)),
            "Webhook", $"{hook.Name}: {d.EventType}"), ct);

        d.Attempts++;
        d.ResponseCode = result.StatusCode;
        d.DurationMs = result.DurationMs;
        d.ResponseExcerpt = result.Success ? result.ResponseExcerpt : $"{result.Error}{(result.ResponseExcerpt is null ? "" : " — " + result.ResponseExcerpt)}";
        if (d.ResponseExcerpt is { Length: > 1000 }) d.ResponseExcerpt = d.ResponseExcerpt[..1000];
        hook.LastDeliveryAt = now;
        hook.LastStatusCode = result.StatusCode;
        if (result.Success)
        {
            d.Status = DeliveryStatus.Succeeded;
            d.DeliveredAt = now;
            if (!test)
            {
                hook.ConsecutiveFailures = 0;
                if (hook.Status == WebhookStatus.Failing) hook.Status = WebhookStatus.Active;
            }
        }
        else if (test)
        {
            d.Status = DeliveryStatus.Failed;
        }
        else
        {
            hook.ConsecutiveFailures++;
            if (hook.ConsecutiveFailures >= AutoDisableAfter && hook.Status != WebhookStatus.Disabled)
            {
                hook.Status = WebhookStatus.Disabled;
                audit.Write(AuditActions.IntegrationChanged, "Webhook", hook.Id,
                    $"Webhook '{hook.Name}' disabled automatically after {hook.ConsecutiveFailures} consecutive failed deliveries");
            }
            else if (hook.Status == WebhookStatus.Active) hook.Status = WebhookStatus.Failing;

            if (d.Attempts >= MaxAttempts || hook.Status == WebhookStatus.Disabled) d.Status = DeliveryStatus.DeadLetter;
            else
            {
                d.Status = DeliveryStatus.Failed;
                d.NextAttemptAt = now.Add(Backoff(d.Attempts));
                jobs.Enqueue(WebhookDeliveryJobHandler.JobType, new WebhookDeliveryJob(d.Id), d.NextAttemptAt, $"webhook-delivery:{d.Id:N}:{d.Attempts + 1}:{Guid.NewGuid():N}");
            }
        }
        await db.SaveChangesAsync(ct);
        if (hook.Status == WebhookStatus.Disabled && !test && !result.Success && hook.ConsecutiveFailures == AutoDisableAfter)
            await NotifyAdminsAsync(hook, ct);
        return d;
    }

    private async Task NotifyAdminsAsync(Webhook hook, CancellationToken ct)
    {
        var admins = await db.UserRoles.AsNoTracking()
            .Where(ur => db.RolePermissions.Any(p => p.RoleId == ur.RoleId && p.PermissionCode == Permissions.AdminIntegrations))
            .Select(ur => ur.UserId).Distinct().ToListAsync(ct);
        await notifications.NotifyAsync(new NotifyRequest(admins, NotificationTypes.System, $"Webhook '{hook.Name}' was disabled",
            $"{AutoDisableAfter} consecutive deliveries to {ActionExecutor.RedactUrl(hook.Url)} failed. Fix the endpoint, then enable the webhook and redeliver.",
            "/admin/webhooks", NotificationSeverity.Warning, false, "Webhook", hook.Id, $"webhook-disabled:{hook.Id:N}:{hook.UpdatedAt.Ticks}"), ct);
    }
}
