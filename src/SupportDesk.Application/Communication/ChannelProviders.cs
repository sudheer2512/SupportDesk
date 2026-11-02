using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Communication;

/// <summary>Provider names stored in ChannelAccount.Provider.</summary>
public static class ChannelProviders
{
    public const string FileDrop = "FileDrop";
    public const string Imap = "Imap";
    public const string Smtp = "Smtp";
    public const string Microsoft365 = "Microsoft365";
    public const string Gmail = "Gmail";
    public const string MetaCloud = "MetaCloud";
    public const string MetaGraph = "MetaGraph";
    public const string Twilio = "Twilio";
    public const string GenericSms = "GenericSms";
    public const string GenericVoice = "GenericVoice";
    public const string Widget = "Widget";
    public const string Simulator = "Simulator";

    public sealed record Info(string Provider, ChannelType[] Types, string Description, string[] SettingKeys, string[] SecretKeys, bool Polling, bool Webhooks);

    public static readonly IReadOnlyList<Info> Catalog =
    [
        new(FileDrop, [ChannelType.Email], "Demo and test mailbox: reads .eml files from the server's data folder (maildrop › mailbox address › inbox) and saves sent mail in its sent folder.",
            ["pollIntervalSeconds"], [], true, false),
        new(Imap, [ChannelType.Email], "IMAP (UID cursor) for inbound and SMTP for outbound; SSL/STARTTLS; password or OAuth2 (XOAUTH2).",
            ["imapHost", "imapPort", "imapSecurity", "smtpHost", "smtpPort", "smtpSecurity", "username", "authMode", "folder", "markSeen", "pollIntervalSeconds"],
            ["password", "accessToken"], true, false),
        new(Smtp, [ChannelType.Email], "Outbound-only SMTP mailbox (no polling).", ["smtpHost", "smtpPort", "smtpSecurity", "username", "authMode"], ["password", "accessToken"], false, false),
        new(Microsoft365, [ChannelType.Email], "Microsoft 365 via Microsoft Graph: client-credentials token, inbox delta query, sendMail.",
            ["tenantId", "clientId", "mailbox", "pollIntervalSeconds"], ["clientSecret"], true, false),
        new(Gmail, [ChannelType.Email], "Gmail API: refresh-token OAuth, history-id cursor, messages.send (raw MIME).",
            ["clientId", "userId", "pollIntervalSeconds"], ["clientSecret", "refreshToken"], true, false),
        new(MetaCloud, [ChannelType.WhatsApp], "WhatsApp Business Cloud API (Meta): text/template/media, webhooks with X-Hub-Signature-256.",
            ["phoneNumberId", "businessAccountId", "graphVersion", "verifyToken", "serviceWindowTemplate", "serviceWindowTemplateLanguage"],
            ["accessToken", "appSecret"], false, true),
        new(MetaGraph, [ChannelType.Facebook, ChannelType.Instagram], "Facebook Messenger / Instagram messaging via the Graph Send API and webhooks.",
            ["pageId", "graphVersion", "verifyToken"], ["pageAccessToken", "appSecret"], false, true),
        new(Twilio, [ChannelType.Sms], "Twilio Programmable Messaging (REST) with X-Twilio-Signature webhook validation.",
            ["accountSid", "messagingServiceSid", "webhookUrl"], ["authToken"], false, true),
        new(GenericSms, [ChannelType.Sms], "Generic HTTP SMS gateway: configurable URL, method, body template and HMAC-signed inbound webhooks.",
            ["sendUrl", "method", "contentType", "bodyTemplate", "authHeaderName", "authHeaderValueTemplate", "messageIdPath"], ["apiKey", "webhookSecret"], false, true),
        new(GenericVoice, [ChannelType.Voice], "Generic telephony webhook (call started/answered/ended/missed/voicemail), HMAC-SHA256 signed.",
            ["createTicketsForAnsweredCalls"], ["webhookSecret"], false, true),
        new(Widget, [ChannelType.Chat], "Embeddable live-chat widget (/widget/widget.js) with bot-first mode and human handoff.",
            ["greeting", "botEnabled", "requireEmail", "verifyIdentity", "offlineMessage", "title"], [], false, false),
        new(Simulator, [ChannelType.WhatsApp, ChannelType.Sms, ChannelType.Facebook, ChannelType.Instagram, ChannelType.Voice],
            "Channel simulator for demos and tests: outbound messages are recorded (not sent); inbound events are injected through the simulator API.",
            [], [], false, false)
    ];

    public static Info? Find(string provider) => Catalog.FirstOrDefault(p => p.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));

    public static Channel ToChannel(ChannelType type) => type switch
    {
        ChannelType.Email => Channel.Email,
        ChannelType.WhatsApp => Channel.WhatsApp,
        ChannelType.Sms => Channel.Sms,
        ChannelType.Chat => Channel.Chat,
        ChannelType.Voice => Channel.Phone,
        ChannelType.Facebook => Channel.Facebook,
        ChannelType.Instagram => Channel.Instagram,
        ChannelType.WebForm => Channel.WebForm,
        _ => Channel.Api
    };

    public static ChannelType? ToChannelType(Channel channel) => channel switch
    {
        Channel.Email => ChannelType.Email,
        Channel.WhatsApp => ChannelType.WhatsApp,
        Channel.Sms => ChannelType.Sms,
        Channel.Chat => ChannelType.Chat,
        Channel.Phone => ChannelType.Voice,
        Channel.Facebook => ChannelType.Facebook,
        Channel.Instagram => ChannelType.Instagram,
        _ => null
    };
}

/// <summary>Resolves the adapter for a channel account's provider.</summary>
public sealed class ChannelProviderRegistry(IEnumerable<IEmailProvider> email, IEnumerable<IMessagingProvider> messaging)
{
    private readonly List<IEmailProvider> _email = email.ToList();
    private readonly List<IMessagingProvider> _messaging = messaging.ToList();

    public IEmailProvider? Email(string provider) => _email.FirstOrDefault(p => p.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));

    public IMessagingProvider? Messaging(string provider) => _messaging.FirstOrDefault(p => p.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));

    public IEmailProvider RequireEmail(string provider) =>
        Email(provider) ?? throw new ChannelConfigurationException($"No e-mail adapter is installed for provider '{provider}'.");

    public IMessagingProvider RequireMessaging(string provider) =>
        Messaging(provider) ?? throw new ChannelConfigurationException($"No messaging adapter is installed for provider '{provider}'.");
}

/// <summary>Builds <see cref="ChannelAccountContext"/> instances (decrypts secrets on demand, in memory only).</summary>
public sealed class ChannelContextFactory(ISecretProtector protector)
{
    public ChannelAccountContext Create(ChannelAccount account)
    {
        var settings = Json.Object(account.SettingsJson);
        var secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(account.SecretsProtected))
        {
            try
            {
                foreach (var (k, v) in Json.DeserializeOr(protector.Unprotect(account.SecretsProtected), new Dictionary<string, string>())) secrets[k] = v;
            }
            catch (CryptographicException)
            {
                throw new ChannelConfigurationException("The channel's stored credentials cannot be decrypted (master key changed). Re-enter the secrets.");
            }
        }
        return new ChannelAccountContext(account, settings, secrets);
    }

    public string? ProtectSecrets(IReadOnlyDictionary<string, string> secrets) =>
        secrets.Count == 0 ? null : protector.Protect(Json.Serialize(secrets));

    public Dictionary<string, string> ReadSecrets(ChannelAccount account) =>
        string.IsNullOrEmpty(account.SecretsProtected)
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(Json.DeserializeOr(protector.Unprotect(account.SecretsProtected), new Dictionary<string, string>()), StringComparer.OrdinalIgnoreCase);
}

/// <summary>Tenant-level omnichannel settings (tenant setting "communication").</summary>
public sealed record CommunicationSettings
{
    /// <summary>Encrypted HMAC key used to sign plus-address reply tokens (generated on first use).</summary>
    public string? ReplyTokenKeyProtected { get; init; }
    /// <summary>Maximum automatic acknowledgements per sender per hour (mail-loop protection).</summary>
    public int MaxAcknowledgementsPerSenderPerHour { get; init; } = 3;
}

/// <summary>Per-tenant secrets and public links used by the omnichannel module.</summary>
public sealed class CommunicationKeys(DeskDbContext db, TenantSettingsService settings, ISecretProtector protector, IConfiguration config)
{
    public const string SettingKey = "communication";
    private byte[]? _replyKey;

    public async Task<byte[]> ReplyTokenKeyAsync(CancellationToken ct = default)
    {
        if (_replyKey is not null) return _replyKey;
        var s = await settings.GetAsync<CommunicationSettings>(SettingKey, ct);
        if (s.ReplyTokenKeyProtected is { Length: > 0 } p) return _replyKey = Convert.FromBase64String(protector.Unprotect(p));
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            await settings.SetAsync(SettingKey, s with { ReplyTokenKeyProtected = protector.Protect(Convert.ToBase64String(key)) }, ct);
            return _replyKey = key;
        }
        catch (DbUpdateException)
        {
            // Another worker created the key concurrently — use theirs.
            db.ChangeTracker.Clear();
            var row = await db.TenantSettings.AsNoTracking().FirstAsync(x => x.Key == SettingKey, ct);
            var stored = Json.DeserializeOr(row.ValueJson, new CommunicationSettings());
            return _replyKey = Convert.FromBase64String(protector.Unprotect(stored.ReplyTokenKeyProtected!));
        }
    }

    public Task<CommunicationSettings> SettingsAsync(CancellationToken ct = default) => settings.GetAsync<CommunicationSettings>(SettingKey, ct);

    /// <summary>Absolute base URL for links in outbound messages (Communication:PublicBaseUrl, App:PublicUrl).</summary>
    public string PublicBaseUrl =>
        (config["Communication:PublicBaseUrl"] is { Length: > 0 } a ? a : config["App:PublicUrl"] is { Length: > 0 } b ? b : "http://localhost:5830").TrimEnd('/');
}

/// <summary>
/// Plus-address reply tokens: "{ticketNumber}x{hmac10}" (lower-case alphanumeric, safe in e-mail local parts).
/// The HMAC (per-tenant key) prevents strangers from injecting mail into arbitrary tickets by guessing numbers.
/// </summary>
public static class ReplyTokens
{
    public static string Create(byte[] key, long ticketNumber) => $"{ticketNumber}x{Signature(key, ticketNumber)}";

    public static long? Parse(byte[] key, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var t = token.Trim().ToLowerInvariant();
        var i = t.IndexOf('x');
        if (i <= 0 || !long.TryParse(t[..i], out var number)) return null;
        var expected = Signature(key, number);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(t[(i + 1)..])) ? number : null;
    }

    private static string Signature(byte[] key, long number) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"reply-token:{number}")))[..10].ToLowerInvariant();
}

internal static class ChannelJson
{
    public static string Merge(string existingJson, JsonElement? patch)
    {
        if (patch is not { ValueKind: JsonValueKind.Object } p) return existingJson;
        var current = Json.Object(existingJson);
        foreach (var prop in p.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Null) current.Remove(prop.Name);
            else current[prop.Name] = prop.Value.Clone();
        }
        return Json.Serialize(current);
    }
}
