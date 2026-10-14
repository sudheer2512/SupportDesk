using System.Text.Json;

namespace SupportDesk.Contracts.Communication;

/// <summary>A configured channel endpoint. Secrets are never returned — only the names of the secrets that are set.</summary>
public sealed record ChannelAccountDto(
    Guid Id,
    string Type,
    string Provider,
    string Name,
    string Address,
    string? DisplayName,
    Guid? BrandId,
    Guid? DepartmentId,
    JsonElement Settings,
    IReadOnlyList<string> SecretKeys,
    bool IsActive,
    string Status,
    string? LastError,
    DateTime? LastPolledAt,
    string? SignatureHtml,
    bool AutoAcknowledge,
    bool AcceptUnknownSenders,
    /// <summary>Relative webhook URL to configure at the provider (null when the provider does not use webhooks).</summary>
    string? WebhookPath,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string RowVersion);

public sealed record CreateChannelAccountRequest
{
    /// <summary>Email, WhatsApp, Sms, Chat, Voice, Facebook, Instagram.</summary>
    public string Type { get; init; } = "";
    /// <summary>FileDrop, Imap, Smtp, Microsoft365, Gmail, MetaCloud, MetaGraph, Twilio, GenericSms, GenericVoice, Widget, Simulator.</summary>
    public string Provider { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>E-mail address, E.164 phone number, page id; for chat widgets leave empty to generate a widget key.</summary>
    public string? Address { get; init; }
    public string? DisplayName { get; init; }
    public Guid? BrandId { get; init; }
    public Guid? DepartmentId { get; init; }
    public JsonElement? Settings { get; init; }
    /// <summary>Provider secrets (passwords, tokens). Stored encrypted; never returned.</summary>
    public Dictionary<string, string>? Secrets { get; init; }
    public string? SignatureHtml { get; init; }
    public bool AutoAcknowledge { get; init; } = true;
    public bool AcceptUnknownSenders { get; init; } = true;
    public bool IsActive { get; init; } = true;
}

/// <summary>Partial update. Secrets: a key with a value replaces it, a key with an empty value removes it, absent keys are kept.</summary>
public sealed record UpdateChannelAccountRequest
{
    public string RowVersion { get; init; } = "";
    public string? Name { get; init; }
    public string? Address { get; init; }
    public string? DisplayName { get; init; }
    public Guid? BrandId { get; init; }
    public Guid? DepartmentId { get; init; }
    public JsonElement? Settings { get; init; }
    public Dictionary<string, string>? Secrets { get; init; }
    public string? SignatureHtml { get; init; }
    public bool? AutoAcknowledge { get; init; }
    public bool? AcceptUnknownSenders { get; init; }
    public bool? IsActive { get; init; }
    /// <summary>Reset the provider cursor (re-read the mailbox from the provider's default starting point).</summary>
    public bool ResetCursor { get; init; }
}

public sealed record ChannelTestResultDto(bool Success, string Message, IReadOnlyDictionary<string, string>? Details, string Status, DateTime TestedAt);

public sealed record ChannelProviderDto(string Provider, IReadOnlyList<string> ChannelTypes, string Description, IReadOnlyList<string> SettingKeys,
    IReadOnlyList<string> SecretKeys, bool Polling, bool Webhooks);

// ───────────────────────────── Simulator ─────────────────────────────

public sealed record SimulatedAttachment(string FileName, string ContentType, string Base64, string? ContentId = null, bool Inline = false);

public sealed record SimulateEmailRequest
{
    public string From { get; init; } = "";
    public string? FromName { get; init; }
    /// <summary>Defaults to the mailbox address.</summary>
    public IReadOnlyList<string>? To { get; init; }
    public IReadOnlyList<string>? Cc { get; init; }
    public string Subject { get; init; } = "";
    public string? Text { get; init; }
    public string? Html { get; init; }
    /// <summary>Optional explicit Message-ID (re-sending the same id simulates a duplicate delivery).</summary>
    public string? MessageId { get; init; }
    public string? InReplyTo { get; init; }
    public IReadOnlyList<string>? References { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public IReadOnlyList<SimulatedAttachment>? Attachments { get; init; }
}

public sealed record SimulateMessageRequest
{
    /// <summary>Phone number (WhatsApp/SMS) or social user id (Messenger/Instagram PSID).</summary>
    public string From { get; init; } = "";
    public string? FromName { get; init; }
    public string? Text { get; init; }
    /// <summary>Optional provider message id (re-sending the same id simulates a duplicate delivery).</summary>
    public string? ExternalId { get; init; }
    public SimulatedAttachment? Media { get; init; }
}

public sealed record SimulateCallRequest
{
    /// <summary>started, answered, ended, missed, voicemail</summary>
    public string Event { get; init; } = "";
    public string? CallId { get; init; }
    public string From { get; init; } = "";
    public string? To { get; init; }
    /// <summary>inbound (default) or outbound</summary>
    public string? Direction { get; init; }
    public int? DurationSeconds { get; init; }
    public string? RecordingUrl { get; init; }
    public string? AgentEmail { get; init; }
    public string? Disposition { get; init; }
    public string? Notes { get; init; }
}

public sealed record SimulateStatusRequest(string ProviderMessageId, string Status, string? Error = null);

public sealed record InboundResultDto(string Outcome, Guid? MessageId, Guid? ConversationId, Guid? TicketId, long? TicketNumber, string? Detail);

public sealed record OutboundMessageDto(Guid Id, string Channel, string? To, string? Subject, string? BodyText, string Status, Guid? TicketId,
    Guid? ThreadId, string? InternetMessageId, DateTime OccurredAt, IReadOnlyList<MessageDeliveryDto> Deliveries);

public sealed record MessageDeliveryDto(Guid Id, string Recipient, string Status, int Attempts, DateTime NextAttemptAt, DateTime? LastAttemptAt,
    string? ProviderMessageId, string? Error, JsonElement StatusHistory, DateTime UpdatedAt);
