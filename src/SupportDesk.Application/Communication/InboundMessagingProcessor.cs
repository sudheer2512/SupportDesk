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

/// <summary>
/// Inbound pipeline for messaging channels (WhatsApp, SMS, Messenger/Instagram) and telephony: dedupe per provider
/// message id, contact matching by channel identity (phone, WhatsApp id, PSID), one open conversation per customer and
/// channel (WhatsApp 24-hour service window tracked), media through the file service, ticket creation or customer
/// message, delivery receipts, and call records with call-log threads (missed calls and voicemails create tickets).
/// </summary>
public sealed class InboundMessagingProcessor(
    DeskDbContext db,
    TicketService tickets,
    ConversationService conversation,
    ContactResolver contacts,
    FileService files,
    ChannelProviderRegistry registry,
    ChannelContextFactory contexts,
    IEventPublisher events,
    ThreadAppender threads,
    OutboundService outbound,
    ILogger<InboundMessagingProcessor> logger)
{
    public async Task<InboundOutcome> ProcessAsync(ChannelAccount account, ChannelWebhookEvent ev, CancellationToken ct)
    {
        if (ev.Message is { } m) return await ProcessMessageAsync(account, m, ct);
        if (ev.Status is { } s)
            return await outbound.ApplyProviderStatusAsync(account, s, ct)
                ? new InboundOutcome(InboundOutcomes.Status, Detail: s.Status.ToString())
                : new InboundOutcome(InboundOutcomes.Ignored, Detail: "Unknown message id.");
        if (ev.Call is { } c) return await ProcessCallAsync(account, c, ct);
        return new InboundOutcome(InboundOutcomes.Ignored, Detail: $"Event '{ev.EventType}' is not handled.");
    }

    // ───────────────────────────── Messages ─────────────────────────────

    public async Task<InboundOutcome> ProcessMessageAsync(ChannelAccount account, InboundChannelMessage m, CancellationToken ct)
    {
        var channel = ChannelProviders.ToChannel(account.Type);
        var now = db.Clock.UtcNow;
        var externalId = TextNormalization.Truncate(m.ExternalId, 400);
        var msg = await db.Messages.FirstOrDefaultAsync(x => x.ChannelAccountId == account.Id && x.Direction == MessageDirection.Inbound && x.ExternalId == externalId, ct);
        if (msg is not null)
        {
            if (msg.Status is not (MessageStatus.Received or MessageStatus.Failed))
                return new InboundOutcome(InboundOutcomes.Duplicate, msg.Id, msg.ConversationId, msg.TicketId, null, "This message was already received.");
            if (msg.Status == MessageStatus.Received && msg.CreatedAt > now.AddMinutes(-5))
                return new InboundOutcome(InboundOutcomes.Processing, msg.Id, Detail: "This message is being processed.");
        }
        else
        {
            msg = new Message
            {
                ChannelAccountId = account.Id, Channel = channel, Direction = MessageDirection.Inbound, ExternalId = externalId, FromAddress = m.From, FromName = m.FromName,
                ToJson = m.To is null ? null : Json.Serialize(new[] { m.To }), BodyText = m.Text, ContentType = m.ContentType, Status = MessageStatus.Received,
                MetadataJson = m.MetadataJson, OccurredAt = m.OccurredAt == default || m.OccurredAt > now.AddMinutes(5) ? now : m.OccurredAt, CreatedAt = now
            };
            db.Messages.Add(msg);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                return new InboundOutcome(InboundOutcomes.Duplicate, Detail: "This message was already received.");
            }
        }

        try
        {
            return await ProcessClaimedMessageAsync(account, channel, m, msg.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            var row = await db.Messages.FirstAsync(x => x.Id == msg.Id, ct);
            row.Status = MessageStatus.Failed;
            row.ProcessingError = TextNormalization.Truncate($"{ex.GetType().Name}: {ex.Message}", 2000);
            await db.SaveChangesAsync(ct);
            logger.LogWarning(ex, "Inbound {Channel} message {Message} failed", channel, msg.Id);
            throw;
        }
    }

    private async Task<InboundOutcome> ProcessClaimedMessageAsync(ChannelAccount account, Channel channel, InboundChannelMessage m, Guid messageId, CancellationToken ct)
    {
        var isPhone = channel is Channel.WhatsApp or Channel.Sms;
        var key = isPhone ? (TextNormalization.NormalizePhone(m.From) is { } digits ? "+" + digits : null) : m.From.Trim();
        if (string.IsNullOrEmpty(key))
            return await FinishAsync(messageId, MessageStatus.Rejected, "The sender id is missing or invalid.", InboundOutcomes.Rejected, null, null, ct);

        // Contact by channel identity.
        var contact = await contacts.FindAsync(channel, key, ct);
        if (contact is null && isPhone) contact = await contacts.FindAsync(Channel.Phone, key, ct);
        var conv = await db.Conversations.FirstOrDefaultAsync(c => c.ChannelAccountId == account.Id && c.ExternalKey == key && c.Status != ConversationStatus.Closed, ct);
        if (contact is null && conv?.ContactId is { } convContact) contact = await db.Contacts.FirstOrDefaultAsync(c => c.Id == convContact, ct);
        if (contact is null)
        {
            if (!account.AcceptUnknownSenders)
                return await FinishAsync(messageId, MessageStatus.Rejected, "This channel accepts messages from known contacts only.", InboundOutcomes.Rejected, null, null, ct);
            contact = await contacts.FindOrCreateAsync(null, isPhone ? key : null, m.FromName, null, channel, ct);
            if (!isPhone) await contacts.AttachIdentityAsync(contact.Id, channel, key, verified: true, ct);
            await db.SaveChangesAsync(ct);
        }
        else if (await db.ContactIdentities.AnyAsync(i => i.ContactId == contact.Id && i.Channel == channel, ct) == false)
        {
            await contacts.AttachIdentityAsync(contact.Id, channel, key, verified: true, ct);
            await db.SaveChangesAsync(ct);
        }

        // Conversation: one open conversation per customer and channel account.
        var now = db.Clock.UtcNow;
        Ticket? ticket = null;
        if (conv?.TicketId is { } existingTicket)
        {
            ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == existingTicket, ct);
            if (ticket is null || ticket.StatusCategory == StatusCategory.Closed)
            {
                conv.Status = ConversationStatus.Closed;
                await db.SaveChangesAsync(ct);
                conv = null;
                ticket = null;
            }
        }
        if (conv is null)
        {
            conv = new Conversation
            {
                ChannelAccountId = account.Id, Channel = channel, ExternalKey = key, ContactId = contact.Id, Status = ConversationStatus.Open,
                Subject = $"{ChannelLabel(channel)} conversation with {contact.FullName}", StartedAt = now, LastMessageAt = now
            };
            db.Conversations.Add(conv);
        }
        conv.LastMessageAt = now;
        conv.ContactId ??= contact.Id;
        if (channel == Channel.WhatsApp) conv.ServiceWindowEndsAt = (m.OccurredAt == default ? now : m.OccurredAt).AddHours(24);
        await db.SaveChangesAsync(ct);

        // Media.
        var fileIds = new List<Guid>();
        var rejected = new List<object>();
        var provider = account.Provider == ChannelProviders.Simulator ? null : registry.Messaging(account.Provider);
        foreach (var media in m.Media)
        {
            try
            {
                var bytes = media.Content ?? (provider is null ? null : await provider.DownloadMediaAsync(contexts.Create(account), media, ct));
                if (bytes is null) { rejected.Add(new { media.FileName, reason = "Media could not be downloaded." }); continue; }
                var name = media.FileName ?? $"{channel.ToString().ToLowerInvariant()}-media{ExtensionFor(media.ContentType)}";
                fileIds.Add((await files.UploadBytesAsync(bytes, name, media.ContentType, "attachment", ct)).Id);
            }
            catch (Exception ex) when (ex is DomainException or HttpRequestException or ChannelConfigurationException)
            {
                rejected.Add(new { media.FileName, reason = ex.Message });
            }
        }

        var text = m.Text ?? m.Media.Select(x => x.Caption).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? (m.Media.Count > 0 ? "(attachment)" : "(empty message)");
        var bodyHtml = TextNormalization.TextToHtml(text);
        var meta = Json.Serialize(new { channel = channel.ToString().ToLowerInvariant(), accountId = account.Id, from = key, externalId = m.ExternalId, contentType = m.ContentType,
            rejectedMedia = rejected.Count > 0 ? rejected : null });
        TicketThread? thread = null;
        if (ticket is not null)
        {
            thread = await conversation.AddCustomerMessageAsync(ticket.Id, new CustomerMessageInput(bodyHtml, text, channel, contact.Id, m.FromName ?? contact.FullName, key,
                null, null, null, messageId, conv.Id, meta, fileIds, null, $"msg:{messageId:N}"), ct);
        }
        string outcome;
        if (thread is null)
        {
            var created = await tickets.CreateAsync(new CreateTicketRequest
            {
                Subject = $"{ChannelLabel(channel)} from {contact.FullName}: {TextNormalization.Truncate(text.ReplaceLineEndings(" "), 80)}", DescriptionHtml = bodyHtml,
                Channel = channel.ToString(), ContactId = contact.Id, CustomerId = contact.CustomerId, BrandId = account.BrandId, DepartmentId = account.DepartmentId,
                AttachmentFileIds = fileIds, ClientRequestId = $"msg:{messageId:N}"
            }, new TicketCreationSource(CreateInitialThread: true, ChannelAccountId: account.Id, ConversationId: conv.Id, MessageId: messageId, FromAddress: key,
                AuthorName: m.FromName ?? contact.FullName, ChannelMetadataJson: meta, EnforceRequiredFields: false), ct);
            ticket = created;
            thread = await db.TicketThreads.AsNoTracking().FirstOrDefaultAsync(t => t.TicketId == created.Id && t.MessageId == messageId, ct);
            outcome = InboundOutcomes.Created;
        }
        else
        {
            outcome = InboundOutcomes.Threaded;
        }
        var convRow = await db.Conversations.FirstAsync(c => c.Id == conv.Id, ct);
        convRow.TicketId = thread?.TicketId ?? ticket!.Id;
        convRow.LastMessageAt = db.Clock.UtcNow;
        var msg = await db.Messages.FirstAsync(x => x.Id == messageId, ct);
        msg.TicketId = convRow.TicketId;
        msg.ThreadId = thread?.Id;
        msg.ConversationId = convRow.Id;
        msg.ContactId = contact.Id;
        msg.BodyHtml = bodyHtml;
        msg.BodyText = text;
        return await FinishAsync(messageId, MessageStatus.Processed, null, outcome, convRow.TicketId, ticket!.Number, ct);
    }

    private async Task<InboundOutcome> FinishAsync(Guid messageId, MessageStatus status, string? error, string outcome, Guid? ticketId, long? number, CancellationToken ct)
    {
        var msg = await db.Messages.FirstAsync(x => x.Id == messageId, ct);
        msg.Status = status;
        msg.ProcessingError = error;
        if (status == MessageStatus.Processed)
            events.Publish(EventTypes.MessageReceived, "Message", msg.Id, new { messageId = msg.Id, ticketId = msg.TicketId, conversationId = msg.ConversationId, channel = msg.Channel.ToString() });
        await db.SaveChangesAsync(ct);
        return new InboundOutcome(outcome, msg.Id, msg.ConversationId, ticketId ?? msg.TicketId, number, error);
    }

    // ───────────────────────────── Calls ─────────────────────────────

    public async Task<InboundOutcome> ProcessCallAsync(ChannelAccount account, CallEvent e, CancellationToken ct)
    {
        var callId = TextNormalization.Truncate(e.CallId, 400);
        var inbound = !string.Equals(e.Direction, "outbound", StringComparison.OrdinalIgnoreCase);
        var record = await db.CallRecords.FirstOrDefaultAsync(c => c.ChannelAccountId == account.Id && c.ExternalCallId == callId, ct);
        if (record is null)
        {
            record = new CallRecord
            {
                ChannelAccountId = account.Id, ExternalCallId = callId, Direction = inbound ? CallDirection.Inbound : CallDirection.Outbound,
                FromNumber = e.From, ToNumber = e.To, Status = CallStatus.Ringing, StartedAt = e.OccurredAt
            };
            db.CallRecords.Add(record);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                record = await db.CallRecords.FirstAsync(c => c.ChannelAccountId == account.Id && c.ExternalCallId == callId, ct);
            }
        }
        var customerNumber = inbound ? record.FromNumber : record.ToNumber;
        var contact = record.ContactId is { } existingContact ? await db.Contacts.FirstOrDefaultAsync(c => c.Id == existingContact, ct) : await contacts.FindAsync(Channel.Phone, customerNumber, ct);
        if (e.AgentEmail is { Length: > 0 } agentEmail && TextNormalization.NormalizeEmail(agentEmail) is { } normalizedAgent)
            record.AgentUserId ??= await db.Users.Where(u => u.EmailNormalized == normalizedAgent && u.IsActive).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);

        var before = record.Status;
        var at = e.OccurredAt == default ? db.Clock.UtcNow : e.OccurredAt;
        switch (e.EventType.ToLowerInvariant())
        {
            case "started" or "ringing" or "initiated":
                break;
            case "answered" or "in-progress" or "connected":
                if (record.Status == CallStatus.Ringing) record.Status = CallStatus.InProgress;
                record.AnsweredAt ??= at;
                break;
            case "ended" or "completed" or "hangup":
                record.EndedAt ??= at;
                if (record.Status is CallStatus.Ringing or CallStatus.InProgress)
                    record.Status = record.AnsweredAt is not null ? CallStatus.Completed : inbound ? CallStatus.Missed : CallStatus.Failed;
                break;
            case "missed" or "no-answer" or "busy" or "abandoned":
                record.EndedAt ??= at;
                if (record.Status is not CallStatus.Voicemail) record.Status = CallStatus.Missed;
                break;
            case "voicemail":
                record.EndedAt ??= at;
                record.Status = CallStatus.Voicemail;
                break;
            case "failed":
                record.EndedAt ??= at;
                record.Status = CallStatus.Failed;
                break;
            default:
                return new InboundOutcome(InboundOutcomes.Ignored, Detail: $"Unknown call event '{e.EventType}'.");
        }
        if (e.DurationSeconds is { } dur && dur >= 0) record.DurationSeconds = dur;
        else if (record.EndedAt is { } end && record.AnsweredAt is { } ans) record.DurationSeconds = Math.Max(0, (int)(end - ans).TotalSeconds);
        if (!string.IsNullOrWhiteSpace(e.RecordingUrl) && Uri.TryCreate(e.RecordingUrl, UriKind.Absolute, out var rec) && rec.Scheme is "https" or "http")
            record.RecordingUrl = e.RecordingUrl;
        if (e.Disposition is { Length: > 0 } disp) record.Disposition = TextNormalization.Truncate(disp, 200);
        if (e.Notes is { Length: > 0 } notes) record.Notes = TextNormalization.Truncate(notes, 4000);

        var terminal = record.Status is CallStatus.Completed or CallStatus.Missed or CallStatus.Voicemail or CallStatus.Failed;
        var settings = contexts.Create(account);
        var wantsTicket = inbound && (record.Status is CallStatus.Missed or CallStatus.Voicemail
                                      || (record.Status == CallStatus.Completed && settings.Bool("createTicketsForAnsweredCalls", true)));
        if (terminal && wantsTicket && contact is null)
        {
            contact = await contacts.FindOrCreateAsync(null, customerNumber, null, null, Channel.Phone, ct);
            await db.SaveChangesAsync(ct);
        }
        record.ContactId ??= contact?.Id;

        // Conversation per call (shown in the omnichannel inbox).
        var conv = await db.Conversations.FirstOrDefaultAsync(c => c.ChannelAccountId == account.Id && c.ExternalKey == callId, ct);
        if (conv is null)
        {
            conv = new Conversation
            {
                ChannelAccountId = account.Id, Channel = Channel.Phone, ExternalKey = callId, ContactId = contact?.Id, Status = ConversationStatus.Open,
                Subject = $"{(inbound ? "Call from" : "Call to")} {contact?.FullName ?? customerNumber}", StartedAt = record.StartedAt, LastMessageAt = at
            };
            db.Conversations.Add(conv);
        }
        conv.LastMessageAt = at;
        conv.ContactId ??= contact?.Id;
        if (terminal) conv.Status = ConversationStatus.Closed;
        else if (record.Status == CallStatus.InProgress) { conv.Status = ConversationStatus.AgentActive; conv.AssignedUserId ??= record.AgentUserId; }
        await db.SaveChangesAsync(ct);

        if (terminal && before != record.Status)
        {
            if (record.TicketId is null && wantsTicket && contact is not null)
                record.TicketId = await TicketForCallAsync(account, record, contact, ct);
            if (record.TicketId is { } ticketId)
            {
                await AppendCallLogAsync(ticketId, record, contact, ct);
                var callRow = await db.CallRecords.FirstAsync(c => c.Id == record.Id, ct);
                callRow.TicketId = ticketId;
                var convRow = await db.Conversations.FirstAsync(c => c.ExternalKey == callId && c.ChannelAccountId == account.Id, ct);
                convRow.TicketId ??= ticketId;
                await db.SaveChangesAsync(ct);
            }
        }
        events.Publish(EventTypes.ConversationUpdated, "Conversation", conv.Id, new ChatEventPayload(conv.Id, null, record.Status.ToString()));
        await db.SaveChangesAsync(ct);
        var ticketNumber = record.TicketId is { } t ? await db.Tickets.Where(x => x.Id == t).Select(x => (long?)x.Number).FirstOrDefaultAsync(ct) : null;
        return new InboundOutcome(InboundOutcomes.Call, null, conv.Id, record.TicketId, ticketNumber, record.Status.ToString());
    }

    private async Task<Guid?> TicketForCallAsync(ChannelAccount account, CallRecord record, Contact contact, CancellationToken ct)
    {
        var now = db.Clock.UtcNow;
        if (record.Status is CallStatus.Missed or CallStatus.Voicemail)
        {
            // Repeated missed calls within two hours land on the same open ticket instead of flooding the queue.
            var recent = await db.CallRecords.AsNoTracking()
                .Where(c => c.ContactId == contact.Id && c.TicketId != null && c.Id != record.Id && (c.Status == CallStatus.Missed || c.Status == CallStatus.Voicemail)
                            && c.StartedAt > now.AddHours(-2))
                .OrderByDescending(c => c.StartedAt).Select(c => c.TicketId).FirstOrDefaultAsync(ct);
            if (recent is { } r && await db.Tickets.AnyAsync(t => t.Id == r && t.StatusCategory < StatusCategory.Resolved, ct)) return r;
        }
        var subject = record.Status switch
        {
            CallStatus.Missed => $"Missed call from {contact.FullName}",
            CallStatus.Voicemail => $"Voicemail from {contact.FullName}",
            _ => $"Phone call with {contact.FullName}"
        };
        var description = TextNormalization.TextToHtml(CallSummary(record, contact));
        var request = new CreateTicketRequest
        {
            Subject = subject, DescriptionHtml = description, Channel = Channel.Phone.ToString(), ContactId = contact.Id, CustomerId = contact.CustomerId,
            BrandId = account.BrandId, DepartmentId = account.DepartmentId, ClientRequestId = $"call:{record.Id:N}",
            AssigneeId = record.Status == CallStatus.Completed ? record.AgentUserId : null
        };
        var source = new TicketCreationSource(CreateInitialThread: false, ChannelAccountId: account.Id, EnforceRequiredFields: false);
        try
        {
            return (await tickets.CreateAsync(request, source, ct)).Id;
        }
        catch (ValidationException) when (request.AssigneeId is not null)
        {
            db.ChangeTracker.Clear();
            return (await tickets.CreateAsync(request with { AssigneeId = null }, source, ct)).Id;
        }
    }

    private async Task AppendCallLogAsync(Guid ticketId, CallRecord record, Contact? contact, CancellationToken ct)
    {
        var summary = CallSummary(record, contact);
        await threads.AppendAsync(ticketId, _ => new TicketThread
        {
            Kind = ThreadKind.CallLog, Visibility = Visibility.Internal, Channel = Channel.Phone,
            Direction = record.Direction == CallDirection.Inbound ? MessageDirection.Inbound : MessageDirection.Outbound,
            AuthorType = AuthorType.System, AuthorName = "Telephony", AuthorContactId = contact?.Id, AuthorUserId = record.AgentUserId,
            FromAddress = record.FromNumber, Subject = $"Call {record.Status.ToString().ToLowerInvariant()}",
            BodyHtml = TextNormalization.TextToHtml(summary), BodyText = summary,
            ChannelMetadataJson = Json.Serialize(new
            {
                callRecordId = record.Id, callId = record.ExternalCallId, direction = record.Direction.ToString(), status = record.Status.ToString(), record.FromNumber,
                record.ToNumber, record.StartedAt, record.AnsweredAt, record.EndedAt, record.DurationSeconds, record.RecordingUrl, record.Disposition
            })
        }, $"call:{record.Id:N}:{record.Status}", ct);
    }

    private static string CallSummary(CallRecord r, Contact? contact)
    {
        var who = contact?.FullName is { } n ? $"{n} ({(r.Direction == CallDirection.Inbound ? r.FromNumber : r.ToNumber)})" : r.Direction == CallDirection.Inbound ? r.FromNumber : r.ToNumber;
        var duration = r.DurationSeconds > 0 ? $"{r.DurationSeconds / 60}m {r.DurationSeconds % 60}s" : "—";
        var lines = new List<string>
        {
            r.Status switch
            {
                CallStatus.Missed => $"Missed {r.Direction.ToString().ToLowerInvariant()} call from {who}.",
                CallStatus.Voicemail => $"Voicemail left by {who}.",
                CallStatus.Failed => $"Call with {who} failed.",
                _ => $"{r.Direction} call with {who} — duration {duration}."
            },
            $"Started: {r.StartedAt:yyyy-MM-dd HH:mm} UTC"
        };
        if (r.RecordingUrl is not null) lines.Add($"Recording: {r.RecordingUrl}");
        if (r.Disposition is not null) lines.Add($"Disposition: {r.Disposition}");
        if (r.Notes is not null) lines.Add($"Notes: {r.Notes}");
        return string.Join("\n", lines);
    }

    public static string ChannelLabel(Channel channel) => channel switch
    {
        Channel.WhatsApp => "WhatsApp message",
        Channel.Sms => "SMS",
        Channel.Facebook => "Messenger message",
        Channel.Instagram => "Instagram message",
        _ => $"{channel} message"
    };

    private static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "application/pdf" => ".pdf",
        "audio/ogg" or "audio/ogg; codecs=opus" => ".ogg",
        "audio/mpeg" => ".mp3",
        "video/mp4" => ".mp4",
        _ => ".bin"
    };
}
