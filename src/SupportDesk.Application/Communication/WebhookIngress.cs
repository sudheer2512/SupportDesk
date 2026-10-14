using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Communication;

public sealed record WebhookResponse(int StatusCode, string? Body = null, string ContentType = "text/plain");

public sealed record InboundEventPayload(Guid InboundEventId);

/// <summary>
/// Anonymous provider callbacks (/api/v1/channels/webhooks/{provider}/{webhookKey}). The tenant comes from the channel
/// account that owns the unguessable webhook key; the provider adapter verifies the signature before anything is stored.
/// Every event is recorded in InboundEvents (unique per provider + dedup key) so redelivered webhooks are idempotent;
/// failed events are retried by a durable job.
/// </summary>
public sealed class WebhookIngress(
    DeskDbContext db,
    RequestContext ctx,
    ChannelProviderRegistry registry,
    ChannelContextFactory contexts,
    InboundMessagingProcessor processor,
    IJobQueue jobs,
    ILogger<WebhookIngress> logger)
{
    public async Task<WebhookResponse> HandleAsync(string provider, string webhookKey, WebhookRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(webhookKey) || webhookKey.Length > 64) return new WebhookResponse(404);
        var account = await db.AllTenants<ChannelAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.WebhookKey == webhookKey, ct);
        if (account is null || !account.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) || !account.IsActive) return new WebhookResponse(404);
        var tenant = await db.Tenants.AsNoTracking().Where(t => t.Id == account.TenantId).Select(t => t.Status).FirstOrDefaultAsync(ct);
        if (tenant is TenantStatus.Cancelled or TenantStatus.PendingDeletion or TenantStatus.Suspended) return new WebhookResponse(410);
        ctx.SetSystem(account.TenantId, $"Webhook ({account.Provider})", ActorType.Integration);
        var adapter = registry.Messaging(account.Provider);
        if (adapter is null) return new WebhookResponse(404);
        var context = contexts.Create(account);

        if (HttpMethods(request.Method) == "GET")
        {
            var challenge = adapter.VerifyHandshake(context, request);
            return challenge is null ? new WebhookResponse(403, "Verification failed.") : new WebhookResponse(200, challenge);
        }

        WebhookParseResult parsed;
        try
        {
            parsed = adapter.ParseWebhook(context, request);
        }
        catch (WebhookRejectedException ex)
        {
            logger.LogWarning("Rejected {Provider} webhook for channel {Account}: {Reason}", account.Provider, account.Id, ex.Message);
            return new WebhookResponse(401, "Invalid signature.");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
        {
            return new WebhookResponse(400, "Malformed payload.");
        }

        foreach (var ev in parsed.Events)
        {
            var dedup = TextNormalization.Truncate($"{account.Id:N}:{ev.DedupKey}", 400);
            var row = new InboundEvent
            {
                TenantId = account.TenantId, ChannelAccountId = account.Id, Provider = account.Provider, DedupKey = dedup, EventType = ev.EventType,
                PayloadJson = Json.Serialize(ev), ReceivedAt = db.Clock.UtcNow, Status = InboundEventStatus.Received
            };
            db.InboundEvents.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                var existing = await db.InboundEvents.FirstOrDefaultAsync(e => e.Provider == account.Provider && e.DedupKey == dedup, ct);
                if (existing is null || existing.Status is InboundEventStatus.Processed or InboundEventStatus.Ignored) continue; // redelivery: already handled
                row = existing;
            }
            await ProcessEventAsync(account, row.Id, ev, scheduleRetry: true, ct);
        }
        return new WebhookResponse(200, parsed.ResponseBody ?? "", parsed.ResponseContentType ?? "text/plain");
    }

    /// <summary>Processes a stored inbound event; returns false when it failed (a retry job is scheduled when requested).</summary>
    public async Task<bool> ProcessEventAsync(ChannelAccount account, Guid inboundEventId, ChannelWebhookEvent ev, bool scheduleRetry, CancellationToken ct)
    {
        string? error = null;
        InboundOutcome? outcome = null;
        try
        {
            db.ChangeTracker.Clear();
            outcome = await processor.ProcessAsync(account, ev, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = TextNormalization.Truncate($"{ex.GetType().Name}: {ex.Message}", 2000);
            logger.LogWarning(ex, "Inbound {Provider} event {Event} failed", account.Provider, inboundEventId);
        }
        db.ChangeTracker.Clear();
        var row = await db.InboundEvents.FirstAsync(e => e.Id == inboundEventId, ct);
        row.Attempts++;
        if (error is null)
        {
            row.Status = outcome?.Outcome is InboundOutcomes.Ignored or InboundOutcomes.Rejected ? InboundEventStatus.Ignored : InboundEventStatus.Processed;
            row.ProcessedAt = db.Clock.UtcNow;
            row.Error = outcome?.Outcome is InboundOutcomes.Ignored or InboundOutcomes.Rejected ? outcome.Detail : null;
        }
        else
        {
            row.Status = InboundEventStatus.Failed;
            row.Error = error;
            if (scheduleRetry)
                jobs.Enqueue(InboundJobTypes.RetryEvent, new InboundEventPayload(row.Id), db.Clock.UtcNow.AddMinutes(1), $"inbound-retry:{row.Id:N}", maxAttempts: 8);
        }
        await db.SaveChangesAsync(ct);
        return error is null;
    }

    private static string HttpMethods(string method) => method.ToUpperInvariant();
}

public static class InboundJobTypes
{
    public const string RetryEvent = "inbound-event.retry";
}

/// <summary>Retries a failed inbound webhook event (durable job with backoff; dead-letters after max attempts).</summary>
public sealed class InboundEventRetryJob(DeskDbContext db, WebhookIngress ingress) : JobHandler<InboundEventPayload>
{
    public override string Type => InboundJobTypes.RetryEvent;

    protected override async Task ExecuteAsync(InboundEventPayload payload, BackgroundJob job, CancellationToken ct)
    {
        var row = await db.InboundEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == payload.InboundEventId && e.TenantId == job.TenantId, ct);
        if (row is null || row.Status is InboundEventStatus.Processed or InboundEventStatus.Ignored) return;
        var account = await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == row.ChannelAccountId, ct);
        var ev = Json.Deserialize<ChannelWebhookEvent>(row.PayloadJson);
        if (account is null || ev is null) return;
        if (!await ingress.ProcessEventAsync(account, row.Id, ev, scheduleRetry: false, ct))
            throw new InvalidOperationException($"Inbound event {row.Id} failed again.");
    }
}
