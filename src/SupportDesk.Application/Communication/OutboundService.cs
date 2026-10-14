using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Communication;

public static class ThreadDeliveryStatus
{
    public const string Queued = "Queued";
    public const string Sent = "Sent";
    public const string Delivered = "Delivered";
    public const string Read = "Read";
    public const string Failed = "Failed";
    public const string Bounced = "Bounced";
}

public sealed record DeliverMessagePayload(Guid DeliveryId);

/// <summary>Outbound message metadata stored in Message.MetadataJson (everything needed to (re)build the provider request).</summary>
public sealed record OutboundMeta
{
    /// <summary>reply, ack, csat, notice, adhoc, chat</summary>
    public string Kind { get; init; } = "reply";
    public string? ReplyTo { get; init; }
    public List<string>? Bcc { get; init; }
    public List<Guid>? AttachmentFileIds { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public string? TemplateName { get; init; }
    public string? TemplateLanguage { get; init; }
    public List<string>? TemplateParameters { get; init; }
    public string? MediaUrl { get; init; }
    public string? FailureReason { get; init; }
}

/// <summary>
/// Turns public agent replies and system notices into channel messages: reply-from-channel (same mailbox / number /
/// conversation), brand display name, agent + mailbox signatures, RFC 5322 threading headers (Message-ID, In-Reply-To,
/// References), subject tag, signed reply-token Reply-To; consent and WhatsApp 24-hour window enforcement. Each message is
/// stored with a <see cref="MessageDelivery"/> and handed to the delivery job (retries/backoff/dead-letter).
/// </summary>
public sealed class OutboundService(
    DeskDbContext db,
    TenantSettingsService settings,
    CommunicationKeys keys,
    IJobQueue jobs,
    IEventPublisher events,
    HtmlSanitization html,
    ILogger<OutboundService> logger)
{
    public static readonly DateTime DeadLetter = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    // ───────────────────────────── Agent replies ─────────────────────────────

    public async Task QueueReplyAsync(Guid threadId, CancellationToken ct)
    {
        var thread = await db.TicketThreads.FirstOrDefaultAsync(t => t.Id == threadId, ct);
        if (thread is null || thread.Visibility != Visibility.Public || thread.Direction != MessageDirection.Outbound || thread.DeliveryStatus != ThreadDeliveryStatus.Queued) return;
        if (await db.Messages.AnyAsync(m => m.ThreadId == threadId && m.Direction == MessageDirection.Outbound, ct)) return;
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == thread.TicketId, ct);
        if (ticket is null) return;
        var contact = ticket.ContactId is { } cid ? await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct) : null;

        if (thread.Channel == Channel.Chat && await DeliverToLiveChatAsync(thread, ticket, ct)) return;
        if (thread.Channel is Channel.WhatsApp or Channel.Sms or Channel.Facebook or Channel.Instagram)
        {
            await QueueMessagingReplyAsync(thread, ticket, contact, ct);
            return;
        }
        await QueueEmailReplyAsync(thread, ticket, contact, ct);
    }

    private async Task QueueEmailReplyAsync(TicketThread thread, Ticket ticket, Contact? contact, CancellationToken ct)
    {
        var account = await EmailAccountForTicketAsync(ticket, ct);
        if (account is null)
        {
            await FailThreadAsync(thread, "No outbound e-mail channel is configured for this brand.", ct);
            return;
        }
        var to = Json.StringList(thread.ToJson).Select(TextNormalization.NormalizeEmail).Where(e => e is not null).Select(e => e!).Distinct().ToList();
        if (to.Count == 0)
        {
            await FailThreadAsync(thread, "The ticket has no customer e-mail address to reply to.", ct);
            return;
        }
        var agent = thread.AuthorUserId is { } uid ? await db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => new { u.DisplayName, u.Signature }).FirstOrDefaultAsync(ct) : null;
        var body = thread.BodyHtml ?? "";
        if (!string.IsNullOrWhiteSpace(agent?.Signature)) body += $"<div class=\"sd-signature\">{html.Sanitize(agent.Signature)}</div>";
        if (!string.IsNullOrWhiteSpace(account.SignatureHtml)) body += $"<div class=\"sd-mailbox-signature\">{account.SignatureHtml}</div>";
        var attachmentIds = await db.TicketAttachments.AsNoTracking().Where(a => a.ThreadId == thread.Id && a.Visibility == Visibility.Public)
            .Select(a => a.FileId).ToListAsync(ct);
        var msg = await BuildEmailAsync(account, ticket, to, Json.StringList(thread.CcJson), Json.StringList(thread.BccJson), $"Re: {ticket.Subject}", body,
            new OutboundMeta { Kind = "reply", AttachmentFileIds = attachmentIds.Count > 0 ? attachmentIds : null }, threaded: true, ct);
        msg.ThreadId = thread.Id;
        msg.ContactId = contact?.Id;
        msg.AuthorUserId = thread.AuthorUserId;
        msg.ConversationId = await db.Conversations.Where(c => c.TicketId == ticket.Id && c.ChannelAccountId == account.Id).OrderByDescending(c => c.LastMessageAt)
            .Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        thread.MessageId = msg.Id;
        thread.FromAddress ??= account.Address;
        var blocked = contact is null ? null : await ConsentBlockAsync(contact, Channel.Email, ConsentPurpose.Transactional, ct);
        await EnqueueAsync(msg, string.Join(", ", to), blocked, ct);
        if (blocked is not null) thread.DeliveryStatus = ThreadDeliveryStatus.Failed;
        await db.SaveChangesAsync(ct);
    }

    private async Task QueueMessagingReplyAsync(TicketThread thread, Ticket ticket, Contact? contact, CancellationToken ct)
    {
        var type = ChannelProviders.ToChannelType(thread.Channel)!.Value;
        var conv = await db.Conversations.AsNoTracking().Where(c => c.TicketId == ticket.Id && c.Channel == thread.Channel)
            .OrderByDescending(c => c.LastMessageAt).FirstOrDefaultAsync(ct);
        var account = conv is not null ? await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == conv.ChannelAccountId && a.IsActive, ct) : null;
        if (account is null && ticket.ChannelAccountId is { } tid)
            account = await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == tid && a.Type == type && a.IsActive, ct);
        account ??= await db.ChannelAccounts.Where(a => a.Type == type && a.IsActive).OrderBy(a => a.CreatedAt).FirstOrDefaultAsync(ct);
        if (account is null)
        {
            await FailThreadAsync(thread, $"No active {type} channel is configured.", ct);
            return;
        }
        var recipient = conv?.ExternalKey ?? RecipientFor(type, contact);
        if (string.IsNullOrWhiteSpace(recipient))
        {
            await FailThreadAsync(thread, $"The contact has no {type} address.", ct);
            return;
        }
        var meta = new OutboundMeta { Kind = "reply" };
        string? blocked = contact is null ? null : await ConsentBlockAsync(contact, thread.Channel, ConsentPurpose.Transactional, ct);
        if (blocked is null && type == ChannelType.WhatsApp && (conv?.ServiceWindowEndsAt ?? DateTime.MinValue) < db.Clock.UtcNow)
        {
            var settingsMap = Json.Object(account.SettingsJson);
            var template = settingsMap.TryGetValue("serviceWindowTemplate", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(template))
                blocked = "The WhatsApp 24-hour customer service window has closed. A pre-approved template is required: configure 'serviceWindowTemplate' on the channel or wait for the customer to write again.";
            else
                meta = meta with
                {
                    TemplateName = template,
                    TemplateLanguage = settingsMap.TryGetValue("serviceWindowTemplateLanguage", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : "en",
                    TemplateParameters = [contact?.FullName ?? "there", $"#{ticket.Number}"]
                };
        }
        var now = db.Clock.UtcNow;
        var text = TextNormalization.HtmlToText(thread.BodyHtml);
        var attachmentNames = await db.TicketAttachments.AsNoTracking().Where(a => a.ThreadId == thread.Id && a.Visibility == Visibility.Public).Select(a => a.FileName).ToListAsync(ct);
        if (attachmentNames.Count > 0) text += $"\n\n(Attachments are available in your support portal: {string.Join(", ", attachmentNames)})";
        var msg = new Message
        {
            ChannelAccountId = account.Id, ConversationId = conv?.Id, TicketId = ticket.Id, ThreadId = thread.Id, Channel = thread.Channel, Direction = MessageDirection.Outbound,
            FromAddress = account.Address, FromName = account.DisplayName ?? account.Name, ToJson = Json.Serialize(new[] { recipient }), BodyText = text,
            BodyHtml = thread.BodyHtml, ContentType = meta.TemplateName is null ? "text" : "template", ContactId = contact?.Id, AuthorUserId = thread.AuthorUserId,
            Status = MessageStatus.Queued, OccurredAt = now, CreatedAt = now
        };
        msg.ExternalId = $"out:{msg.Id:N}";
        msg.MetadataJson = Json.Serialize(meta);
        thread.MessageId = msg.Id;
        await EnqueueAsync(msg, recipient, blocked, ct);
        if (blocked is not null) thread.DeliveryStatus = ThreadDeliveryStatus.Failed;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Delivers a chat reply straight to the live visitor when the chat is still open. Returns false when the chat has ended.</summary>
    private async Task<bool> DeliverToLiveChatAsync(TicketThread thread, Ticket ticket, CancellationToken ct)
    {
        var conv = await db.Conversations.AsNoTracking()
            .Where(c => c.TicketId == ticket.Id && c.Channel == Channel.Chat && c.Status != ConversationStatus.Closed)
            .OrderByDescending(c => c.LastMessageAt).FirstOrDefaultAsync(ct);
        if (conv is null) return false;
        var now = db.Clock.UtcNow;
        var msg = new Message
        {
            ChannelAccountId = conv.ChannelAccountId, ConversationId = conv.Id, TicketId = ticket.Id, ThreadId = thread.Id, Channel = Channel.Chat,
            Direction = MessageDirection.Outbound, ExternalId = $"thread:{thread.Id:N}", FromName = thread.AuthorName, BodyText = TextNormalization.HtmlToText(thread.BodyHtml),
            BodyHtml = thread.BodyHtml, ContentType = "text", AuthorUserId = thread.AuthorUserId, Status = MessageStatus.Delivered, OccurredAt = now, CreatedAt = now,
            MetadataJson = Json.Serialize(new OutboundMeta { Kind = "chat" })
        };
        db.Messages.Add(msg);
        thread.MessageId = msg.Id;
        thread.DeliveryStatus = ThreadDeliveryStatus.Delivered;
        thread.ConversationId ??= conv.Id;
        await db.Conversations.Where(c => c.Id == conv.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.LastMessageAt, now).SetProperty(c => c.UpdatedAt, now), ct);
        events.Publish(EventTypes.ChatMessage, "Conversation", conv.Id, new ChatEventPayload(conv.Id, msg.Id));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Already delivered by the agent chat path (unique external id per thread).
            db.ChangeTracker.Clear();
        }
        return true;
    }

    // ───────────────────────────── Acknowledgements & notices ─────────────────────────────

    /// <summary>Automatic "we received your request" reply on the same mailbox (Auto-Submitted: auto-replied; rate-limited per sender).</summary>
    public async Task QueueAcknowledgementAsync(ChannelAccount account, Guid ticketId, Guid inboundMessageId, CancellationToken ct)
    {
        var ticket = await db.Tickets.AsNoTracking().FirstAsync(t => t.Id == ticketId, ct);
        var inbound = await db.Messages.AsNoTracking().FirstAsync(m => m.Id == inboundMessageId, ct);
        var to = TextNormalization.NormalizeEmail(inbound.FromAddress);
        if (to is null) return;
        var since = db.Clock.UtcNow.AddHours(-1);
        var max = (await keys.SettingsAsync(ct)).MaxAcknowledgementsPerSenderPerHour;
        var toJson = Json.Serialize(new[] { to });
        var recent = await db.Messages.CountAsync(m => m.ChannelAccountId == account.Id && m.Direction == MessageDirection.Outbound && m.ToJson == toJson
                                                       && m.CreatedAt > since && m.MetadataJson!.Contains("\"kind\":\"ack\""), ct);
        if (recent >= max)
        {
            logger.LogInformation("Acknowledgement to {Recipient} suppressed (rate limit {Max}/hour)", to, max);
            return;
        }
        var display = await DisplayNumberAsync(ticket, ct);
        var brand = ticket.BrandId is { } b ? await db.Brands.AsNoTracking().FirstOrDefaultAsync(x => x.Id == b, ct) : null;
        var body = $"<p>Hello{(inbound.FromName is { Length: > 0 } n ? " " + System.Net.WebUtility.HtmlEncode(n) : "")},</p>" +
                   $"<p>Thank you for contacting {System.Net.WebUtility.HtmlEncode(brand?.Name ?? account.DisplayName ?? account.Name)}. We have received your request and logged it as <strong>{display}</strong>.</p>" +
                   "<p>Our team will get back to you shortly. To add more information, simply reply to this e-mail and keep the reference in the subject.</p>";
        var msg = await BuildEmailAsync(account, ticket, [to], [], [], $"Re: {ticket.Subject}", body,
            new OutboundMeta { Kind = "ack", Headers = new() { ["Auto-Submitted"] = "auto-replied", ["X-Auto-Response-Suppress"] = "All", ["Precedence"] = "auto_reply" } },
            threaded: true, ct);
        msg.ContactId = inbound.ContactId;
        msg.ConversationId = inbound.ConversationId;
        await EnqueueAsync(msg, to, null, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Sends a system notice about a ticket (CSAT survey, status notice) through the ticket's channel: e-mail on the
    /// ticket's mailbox when the contact has an address, otherwise the conversation's messaging channel. Returns the
    /// message id, or null when no route exists.
    /// </summary>
    public async Task<Message?> QueueTicketNoticeAsync(Ticket ticket, string subject, string bodyHtml, string text, string kind, ConsentPurpose purpose, CancellationToken ct)
    {
        var contact = ticket.ContactId is { } cid ? await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct) : null;
        if (contact is null) return null;
        if (ticket.Channel is Channel.WhatsApp or Channel.Sms or Channel.Facebook or Channel.Instagram)
        {
            var conv = await db.Conversations.AsNoTracking().Where(c => c.TicketId == ticket.Id && c.Channel == ticket.Channel).OrderByDescending(c => c.LastMessageAt).FirstOrDefaultAsync(ct);
            var type = ChannelProviders.ToChannelType(ticket.Channel)!.Value;
            var account = conv is null ? null : await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == conv.ChannelAccountId && a.IsActive, ct);
            account ??= await db.ChannelAccounts.Where(a => a.Type == type && a.IsActive).OrderBy(a => a.CreatedAt).FirstOrDefaultAsync(ct);
            var recipient = conv?.ExternalKey ?? RecipientFor(type, contact);
            var windowOpen = type != ChannelType.WhatsApp || (conv?.ServiceWindowEndsAt ?? DateTime.MinValue) > db.Clock.UtcNow;
            if (account is not null && recipient is not null && windowOpen)
            {
                var now = db.Clock.UtcNow;
                var m = new Message
                {
                    ChannelAccountId = account.Id, ConversationId = conv?.Id, TicketId = ticket.Id, Channel = ticket.Channel, Direction = MessageDirection.Outbound,
                    FromAddress = account.Address, ToJson = Json.Serialize(new[] { recipient }), BodyText = text, ContentType = "text", ContactId = contact.Id,
                    Status = MessageStatus.Queued, OccurredAt = now, CreatedAt = now, MetadataJson = Json.Serialize(new OutboundMeta { Kind = kind })
                };
                m.ExternalId = $"out:{m.Id:N}";
                await EnqueueAsync(m, recipient, await ConsentBlockAsync(contact, ticket.Channel, purpose, ct), ct);
                await db.SaveChangesAsync(ct);
                return m;
            }
        }
        if (TextNormalization.NormalizeEmail(contact.Email) is not { } email) return null;
        var mailbox = await EmailAccountForTicketAsync(ticket, ct);
        if (mailbox is null) return null;
        var msg = await BuildEmailAsync(mailbox, ticket, [email], [], [], subject, bodyHtml,
            new OutboundMeta { Kind = kind, Headers = new() { ["Auto-Submitted"] = "auto-generated" } }, threaded: true, ct);
        msg.ContactId = contact.Id;
        await EnqueueAsync(msg, email, await ConsentBlockAsync(contact, Channel.Email, purpose, ct), ct);
        await db.SaveChangesAsync(ct);
        return msg;
    }

    /// <summary>Ad-hoc message for automation/notifications (IOutboundMessenger). Returns the stored message.</summary>
    public async Task<Message> QueueAdhocAsync(OutboundMessageRequest r, CancellationToken ct)
    {
        var contact = r.ContactId is { } cid ? await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct) : null;
        var ticket = r.TicketId is { } tid ? await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tid, ct) : null;
        var user = r.UserId is { } uid ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == uid, ct) : null;
        if (r.Channel == Channel.Email)
        {
            var to = TextNormalization.NormalizeEmail(r.To ?? contact?.Email ?? user?.Email)
                     ?? throw new DomainException("recipient_missing", "No e-mail address is available for the recipient.");
            var account = ticket is not null ? await EmailAccountForTicketAsync(ticket, ct) : await DefaultEmailAccountAsync(null, ct);
            if (account is null) throw new DomainException("channel_unavailable", "No outbound e-mail channel is configured.");
            var msg = await BuildEmailAsync(account, ticket, [to], [], [], r.Subject, html.Sanitize(r.BodyHtml), new OutboundMeta { Kind = "adhoc" }, threaded: ticket is not null, ct);
            msg.ContactId = contact?.Id;
            await EnqueueAsync(msg, to, contact is null ? null : await ConsentBlockAsync(contact, Channel.Email, ConsentPurpose.Transactional, ct), ct);
            await db.SaveChangesAsync(ct);
            return msg;
        }
        var type = ChannelProviders.ToChannelType(r.Channel) ?? throw new DomainException("channel_unavailable", $"Messages cannot be sent through {r.Channel}.");
        var acc = await db.ChannelAccounts.Where(a => a.Type == type && a.IsActive).OrderBy(a => a.CreatedAt).FirstOrDefaultAsync(ct)
                  ?? throw new DomainException("channel_unavailable", $"No active {type} channel is configured.");
        var recipient = r.To ?? RecipientFor(type, contact) ?? throw new DomainException("recipient_missing", $"No {type} address is available for the recipient.");
        var now = db.Clock.UtcNow;
        var m = new Message
        {
            ChannelAccountId = acc.Id, TicketId = ticket?.Id, Channel = r.Channel, Direction = MessageDirection.Outbound, FromAddress = acc.Address,
            ToJson = Json.Serialize(new[] { recipient }), Subject = r.Subject, BodyText = TextNormalization.HtmlToText(r.BodyHtml), BodyHtml = html.Sanitize(r.BodyHtml),
            ContentType = r.TemplateName is null ? "text" : "template", ContactId = contact?.Id, Status = MessageStatus.Queued, OccurredAt = now, CreatedAt = now,
            MetadataJson = Json.Serialize(new OutboundMeta { Kind = "adhoc", TemplateName = r.TemplateName, TemplateLanguage = r.TemplateName is null ? null : "en" })
        };
        m.ExternalId = $"out:{m.Id:N}";
        await EnqueueAsync(m, recipient, contact is null ? null : await ConsentBlockAsync(contact, r.Channel, ConsentPurpose.Transactional, ct), ct);
        await db.SaveChangesAsync(ct);
        return m;
    }

    // ───────────────────────────── Provider feedback ─────────────────────────────

    public async Task MarkBouncedAsync(Guid outboundMessageId, string reason, CancellationToken ct)
    {
        var msg = await db.Messages.FirstOrDefaultAsync(m => m.Id == outboundMessageId && m.Direction == MessageDirection.Outbound, ct);
        if (msg is null) return;
        var now = db.Clock.UtcNow;
        msg.Status = MessageStatus.Failed;
        msg.ProcessingError = reason;
        foreach (var d in await db.MessageDeliveries.Where(d => d.MessageId == msg.Id).ToListAsync(ct))
        {
            d.Status = MessageStatus.Failed;
            d.Error = reason;
            d.NextAttemptAt = DeadLetter;
            d.UpdatedAt = now;
            d.StatusHistoryJson = DeliveryHistory.Append(d.StatusHistoryJson, "Bounced", now, reason);
        }
        if (msg.ThreadId is { } threadId) await SetThreadStatusAsync(threadId, ThreadDeliveryStatus.Bounced, reason, ct);
        events.Publish(EventTypes.MessageStatusChanged, "Message", msg.Id, new MessageStatusPayload(msg.Id, msg.TicketId, msg.ThreadId, "Bounced", reason));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Applies a provider delivery receipt (sent/delivered/read/failed). Status never moves backwards except to Failed.</summary>
    public async Task<bool> ApplyProviderStatusAsync(ChannelAccount account, DeliveryStatusUpdate update, CancellationToken ct)
    {
        var msg = await db.Messages.FirstOrDefaultAsync(m => m.ChannelAccountId == account.Id && m.Direction == MessageDirection.Outbound && m.ExternalId == update.ProviderMessageId, ct);
        if (msg is null)
        {
            var byDelivery = await db.MessageDeliveries.Where(d => d.ProviderMessageId == update.ProviderMessageId).Select(d => d.MessageId).FirstOrDefaultAsync(ct);
            if (byDelivery != Guid.Empty) msg = await db.Messages.FirstOrDefaultAsync(m => m.Id == byDelivery && m.ChannelAccountId == account.Id, ct);
        }
        if (msg is null) return false;
        var delivery = await db.MessageDeliveries.Where(d => d.MessageId == msg.Id).OrderByDescending(d => d.UpdatedAt).FirstOrDefaultAsync(ct);
        var now = db.Clock.UtcNow;
        var advance = update.Status == MessageStatus.Failed || Rank(update.Status) > Rank(msg.Status);
        if (delivery is not null)
        {
            delivery.StatusHistoryJson = DeliveryHistory.Append(delivery.StatusHistoryJson, update.Status.ToString(), update.OccurredAt, update.Error);
            if (advance)
            {
                delivery.Status = update.Status;
                if (update.Status == MessageStatus.Failed) { delivery.Error = update.Error ?? "Reported as failed by the provider."; delivery.NextAttemptAt = DeadLetter; }
            }
            delivery.UpdatedAt = now;
        }
        if (advance)
        {
            msg.Status = update.Status;
            if (update.Status == MessageStatus.Failed) msg.ProcessingError = update.Error;
            if (msg.ThreadId is { } threadId) await SetThreadStatusAsync(threadId, update.Status.ToString(), update.Error, ct);
            events.Publish(EventTypes.MessageStatusChanged, "Message", msg.Id, new MessageStatusPayload(msg.Id, msg.TicketId, msg.ThreadId, update.Status.ToString(), update.Error));
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    public static int Rank(MessageStatus s) => s switch
    {
        MessageStatus.Queued => 0,
        MessageStatus.Sending => 1,
        MessageStatus.Sent => 2,
        MessageStatus.Delivered => 3,
        MessageStatus.Read => 4,
        _ => -1
    };

    // ───────────────────────────── Helpers ─────────────────────────────

    public async Task<ChannelAccount?> EmailAccountForTicketAsync(Ticket ticket, CancellationToken ct)
    {
        if (ticket.ChannelAccountId is { } id && await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == id && a.Type == ChannelType.Email && a.IsActive, ct) is { } own)
            return own;
        return await DefaultEmailAccountAsync(ticket.BrandId, ct);
    }

    public async Task<ChannelAccount?> DefaultEmailAccountAsync(Guid? brandId, CancellationToken ct)
    {
        var accounts = await db.ChannelAccounts.Where(a => a.Type == ChannelType.Email && a.IsActive).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        return accounts.FirstOrDefault(a => a.BrandId == brandId && brandId != null && IsDefault(a)) ?? accounts.FirstOrDefault(a => a.BrandId == brandId && brandId != null)
               ?? accounts.FirstOrDefault(IsDefault) ?? accounts.FirstOrDefault(a => a.BrandId == null) ?? accounts.FirstOrDefault();
    }

    private static bool IsDefault(ChannelAccount a) => Json.Object(a.SettingsJson).TryGetValue("isDefault", out var v) && v.ValueKind == JsonValueKind.True;

    private async Task<Message> BuildEmailAsync(ChannelAccount account, Ticket? ticket, IReadOnlyList<string> to, IReadOnlyList<string> cc, IReadOnlyList<string> bcc,
        string subject, string bodyHtml, OutboundMeta meta, bool threaded, CancellationToken ct)
    {
        var emailSettings = await settings.EmailAsync(ct);
        var now = db.Clock.UtcNow;
        var msg = new Message
        {
            ChannelAccountId = account.Id, TicketId = ticket?.Id, Channel = Channel.Email, Direction = MessageDirection.Outbound, FromAddress = account.Address,
            ToJson = Json.Serialize(to), CcJson = cc.Count > 0 ? Json.Serialize(cc) : null, BodyHtml = bodyHtml, BodyText = TextNormalization.HtmlToText(bodyHtml),
            ContentType = "text", Status = MessageStatus.Queued, OccurredAt = now, CreatedAt = now
        };
        var messageId = EmailThreading.NewMessageId(msg.Id, emailSettings.MessageIdDomain);
        msg.ExternalId = messageId;
        msg.InternetMessageId = messageId;
        var replyTo = (string?)null;
        if (ticket is not null)
        {
            var display = await DisplayNumberAsync(ticket, ct);
            var tag = display.TrimStart('[').TrimEnd(']');
            var isReply = subject.TrimStart().StartsWith("Re:", StringComparison.OrdinalIgnoreCase);
            var core = EmailThreading.StripReplyPrefixes(EmailThreading.StripTicketTag(subject));
            subject = (isReply ? "Re: " : "") + EmailThreading.TagSubject(core, tag);
            if (threaded)
            {
                var parent = await db.Messages.AsNoTracking()
                    .Where(m => m.TicketId == ticket.Id && m.Channel == Channel.Email && m.InternetMessageId != null && m.Id != msg.Id
                                && m.Status != MessageStatus.Duplicate && m.Status != MessageStatus.Rejected)
                    .OrderByDescending(m => m.Direction == MessageDirection.Inbound).ThenByDescending(m => m.OccurredAt)
                    .Select(m => new { m.InternetMessageId, m.ReferencesJson }).FirstOrDefaultAsync(ct);
                if (parent is not null)
                {
                    msg.InReplyTo = parent.InternetMessageId;
                    var refs = Json.StringList(parent.ReferencesJson).Select(EmailThreading.NormalizeMessageId).Where(x => x is not null).Select(x => x!).ToList();
                    refs.Add(parent.InternetMessageId!);
                    msg.ReferencesJson = Json.Serialize(refs.Distinct().TakeLast(20));
                }
            }
            if (emailSettings.UseReplyTokens)
                replyTo = EmailThreading.WithReplyToken(account.Address, ReplyTokens.Create(await keys.ReplyTokenKeyAsync(ct), ticket.Number));
        }
        msg.Subject = TextNormalization.Truncate(subject, 500);
        var brandName = account.BrandId is { } bid ? await db.Brands.AsNoTracking().Where(b => b.Id == bid).Select(b => b.Name).FirstOrDefaultAsync(ct)
            : ticket?.BrandId is { } tb ? await db.Brands.AsNoTracking().Where(b => b.Id == tb).Select(b => b.Name).FirstOrDefaultAsync(ct) : null;
        msg.FromName = account.DisplayName ?? brandName ?? account.Name;
        var headers = new Dictionary<string, string>(meta.Headers ?? []);
        if (ticket is not null) headers["X-SupportDesk-Ticket"] = ticket.Number.ToString();
        msg.MetadataJson = Json.Serialize(meta with { ReplyTo = replyTo, Bcc = bcc.Count > 0 ? bcc.ToList() : null, Headers = headers });
        return msg;
    }

    /// <summary>Adds the message with its delivery (queued, or failed immediately with <paramref name="blockedReason"/>) and the delivery job.</summary>
    private async Task EnqueueAsync(Message msg, string recipient, string? blockedReason, CancellationToken ct)
    {
        var now = db.Clock.UtcNow;
        var delivery = new MessageDelivery { MessageId = msg.Id, Recipient = TextNormalization.Truncate(recipient, 440), NextAttemptAt = now, UpdatedAt = now };
        if (blockedReason is null)
        {
            delivery.Status = MessageStatus.Queued;
            delivery.StatusHistoryJson = DeliveryHistory.Append("[]", "Queued", now, null);
            msg.Status = MessageStatus.Queued;
        }
        else
        {
            delivery.Status = MessageStatus.Failed;
            delivery.Error = blockedReason;
            delivery.NextAttemptAt = DeadLetter;
            delivery.StatusHistoryJson = DeliveryHistory.Append("[]", "Failed", now, blockedReason);
            msg.Status = MessageStatus.Failed;
            msg.ProcessingError = blockedReason;
            msg.MetadataJson = Json.Serialize(Json.DeserializeOr(msg.MetadataJson, new OutboundMeta()) with { FailureReason = blockedReason });
        }
        db.Messages.Add(msg);
        db.MessageDeliveries.Add(delivery);
        if (blockedReason is null)
        {
            jobs.Enqueue(DeliveryJobTypes.Deliver, new DeliverMessagePayload(delivery.Id), idempotencyKey: $"deliver:{delivery.Id:N}");
            events.Publish(EventTypes.MessageQueued, "Message", msg.Id, new MessageStatusPayload(msg.Id, msg.TicketId, msg.ThreadId, "Queued", null));
        }
        else
        {
            events.Publish(EventTypes.MessageStatusChanged, "Message", msg.Id, new MessageStatusPayload(msg.Id, msg.TicketId, msg.ThreadId, "Failed", blockedReason));
        }
        await Task.CompletedTask;
    }

    private async Task FailThreadAsync(TicketThread thread, string reason, CancellationToken ct)
    {
        thread.DeliveryStatus = ThreadDeliveryStatus.Failed;
        thread.ChannelMetadataJson = MergeMeta(thread.ChannelMetadataJson, "deliveryError", reason);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Reply {Thread} not sent: {Reason}", thread.Id, reason);
    }

    private async Task SetThreadStatusAsync(Guid threadId, string status, string? error, CancellationToken ct)
    {
        var thread = await db.TicketThreads.FirstOrDefaultAsync(t => t.Id == threadId, ct);
        if (thread is null) return;
        thread.DeliveryStatus = status;
        if (error is not null) thread.ChannelMetadataJson = MergeMeta(thread.ChannelMetadataJson, "deliveryError", error);
    }

    public static string MergeMeta(string? json, string key, object? value)
    {
        var map = Json.Object(json);
        map[key] = Json.ToElement(value);
        return Json.Serialize(map);
    }

    /// <summary>Returns the opt-out reason when the contact withdrew consent for the channel and purpose.</summary>
    public async Task<string?> ConsentBlockAsync(Contact contact, Channel channel, ConsentPurpose purpose, CancellationToken ct)
    {
        var consent = await db.ContactConsents.AsNoTracking().FirstOrDefaultAsync(c => c.ContactId == contact.Id && c.Channel == channel && c.Purpose == purpose, ct);
        return consent?.Status == ConsentStatus.OptedOut
            ? $"{contact.FullName} has opted out of {channel} messages ({purpose.ToString().ToLowerInvariant()}) since {consent.RecordedAt:yyyy-MM-dd}; the message was not sent."
            : null;
    }

    public static string? RecipientFor(ChannelType type, Contact? contact)
    {
        if (contact is null) return null;
        var phone = type switch
        {
            ChannelType.WhatsApp => contact.WhatsAppNumber ?? contact.Mobile ?? contact.Phone,
            ChannelType.Sms or ChannelType.Voice => contact.Mobile ?? contact.Phone ?? contact.WhatsAppNumber,
            _ => null
        };
        return TextNormalization.NormalizePhone(phone) is { } digits ? "+" + digits : null;
    }

    private async Task<string> DisplayNumberAsync(Ticket ticket, CancellationToken ct)
    {
        var prefix = (await settings.TicketsAsync(ct)).NumberPrefix;
        return string.IsNullOrEmpty(prefix) || prefix == "#" ? $"[#{ticket.Number}]" : prefix.EndsWith('-') ? $"[{prefix}{ticket.Number}]" : $"[{prefix}-{ticket.Number}]";
    }
}

public sealed record MessageStatusPayload(Guid MessageId, Guid? TicketId, Guid? ThreadId, string Status, string? Error);

public sealed record ChatEventPayload(Guid ConversationId, Guid? MessageId = null, string? Status = null);

public static class DeliveryJobTypes
{
    public const string Deliver = "message.deliver";
}

/// <summary>Append-only status history of a delivery ([{"status":"Sent","at":"…","detail":"…"}]).</summary>
public static class DeliveryHistory
{
    public sealed record Entry(string Status, DateTime At, string? Detail);

    public static string Append(string? json, string status, DateTime at, string? detail)
    {
        var list = Json.DeserializeOr(json, new List<Entry>());
        if (list.Count > 0 && list[^1].Status == status && list[^1].Detail == detail) return Json.Serialize(list);
        list.Add(new Entry(status, at, detail is null ? null : TextNormalization.Truncate(detail, 500)));
        if (list.Count > 50) list = list.TakeLast(50).ToList();
        return Json.Serialize(list);
    }
}
