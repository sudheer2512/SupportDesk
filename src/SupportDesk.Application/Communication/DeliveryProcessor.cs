using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Communication;

/// <summary>
/// Sends one queued <see cref="MessageDelivery"/> through its channel provider. Deliveries are claimed atomically
/// (Queued/Failed and due → Sending), so the delivery job and the retry sweeper can never send twice. Transient failures
/// back off (30 s·4ⁿ, max 1 h) and dead-letter after <see cref="MaxAttempts"/>; permanent failures (configuration,
/// rejected recipient) dead-letter immediately. Ticket threads and status history follow every transition.
/// </summary>
public sealed class DeliveryProcessor(
    DeskDbContext db,
    ChannelProviderRegistry registry,
    ChannelContextFactory contexts,
    FileService files,
    IEventPublisher events,
    UsageMeter usage,
    ILogger<DeliveryProcessor> logger)
{
    public const int MaxAttempts = 6;

    public async Task<bool> DeliverAsync(Guid deliveryId, CancellationToken ct)
    {
        var now = db.Clock.UtcNow;
        var stale = now.AddMinutes(-5);
        var claimed = await db.MessageDeliveries
            .Where(d => d.Id == deliveryId && (((d.Status == MessageStatus.Queued || d.Status == MessageStatus.Failed) && d.NextAttemptAt <= now)
                                              || (d.Status == MessageStatus.Sending && d.UpdatedAt < stale)))
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.Status, MessageStatus.Sending).SetProperty(d => d.UpdatedAt, now), ct);
        if (claimed == 0) return false;

        var delivery = await db.MessageDeliveries.FirstAsync(d => d.Id == deliveryId, ct);
        var msg = await db.Messages.FirstAsync(m => m.Id == delivery.MessageId, ct);
        var account = await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == msg.ChannelAccountId, ct);
        SendResult result;
        try
        {
            if (account is null || !account.IsActive) result = SendResult.Fail("The channel is disabled or was removed.", permanent: true);
            else if (msg.Channel == Channel.Email) result = await SendEmailAsync(account, msg, ct);
            else result = await SendMessagingAsync(account, msg, delivery.Recipient, ct);
        }
        catch (ChannelConfigurationException ex)
        {
            result = SendResult.Fail(ex.Message, permanent: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or TimeoutException or TaskCanceledException or InvalidOperationException
                                       or MailTransportException && !ct.IsCancellationRequested)
        {
            result = SendResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        await ApplyAsync(delivery, msg, result, ct);
        return result.Success;
    }

    private async Task<SendResult> SendEmailAsync(ChannelAccount account, Message msg, CancellationToken ct)
    {
        var provider = registry.RequireEmail(account.Provider);
        var meta = Json.DeserializeOr(msg.MetadataJson, new OutboundMeta());
        var attachments = new List<EmailAttachment>();
        foreach (var fileId in meta.AttachmentFileIds ?? [])
        {
            var (file, stream) = await files.OpenAsync(fileId, ct);
            await using (stream)
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                attachments.Add(new EmailAttachment(file.FileName, file.ContentType, ms.ToArray()));
            }
        }
        var email = new OutboundEmail(
            msg.FromAddress ?? account.Address, msg.FromName, Json.StringList(msg.ToJson), Json.StringList(msg.CcJson), meta.Bcc ?? [], meta.ReplyTo,
            msg.Subject ?? "", msg.BodyHtml ?? "", msg.BodyText ?? "", msg.InternetMessageId ?? msg.ExternalId, msg.InReplyTo, Json.StringList(msg.ReferencesJson),
            attachments, meta.Headers ?? []);
        return await provider.SendAsync(contexts.Create(account), email, ct);
    }

    private async Task<SendResult> SendMessagingAsync(ChannelAccount account, Message msg, string recipient, CancellationToken ct)
    {
        var provider = registry.RequireMessaging(account.Provider);
        var meta = Json.DeserializeOr(msg.MetadataJson, new OutboundMeta());
        return await provider.SendAsync(contexts.Create(account), new OutboundChannelMessage(msg.Id, recipient, msg.BodyText, meta.TemplateName, meta.TemplateLanguage,
            meta.TemplateParameters, meta.MediaUrl), ct);
    }

    private async Task ApplyAsync(MessageDelivery delivery, Message msg, SendResult result, CancellationToken ct)
    {
        var now = db.Clock.UtcNow;
        delivery.Attempts++;
        delivery.LastAttemptAt = now;
        delivery.UpdatedAt = now;
        string threadStatus;
        if (result.Success)
        {
            delivery.Status = MessageStatus.Sent;
            delivery.ProviderMessageId = result.ProviderMessageId;
            delivery.Error = null;
            delivery.StatusHistoryJson = DeliveryHistory.Append(delivery.StatusHistoryJson, "Sent", now, result.ProviderMessageId);
            if (OutboundService.Rank(msg.Status) < OutboundService.Rank(MessageStatus.Sent)) msg.Status = MessageStatus.Sent;
            msg.ProcessingError = null;
            if (msg.Channel != Channel.Email && result.ProviderMessageId is { Length: > 0 } pid && pid.Length <= 400 &&
                !await db.Messages.AnyAsync(m => m.ChannelAccountId == msg.ChannelAccountId && m.Direction == MessageDirection.Outbound && m.ExternalId == pid, ct))
                msg.ExternalId = pid;
            threadStatus = ThreadDeliveryStatus.Sent;
        }
        else
        {
            var dead = result.Permanent || delivery.Attempts >= MaxAttempts;
            delivery.Status = MessageStatus.Failed;
            delivery.Error = result.Error is null ? "Unknown error" : Domain.Rules.TextNormalization.Truncate(result.Error, 2000);
            delivery.NextAttemptAt = dead ? OutboundService.DeadLetter : now.AddSeconds(Math.Min(3600, 30 * Math.Pow(4, delivery.Attempts - 1)));
            delivery.StatusHistoryJson = DeliveryHistory.Append(delivery.StatusHistoryJson, dead ? "Failed" : "Retrying", now, delivery.Error);
            if (dead)
            {
                msg.Status = MessageStatus.Failed;
                msg.ProcessingError = delivery.Error;
                logger.LogWarning("Message {Message} on channel {Channel} dead-lettered after {Attempts} attempt(s): {Error}", msg.Id, msg.ChannelAccountId, delivery.Attempts, delivery.Error);
            }
            threadStatus = dead ? ThreadDeliveryStatus.Failed : ThreadDeliveryStatus.Queued;
        }
        if (msg.ThreadId is { } threadId && await db.TicketThreads.FirstOrDefaultAsync(t => t.Id == threadId, ct) is { } thread &&
            thread.DeliveryStatus is not (ThreadDeliveryStatus.Delivered or ThreadDeliveryStatus.Read))
        {
            thread.DeliveryStatus = threadStatus;
            if (!result.Success && threadStatus == ThreadDeliveryStatus.Failed) thread.ChannelMetadataJson = OutboundService.MergeMeta(thread.ChannelMetadataJson, "deliveryError", delivery.Error);
        }
        if (result.Success || delivery.NextAttemptAt == OutboundService.DeadLetter)
            events.Publish(EventTypes.MessageStatusChanged, "Message", msg.Id, new MessageStatusPayload(msg.Id, msg.TicketId, msg.ThreadId, delivery.Status.ToString(), delivery.Error));
        await db.SaveChangesAsync(ct);
        if (result.Success) await usage.IncrementAsync(UsageMetrics.MessagesSent, 1, ct);
    }
}

/// <summary>Thrown by mail transports for SMTP/IMAP protocol failures (treated as transient).</summary>
public sealed class MailTransportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Durable job that sends one message right after it was queued.</summary>
public sealed class DeliverMessageJob(DeliveryProcessor processor) : JobHandler<DeliverMessagePayload>
{
    public override string Type => DeliveryJobTypes.Deliver;

    protected override async Task ExecuteAsync(DeliverMessagePayload payload, BackgroundJob job, CancellationToken ct) =>
        await processor.DeliverAsync(payload.DeliveryId, ct);
}

/// <summary>Sweeps due retries and stuck deliveries for every tenant (every 15 s).</summary>
public sealed class MessageDeliveryTask(TenantScopeRunner tenants) : IRecurringTask
{
    public string Name => "message-delivery";
    public TimeSpan Interval => TimeSpan.FromSeconds(15);

    public Task RunAsync(CancellationToken ct) => tenants.ForEachActiveTenantAsync(Name, async (sp, _, token) =>
    {
        var db = sp.GetRequiredService<DeskDbContext>();
        var now = db.Clock.UtcNow;
        var stale = now.AddMinutes(-5);
        var due = await db.MessageDeliveries.AsNoTracking()
            .Where(d => ((d.Status == MessageStatus.Queued || d.Status == MessageStatus.Failed) && d.NextAttemptAt <= now) || (d.Status == MessageStatus.Sending && d.UpdatedAt < stale))
            .OrderBy(d => d.NextAttemptAt).Select(d => d.Id).Take(200).ToListAsync(token);
        var processor = sp.GetRequiredService<DeliveryProcessor>();
        foreach (var id in due)
        {
            db.ChangeTracker.Clear();
            await processor.DeliverAsync(id, token);
        }
    }, ct);
}

/// <summary>Turns committed public agent replies (ticket.reply_added) into channel messages.</summary>
public sealed class ChannelReplyHandler(OutboundService outbound) : IOutboxHandler
{
    public string Name => "channel-replies";
    public int Order => HandlerOrder.Integrations;
    public bool CanHandle(string eventType) => eventType == EventTypes.TicketReplyAdded;

    public async Task HandleAsync(OutboxEvent ev, CancellationToken ct)
    {
        var payload = Json.Deserialize<TicketEventPayload>(ev.PayloadJson);
        if (payload?.ThreadId is { } threadId) await outbound.QueueReplyAsync(threadId, ct);
    }
}

/// <summary><see cref="IOutboundMessenger"/> for automation and notifications: queues a message on the tenant's channel.</summary>
public sealed class OutboundMessenger(OutboundService outbound, ISystemMailer mailer, DeskDbContext db) : IOutboundMessenger
{
    public async Task<string> SendAsync(OutboundMessageRequest request, CancellationToken ct)
    {
        try
        {
            var msg = await outbound.QueueAdhocAsync(request, ct);
            return msg.Id.ToString();
        }
        catch (DomainException ex) when (ex.Code == "channel_unavailable" && request.Channel == Channel.Email && request.To is { Length: > 0 } to)
        {
            // No mailbox configured: fall back to the platform mail transport.
            await mailer.SendAsync(db.Request.TenantId, to, request.Subject, request.BodyHtml, ct: ct);
            return "system-mail";
        }
    }
}
