using System.Text.Json;
using SupportDesk.Application.Communication;
using SupportDesk.Domain.Communication;

namespace SupportDesk.Integrations.Messaging;

/// <summary>
/// Generic telephony webhook (PBX / cloud telephony): JSON {"callId","event":"started|answered|ended|missed|voicemail",
/// "from","to","direction","timestamp","durationSeconds","recordingUrl","agentEmail","disposition","notes"} (or an array of
/// them), signed with X-Signature = hex HMAC-SHA256(webhookSecret, body). Voice lines do not send messages.
/// </summary>
public sealed class GenericVoiceProvider : IMessagingProvider
{
    public string Provider => ChannelProviders.GenericVoice;
    public IReadOnlyCollection<ChannelType> ChannelTypes { get; } = [ChannelType.Voice];

    public Task<SendResult> SendAsync(ChannelAccountContext account, OutboundChannelMessage message, CancellationToken ct) =>
        Task.FromResult(SendResult.Fail("Voice lines cannot send messages; reply by e-mail or SMS instead.", permanent: true));

    public string? VerifyHandshake(ChannelAccountContext account, WebhookRequest request) => null;

    public WebhookParseResult ParseWebhook(ChannelAccountContext account, WebhookRequest request)
    {
        if (!WebhookSignatures.VerifyHmacSha256(account.Secret("webhookSecret"), request.Body, request.Header("X-Signature")))
            throw new WebhookRejectedException("X-Signature does not match.");
        using var doc = JsonDocument.Parse(request.Body);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList()
            : doc.RootElement.TryGetProperty("events", out var evs) && evs.ValueKind == JsonValueKind.Array ? evs.EnumerateArray().ToList() : [doc.RootElement];
        var events = new List<ChannelWebhookEvent>();
        foreach (var e in items)
        {
            string? S(string name) => e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;
            var callId = S("callId") ?? S("call_id");
            var type = S("event") ?? S("status");
            if (callId is null || type is null) continue;
            var at = DateTime.TryParse(S("timestamp"), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var ts)
                ? ts : DateTime.UtcNow;
            int? duration = int.TryParse(S("durationSeconds") ?? S("duration"), out var d) ? d : null;
            events.Add(new ChannelWebhookEvent($"call:{callId}:{type.ToLowerInvariant()}", "call", Call: new CallEvent(callId, type, S("from") ?? "", S("to") ?? account.Account.Address,
                at, S("direction") ?? "inbound", duration, S("recordingUrl"), S("agentEmail"), S("disposition"), S("notes"))));
        }
        return new WebhookParseResult(events);
    }

    public Task<byte[]?> DownloadMediaAsync(ChannelAccountContext account, InboundMedia media, CancellationToken ct) => Task.FromResult<byte[]?>(null);

    public Task<ConnectionTestResult> TestAsync(ChannelAccountContext account, CancellationToken ct) =>
        Task.FromResult(account.Secret("webhookSecret") is null
            ? new ConnectionTestResult(false, "Set the webhookSecret secret and configure your telephony system to sign webhooks with it.")
            : new ConnectionTestResult(true, "Voice line ready. Point your telephony webhooks at the channel's webhook URL."));
}

/// <summary>
/// Simulator provider for demos and tests: outbound messages are recorded (with a provider id) but never leave the system;
/// inbound traffic is injected through the simulator API, so there are no webhooks.
/// </summary>
public sealed class SimulatorProvider : IMessagingProvider
{
    public string Provider => ChannelProviders.Simulator;
    public IReadOnlyCollection<ChannelType> ChannelTypes { get; } = [ChannelType.WhatsApp, ChannelType.Sms, ChannelType.Facebook, ChannelType.Instagram, ChannelType.Voice];

    public Task<SendResult> SendAsync(ChannelAccountContext account, OutboundChannelMessage message, CancellationToken ct) =>
        Task.FromResult(account.Account.Type == ChannelType.Voice
            ? SendResult.Fail("Voice lines cannot send messages.", permanent: true)
            : SendResult.Ok($"sim.{message.MessageId:N}"));

    public string? VerifyHandshake(ChannelAccountContext account, WebhookRequest request) => null;

    public WebhookParseResult ParseWebhook(ChannelAccountContext account, WebhookRequest request) =>
        throw new WebhookRejectedException("Simulator channels do not accept webhooks; use the simulator API.");

    public Task<byte[]?> DownloadMediaAsync(ChannelAccountContext account, InboundMedia media, CancellationToken ct) => Task.FromResult(media.Content);

    public Task<ConnectionTestResult> TestAsync(ChannelAccountContext account, CancellationToken ct) =>
        Task.FromResult(new ConnectionTestResult(true, "Simulator mode: outbound messages are recorded, not sent. Inject inbound traffic with the channel simulator."));
}
