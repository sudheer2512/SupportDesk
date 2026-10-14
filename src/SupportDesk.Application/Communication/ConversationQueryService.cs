using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Communication;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Communication;

/// <summary>
/// The unified omnichannel inbox: conversations of every channel, filterable by channel/status/assignee, keyset paged by
/// last activity, restricted to conversations whose ticket the agent may see (unlinked bot/visitor conversations require
/// tickets.view). Message detail includes delivery status history.
/// </summary>
public sealed class ConversationQueryService(DeskDbContext db, RequestContext ctx, TicketService tickets)
{
    public async Task<PagedResult<ConversationListItemDto>> ListAsync(ConversationQuery q, CancellationToken ct = default)
    {
        ctx.RequireAgent();
        ctx.Require(Permissions.TicketsView);
        var limit = Math.Clamp(q.Limit, 1, 200);
        var visible = db.Tickets.VisibleTo(ctx, db).Select(t => t.Id);
        var query = db.Conversations.AsNoTracking().Where(c => c.TicketId == null || visible.Contains(c.TicketId.Value));
        if (!string.IsNullOrWhiteSpace(q.Channel))
        {
            if (!Enum.TryParse<Channel>(q.Channel, true, out var ch)) throw new ValidationException(new Dictionary<string, string> { ["channel"] = "Unknown channel." });
            query = query.Where(c => c.Channel == ch);
        }
        if (!string.IsNullOrWhiteSpace(q.Status))
        {
            if (q.Status.Equals("active", StringComparison.OrdinalIgnoreCase)) query = query.Where(c => c.Status != ConversationStatus.Closed);
            else if (Enum.TryParse<ConversationStatus>(q.Status, true, out var st)) query = query.Where(c => c.Status == st);
            else throw new ValidationException(new Dictionary<string, string> { ["status"] = "Unknown status." });
        }
        if (q.Mine) query = query.Where(c => c.AssignedUserId == ctx.UserId);
        else if (q.AssigneeId is { } a) query = query.Where(c => c.AssignedUserId == a);
        if (q.Unassigned) query = query.Where(c => c.AssignedUserId == null);
        if (q.ChannelAccountId is { } acc) query = query.Where(c => c.ChannelAccountId == acc);
        if (q.ContactId is { } contact) query = query.Where(c => c.ContactId == contact);
        if (q.TicketId is { } ticket) query = query.Where(c => c.TicketId == ticket);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = q.Q.Trim();
            query = query.Where(c => (c.Subject != null && c.Subject.Contains(term)) || (c.VisitorName != null && c.VisitorName.Contains(term))
                                     || (c.VisitorEmail != null && c.VisitorEmail.Contains(term)) || c.ExternalKey.Contains(term)
                                     || db.Contacts.Any(x => x.Id == c.ContactId && x.FullName.Contains(term)));
        }
        if (DbExtensions.DecodeCursor(q.Cursor) is { } cursor)
        {
            var at = DateTime.Parse(cursor.V, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            query = query.Where(c => c.LastMessageAt < at || (c.LastMessageAt == at && c.Id.CompareTo(cursor.Id) < 0));
        }
        var rows = await query.OrderByDescending(c => c.LastMessageAt).ThenByDescending(c => c.Id).Take(limit + 1).ToListAsync(ct);
        var page = rows.Take(limit).ToList();
        var items = await MapAsync(page, ct);
        var next = rows.Count > limit ? DbExtensions.EncodeCursor(page[^1].LastMessageAt.ToString("O", CultureInfo.InvariantCulture), page[^1].Id) : null;
        return new PagedResult<ConversationListItemDto>(items, next);
    }

    public async Task<ConversationDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var conv = await LoadVisibleAsync(id, ct);
        var messages = await db.Messages.AsNoTracking().Where(m => m.ConversationId == id).OrderBy(m => m.OccurredAt).ThenBy(m => m.CreatedAt).Take(300).ToListAsync(ct);
        var calls = conv.Channel == Channel.Phone
            ? await db.CallRecords.AsNoTracking().Where(c => c.ChannelAccountId == conv.ChannelAccountId && c.ExternalCallId == conv.ExternalKey).ToListAsync(ct)
            : conv.ContactId is { } cid ? await db.CallRecords.AsNoTracking().Where(c => c.ContactId == cid && c.TicketId == conv.TicketId && conv.TicketId != null).ToListAsync(ct) : [];
        var summary = (await MapAsync([conv], ct))[0];
        bool? window = conv.Channel == Channel.WhatsApp ? conv.ServiceWindowEndsAt > db.Clock.UtcNow : null;
        return new ConversationDetailDto(summary, conv.ExternalKey, conv.VisitorEmail, conv.Rating, Json.ElementOrNull(conv.MetadataJson),
            messages.Select(MapMessage).ToList(), calls.Select(MapCall).ToList(), window);
    }

    public async Task<PagedResult<ConversationMessageDto>> MessagesAsync(Guid conversationId, string? cursor, int limit, CancellationToken ct = default)
    {
        await LoadVisibleAsync(conversationId, ct);
        limit = Math.Clamp(limit, 1, 500);
        var q = db.Messages.AsNoTracking().Where(m => m.ConversationId == conversationId);
        if (DbExtensions.DecodeCursor(cursor) is { } c)
        {
            var at = DateTime.Parse(c.V, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            q = q.Where(m => m.OccurredAt > at || (m.OccurredAt == at && m.Id.CompareTo(c.Id) > 0));
        }
        var rows = await q.OrderBy(m => m.OccurredAt).ThenBy(m => m.Id).Take(limit + 1).ToListAsync(ct);
        var page = rows.Take(limit).ToList();
        return new PagedResult<ConversationMessageDto>(page.Select(MapMessage).ToList(),
            rows.Count > limit ? DbExtensions.EncodeCursor(page[^1].OccurredAt.ToString("O", CultureInfo.InvariantCulture), page[^1].Id) : null);
    }

    public async Task<MessageDetailDto> MessageAsync(Guid messageId, CancellationToken ct = default)
    {
        var msg = await LoadVisibleMessageAsync(messageId, ct);
        var deliveries = await db.MessageDeliveries.AsNoTracking().Where(d => d.MessageId == messageId).OrderBy(d => d.UpdatedAt).ToListAsync(ct);
        var meta = Json.Object(msg.MetadataJson);
        Guid? raw = meta.TryGetValue("rawFileId", out var r) && r.ValueKind == JsonValueKind.String && Guid.TryParse(r.GetString(), out var g) ? g : null;
        return new MessageDetailDto(MapMessage(msg), deliveries.Select(MapDelivery).ToList(), raw);
    }

    /// <summary>The raw RFC 822 source of an inbound e-mail (stored file id), after the same visibility checks.</summary>
    public async Task<Guid> RawFileAsync(Guid messageId, CancellationToken ct = default)
    {
        var detail = await MessageAsync(messageId, ct);
        return detail.RawFileId ?? throw new NotFoundException("Original message source");
    }

    public async Task<List<OutboundMessageDto>> OutboundAsync(Guid channelAccountId, int limit, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminChannels);
        if (!await db.ChannelAccounts.AnyAsync(a => a.Id == channelAccountId, ct)) throw new NotFoundException("Channel", channelAccountId);
        var messages = await db.Messages.AsNoTracking().Where(m => m.ChannelAccountId == channelAccountId && m.Direction == MessageDirection.Outbound)
            .OrderByDescending(m => m.CreatedAt).Take(Math.Clamp(limit, 1, 200)).ToListAsync(ct);
        var ids = messages.Select(m => m.Id).ToList();
        var deliveries = (await db.MessageDeliveries.AsNoTracking().Where(d => ids.Contains(d.MessageId)).ToListAsync(ct)).ToLookup(d => d.MessageId);
        return messages.Select(m => new OutboundMessageDto(m.Id, m.Channel.ToString(), string.Join(", ", Json.StringList(m.ToJson)), m.Subject, m.BodyText, m.Status.ToString(),
            m.TicketId, m.ThreadId, m.InternetMessageId, m.OccurredAt, deliveries[m.Id].Select(MapDelivery).ToList())).ToList();
    }

    // ───────────────────────────── Access ─────────────────────────────

    private async Task<Conversation> LoadVisibleAsync(Guid id, CancellationToken ct)
    {
        ctx.RequireAgent();
        ctx.Require(Permissions.TicketsView);
        var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Conversation", id);
        if (conv.TicketId is { } tid) await EnsureTicketVisibleAsync(tid, id, ct);
        return conv;
    }

    private async Task<Message> LoadVisibleMessageAsync(Guid id, CancellationToken ct)
    {
        ctx.RequireAgent();
        var msg = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new NotFoundException("Message", id);
        if (ctx.Has(Permissions.AdminChannels)) return msg;
        ctx.Require(Permissions.TicketsView);
        if (msg.TicketId is { } tid) await EnsureTicketVisibleAsync(tid, id, ct);
        return msg;
    }

    private async Task EnsureTicketVisibleAsync(Guid ticketId, Guid entityId, CancellationToken ct)
    {
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId, ct) ?? throw new NotFoundException("Conversation", entityId);
        try { await tickets.EnsureVisibleAsync(ticket, ct); }
        catch (NotFoundException) { throw new NotFoundException("Conversation", entityId); }
    }

    // ───────────────────────────── Mapping ─────────────────────────────

    private async Task<List<ConversationListItemDto>> MapAsync(IReadOnlyList<Conversation> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var accountIds = rows.Select(r => r.ChannelAccountId).Distinct().ToList();
        var accounts = await db.ChannelAccounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var contactIds = rows.Where(r => r.ContactId != null).Select(r => r.ContactId!.Value).Distinct().ToList();
        var contacts = await db.Contacts.AsNoTracking().Where(c => contactIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.FullName, ct);
        var ticketIds = rows.Where(r => r.TicketId != null).Select(r => r.TicketId!.Value).Distinct().ToList();
        var tickets = await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => new { t.Number, t.StatusCategory }, ct);
        var userIds = rows.Where(r => r.AssignedUserId != null).Select(r => r.AssignedUserId!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var convIds = rows.Select(r => r.Id).ToList();
        var last = (await db.Messages.AsNoTracking().Where(m => m.ConversationId != null && convIds.Contains(m.ConversationId.Value))
                .GroupBy(m => m.ConversationId!.Value)
                .Select(g => g.OrderByDescending(m => m.OccurredAt).ThenByDescending(m => m.CreatedAt).Select(m => new { m.ConversationId, m.BodyText, m.Direction }).First())
                .ToListAsync(ct))
            .ToDictionary(x => x.ConversationId!.Value);
        return rows.Select(c =>
        {
            last.TryGetValue(c.Id, out var lm);
            var t = c.TicketId is { } tid && tickets.TryGetValue(tid, out var tv) ? tv : null;
            return new ConversationListItemDto(c.Id, c.Channel.ToString(), c.Status.ToString(), c.ChannelAccountId, accounts.GetValueOrDefault(c.ChannelAccountId), c.Subject,
                c.ContactId, c.ContactId is { } cid ? contacts.GetValueOrDefault(cid) : null, c.VisitorName, c.TicketId, t?.Number, t?.StatusCategory.ToString(),
                c.AssignedUserId, c.AssignedUserId is { } u ? users.GetValueOrDefault(u) : null,
                lm?.BodyText is { } text ? TextNormalization.Truncate(text.ReplaceLineEndings(" "), 160) : null, lm?.Direction.ToString(),
                c.StartedAt, c.LastMessageAt, c.ServiceWindowEndsAt, c.IdentityVerified);
        }).ToList();
    }

    public static string AuthorOf(Message m)
    {
        if (m.Direction == MessageDirection.Inbound) return "customer";
        var kind = Json.Object(m.MetadataJson).TryGetValue("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        return kind switch
        {
            "bot" => "bot",
            "system" or "ack" or "csat" or "notice" => "system",
            _ => m.AuthorUserId is not null ? "agent" : "system"
        };
    }

    public static ConversationMessageDto MapMessage(Message m) => new(m.Id, m.Direction.ToString(), m.Channel.ToString(), m.ContentType, AuthorOf(m), m.FromName,
        m.FromAddress, Json.StringList(m.ToJson), m.Subject, m.BodyText, m.BodyHtml, m.Status.ToString(), m.IsAutoReply, m.TicketId, m.ThreadId, m.InternetMessageId,
        Json.ElementOrNull(m.MetadataJson), m.OccurredAt);

    public static MessageDeliveryDto MapDelivery(MessageDelivery d) => new(d.Id, d.Recipient, d.Status.ToString(), d.Attempts, d.NextAttemptAt, d.LastAttemptAt,
        d.ProviderMessageId, d.Error, Json.Element(d.StatusHistoryJson, "[]"), d.UpdatedAt);

    private static CallRecordDto MapCall(CallRecord c) => new(c.Id, c.ExternalCallId, c.Direction.ToString(), c.Status.ToString(), c.FromNumber, c.ToNumber,
        c.StartedAt, c.AnsweredAt, c.EndedAt, c.DurationSeconds, c.RecordingUrl, c.AgentUserId, c.TicketId, c.Disposition);
}
