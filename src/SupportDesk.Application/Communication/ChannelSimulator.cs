using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Communication;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Communication;

/// <summary>
/// Channel simulator for demos and tests (admin.channels): injects inbound e-mail (composed and re-parsed as real MIME),
/// WhatsApp/SMS/Messenger messages, calls and delivery receipts through exactly the same pipeline as real providers, and
/// lists the outbound messages of an account.
/// </summary>
public sealed class ChannelSimulator(
    DeskDbContext db,
    RequestContext ctx,
    IMimeCodec mime,
    InboundEmailProcessor email,
    InboundMessagingProcessor messaging,
    OutboundService outbound,
    ConversationQueryService queries,
    TenantSettingsService settings)
{
    public async Task<InboundResultDto> EmailAsync(Guid accountId, SimulateEmailRequest r, CancellationToken ct)
    {
        var account = await AccountAsync(accountId, ct);
        if (account.Type != ChannelType.Email) throw new DomainException(UnsupportedCode, "This channel is not a mailbox.");
        var from = TextNormalization.NormalizeEmail(r.From) ?? throw new ValidationException(new Dictionary<string, string> { ["from"] = "Enter the sender's e-mail address." });
        var htmlBody = r.Html ?? "";
        var textBody = r.Text ?? (string.IsNullOrEmpty(r.Html) ? "" : TextNormalization.HtmlToText(r.Html));
        var domain = (await settings.EmailAsync(ct)).MessageIdDomain;
        var attachments = (r.Attachments ?? []).Select(a => new EmailAttachment(a.FileName, a.ContentType, Decode(a.Base64), a.ContentId, a.Inline)).ToList();
        var raw = mime.Compose(new OutboundEmail(from, r.FromName, r.To is { Count: > 0 } to ? to : [account.Address], r.Cc ?? [], [], null, r.Subject, htmlBody, textBody,
            EmailThreading.NormalizeMessageId(r.MessageId) ?? $"{Guid.NewGuid():N}@simulator.{domain}", EmailThreading.NormalizeMessageId(r.InReplyTo),
            (r.References ?? []).Select(EmailThreading.NormalizeMessageId).Where(x => x is not null).Select(x => x!).ToList(), attachments, r.Headers ?? []));
        var parsed = mime.Parse(raw);
        var outcome = await email.ProcessAsync(account, parsed, "simulator", ct);
        return Map(outcome);
    }

    public async Task<InboundResultDto> MessageAsync(Guid accountId, SimulateMessageRequest r, CancellationToken ct)
    {
        var account = await AccountAsync(accountId, ct);
        if (account.Type is not (ChannelType.WhatsApp or ChannelType.Sms or ChannelType.Facebook or ChannelType.Instagram))
            throw new DomainException(UnsupportedCode, "Messages can be simulated on WhatsApp, SMS, Facebook and Instagram channels.");
        if (string.IsNullOrWhiteSpace(r.From)) throw new ValidationException(new Dictionary<string, string> { ["from"] = "Enter the sender." });
        if (string.IsNullOrWhiteSpace(r.Text) && r.Media is null) throw new ValidationException(new Dictionary<string, string> { ["text"] = "Enter a message or attach media." });
        var media = r.Media is { } m ? new List<InboundMedia> { new(m.ContentType, m.FileName, Content: Decode(m.Base64)) } : [];
        var message = new InboundChannelMessage(r.ExternalId ?? $"sim.{Guid.NewGuid():N}", r.From.Trim(), r.FromName, account.Address, r.Text,
            media.Count > 0 ? (media[0].ContentType.StartsWith("image/") ? "image" : "document") : "text", media, db.Clock.UtcNow, Json.Serialize(new { simulated = true }));
        return Map(await messaging.ProcessMessageAsync(account, message, ct));
    }

    public async Task<InboundResultDto> CallAsync(Guid accountId, SimulateCallRequest r, CancellationToken ct)
    {
        var account = await AccountAsync(accountId, ct);
        if (account.Type != ChannelType.Voice) throw new DomainException(UnsupportedCode, "Calls can be simulated on voice channels.");
        if (string.IsNullOrWhiteSpace(r.Event) || string.IsNullOrWhiteSpace(r.From))
            throw new ValidationException(new Dictionary<string, string> { ["event"] = "Event and caller are required." });
        var call = new CallEvent(r.CallId ?? $"sim-call-{Guid.NewGuid():N}", r.Event, r.From.Trim(), r.To ?? account.Address, db.Clock.UtcNow, r.Direction ?? "inbound",
            r.DurationSeconds, r.RecordingUrl, r.AgentEmail, r.Disposition, r.Notes);
        return Map(await messaging.ProcessCallAsync(account, call, ct));
    }

    public async Task<InboundResultDto> StatusAsync(Guid accountId, SimulateStatusRequest r, CancellationToken ct)
    {
        var account = await AccountAsync(accountId, ct);
        if (!Enum.TryParse<MessageStatus>(r.Status, true, out var status) || status is not (MessageStatus.Sent or MessageStatus.Delivered or MessageStatus.Read or MessageStatus.Failed))
            throw new ValidationException(new Dictionary<string, string> { ["status"] = "Use Sent, Delivered, Read or Failed." });
        var applied = await outbound.ApplyProviderStatusAsync(account, new DeliveryStatusUpdate(r.ProviderMessageId, status, db.Clock.UtcNow, r.Error), ct);
        return new InboundResultDto(applied ? InboundOutcomes.Status : InboundOutcomes.Ignored, null, null, null, null, applied ? status.ToString() : "Unknown message id.");
    }

    public Task<List<OutboundMessageDto>> OutboundAsync(Guid accountId, int limit, CancellationToken ct) => queries.OutboundAsync(accountId, limit, ct);

    private async Task<ChannelAccount> AccountAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminChannels);
        var account = await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("Channel", id);
        if (!account.IsActive) throw new DomainException("channel_disabled", "The channel is disabled.");
        return account;
    }

    private static byte[] Decode(string base64)
    {
        try { return Convert.FromBase64String(base64); }
        catch (FormatException) { throw new ValidationException(new Dictionary<string, string> { ["attachments"] = "Attachment content must be base64." }); }
    }

    private const string UnsupportedCode = "simulator_unsupported";

    private static InboundResultDto Map(InboundOutcome o) => new(o.Outcome, o.MessageId, o.ConversationId, o.TicketId, o.TicketNumber, o.Detail);
}

/// <summary>Pushes omnichannel changes to connected agents and chat visitors after commit.</summary>
public sealed class CommunicationRealtimeHandler(DeskDbContext db, IRealtimePublisher realtime, IChatRealtime chat) : IOutboxHandler
{
    public string Name => "communication-realtime";
    public int Order => HandlerOrder.Realtime;

    private static readonly HashSet<string> Types =
        [EventTypes.MessageReceived, EventTypes.MessageStatusChanged, EventTypes.ConversationUpdated, EventTypes.ChatMessage, EventTypes.MessageQueued];

    public bool CanHandle(string eventType) => Types.Contains(eventType);

    public async Task HandleAsync(OutboxEvent ev, CancellationToken ct)
    {
        if (ev.TenantId is not { } tenantId) return;
        switch (ev.Type)
        {
            case EventTypes.ChatMessage:
            {
                var p = Json.Deserialize<ChatEventPayload>(ev.PayloadJson);
                if (p?.MessageId is not { } mid) return;
                var msg = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mid, ct);
                if (msg is null) return;
                await chat.MessageAsync(tenantId, p.ConversationId, ChatService.MapForVisitor(msg), ct);
                await realtime.ToConversationAsync(tenantId, p.ConversationId, "conversation.message", ConversationQueryService.MapMessage(msg), ct);
                await PushConversationAsync(tenantId, p.ConversationId, "message", msg.Id, ct);
                break;
            }
            case EventTypes.ConversationUpdated:
            {
                var p = Json.Deserialize<ChatEventPayload>(ev.PayloadJson);
                if (p is null) return;
                await PushConversationAsync(tenantId, p.ConversationId, "status", null, ct);
                break;
            }
            case EventTypes.MessageReceived:
            {
                using var doc = JsonDocument.Parse(ev.PayloadJson);
                var root = doc.RootElement;
                Guid? G(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g) ? g : null;
                var payload = new { messageId = G("messageId"), conversationId = G("conversationId"), ticketId = G("ticketId"), channel = root.TryGetProperty("channel", out var c) ? c.GetString() : null };
                await realtime.ToTenantAsync(tenantId, "conversation.message", payload, ct);
                if (payload.conversationId is { } cid)
                {
                    await realtime.ToConversationAsync(tenantId, cid, "conversation.message", payload, ct);
                    await PushConversationAsync(tenantId, cid, "message", payload.messageId, ct);
                }
                break;
            }
            case EventTypes.MessageStatusChanged or EventTypes.MessageQueued:
            {
                var p = Json.Deserialize<MessageStatusPayload>(ev.PayloadJson);
                if (p is null) return;
                if (p.TicketId is { } ticketId) await realtime.ToTicketAsync(tenantId, ticketId, "message.status", p, ct);
                var convId = await db.Messages.AsNoTracking().Where(m => m.Id == p.MessageId).Select(m => m.ConversationId).FirstOrDefaultAsync(ct);
                if (convId is { } c2) await PushConversationAsync(tenantId, c2, "delivery", p.MessageId, ct);
                break;
            }
        }
    }

    /// <summary>`conversation.updated` on the desk hub (tenant group): inbox lists and open conversations refresh from it.</summary>
    private async Task PushConversationAsync(Guid tenantId, Guid conversationId, string kind, Guid? messageId, CancellationToken ct)
    {
        var conv = await db.Conversations.AsNoTracking().Where(c => c.Id == conversationId)
            .Select(c => new { c.Id, c.Channel, c.Status, c.TicketId, c.AssignedUserId, c.LastMessageAt }).FirstOrDefaultAsync(ct);
        if (conv is null) return;
        await realtime.ToTenantAsync(tenantId, "conversation.updated", new
        {
            conversationId = conv.Id, channel = conv.Channel.ToString(), status = conv.Status.ToString(), ticketId = conv.TicketId,
            assignedUserId = conv.AssignedUserId, lastMessageAt = conv.LastMessageAt, kind, messageId
        }, ct);
    }
}
