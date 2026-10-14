using System.Text.Json;
using SupportDesk.Domain.Communication;

namespace SupportDesk.Application.Communication;

// Provider ports of the omnichannel module. Adapters live in SupportDesk.Integrations (Email/, Messaging/) and are
// resolved by ChannelAccount.Provider through ChannelProviderRegistry. Ports never see the database.

/// <summary>A channel account with its typed settings and decrypted secrets (in memory only — never logged).</summary>
public sealed class ChannelAccountContext(ChannelAccount account, IReadOnlyDictionary<string, JsonElement> settings, IReadOnlyDictionary<string, string> secrets)
{
    public ChannelAccount Account { get; } = account;
    public IReadOnlyDictionary<string, JsonElement> Settings { get; } = settings;
    public IReadOnlyDictionary<string, string> Secrets { get; } = secrets;

    public string? Setting(string key) => Settings.TryGetValue(key, out var v) ? v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
        _ => null
    } : null;

    public string SettingOr(string key, string fallback) => Setting(key) is { Length: > 0 } s ? s : fallback;

    public int Int(string key, int fallback) => Settings.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i
        : int.TryParse(Setting(key), out var p) ? p : fallback;

    public bool Bool(string key, bool fallback)
    {
        if (!Settings.TryGetValue(key, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => fallback
        };
    }

    public string? Secret(string key) => Secrets.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    public string RequireSetting(string key) => Setting(key) is { Length: > 0 } s ? s : throw new ChannelConfigurationException($"The '{key}' setting is required for {Account.Provider}.");

    public string RequireSecret(string key) => Secret(key) ?? throw new ChannelConfigurationException($"The '{key}' secret is required for {Account.Provider}.");
}

/// <summary>A channel is misconfigured (missing setting/secret). Permanent: retrying will not help.</summary>
public sealed class ChannelConfigurationException(string message) : Exception(message);

/// <summary>A webhook was rejected (bad signature, unknown verify token). Never processed or stored.</summary>
public sealed class WebhookRejectedException(string message) : Exception(message);

public sealed record ConnectionTestResult(bool Success, string Message, IReadOnlyDictionary<string, string>? Details = null);

/// <summary>Result of an outbound send. Permanent failures are dead-lettered immediately (no retry).</summary>
/// <summary>Outcome of a provider send. <see cref="DeadToken"/> (push only): the device token will never work again — stop using it.</summary>
public sealed record SendResult(bool Success, string? ProviderMessageId, string? Error, bool Permanent = false, bool DeadToken = false)
{
    public static SendResult Ok(string? providerId) => new(true, providerId, null);
    public static SendResult Fail(string error, bool permanent = false) => new(false, null, error, permanent);
    public static SendResult Dead(string error) => new(false, null, error, Permanent: true, DeadToken: true);
}

// ───────────────────────────── E-mail ─────────────────────────────

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content, string? ContentId = null, bool Inline = false);

/// <summary>A parsed RFC 5322 message (provider-agnostic). Raw holds the original bytes when available.</summary>
public sealed record InboundEmail(
    string? MessageId,
    string? InReplyTo,
    IReadOnlyList<string> References,
    string FromAddress,
    string? FromName,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    string Subject,
    string? TextBody,
    string? HtmlBody,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<EmailAttachment> Attachments,
    DateTime? Date,
    byte[]? Raw,
    /// <summary>multipart/report delivery status notification (bounce).</summary>
    bool IsDeliveryReport = false);

/// <summary>One fetched item: the provider's id (IMAP UID, file name, Graph/Gmail id) and the parsed message.</summary>
public sealed record FetchedEmail(string ProviderId, InboundEmail Email);

public sealed record EmailFetchResult(IReadOnlyList<FetchedEmail> Items, string? NextCursor, bool HasMore = false);

public sealed record OutboundEmail(
    string From,
    string? FromName,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc,
    string? ReplyTo,
    string Subject,
    string HtmlBody,
    string TextBody,
    string MessageId,
    string? InReplyTo,
    IReadOnlyList<string> References,
    IReadOnlyList<EmailAttachment> Attachments,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>Mailbox provider: polls new inbound mail since a cursor and sends outbound mail.</summary>
public interface IEmailProvider
{
    /// <summary>ChannelAccount.Provider value served by this adapter (FileDrop, Imap, Smtp, Microsoft365, Gmail).</summary>
    string Provider { get; }
    bool SupportsPolling { get; }
    /// <summary>Returns messages newer than <paramref name="cursor"/>. The caller stores NextCursor only after processing succeeded.</summary>
    Task<EmailFetchResult> FetchAsync(ChannelAccountContext account, string? cursor, int max, CancellationToken ct);
    /// <summary>Called after fetched items were stored (FileDrop moves files to processed/, IMAP flags \Seen).</summary>
    Task AcknowledgeAsync(ChannelAccountContext account, IReadOnlyList<string> providerIds, CancellationToken ct);
    Task<SendResult> SendAsync(ChannelAccountContext account, OutboundEmail email, CancellationToken ct);
    Task<ConnectionTestResult> TestAsync(ChannelAccountContext account, CancellationToken ct);
}

/// <summary>MIME parsing/composition (MimeKit adapter) used by the pipeline, the simulator and file-drop.</summary>
public interface IMimeCodec
{
    InboundEmail Parse(byte[] raw);
    byte[] Compose(OutboundEmail email);
}

// ───────────────────────────── Messaging ─────────────────────────────

public sealed record OutboundChannelMessage(
    Guid MessageId,
    string To,
    string? Text,
    string? TemplateName = null,
    string? TemplateLanguage = null,
    IReadOnlyList<string>? TemplateParameters = null,
    string? MediaUrl = null,
    string? MediaType = null,
    string? MediaFileName = null);

public sealed record WebhookRequest(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body,
    string? ContentType)
{
    public string? Header(string name) => Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    public string BodyText => System.Text.Encoding.UTF8.GetString(Body);
}

public sealed record InboundMedia(string ContentType, string? FileName, string? Url = null, string? ProviderMediaId = null, byte[]? Content = null, string? Caption = null);

public sealed record InboundChannelMessage(
    string ExternalId,
    string From,
    string? FromName,
    string? To,
    string? Text,
    string ContentType,
    IReadOnlyList<InboundMedia> Media,
    DateTime OccurredAt,
    string? MetadataJson = null);

public sealed record DeliveryStatusUpdate(string ProviderMessageId, Domain.Communication.MessageStatus Status, DateTime OccurredAt, string? Error = null, string? Recipient = null);

/// <summary>Telephony event: started, answered, ended, missed, voicemail.</summary>
public sealed record CallEvent(
    string CallId,
    string EventType,
    string From,
    string To,
    DateTime OccurredAt,
    string Direction = "inbound",
    int? DurationSeconds = null,
    string? RecordingUrl = null,
    string? AgentEmail = null,
    string? Disposition = null,
    string? Notes = null);

/// <summary>One parsed webhook event. DedupKey is unique per provider event (message id + kind).</summary>
public sealed record ChannelWebhookEvent(string DedupKey, string EventType, InboundChannelMessage? Message = null, DeliveryStatusUpdate? Status = null, CallEvent? Call = null);

public sealed record WebhookParseResult(IReadOnlyList<ChannelWebhookEvent> Events, string? ResponseBody = null, string? ResponseContentType = null);

/// <summary>Messaging provider (WhatsApp, SMS, Messenger/Instagram, telephony): send + webhook verification and parsing.</summary>
public interface IMessagingProvider
{
    string Provider { get; }
    IReadOnlyCollection<ChannelType> ChannelTypes { get; }
    Task<SendResult> SendAsync(ChannelAccountContext account, OutboundChannelMessage message, CancellationToken ct);
    /// <summary>GET subscription handshake (Meta hub.challenge). Returns the body to echo, or null to reject.</summary>
    string? VerifyHandshake(ChannelAccountContext account, WebhookRequest request);
    /// <summary>Verifies the signature (throws <see cref="WebhookRejectedException"/>) and parses the events.</summary>
    WebhookParseResult ParseWebhook(ChannelAccountContext account, WebhookRequest request);
    /// <summary>Downloads inbound media referenced by provider id/URL (null when unavailable).</summary>
    Task<byte[]?> DownloadMediaAsync(ChannelAccountContext account, InboundMedia media, CancellationToken ct);
    Task<ConnectionTestResult> TestAsync(ChannelAccountContext account, CancellationToken ct);
}

// ───────────────────────────── Notifications ─────────────────────────────

/// <summary>Mobile/web push (FCM HTTP v1 and/or APNs, or a log-only fallback when not configured).</summary>
public interface IPushSender
{
    bool IsConfigured { get; }
    Task<SendResult> SendAsync(string deviceToken, string title, string body, IReadOnlyDictionary<string, string> data, CancellationToken ct);

    /// <summary>
    /// Platform-aware send: a router can deliver iOS tokens through APNs directly and Android/Web tokens through FCM.
    /// Single-provider senders ignore the platform. A <see cref="SendResult.Permanent"/> failure means the token is dead.
    /// </summary>
    Task<SendResult> SendAsync(DevicePlatform platform, string deviceToken, string title, string body, IReadOnlyDictionary<string, string> data,
        CancellationToken ct) => SendAsync(deviceToken, title, body, data, ct);
}

/// <summary>Microsoft Teams incoming-webhook notifier.</summary>
public interface ITeamsNotifier
{
    Task<SendResult> SendAsync(string webhookUrl, string title, string text, string? link, CancellationToken ct);
}

/// <summary>Pushes chat events to live-chat visitors and agents (SignalR /hubs/chat in the API; no-op elsewhere).</summary>
public interface IChatRealtime
{
    Task MessageAsync(Guid tenantId, Guid conversationId, object message, CancellationToken ct = default);
    Task StatusAsync(Guid tenantId, Guid conversationId, object status, CancellationToken ct = default);
    Task TypingAsync(Guid tenantId, Guid conversationId, object typing, CancellationToken ct = default);
}

public sealed class NullChatRealtime : IChatRealtime
{
    public Task MessageAsync(Guid tenantId, Guid conversationId, object message, CancellationToken ct = default) => Task.CompletedTask;
    public Task StatusAsync(Guid tenantId, Guid conversationId, object status, CancellationToken ct = default) => Task.CompletedTask;
    public Task TypingAsync(Guid tenantId, Guid conversationId, object typing, CancellationToken ct = default) => Task.CompletedTask;
}
