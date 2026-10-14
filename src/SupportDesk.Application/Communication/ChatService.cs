using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Customers;
using SupportDesk.Application.Identity;
using SupportDesk.Application.Notifications;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Communication;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Communication;

/// <summary>
/// Signed anonymous visitor tokens: "{conversationId}.{tenantId}.{nonce}.{hmac}". The HMAC key is derived from the master
/// key; only a SHA-256 hash of the token is stored on the conversation, so a database leak does not expose live sessions.
/// </summary>
public sealed class ChatTokens
{
    private readonly byte[] _key;

    public ChatTokens(ISecretProtector protector) =>
        _key = protector is AesGcmSecretProtector aes ? aes.DeriveKey("supportdesk:chat-visitor-token") : RandomNumberGenerator.GetBytes(32);

    public string Create(Guid tenantId, Guid conversationId)
    {
        var payload = $"{conversationId:N}.{tenantId:N}.{TextNormalization.RandomToken(18)}";
        return $"{payload}.{Sign(payload)}";
    }

    public (Guid TenantId, Guid ConversationId)? Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200) return null;
        var parts = token.Split('.');
        if (parts.Length != 4) return null;
        var payload = string.Join('.', parts[..3]);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(payload)), Encoding.ASCII.GetBytes(parts[3]))) return null;
        return Guid.TryParseExact(parts[0], "N", out var conv) && Guid.TryParseExact(parts[1], "N", out var tenant) ? (tenant, conv) : null;
    }

    private string Sign(string payload) =>
        Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Live chat: anonymous widget sessions with signed visitor tokens, bot-first answers (IConversationAssistant) with handoff
/// to a human (ticket creation + routing + chat.waiting notifications), agent accept/reply/close, typing indicators,
/// optional e-mail OTP identity verification linking the visitor to a contact, and the transcript in the ticket timeline.
/// </summary>
public sealed class ChatService(
    DeskDbContext db,
    RequestContext ctx,
    ChatTokens tokens,
    IConversationAssistant assistant,
    IChatRealtime chat,
    IRealtimePublisher realtime,
    TicketService tickets,
    ConversationService conversation,
    ContactResolver contacts,
    NotificationService notifications,
    AuthService auth,
    TenantSettingsService settings,
    ThreadAppender threads,
    ChannelContextFactory contexts,
    IEventPublisher events,
    ILogger<ChatService> logger)
{
    private const int MaxText = 4000;
    /// <summary>Client request ids of replies sent from the live chat (delivered synchronously, skipped by the reply handler).</summary>
    public const string ChatReplyPrefix = "chat:";

    // ───────────────────────────── Visitor (anonymous) ─────────────────────────────

    public async Task<ChatWidgetConfigDto> ConfigAsync(string widgetKey, CancellationToken ct)
    {
        var account = await ResolveWidgetAsync(widgetKey, ct);
        var s = contexts.Create(account);
        var brand = account.BrandId is { } b ? await db.Brands.AsNoTracking().FirstOrDefaultAsync(x => x.Id == b, ct)
            : await db.Brands.AsNoTracking().FirstOrDefaultAsync(x => x.IsDefault, ct);
        return new ChatWidgetConfigDto(account.Address, s.SettingOr("title", brand?.Name is { } bn ? $"Chat with {bn}" : account.Name), brand?.Slug, brand?.Name,
            brand?.PrimaryColor ?? "#2563eb", brand?.AccentColor ?? "#0ea5e9", s.SettingOr("greeting", "Hi! How can we help you today?"), s.Bool("botEnabled", true),
            s.Bool("requireEmail", false), s.Bool("verifyIdentity", true), await AgentsOnlineAsync(account, ct), s.Setting("offlineMessage"));
    }

    public async Task<ChatSessionDto> StartAsync(string widgetKey, StartChatRequest r, CancellationToken ct)
    {
        var account = await ResolveWidgetAsync(widgetKey, ct);
        var s = contexts.Create(account);
        var email = TextNormalization.NormalizeEmail(r.Email);
        if (s.Bool("requireEmail", false) && email is null) throw new ValidationException(new Dictionary<string, string> { ["email"] = "Please enter your e-mail address." });
        var now = db.Clock.UtcNow;
        var conv = new Conversation
        {
            ChannelAccountId = account.Id, Channel = Channel.Chat, Status = s.Bool("botEnabled", true) ? ConversationStatus.BotActive : ConversationStatus.WaitingForAgent,
            VisitorName = Clean(r.Name, 120), VisitorEmail = email, StartedAt = now, LastMessageAt = now,
            Subject = "Live chat", MetadataJson = Json.Serialize(new { pageUrl = Clean(r.PageUrl, 500), locale = Clean(r.Locale, 20), ip = ctx.IpAddress, userAgent = Clean(ctx.UserAgent, 300) })
        };
        conv.ExternalKey = $"chat:{conv.Id:N}";
        var token = tokens.Create(account.TenantId, conv.Id);
        conv.VisitorTokenHash = TokenHasher.Hash(token);
        db.Conversations.Add(conv);
        AddMessage(conv, MessageDirection.Outbound, s.SettingOr("greeting", "Hi! How can we help you today?"), "bot", "Assistant", null, $"greet:{conv.Id:N}");
        events.Publish(EventTypes.ConversationUpdated, "Conversation", conv.Id, new ChatEventPayload(conv.Id, null, conv.Status.ToString()));
        await db.SaveChangesAsync(ct);
        if (!string.IsNullOrWhiteSpace(r.Message)) await HandleVisitorTextAsync(conv.Id, r.Message!, ct);
        return new ChatSessionDto(conv.Id, token, (await LoadAsync(conv.Id, ct)).Status.ToString(), await MessagesAsync(conv.Id, null, ct));
    }

    /// <summary>Validates a visitor token (signature + stored hash) and binds the request to the conversation's tenant.</summary>
    public async Task<Conversation> AuthenticateVisitorAsync(Guid conversationId, string? token, CancellationToken ct)
    {
        var parsed = tokens.Validate(token);
        if (parsed is null || parsed.Value.ConversationId != conversationId) throw new ForbiddenException("This chat session is not valid. Start a new chat.");
        if (!ctx.HasTenant) ctx.SetSystem(parsed.Value.TenantId, "Live chat visitor", ActorType.System);
        else if (ctx.TenantId != parsed.Value.TenantId) throw new ForbiddenException("This chat session is not valid.");
        var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == conversationId && c.Channel == Channel.Chat, ct);
        if (conv?.VisitorTokenHash is null || !TokenHasher.FixedEquals(conv.VisitorTokenHash, TokenHasher.Hash(token!)))
            throw new ForbiddenException("This chat session is not valid. Start a new chat.");
        return conv;
    }

    public async Task<ChatStateDto> StateAsync(Guid conversationId, string? token, DateTime? after, CancellationToken ct)
    {
        var conv = await AuthenticateVisitorAsync(conversationId, token, ct);
        return new ChatStateDto(conv.Id, conv.Status.ToString(), await AgentNameAsync(conv, ct), conv.IdentityVerified, conv.TicketId, await MessagesAsync(conv.Id, after, ct));
    }

    public async Task<ChatStateDto> VisitorMessageAsync(Guid conversationId, string? token, string text, CancellationToken ct)
    {
        var conv = await AuthenticateVisitorAsync(conversationId, token, ct);
        if (conv.Status == ConversationStatus.Closed) throw new ConflictException("chat_closed", "This chat has ended. Start a new chat to continue.");
        if (string.IsNullOrWhiteSpace(text)) throw new ValidationException(new Dictionary<string, string> { ["text"] = "Write a message first." });
        await HandleVisitorTextAsync(conv.Id, text, ct);
        return await StateAsync(conversationId, token, null, ct);
    }

    public async Task<ChatStateDto> RequestHumanAsync(Guid conversationId, string? token, CancellationToken ct)
    {
        var conv = await AuthenticateVisitorAsync(conversationId, token, ct);
        if (conv.Status == ConversationStatus.BotActive) await HandoffAsync(conv.Id, "Visitor asked for an agent", ct);
        return await StateAsync(conversationId, token, null, ct);
    }

    public async Task VisitorTypingAsync(Guid conversationId, string? token, bool isTyping, CancellationToken ct)
    {
        var conv = await AuthenticateVisitorAsync(conversationId, token, ct);
        await chat.TypingAsync(ctx.TenantId, conv.Id, new { conversationId = conv.Id, from = "visitor", name = conv.VisitorName ?? "Visitor", isTyping }, ct);
    }

    public async Task<ChatVerifyResponse> RequestVerificationAsync(Guid conversationId, string? token, string email, CancellationToken ct)
    {
        var conv = await AuthenticateVisitorAsync(conversationId, token, ct);
        var normalized = TextNormalization.NormalizeEmail(email) ?? throw new ValidationException(new Dictionary<string, string> { ["email"] = "Enter a valid e-mail address." });
        var recent = await db.OtpChallenges.CountAsync(c => c.PrincipalId == conv.Id && c.Purpose == OtpPurpose.ChatIdentity && c.CreatedAt > db.Clock.UtcNow.AddMinutes(-15), ct);
        if (recent >= 5) throw new DomainException("rate_limited", "Too many codes requested. Try again in a few minutes.", 429);
        var demo = (await settings.SecurityAsync(ct)).DemoMode;
        var (_, demoCode) = await auth.CreateOtpAsync(PrincipalKind.Contact, conv.Id, OtpPurpose.ChatIdentity, normalized, sendEmail: true, demo, ct);
        var row = await db.Conversations.FirstAsync(c => c.Id == conv.Id, ct);
        row.VisitorEmail = normalized;
        await db.SaveChangesAsync(ct);
        return new ChatVerifyResponse("sent", demoCode, "We sent a 6-digit code to your e-mail address.");
    }

    public async Task<ChatVerifyResponse> ConfirmVerificationAsync(Guid conversationId, string? token, string code, CancellationToken ct)
    {
        var conv = await AuthenticateVisitorAsync(conversationId, token, ct);
        var challenge = await db.OtpChallenges.Where(c => c.PrincipalId == conv.Id && c.Purpose == OtpPurpose.ChatIdentity && c.ConsumedAt == null)
            .OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
        if (challenge is null || challenge.ExpiresAt < db.Clock.UtcNow || challenge.Attempts >= 5)
            return new ChatVerifyResponse("expired", null, "The code has expired. Request a new one.");
        challenge.Attempts++;
        var digits = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (!TokenHasher.FixedEquals(TokenHasher.Hash(digits), challenge.CodeHash))
        {
            await db.SaveChangesAsync(ct);
            return new ChatVerifyResponse("invalid", null, "That code is not correct.");
        }
        challenge.ConsumedAt = db.Clock.UtcNow;
        var email = challenge.Destination!;
        var contact = await contacts.FindOrCreateAsync(email, null, conv.VisitorName, null, Channel.Chat, ct);
        await db.SaveChangesAsync(ct);
        await contacts.AttachIdentityAsync(contact.Id, Channel.Email, email, verified: true, ct);
        var row = await db.Conversations.FirstAsync(c => c.Id == conv.Id, ct);
        row.ContactId = contact.Id;
        row.IdentityVerified = true;
        row.VisitorEmail = email;
        row.VisitorName ??= contact.FullName;
        AddMessage(row, MessageDirection.Outbound, $"Thanks — your e-mail address {email} is verified.", "system", "System", null, $"verified:{challenge.Id:N}");
        await db.SaveChangesAsync(ct);
        if (row.TicketId is { } ticketId) await LinkTicketContactAsync(ticketId, contact.Id, ct);
        await PushStatusAsync(row, ct);
        return new ChatVerifyResponse("verified", null, "Your identity is verified.");
    }

    public async Task<ChatStateDto> VisitorCloseAsync(Guid conversationId, string? token, CancellationToken ct)
    {
        var conv = await AuthenticateVisitorAsync(conversationId, token, ct);
        if (conv.Status != ConversationStatus.Closed) await CloseCoreAsync(conv.Id, "Visitor ended the chat", ct);
        return await StateAsync(conversationId, token, null, ct);
    }

    public async Task RateAsync(Guid conversationId, string? token, int rating, string? comment, CancellationToken ct)
    {
        var conv = await AuthenticateVisitorAsync(conversationId, token, ct);
        if (rating is < 1 or > 5) throw new ValidationException(new Dictionary<string, string> { ["rating"] = "Rate from 1 to 5." });
        var row = await db.Conversations.FirstAsync(c => c.Id == conv.Id, ct);
        row.Rating = rating;
        row.MetadataJson = OutboundService.MergeMeta(row.MetadataJson, "ratingComment", Clean(comment, 1000));
        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────────── Agents ─────────────────────────────

    public async Task AcceptAsync(Guid conversationId, CancellationToken ct)
    {
        ctx.Require(Permissions.TicketsReply);
        var conv = await LoadAgentConversationAsync(conversationId, ct);
        if (conv.Status == ConversationStatus.Closed) throw new ConflictException("chat_closed", "This chat has already ended.");
        if (conv.Status == ConversationStatus.AgentActive && conv.AssignedUserId is { } other)
        {
            if (other != ctx.UserId) throw new ConflictException("chat_taken", "Another agent already accepted this chat.");
            return;
        }
        if (conv.TicketId is null) await HandoffAsync(conv.Id, "Accepted by an agent", ct);
        conv = await db.Conversations.FirstAsync(c => c.Id == conversationId, ct);
        conv.Status = ConversationStatus.AgentActive;
        conv.AssignedUserId = ctx.UserId;
        AddMessage(conv, MessageDirection.Outbound, $"{ctx.ActorName ?? "An agent"} joined the chat.", "system", "System", null, $"joined:{conv.Id:N}:{ctx.UserId:N}");
        events.Publish(EventTypes.ConversationUpdated, "Conversation", conv.Id, new ChatEventPayload(conv.Id, null, conv.Status.ToString()));
        await db.SaveChangesAsync(ct);
        if (conv.TicketId is { } ticketId && ctx.UserId is { } me)
        {
            var t = await tickets.LoadForUpdateAsync(ticketId, ct);
            if (t.AssigneeId != me)
            {
                try
                {
                    await tickets.AssignCoreAsync(t, me, null, "Accepted live chat", ct);
                    await tickets.SaveAsync(t, ct);
                }
                catch (DomainException ex)
                {
                    db.ChangeTracker.Clear();
                    logger.LogInformation("Chat {Conversation} accepted but ticket not reassigned: {Reason}", conv.Id, ex.Message);
                }
            }
        }
        await PushStatusAsync(await LoadAsync(conversationId, ct), ct);
        await PushLatestAsync(conversationId, ct);
    }

    public async Task<ConversationMessageDto> AgentSendAsync(Guid conversationId, string text, string? clientRequestId, CancellationToken ct)
    {
        ctx.Require(Permissions.TicketsReply);
        var conv = await LoadAgentConversationAsync(conversationId, ct);
        if (conv.Status == ConversationStatus.Closed) throw new ConflictException("chat_closed", "This chat has ended. Reply on the ticket instead (it will be e-mailed).");
        if (string.IsNullOrWhiteSpace(text)) throw new ValidationException(new Dictionary<string, string> { ["text"] = "Write a message first." });
        if (conv.TicketId is null) await HandoffAsync(conv.Id, "Agent replied", ct);
        conv = await db.Conversations.FirstAsync(c => c.Id == conversationId, ct);
        if (conv.Status != ConversationStatus.AgentActive)
        {
            conv.Status = ConversationStatus.AgentActive;
            conv.AssignedUserId ??= ctx.UserId;
            await db.SaveChangesAsync(ct);
        }
        var thread = await conversation.ReplyAsync(conv.TicketId!.Value, new ReplyRequest
        {
            BodyHtml = TextNormalization.TextToHtml(Trim(text)), Channel = Channel.Chat.ToString(), Force = true, ClientRequestId = $"{ChatReplyPrefix}{TextNormalization.Truncate(clientRequestId ?? Guid.NewGuid().ToString("N"), 80)}"
        }, ct);
        var now = db.Clock.UtcNow;
        var msg = await db.Messages.FirstOrDefaultAsync(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.Outbound, ct);
        if (msg is null)
        {
            msg = new Message
            {
                ChannelAccountId = conv.ChannelAccountId, ConversationId = conv.Id, TicketId = conv.TicketId, ThreadId = thread.Id, Channel = Channel.Chat,
                Direction = MessageDirection.Outbound, ExternalId = $"thread:{thread.Id:N}", FromName = ctx.ActorName, BodyText = Trim(text), BodyHtml = thread.BodyHtml,
                ContentType = "text", AuthorUserId = ctx.UserId, Status = MessageStatus.Delivered, OccurredAt = now, CreatedAt = now,
                MetadataJson = Json.Serialize(new OutboundMeta { Kind = "chat" })
            };
            db.Messages.Add(msg);
            var th = await db.TicketThreads.FirstAsync(t => t.Id == thread.Id, ct);
            th.MessageId = msg.Id;
            th.DeliveryStatus = ThreadDeliveryStatus.Delivered;
            th.ConversationId = conv.Id;
            conv.LastMessageAt = now;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                msg = await db.Messages.AsNoTracking().FirstAsync(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.Outbound, ct);
            }
        }
        var dto = MapForVisitor(msg);
        await chat.MessageAsync(ctx.TenantId, conv.Id, dto, ct);
        return ConversationQueryService.MapMessage(msg);
    }

    public async Task AgentTypingAsync(Guid conversationId, bool isTyping, CancellationToken ct)
    {
        ctx.Require(Permissions.TicketsReply);
        var conv = await LoadAgentConversationAsync(conversationId, ct);
        await chat.TypingAsync(ctx.TenantId, conv.Id, new { conversationId = conv.Id, from = "agent", name = ctx.ActorName ?? "Agent", isTyping }, ct);
    }

    public async Task CloseAsync(Guid conversationId, string? reason, CancellationToken ct)
    {
        ctx.Require(Permissions.TicketsReply);
        var conv = await LoadAgentConversationAsync(conversationId, ct);
        if (conv.Status == ConversationStatus.Closed) return;
        await CloseCoreAsync(conv.Id, reason ?? $"Closed by {ctx.ActorName ?? "the agent"}", ct);
    }

    /// <summary>Checks that the agent may see the conversation (ticket visibility for linked chats).</summary>
    public async Task<Conversation> LoadAgentConversationAsync(Guid conversationId, CancellationToken ct)
    {
        ctx.RequireAgent();
        var conv = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct) ?? throw new NotFoundException("Conversation", conversationId);
        if (conv.TicketId is { } ticketId)
        {
            var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId, ct) ?? throw new NotFoundException("Conversation", conversationId);
            await tickets.EnsureVisibleAsync(ticket, ct);
        }
        else ctx.Require(Permissions.TicketsView);
        return conv;
    }

    // ───────────────────────────── Core ─────────────────────────────

    private async Task HandleVisitorTextAsync(Guid conversationId, string text, CancellationToken ct)
    {
        text = Trim(text);
        var conv = await LoadAsync(conversationId, ct);
        var msg = AddMessage(conv, MessageDirection.Inbound, text, "visitor", conv.VisitorName ?? "Visitor", null, $"v:{SequentialGuid.New():N}");
        msg.ContactId = conv.ContactId;
        conv.LastMessageAt = db.Clock.UtcNow;
        await db.SaveChangesAsync(ct);
        await chat.MessageAsync(ctx.TenantId, conv.Id, MapForVisitor(msg), ct);

        if (conv.Status == ConversationStatus.BotActive)
        {
            AssistantReply? reply = null;
            try
            {
                reply = await assistant.ReplyAsync(conv.Id, text, Json.Object(conv.MetadataJson).TryGetValue("locale", out var l) ? l.ToString() : null, conv.IdentityVerified, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Conversation assistant failed for chat {Conversation}; handing off", conv.Id);
            }
            if (reply is { SuggestHandoff: false } r && r.Confidence >= 0.4 && !string.IsNullOrWhiteSpace(r.Text))
            {
                conv = await LoadAsync(conversationId, ct);
                var bot = AddMessage(conv, MessageDirection.Outbound, r.Text, "bot", "Assistant", Json.Serialize(new { kind = "bot", citations = r.Citations, confidence = r.Confidence }),
                    $"bot:{msg.Id:N}");
                await db.SaveChangesAsync(ct);
                await chat.MessageAsync(ctx.TenantId, conv.Id, MapForVisitor(bot), ct);
                return;
            }
            await HandoffAsync(conv.Id, reply is null ? "No automated answer" : "Assistant suggested a human", ct);
            return;
        }
        if (conv.TicketId is null)
        {
            // Bot disabled: the first visitor message hands the chat straight to the agents (the transcript becomes the ticket).
            await HandoffAsync(conv.Id, "Visitor started the chat", ct);
            return;
        }

        if (conv.TicketId is { } ticketId)
        {
            var thread = await conversation.AddCustomerMessageAsync(ticketId, new CustomerMessageInput(TextNormalization.TextToHtml(text), text, Channel.Chat, conv.ContactId,
                conv.VisitorName ?? "Visitor", conv.VisitorEmail, null, null, null, msg.Id, conv.Id, Json.Serialize(new { channel = "chat", conversationId = conv.Id }),
                null, null, $"msg:{msg.Id:N}"), ct);
            var row = await db.Messages.FirstAsync(m => m.Id == msg.Id, ct);
            row.TicketId = thread?.TicketId ?? ticketId;
            row.ThreadId = thread?.Id;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Hands the chat to a human: creates (or reuses) the ticket, routes it and notifies agents (chat.waiting).</summary>
    public async Task HandoffAsync(Guid conversationId, string reason, CancellationToken ct)
    {
        var conv = await LoadAsync(conversationId, ct);
        if (conv.Status == ConversationStatus.Closed) return;
        var account = await db.ChannelAccounts.AsNoTracking().FirstAsync(a => a.Id == conv.ChannelAccountId, ct);
        Ticket? ticket = conv.TicketId is { } existing ? await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == existing, ct) : null;
        if (ticket is null)
        {
            var (contactId, contactNote) = await ContactForVisitorAsync(conv, ct);
            var transcript = await MessagesAsync(conv.Id, null, ct);
            var firstQuestion = transcript.FirstOrDefault(m => m.Author == "visitor")?.Text ?? "Live chat";
            var body = TranscriptHtml(transcript, conv.VisitorName) + (contactNote is null ? "" : $"<p><em>{System.Net.WebUtility.HtmlEncode(contactNote)}</em></p>");
            var deptSetting = contexts.Create(account).Setting("departmentId");
            ticket = await tickets.CreateAsync(new CreateTicketRequest
            {
                Subject = $"Chat: {TextNormalization.Truncate(firstQuestion.ReplaceLineEndings(" "), 120)}", DescriptionHtml = body, Channel = Channel.Chat.ToString(),
                ContactId = contactId, BrandId = account.BrandId, DepartmentId = account.DepartmentId ?? (Guid.TryParse(deptSetting, out var d) ? d : null),
                ClientRequestId = $"chat:{conv.Id:N}"
            }, new TicketCreationSource(CreateInitialThread: true, ChannelAccountId: account.Id, ConversationId: conv.Id, FromAddress: conv.VisitorEmail,
                AuthorName: conv.VisitorName ?? "Visitor", ChannelMetadataJson: Json.Serialize(new { channel = "chat", conversationId = conv.Id, identityVerified = conv.IdentityVerified, reason }),
                EnforceRequiredFields: false), ct);
            conv = await LoadAsync(conversationId, ct);
            conv.TicketId = ticket.Id;
            conv.ContactId ??= contactId;
            conv.Subject = ticket.Subject;
            await db.Messages.Where(m => m.ConversationId == conv.Id && m.TicketId == null).ExecuteUpdateAsync(u => u.SetProperty(m => m.TicketId, ticket.Id), ct);
        }
        if (conv.Status is ConversationStatus.BotActive or ConversationStatus.Open)
        {
            conv.Status = ConversationStatus.WaitingForAgent;
            conv.AssignedUserId = null;
        }
        var online = await AgentsOnlineAsync(account, ct);
        var notice = online ? "Connecting you to an agent — please stay on this chat."
            : contexts.Create(account).Setting("offlineMessage") ?? "Our agents are offline right now. We've created a ticket and will reply by e-mail.";
        AddMessage(conv, MessageDirection.Outbound, notice, "system", "System", null, $"handoff:{conv.Id:N}:{SequentialGuid.New():N}");
        events.Publish(EventTypes.ConversationUpdated, "Conversation", conv.Id, new ChatEventPayload(conv.Id, null, conv.Status.ToString()));
        await db.SaveChangesAsync(ct);
        await PushStatusAsync(conv, ct);
        await PushLatestAsync(conv.Id, ct);

        var t = await db.Tickets.AsNoTracking().FirstAsync(x => x.Id == ticket.Id, ct);
        var recipients = new List<Guid>();
        if (t.AssigneeId is { } assignee) recipients.Add(assignee);
        else
            recipients.AddRange(await db.AgentDepartments.AsNoTracking().Where(ad => ad.DepartmentId == t.DepartmentId)
                .Join(db.Agents.AsNoTracking().Where(a => a.Availability == AgentAvailability.Online && !a.IsLightAgent), ad => ad.UserId, a => a.UserId, (ad, _) => ad.UserId)
                .Distinct().OrderBy(id => id).Take(50).ToListAsync(ct));
        if (recipients.Count > 0)
            await notifications.NotifyAsync(new NotifyRequest(recipients, NotificationTypes.ChatWaiting, $"Chat waiting: {conv.VisitorName ?? "Visitor"}",
                TextNormalization.Truncate(t.Subject, 200), $"/conversations/{conv.Id}", NotificationSeverity.Warning, false, "Conversation", conv.Id, $"chat-waiting:{conv.Id:N}"), ct);
        await realtime.ToDepartmentsAsync(ctx.TenantId, [t.DepartmentId], "chat.waiting",
            new { conversationId = conv.Id, ticketId = t.Id, number = t.Number, visitor = conv.VisitorName ?? "Visitor", assigneeId = t.AssigneeId }, ct);
    }

    private async Task CloseCoreAsync(Guid conversationId, string reason, CancellationToken ct)
    {
        var conv = await LoadAsync(conversationId, ct);
        conv.Status = ConversationStatus.Closed;
        AddMessage(conv, MessageDirection.Outbound, "This chat has ended. Thank you for contacting us!", "system", "System", null, $"closed:{conv.Id:N}");
        events.Publish(EventTypes.ConversationUpdated, "Conversation", conv.Id, new ChatEventPayload(conv.Id, null, "Closed"));
        await db.SaveChangesAsync(ct);
        if (conv.TicketId is { } ticketId)
        {
            var transcript = await MessagesAsync(conv.Id, null, ct);
            var started = conv.StartedAt;
            await threads.AppendAsync(ticketId, _ => new TicketThread
            {
                Kind = ThreadKind.ChatTranscript, Visibility = Visibility.Public, Channel = Channel.Chat, Direction = MessageDirection.Internal, AuthorType = AuthorType.System,
                AuthorName = "Live chat", Subject = "Chat transcript", ConversationId = conversationId, DeliveryStatus = ThreadDeliveryStatus.Delivered,
                BodyHtml = TranscriptHtml(transcript, conv.VisitorName), BodyText = string.Join("\n", transcript.Select(m => $"[{m.At:HH:mm}] {m.AuthorName}: {m.Text}")),
                ChannelMetadataJson = Json.Serialize(new { conversationId, startedAt = started, endedAt = db.Clock.UtcNow, messages = transcript.Count, reason, rating = conv.Rating })
            }, $"chat-transcript:{conversationId:N}", ct);
        }
        await PushStatusAsync(await LoadAsync(conversationId, ct), ct);
        await PushLatestAsync(conversationId, ct);
    }

    private async Task<(Guid? ContactId, string? Note)> ContactForVisitorAsync(Conversation conv, CancellationToken ct)
    {
        if (conv.ContactId is { } id) return (id, null);
        if (conv.VisitorEmail is not { } email) return (null, null);
        var existing = await contacts.FindAsync(Channel.Email, email, ct);
        if (existing is not null)
            return (null, $"The visitor entered {email}, which belongs to {existing.FullName}, but did not verify it. The ticket was not linked to that contact.");
        var created = await contacts.FindOrCreateAsync(email, null, conv.VisitorName, null, Channel.Chat, ct);
        await db.SaveChangesAsync(ct);
        return (created.Id, null);
    }

    private async Task LinkTicketContactAsync(Guid ticketId, Guid contactId, CancellationToken ct)
    {
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (t is null || t.ContactId is not null) return;
        try
        {
            await tickets.UpdateAsync(ticketId, new UpdateTicketRequest { RowVersion = Convert.ToBase64String(t.RowVersion), ContactId = contactId }, ct);
        }
        catch (DomainException ex)
        {
            db.ChangeTracker.Clear();
            logger.LogInformation("Verified chat contact not linked to ticket {Ticket}: {Reason}", t.Number, ex.Message);
        }
    }

    private Message AddMessage(Conversation conv, MessageDirection direction, string text, string author, string? authorName, string? metadataJson, string externalId)
    {
        var now = db.Clock.UtcNow;
        var msg = new Message
        {
            ChannelAccountId = conv.ChannelAccountId, ConversationId = conv.Id, TicketId = conv.TicketId, Channel = Channel.Chat, Direction = direction,
            ExternalId = externalId, FromName = authorName, BodyText = text, BodyHtml = TextNormalization.TextToHtml(text), ContentType = "text",
            Status = direction == MessageDirection.Inbound ? MessageStatus.Processed : MessageStatus.Delivered,
            MetadataJson = metadataJson ?? Json.Serialize(new { kind = author }), ContactId = direction == MessageDirection.Inbound ? conv.ContactId : null,
            OccurredAt = now, CreatedAt = now
        };
        db.Messages.Add(msg);
        return msg;
    }

    private async Task<ChannelAccount> ResolveWidgetAsync(string widgetKey, CancellationToken ct)
    {
        var key = (widgetKey ?? "").Trim();
        var account = key.Length is 0 or > 120 ? null
            : await db.AllTenants<ChannelAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Type == ChannelType.Chat && a.Address == key && a.IsActive, ct);
        if (account is null) throw new NotFoundException("Chat widget");
        var status = await db.Tenants.AsNoTracking().Where(t => t.Id == account.TenantId).Select(t => t.Status).FirstOrDefaultAsync(ct);
        if (status is not (TenantStatus.Active or TenantStatus.Trial)) throw new NotFoundException("Chat widget");
        if (!ctx.HasTenant) ctx.SetSystem(account.TenantId, "Live chat visitor", ActorType.System);
        else if (ctx.TenantId != account.TenantId) throw new NotFoundException("Chat widget");
        return account;
    }

    private async Task<bool> AgentsOnlineAsync(ChannelAccount account, CancellationToken ct)
    {
        var online = db.Agents.AsNoTracking().Where(a => a.Availability == AgentAvailability.Online && !a.IsLightAgent);
        if (account.DepartmentId is { } d)
            return await online.AnyAsync(a => db.AgentDepartments.Any(ad => ad.UserId == a.UserId && ad.DepartmentId == d), ct);
        return await online.AnyAsync(ct);
    }

    private async Task<Conversation> LoadAsync(Guid id, CancellationToken ct) =>
        await db.Conversations.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Conversation", id);

    private async Task<string?> AgentNameAsync(Conversation conv, CancellationToken ct) =>
        conv.AssignedUserId is { } u ? await db.Users.AsNoTracking().Where(x => x.Id == u).Select(x => x.DisplayName).FirstOrDefaultAsync(ct) : null;

    public async Task<List<ChatMessageDto>> MessagesAsync(Guid conversationId, DateTime? after, CancellationToken ct)
    {
        var q = db.Messages.AsNoTracking().Where(m => m.ConversationId == conversationId);
        if (after is { } a) q = q.Where(m => m.OccurredAt > a);
        return (await q.OrderBy(m => m.OccurredAt).ThenBy(m => m.CreatedAt).Take(500).ToListAsync(ct)).Select(MapForVisitor).ToList();
    }

    private async Task PushStatusAsync(Conversation conv, CancellationToken ct) =>
        await chat.StatusAsync(ctx.TenantId, conv.Id, new { conversationId = conv.Id, status = conv.Status.ToString(), agentName = await AgentNameAsync(conv, ct), identityVerified = conv.IdentityVerified, ticketId = conv.TicketId }, ct);

    private async Task PushLatestAsync(Guid conversationId, CancellationToken ct)
    {
        var last = await db.Messages.AsNoTracking().Where(m => m.ConversationId == conversationId).OrderByDescending(m => m.OccurredAt).ThenByDescending(m => m.CreatedAt).FirstOrDefaultAsync(ct);
        if (last is not null) await chat.MessageAsync(ctx.TenantId, conversationId, MapForVisitor(last), ct);
    }

    public static ChatMessageDto MapForVisitor(Message m)
    {
        var author = m.Direction == MessageDirection.Inbound ? "visitor" : ConversationQueryService.AuthorOf(m);
        IReadOnlyList<ChatCitationDto>? citations = null;
        var meta = Json.Object(m.MetadataJson);
        if (meta.TryGetValue("citations", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Array)
            citations = c.EnumerateArray().Select(x => new ChatCitationDto(
                x.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : 0,
                x.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                x.TryGetProperty("url", out var u) ? u.GetString() : null)).ToList();
        return new ChatMessageDto(m.Id, author, m.FromName, m.BodyText ?? "", m.OccurredAt, citations);
    }

    private static string TranscriptHtml(IReadOnlyList<ChatMessageDto> messages, string? visitorName)
    {
        var sb = new StringBuilder("<p><strong>Live chat transcript</strong></p><ul>");
        foreach (var m in messages)
        {
            var who = m.Author switch { "visitor" => visitorName ?? "Visitor", "bot" => "Assistant", "system" => "System", _ => m.AuthorName ?? "Agent" };
            sb.Append("<li><strong>").Append(System.Net.WebUtility.HtmlEncode(who)).Append("</strong> <small>").Append(m.At.ToString("HH:mm")).Append(" UTC</small>: ")
                .Append(System.Net.WebUtility.HtmlEncode(m.Text)).Append("</li>");
        }
        return sb.Append("</ul>").ToString();
    }

    private static string Trim(string text) => TextNormalization.Truncate(text.Trim(), MaxText);

    private static string? Clean(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : TextNormalization.Truncate(s.Trim(), max);
}
