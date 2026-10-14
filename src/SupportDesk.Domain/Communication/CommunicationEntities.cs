namespace SupportDesk.Domain.Communication;

public enum ChannelType { Email, WhatsApp, Sms, Chat, Voice, Facebook, Instagram, WebForm, Api }

public enum ChannelAccountStatus { Connected, Error, Disabled, PendingVerification }

/// <summary>
/// A configured channel endpoint (mailbox, WhatsApp number, SMS sender, chat widget, phone line, social page).
/// Provider: Imap, Microsoft365, Gmail, FileDrop, Smtp (email); MetaCloud, GenericWhatsApp (WhatsApp); Twilio, GenericSms (SMS);
/// Widget (chat); GenericVoice, Twilio (voice); MetaGraph (Facebook/Instagram).
/// Secrets (passwords, tokens) are stored encrypted in SecretsProtected.
/// </summary>
public class ChannelAccount : AuditedEntity, IConcurrencyVersioned
{
    public ChannelType Type { get; set; }
    public string Provider { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>E-mail address, phone number (E.164), page id or widget key.</summary>
    public string Address { get; set; } = "";
    public string? DisplayName { get; set; }
    public Guid? BrandId { get; set; }
    public Guid? DepartmentId { get; set; }
    public string SettingsJson { get; set; } = "{}";
    public string? SecretsProtected { get; set; }
    public bool IsActive { get; set; } = true;
    public ChannelAccountStatus Status { get; set; } = ChannelAccountStatus.Connected;
    public string? LastError { get; set; }
    public DateTime? LastPolledAt { get; set; }
    /// <summary>Provider cursor (IMAP UID, Gmail historyId, Graph delta link…).</summary>
    public string? PollCursor { get; set; }
    public string? SignatureHtml { get; set; }
    public bool AutoAcknowledge { get; set; } = true;
    /// <summary>Create tickets for unknown senders (otherwise only for known contacts).</summary>
    public bool AcceptUnknownSenders { get; set; } = true;
    /// <summary>Inbound webhook verification key (provider callbacks).</summary>
    public string? WebhookKey { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public enum ConversationStatus { Open, BotActive, WaitingForAgent, AgentActive, Closed }

/// <summary>
/// A channel conversation (e-mail thread, WhatsApp session, chat session, call). Linked to the ticket it created or joined.
/// </summary>
public class Conversation : TenantEntity, IHasTimestamps
{
    public Guid ChannelAccountId { get; set; }
    public Channel Channel { get; set; }
    /// <summary>Provider-level thread key (e-mail root Message-ID, WhatsApp wa_id, chat session id).</summary>
    public string ExternalKey { get; set; } = "";
    public Guid? ContactId { get; set; }
    public Guid? TicketId { get; set; }
    public ConversationStatus Status { get; set; } = ConversationStatus.Open;
    public Guid? AssignedUserId { get; set; }
    public string? Subject { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime LastMessageAt { get; set; }
    /// <summary>WhatsApp 24-hour customer service window end.</summary>
    public DateTime? ServiceWindowEndsAt { get; set; }
    /// <summary>Hash of the anonymous chat visitor token.</summary>
    public string? VisitorTokenHash { get; set; }
    public string? VisitorName { get; set; }
    public string? VisitorEmail { get; set; }
    public bool IdentityVerified { get; set; }
    public string? MetadataJson { get; set; }
    public int? Rating { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum MessageStatus { Received, Processed, Duplicate, Rejected, Queued, Sending, Sent, Delivered, Read, Failed }

/// <summary>
/// A channel-level message (inbound or outbound) with its raw channel metadata. Unique per
/// (ChannelAccountId, Direction, ExternalId) so provider retries never create duplicates.
/// </summary>
public class Message : TenantEntity
{
    public Guid ChannelAccountId { get; set; }
    public Guid? ConversationId { get; set; }
    public Guid? TicketId { get; set; }
    public Guid? ThreadId { get; set; }
    public Channel Channel { get; set; }
    public MessageDirection Direction { get; set; }
    /// <summary>Provider message id (WhatsApp wamid, Twilio SID, Graph id) or e-mail Message-ID.</summary>
    public string ExternalId { get; set; } = "";
    public string? InternetMessageId { get; set; }
    public string? InReplyTo { get; set; }
    public string? ReferencesJson { get; set; }
    public string? FromAddress { get; set; }
    public string? FromName { get; set; }
    public string? ToJson { get; set; }
    public string? CcJson { get; set; }
    public string? Subject { get; set; }
    public string? BodyText { get; set; }
    public string? BodyHtml { get; set; }
    /// <summary>text, image, document, audio, video, location, template, interactive, call</summary>
    public string ContentType { get; set; } = "text";
    public string? MetadataJson { get; set; }
    public string? RawStorageKey { get; set; }
    public MessageStatus Status { get; set; }
    public string? ProcessingError { get; set; }
    public bool IsAutoReply { get; set; }
    public Guid? ContactId { get; set; }
    public Guid? AuthorUserId { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Outbound delivery tracking with retry state.</summary>
public class MessageDelivery : TenantEntity
{
    public Guid MessageId { get; set; }
    public string Recipient { get; set; } = "";
    public MessageStatus Status { get; set; } = MessageStatus.Queued;
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? Error { get; set; }
    /// <summary>[{"status":"Sent","at":"…"},…]</summary>
    public string StatusHistoryJson { get; set; } = "[]";
    public DateTime UpdatedAt { get; set; }
}

public enum InboundEventStatus { Received, Processed, Ignored, Failed }

/// <summary>Inbox for raw provider webhooks/polled items. Unique (Provider, DedupKey) makes handling idempotent.</summary>
public class InboundEvent : Entity
{
    public Guid? TenantId { get; set; }
    public Guid? ChannelAccountId { get; set; }
    public string Provider { get; set; } = "";
    public string DedupKey { get; set; } = "";
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public DateTime ReceivedAt { get; set; }
    public InboundEventStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? Error { get; set; }
}

public enum CallDirection { Inbound, Outbound }
public enum CallStatus { Ringing, InProgress, Completed, Missed, Voicemail, Failed }

public class CallRecord : TenantEntity, IHasTimestamps
{
    public Guid ChannelAccountId { get; set; }
    public string ExternalCallId { get; set; } = "";
    public Guid? TicketId { get; set; }
    public Guid? ContactId { get; set; }
    public Guid? AgentUserId { get; set; }
    public CallDirection Direction { get; set; }
    public string FromNumber { get; set; } = "";
    public string ToNumber { get; set; } = "";
    public CallStatus Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? AnsweredAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int DurationSeconds { get; set; }
    public string? RecordingUrl { get; set; }
    public Guid? RecordingFileId { get; set; }
    public string? Disposition { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum NotificationSeverity { Info, Warning, Critical }

/// <summary>In-app notification for an agent (or portal contact). DedupKey prevents duplicates of the same event.</summary>
public class Notification : TenantEntity
{
    public Guid? UserId { get; set; }
    public Guid? ContactId { get; set; }
    /// <summary>ticket.assigned, ticket.mentioned, sla.warning, sla.breached, approval.requested, followup.due, …</summary>
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public string? Link { get; set; }
    public NotificationSeverity Severity { get; set; }
    public bool RequiresAck { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public string DedupKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public enum DigestMode { None, Hourly, Daily }

/// <summary>Per-user, per-event delivery preferences. Absent row = tenant defaults.</summary>
public class NotificationPreference : TenantEntity
{
    public Guid UserId { get; set; }
    public string EventType { get; set; } = "";
    public bool InApp { get; set; } = true;
    public bool Email { get; set; }
    public bool Push { get; set; }
    public bool Teams { get; set; }
    public DigestMode Digest { get; set; } = DigestMode.None;
}

public class NotificationSettings : TenantEntity
{
    public Guid UserId { get; set; }
    public bool Enabled { get; set; } = true;
    public TimeOnly? QuietHoursStart { get; set; }
    public TimeOnly? QuietHoursEnd { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public TimeOnly DailyDigestAt { get; set; } = new(8, 0);
    public bool DesktopNotifications { get; set; } = true;
    public string? TeamsWebhookUrlProtected { get; set; }
}

public enum NotificationChannel { Email, Push, Teams, Sms, Digest }

/// <summary>Delivery of a notification through an external channel, with retry and quiet-hours scheduling.</summary>
public class NotificationDelivery : TenantEntity
{
    public Guid NotificationId { get; set; }
    public NotificationChannel Channel { get; set; }
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int Attempts { get; set; }
    public DateTime ScheduledFor { get; set; }
    public DateTime? SentAt { get; set; }
    public string? Error { get; set; }
}

public enum DevicePlatform { Android, iOS, Web }

public class PushDevice : TenantEntity
{
    public Guid? UserId { get; set; }
    public Guid? ContactId { get; set; }
    public DevicePlatform Platform { get; set; }
    public string Token { get; set; } = "";
    /// <summary>agent or customer</summary>
    public string AppKind { get; set; } = "agent";
    public bool IsActive { get; set; } = true;
    public DateTime RegisteredAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}
