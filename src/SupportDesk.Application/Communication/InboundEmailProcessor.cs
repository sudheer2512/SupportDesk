using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Customers;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Communication;

/// <summary>Result of processing one inbound item: created, threaded, duplicate, ignored, rejected, status, call, processing.</summary>
public sealed record InboundOutcome(string Outcome, Guid? MessageId = null, Guid? ConversationId = null, Guid? TicketId = null, long? TicketNumber = null,
    string? Detail = null)
{
    public bool Completed => Outcome is not "failed";
}

public static class InboundOutcomes
{
    public const string Created = "created";
    public const string Threaded = "threaded";
    public const string Duplicate = "duplicate";
    public const string Ignored = "ignored";
    public const string Rejected = "rejected";
    public const string Processing = "processing";
    public const string Status = "status";
    public const string Call = "call";
}

/// <summary>
/// The inbound e-mail pipeline: dedupe (unique per mailbox + Message-ID, and across the tenant's mailboxes) → loop/auto-reply
/// and bounce detection → threading (In-Reply-To/References against stored Message-IDs → signed plus-address reply token →
/// subject tag [#1042] for the same contact/customer → new ticket; tickets closed beyond the reopen window get a new linked
/// ticket) → contact matching → quoted-history stripping (original kept as a raw .eml file) → attachments through the file
/// service (inline images rewritten from cid:) → ticket creation or customer message → auto-acknowledgement.
/// Safe to run again for the same message: a crash mid-way never creates a second ticket or thread.
/// </summary>
public sealed partial class InboundEmailProcessor(
    DeskDbContext db,
    TicketService tickets,
    ConversationService conversation,
    ContactResolver contacts,
    FileService files,
    HtmlSanitization html,
    TenantSettingsService settings,
    CommunicationKeys keys,
    IEventPublisher events,
    TicketHistory history,
    ThreadAppender threads,
    OutboundService outbound,
    ILogger<InboundEmailProcessor> logger)
{
    private const int MaxProcessingAttempts = 3;

    public async Task<InboundOutcome> ProcessAsync(ChannelAccount account, InboundEmail email, string? providerId, CancellationToken ct)
    {
        var now = db.Clock.UtcNow;
        var messageId = EmailThreading.NormalizeMessageId(email.MessageId) ?? SyntheticMessageId(email);
        if (messageId.Length > 400) messageId = $"sd-{TextNormalization.Sha256Hex(messageId)}@long-message-id";

        // 1. Claim the message (the unique index makes concurrent deliveries of the same mail safe).
        var msg = await db.Messages.FirstOrDefaultAsync(m => m.ChannelAccountId == account.Id && m.Direction == MessageDirection.Inbound && m.ExternalId == messageId, ct);
        if (msg is not null)
        {
            if (msg.Status is not (MessageStatus.Received or MessageStatus.Failed))
                return new InboundOutcome(InboundOutcomes.Duplicate, msg.Id, msg.ConversationId, msg.TicketId, null, "This message was already received.");
            if (msg.Status == MessageStatus.Received && msg.CreatedAt > now.AddMinutes(-5))
                return new InboundOutcome(InboundOutcomes.Processing, msg.Id, Detail: "This message is being processed.");
        }
        else
        {
            var sibling = await db.Messages.AsNoTracking()
                .Where(m => m.Direction == MessageDirection.Inbound && m.InternetMessageId == messageId && m.ChannelAccountId != account.Id && m.Status == MessageStatus.Processed)
                .Select(m => new { m.TicketId, m.ConversationId }).FirstOrDefaultAsync(ct);
            msg = new Message
            {
                ChannelAccountId = account.Id, Channel = Channel.Email, Direction = MessageDirection.Inbound, ExternalId = messageId, InternetMessageId = messageId,
                InReplyTo = EmailThreading.NormalizeMessageId(email.InReplyTo), ReferencesJson = email.References.Count > 0 ? Json.Serialize(email.References.TakeLast(50)) : null,
                FromAddress = TextNormalization.NormalizeEmail(email.FromAddress) ?? email.FromAddress, FromName = email.FromName, ToJson = Json.Serialize(email.To),
                CcJson = email.Cc.Count > 0 ? Json.Serialize(email.Cc) : null, Subject = TextNormalization.Truncate(email.Subject, 500),
                ContentType = "text", Status = sibling is null ? MessageStatus.Received : MessageStatus.Duplicate,
                TicketId = sibling?.TicketId, ProcessingError = sibling is null ? null : "Already received through another mailbox of this account.",
                MetadataJson = Json.Serialize(new { providerId, attempts = 0 }), OccurredAt = email.Date is { } d && d < now.AddMinutes(5) ? d : now, CreatedAt = now
            };
            db.Messages.Add(msg);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                return new InboundOutcome(InboundOutcomes.Duplicate, Detail: "This message was already received.");
            }
            if (sibling is not null) return new InboundOutcome(InboundOutcomes.Duplicate, msg.Id, sibling.ConversationId, sibling.TicketId, null, msg.ProcessingError);
        }

        try
        {
            return await ProcessClaimedAsync(account, email, msg, providerId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            var row = await db.Messages.FirstAsync(m => m.Id == msg.Id, ct);
            var meta = Json.Object(row.MetadataJson);
            var attempts = (meta.TryGetValue("attempts", out var a) && a.TryGetInt32(out var n) ? n : 0) + 1;
            row.MetadataJson = Json.Serialize(new { providerId, attempts });
            row.ProcessingError = TextNormalization.Truncate($"{ex.GetType().Name}: {ex.Message}", 2000);
            row.Status = attempts >= MaxProcessingAttempts ? MessageStatus.Rejected : MessageStatus.Failed;
            await db.SaveChangesAsync(ct);
            logger.LogWarning(ex, "Inbound e-mail {MessageId} on channel {Channel} failed (attempt {Attempt})", msg.Id, account.Id, attempts);
            if (row.Status == MessageStatus.Rejected)
                return new InboundOutcome(InboundOutcomes.Rejected, msg.Id, Detail: $"Gave up after {attempts} attempts: {row.ProcessingError}");
            throw;
        }
    }

    private async Task<InboundOutcome> ProcessClaimedAsync(ChannelAccount account, InboundEmail email, Message msg, string? providerId, CancellationToken ct)
    {
        var from = TextNormalization.NormalizeEmail(email.FromAddress);
        if (from is null) return await FinishAsync(msg, MessageStatus.Rejected, "The sender address is missing or invalid.", InboundOutcomes.Rejected, ct);

        // 2. Loop protection: mail from one of our own mailboxes is never processed.
        var ownAddresses = await db.ChannelAccounts.AsNoTracking().Where(a => a.Type == ChannelType.Email).Select(a => a.Address).ToListAsync(ct);
        if (ownAddresses.Contains(from, StringComparer.OrdinalIgnoreCase) || (email.Headers.TryGetValue("X-SupportDesk-Tenant", out var tenantHeader) && tenantHeader == db.Request.TenantId.ToString()))
            return await FinishAsync(msg, MessageStatus.Rejected, "Mail sent by this help desk was ignored (loop protection).", InboundOutcomes.Ignored, ct);

        var isAuto = email.IsDeliveryReport || EmailThreading.IsAutoGenerated(email.Headers, from);
        msg.IsAutoReply = isAuto;
        var ticketSettings = await settings.TicketsAsync(ct);
        var emailSettings = await settings.EmailAsync(ct);

        // 3. Threading.
        var refs = new List<string>();
        refs.AddRange(EmailThreading.ParseReferences(email.InReplyTo));
        foreach (var r in email.References) if (EmailThreading.NormalizeMessageId(r) is { } id && !refs.Contains(id)) refs.Add(id);
        Ticket? ticket = null;
        string? threadedBy = null;
        Message? referencedOutbound = null;
        if (refs.Count > 0)
        {
            var hit = await db.Messages.AsNoTracking().Where(m => m.InternetMessageId != null && refs.Contains(m.InternetMessageId) && m.TicketId != null && m.Id != msg.Id)
                .OrderByDescending(m => m.OccurredAt).FirstOrDefaultAsync(ct);
            if (hit is not null)
            {
                ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == hit.TicketId, ct);
                threadedBy = "headers";
                if (hit.Direction == MessageDirection.Outbound) referencedOutbound = hit;
            }
        }
        if (ticket is null && emailSettings.UseReplyTokens &&
            ReplyTokens.Parse(await keys.ReplyTokenKeyAsync(ct), EmailThreading.ExtractReplyToken(email.To.Concat(email.Cc))) is { } tokenNumber)
        {
            ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Number == tokenNumber, ct);
            threadedBy = ticket is null ? null : "reply-token";
        }

        if (isAuto)
        {
            if (referencedOutbound is not null && IsBounce(email, from))
                await outbound.MarkBouncedAsync(referencedOutbound.Id, TextNormalization.Truncate($"Bounced: {email.Subject}", 500), ct);
            if (ticket is null)
                return await FinishAsync(msg, MessageStatus.Processed, "Automatic reply or delivery report: no ticket was created.", InboundOutcomes.Ignored, ct);
            // Auto-replies on a known ticket are recorded as an internal notice (no reopen, no acknowledgement).
            var notice = await threads.AppendAsync(ticket.Id, _ => new TicketThread
            {
                Kind = ThreadKind.SystemMessage, Visibility = Visibility.Internal, Channel = Channel.Email, Direction = MessageDirection.Inbound, AuthorType = AuthorType.System,
                AuthorName = email.FromName ?? from, FromAddress = from, Subject = email.Subject, MessageId = msg.Id,
                BodyHtml = TextNormalization.TextToHtml($"Automatic reply received from {from}: {email.Subject}"),
                BodyText = $"Automatic reply received from {from}: {email.Subject}",
                ChannelMetadataJson = Json.Serialize(new { channel = "email", autoGenerated = true, messageId = msg.ExternalId })
            }, $"msg:{msg.Id:N}", ct);
            msg = await db.Messages.FirstAsync(m => m.Id == msg.Id, ct);
            msg.TicketId = ticket.Id;
            msg.ThreadId = notice.Id;
            return await FinishAsync(msg, MessageStatus.Processed, null, InboundOutcomes.Threaded, ct, ticket);
        }

        // 4. Contact matching (subject-tag threading needs the sender's identity).
        var contact = await contacts.FindAsync(Channel.Email, from, ct);
        if (ticket is null && EmailThreading.ExtractTicketNumber(email.Subject, TagPrefix(ticketSettings.NumberPrefix)) is { } tagged && contact is not null)
        {
            var candidate = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Number == tagged, ct);
            if (candidate is not null && (candidate.ContactId == contact.Id || (contact.CustomerId is not null && candidate.CustomerId == contact.CustomerId)))
            {
                ticket = candidate;
                threadedBy = "subject";
            }
        }
        if (contact is null)
        {
            if (!account.AcceptUnknownSenders && ticket is null)
                return await FinishAsync(msg, MessageStatus.Rejected, "The mailbox accepts mail from known contacts only.", InboundOutcomes.Rejected, ct);
            contact = await contacts.FindOrCreateAsync(from, null, email.FromName, null, Channel.Email, ct);
            await db.SaveChangesAsync(ct);
        }

        // 5. Body: strip quoted history from replies (the full original stays available as the raw .eml).
        var strip = ticket is not null && emailSettings.StripQuotedText;
        string bodyHtml;
        string? quoted;
        if (!string.IsNullOrWhiteSpace(email.HtmlBody))
        {
            var (reply, q) = strip ? QuoteStripper.SplitHtml(email.HtmlBody) : (email.HtmlBody, null);
            bodyHtml = html.Sanitize(reply);
            quoted = q;
        }
        else
        {
            var (reply, q) = strip ? EmailThreading.SplitQuotedText(email.TextBody) : ((email.TextBody ?? "").Trim(), null);
            bodyHtml = TextNormalization.TextToHtml(reply);
            quoted = q;
        }
        if (string.IsNullOrWhiteSpace(TextNormalization.HtmlToText(bodyHtml)) && email.Attachments.Count == 0) bodyHtml = "<p>(empty message)</p>";

        // 6. Raw message and attachments.
        Guid? rawFileId = null;
        if (email.Raw is { Length: > 0 } raw)
        {
            try
            {
                var rawFile = await files.UploadBytesAsync(raw, $"message-{TextNormalization.Sha256Hex(msg.ExternalId)[..16]}.eml", "message/rfc822", "email-raw", ct);
                rawFileId = rawFile.Id;
                msg.RawStorageKey = rawFile.StorageKey;
            }
            catch (DomainException ex)
            {
                logger.LogInformation("Raw copy of inbound message {MessageId} not stored: {Reason}", msg.Id, ex.Message);
            }
        }
        var fileIds = new List<Guid>();
        var inline = new Dictionary<Guid, string>();
        var rejected = new List<object>();
        foreach (var att in email.Attachments)
        {
            try
            {
                var f = await files.UploadBytesAsync(att.Content, att.FileName, att.ContentType, "attachment", ct);
                fileIds.Add(f.Id);
                if (att.ContentId is { Length: > 0 } cid && (att.Inline || bodyHtml.Contains("cid:" + cid, StringComparison.OrdinalIgnoreCase))) inline[f.Id] = cid;
            }
            catch (DomainException ex)
            {
                rejected.Add(new { fileName = att.FileName, reason = ex.Message });
            }
        }

        // 7. Conversation (one per e-mail thread and ticket).
        var rootKey = TextNormalization.Truncate(refs.FirstOrDefault() ?? msg.ExternalId, 440);
        var channelMeta = Json.Serialize(new
        {
            channel = "email", accountId = account.Id, mailbox = account.Address, messageId = msg.ExternalId, inReplyTo = msg.InReplyTo,
            threadedBy, hasQuotedText = quoted is not null, rawFileId, rejectedAttachments = rejected.Count > 0 ? rejected : null
        });
        var to = email.To.ToList();
        var cc = email.Cc.ToList();
        var now = db.Clock.UtcNow;
        string outcome;
        Ticket resultTicket;
        TicketThread? thread;

        if (ticket is not null)
        {
            var conv = await ConversationForTicketAsync(account, ticket.Id, rootKey, email.Subject, contact.Id, now, ct);
            thread = await conversation.AddCustomerMessageAsync(ticket.Id, new CustomerMessageInput(bodyHtml, TextNormalization.HtmlToText(bodyHtml), Channel.Email,
                contact.Id, email.FromName ?? contact.FullName, from, to, cc, email.Subject, msg.Id, conv.Id, channelMeta, fileIds, null, $"msg:{msg.Id:N}"), ct);
            if (thread is not null)
            {
                resultTicket = await db.Tickets.AsNoTracking().FirstAsync(t => t.Id == thread.TicketId, ct);
                outcome = InboundOutcomes.Threaded;
            }
            else
            {
                // Closed beyond the reopen window: a new ticket, linked to the old one.
                await CloseConversationAsync(conv.Id, ct);
                var fresh = await ConversationForTicketAsync(account, null, rootKey, email.Subject, contact.Id, now, ct);
                resultTicket = await CreateTicketAsync(account, email, msg, contact, bodyHtml, fileIds, fresh.Id, from, to, cc, channelMeta, ct);
                await LinkFollowUpAsync(resultTicket, ticket, ct);
                thread = await db.TicketThreads.AsNoTracking().FirstOrDefaultAsync(t => t.TicketId == resultTicket.Id && t.MessageId == msg.Id, ct);
                outcome = InboundOutcomes.Created;
            }
        }
        else
        {
            var conv = await ConversationForTicketAsync(account, null, msg.ExternalId, email.Subject, contact.Id, now, ct);
            resultTicket = await CreateTicketAsync(account, email, msg, contact, bodyHtml, fileIds, conv.Id, from, to, cc, channelMeta, ct);
            thread = await db.TicketThreads.AsNoTracking().FirstOrDefaultAsync(t => t.TicketId == resultTicket.Id && t.MessageId == msg.Id, ct);
            outcome = InboundOutcomes.Created;
        }

        if (thread is not null && inline.Count > 0) await RewriteInlineImagesAsync(thread.Id, resultTicket.Id, inline, outcome == InboundOutcomes.Created, ct);

        // 8. Finish the channel message and link the conversation.
        msg = await db.Messages.FirstAsync(m => m.Id == msg.Id, ct);
        msg.TicketId = resultTicket.Id;
        msg.ThreadId = thread?.Id;
        msg.ContactId = contact.Id;
        msg.BodyHtml = bodyHtml;
        msg.BodyText = TextNormalization.HtmlToText(bodyHtml);
        msg.MetadataJson = Json.Serialize(new { providerId, rawFileId, threadedBy, quoted = quoted is null ? null : TextNormalization.Truncate(TextNormalization.HtmlToText(quoted), 4000) });
        var convId = thread?.ConversationId;
        if (convId is { } cId && await db.Conversations.FirstOrDefaultAsync(c => c.Id == cId, ct) is { } convRow)
        {
            msg.ConversationId = convRow.Id;
            convRow.TicketId = resultTicket.Id;
            convRow.LastMessageAt = now;
            convRow.ContactId ??= contact.Id;
        }
        var result = await FinishAsync(msg, MessageStatus.Processed, null, outcome, ct, resultTicket);

        // 9. Automatic acknowledgement for new tickets (never for auto-generated mail; rate-limited per sender).
        if (outcome == InboundOutcomes.Created && account.AutoAcknowledge && ticketSettings.AutoAcknowledge)
        {
            try { await outbound.QueueAcknowledgementAsync(account, resultTicket.Id, msg.Id, ct); }
            catch (Exception ex) when (ex is DomainException or ChannelConfigurationException)
            {
                logger.LogInformation("Acknowledgement for ticket {Ticket} not queued: {Reason}", resultTicket.Number, ex.Message);
            }
        }
        return result;
    }

    private async Task<Ticket> CreateTicketAsync(ChannelAccount account, InboundEmail email, Message msg, Contact contact, string bodyHtml, List<Guid> fileIds,
        Guid conversationId, string from, List<string> to, List<string> cc, string channelMeta, CancellationToken ct)
    {
        var subject = EmailThreading.StripTicketTag(email.Subject ?? "").Trim();
        if (subject.Length == 0) subject = "(no subject)";
        return await tickets.CreateAsync(new CreateTicketRequest
        {
            Subject = subject, DescriptionHtml = bodyHtml, Channel = Channel.Email.ToString(), ContactId = contact.Id, CustomerId = contact.CustomerId,
            BrandId = account.BrandId, DepartmentId = account.DepartmentId, AttachmentFileIds = fileIds, ClientRequestId = $"msg:{msg.Id:N}"
        }, new TicketCreationSource(CreateInitialThread: true, ChannelAccountId: account.Id, ConversationId: conversationId, MessageId: msg.Id, FromAddress: from,
            AuthorName: email.FromName ?? contact.FullName, To: to, Cc: cc, ChannelMetadataJson: channelMeta, EnforceRequiredFields: false), ct);
    }

    private async Task<Conversation> ConversationForTicketAsync(ChannelAccount account, Guid? ticketId, string key, string? subject, Guid contactId, DateTime now, CancellationToken ct)
    {
        var conv = ticketId is { } tid
            ? await db.Conversations.FirstOrDefaultAsync(c => c.ChannelAccountId == account.Id && c.TicketId == tid && c.Status != ConversationStatus.Closed, ct)
            : null;
        conv ??= await db.Conversations.FirstOrDefaultAsync(c => c.ChannelAccountId == account.Id && c.ExternalKey == key && c.Status != ConversationStatus.Closed
                                                                && (ticketId == null ? c.TicketId == null : c.TicketId == ticketId), ct);
        if (conv is not null) return conv;
        if (await db.Conversations.AnyAsync(c => c.ChannelAccountId == account.Id && c.ExternalKey == key && c.Status != ConversationStatus.Closed, ct))
            key = TextNormalization.Truncate($"{key}#{SequentialGuid.New():N}", 440);
        conv = new Conversation
        {
            ChannelAccountId = account.Id, Channel = Channel.Email, ExternalKey = key, ContactId = contactId, TicketId = ticketId, Status = ConversationStatus.Open,
            Subject = TextNormalization.Truncate(subject, 500), StartedAt = now, LastMessageAt = now
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync(ct);
        return conv;
    }

    private async Task CloseConversationAsync(Guid id, CancellationToken ct) =>
        await db.Conversations.Where(c => c.Id == id).ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, ConversationStatus.Closed).SetProperty(c => c.UpdatedAt, db.Clock.UtcNow), ct);

    private async Task LinkFollowUpAsync(Ticket created, Ticket previous, CancellationToken ct)
    {
        var t = await db.Tickets.FirstAsync(x => x.Id == created.Id, ct);
        db.TicketLinks.Add(new TicketLink { SourceTicketId = created.Id, TargetTicketId = previous.Id, LinkType = TicketLinkType.Related, CreatedAt = db.Clock.UtcNow });
        history.Record(t, TicketEventTypes.Linked, $"Follow-up of closed ticket #{previous.Number} (customer replied after the reopen window)", null, new { previousTicketId = previous.Id });
        await db.SaveChangesAsync(ct);
    }

    private async Task RewriteInlineImagesAsync(Guid threadId, Guid ticketId, Dictionary<Guid, string> inline, bool isInitial, CancellationToken ct)
    {
        var attachments = await db.TicketAttachments.Where(a => a.ThreadId == threadId && inline.Keys.Contains(a.FileId)).ToListAsync(ct);
        if (attachments.Count == 0) return;
        var thread = await db.TicketThreads.FirstAsync(t => t.Id == threadId, ct);
        var ticket = isInitial ? await db.Tickets.FirstAsync(t => t.Id == ticketId, ct) : null;
        foreach (var a in attachments)
        {
            var cid = inline[a.FileId];
            a.IsInline = true;
            a.ContentId = cid;
            var url = $"/api/v1/attachments/{a.Id}/download";
            thread.BodyHtml = ReplaceCid(thread.BodyHtml, cid, url);
            if (ticket is not null) ticket.DescriptionHtml = ReplaceCid(ticket.DescriptionHtml, cid, url);
        }
        await db.SaveChangesAsync(ct);
    }

    private static string? ReplaceCid(string? body, string cid, string url) =>
        body?.Replace($"cid:{cid}", url, StringComparison.OrdinalIgnoreCase);

    private async Task<InboundOutcome> FinishAsync(Message msg, MessageStatus status, string? error, string outcome, CancellationToken ct, Ticket? ticket = null)
    {
        msg.Status = status;
        msg.ProcessingError = error;
        if (status == MessageStatus.Processed && outcome is InboundOutcomes.Created or InboundOutcomes.Threaded)
            events.Publish(EventTypes.MessageReceived, "Message", msg.Id, new { messageId = msg.Id, ticketId = msg.TicketId, conversationId = msg.ConversationId, channel = msg.Channel.ToString() });
        await db.SaveChangesAsync(ct);
        return new InboundOutcome(outcome, msg.Id, msg.ConversationId, ticket?.Id ?? msg.TicketId, ticket?.Number, error);
    }

    private static bool IsBounce(InboundEmail email, string from) =>
        email.IsDeliveryReport || BounceSenderRegex().IsMatch(from) || email.Subject.Contains("Undeliverable", StringComparison.OrdinalIgnoreCase)
        || email.Subject.Contains("Delivery Status Notification", StringComparison.OrdinalIgnoreCase);

    /// <summary>"#" → null (any [#n] tag); "SD-" → "SD-".</summary>
    public static string? TagPrefix(string numberPrefix) => numberPrefix is "#" or "" ? null : numberPrefix;

    private static string SyntheticMessageId(InboundEmail email)
    {
        var basis = email.Raw is { Length: > 0 } raw
            ? TextNormalization.Sha256Hex(raw)
            : TextNormalization.Sha256Hex($"{email.FromAddress}|{email.Subject}|{email.Date:O}|{email.TextBody}|{email.HtmlBody}");
        return $"sd-{basis[..40]}@missing-message-id";
    }

    [GeneratedRegex(@"^(mailer-daemon|postmaster)@", RegexOptions.IgnoreCase)]
    private static partial Regex BounceSenderRegex();
}
