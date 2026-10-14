using System.Text.Json;
using SupportDesk.Contracts.Common;

namespace SupportDesk.Contracts.Communication;

/// <summary>Omnichannel inbox filter. Keyset paged by last activity (newest first).</summary>
public sealed record ConversationQuery
{
    /// <summary>Email, WhatsApp, Sms, Chat, Phone, Facebook, Instagram.</summary>
    public string? Channel { get; init; }
    /// <summary>Open, BotActive, WaitingForAgent, AgentActive, Closed; "active" = everything except Closed.</summary>
    public string? Status { get; init; }
    public Guid? AssigneeId { get; init; }
    /// <summary>Only conversations assigned to the caller.</summary>
    public bool Mine { get; init; }
    public bool Unassigned { get; init; }
    public Guid? ChannelAccountId { get; init; }
    public Guid? ContactId { get; init; }
    public Guid? TicketId { get; init; }
    public string? Q { get; init; }
    public string? Cursor { get; init; }
    public int Limit { get; init; } = 50;
}

public sealed record ConversationListItemDto(
    Guid Id,
    string Channel,
    string Status,
    Guid ChannelAccountId,
    string? ChannelAccountName,
    string? Subject,
    Guid? ContactId,
    string? ContactName,
    string? VisitorName,
    Guid? TicketId,
    long? TicketNumber,
    string? TicketStatusCategory,
    Guid? AssignedUserId,
    string? AssignedUserName,
    string? LastMessagePreview,
    string? LastMessageDirection,
    DateTime StartedAt,
    DateTime LastMessageAt,
    DateTime? ServiceWindowEndsAt,
    bool IdentityVerified);

public sealed record ConversationDetailDto(
    ConversationListItemDto Summary,
    string ExternalKey,
    string? VisitorEmail,
    int? Rating,
    JsonElement? Metadata,
    IReadOnlyList<ConversationMessageDto> Messages,
    IReadOnlyList<CallRecordDto> Calls,
    /// <summary>True while the WhatsApp 24-hour customer service window is open (free-form replies allowed).</summary>
    bool? ServiceWindowOpen);

public sealed record ConversationMessageDto(
    Guid Id,
    string Direction,
    string Channel,
    string ContentType,
    /// <summary>customer, agent, bot, system</summary>
    string Author,
    string? AuthorName,
    string? FromAddress,
    IReadOnlyList<string> To,
    string? Subject,
    string? BodyText,
    string? BodyHtml,
    string Status,
    bool IsAutoReply,
    Guid? TicketId,
    Guid? ThreadId,
    string? InternetMessageId,
    JsonElement? Metadata,
    DateTime OccurredAt,
    /// <summary>Files attached to the ticket entry behind this message (inline images flagged).</summary>
    IReadOnlyList<AttachmentDto>? Attachments = null);

public sealed record CallRecordDto(Guid Id, string ExternalCallId, string Direction, string Status, string FromNumber, string ToNumber, DateTime StartedAt,
    DateTime? AnsweredAt, DateTime? EndedAt, int DurationSeconds, string? RecordingUrl, Guid? AgentUserId, Guid? TicketId, string? Disposition);

public sealed record MessageDetailDto(ConversationMessageDto Message, IReadOnlyList<MessageDeliveryDto> Deliveries, Guid? RawFileId);

public sealed record AgentChatMessageRequest(string Text, string? ClientRequestId = null);

public sealed record CloseConversationRequest(string? Reason = null);

// ───────────────────────────── CSAT (public) ─────────────────────────────

public sealed record CsatSurveyDto(string Status, string TicketNumber, string Subject, string? AgentName, string? BrandName, string? PrimaryColor,
    int? Rating, string? Comment, DateTime ExpiresAt);

public sealed record CsatSubmitRequest(int Rating, string? Comment);
