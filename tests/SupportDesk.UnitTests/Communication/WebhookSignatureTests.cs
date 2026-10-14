using System.Text;
using System.Text.Json;
using SupportDesk.Application.Communication;
using SupportDesk.Application.Platform;
using SupportDesk.Domain.Communication;
using SupportDesk.Integrations.Messaging;

namespace SupportDesk.UnitTests.Communication;

public sealed class WebhookSignatureTests
{
    private static ChannelAccountContext Account(string provider, ChannelType type, string address, object settings, Dictionary<string, string> secrets)
    {
        var json = JsonSerializer.Serialize(settings);
        var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        return new ChannelAccountContext(new ChannelAccount { Provider = provider, Type = type, Address = address, SettingsJson = json }, map,
            new Dictionary<string, string>(secrets, StringComparer.OrdinalIgnoreCase));
    }

    private static WebhookRequest Post(byte[] body, Dictionary<string, string> headers, string url = "https://desk.example.com/api/v1/channels/webhooks/x/key") =>
        new("POST", url, new Dictionary<string, string>(), headers, body, "application/json");

    private const string WhatsAppPayload = """
        {"object":"whatsapp_business_account","entry":[{"id":"WABA","changes":[{"field":"messages","value":{
          "messaging_product":"whatsapp","metadata":{"display_phone_number":"918040001234","phone_number_id":"1234567890"},
          "contacts":[{"profile":{"name":"Rekha Nair"},"wa_id":"919810050002"}],
          "messages":[{"from":"919810050002","id":"wamid.HBgM1","timestamp":"1791950000","type":"text","text":{"body":"POS is frozen again"}}],
          "statuses":[{"id":"wamid.OUT1","status":"read","timestamp":"1791950100","recipient_id":"919810050002"}]}}]}]}
        """;

    [Fact]
    public void Meta_hub_signature_is_verified_in_constant_time_format()
    {
        var body = Encoding.UTF8.GetBytes("{\"a\":1}");
        var sig = "sha256=" + WebhookSignatures.HmacSha256Hex("app-secret", body);
        Assert.True(WebhookSignatures.VerifyHmacSha256("app-secret", body, sig));
        Assert.True(WebhookSignatures.VerifyHmacSha256("app-secret", body, sig.ToUpperInvariant().Replace("SHA256=", "sha256=")));
        Assert.False(WebhookSignatures.VerifyHmacSha256("other-secret", body, sig));
        Assert.False(WebhookSignatures.VerifyHmacSha256("app-secret", Encoding.UTF8.GetBytes("{\"a\":2}"), sig));
        Assert.False(WebhookSignatures.VerifyHmacSha256("app-secret", body, null));
        Assert.False(WebhookSignatures.VerifyHmacSha256(null, body, sig));
    }

    [Fact]
    public void Twilio_signature_matches_the_documented_algorithm()
    {
        // Example from Twilio's request-validation documentation.
        var form = new Dictionary<string, string>
        {
            ["CallSid"] = "CA1234567890ABCDE", ["Caller"] = "+12349013030", ["Digits"] = "1234", ["From"] = "+12349013030", ["To"] = "+18005551212"
        };
        const string url = "https://mycompany.com/myapp.php?foo=1&bar=2";
        var signature = WebhookSignatures.TwilioSignature("12345", url, form);
        Assert.Equal("0/KCTR6DLpKmkAf8muzZqo1nDgQ=", signature);
        Assert.True(WebhookSignatures.VerifyTwilio("12345", url, form, signature));
        Assert.False(WebhookSignatures.VerifyTwilio("12345", url + "&x=1", form, signature));
        Assert.Equal("a b+c", WebhookSignatures.ParseForm("Body=a+b%2Bc")["Body"]);
    }

    [Fact]
    public void WhatsApp_webhook_with_bad_signature_is_rejected()
    {
        var account = Account(ChannelProviders.MetaCloud, ChannelType.WhatsApp, "+918040001234", new { phoneNumberId = "1234567890", verifyToken = "vt" },
            new() { ["appSecret"] = "s3cret", ["accessToken"] = "tok" });
        var body = Encoding.UTF8.GetBytes(WhatsAppPayload);
        var provider = new WhatsAppCloudProvider(new HttpClient());
        Assert.Throws<WebhookRejectedException>(() => provider.ParseWebhook(account, Post(body, new() { ["X-Hub-Signature-256"] = "sha256=deadbeef" })));
        Assert.Throws<WebhookRejectedException>(() => provider.ParseWebhook(account, Post(body, new())));

        var noSecret = Account(ChannelProviders.MetaCloud, ChannelType.WhatsApp, "+918040001234", new { phoneNumberId = "1234567890" }, new());
        Assert.Throws<WebhookRejectedException>(() => provider.ParseWebhook(noSecret,
            Post(body, new() { ["X-Hub-Signature-256"] = "sha256=" + WebhookSignatures.HmacSha256Hex("s3cret", body) })));
    }

    [Fact]
    public void WhatsApp_webhook_with_valid_signature_yields_messages_and_statuses()
    {
        var account = Account(ChannelProviders.MetaCloud, ChannelType.WhatsApp, "+918040001234", new { phoneNumberId = "1234567890", verifyToken = "vt" },
            new() { ["appSecret"] = "s3cret", ["accessToken"] = "tok" });
        var body = Encoding.UTF8.GetBytes(WhatsAppPayload);
        var result = new WhatsAppCloudProvider(new HttpClient()).ParseWebhook(account,
            Post(body, new() { ["X-Hub-Signature-256"] = "sha256=" + WebhookSignatures.HmacSha256Hex("s3cret", body) }));

        var message = Assert.Single(result.Events, e => e.Message is not null).Message!;
        Assert.Equal("wamid.HBgM1", message.ExternalId);
        Assert.Equal("+919810050002", message.From);
        Assert.Equal("Rekha Nair", message.FromName);
        Assert.Equal("POS is frozen again", message.Text);
        var status = Assert.Single(result.Events, e => e.Status is not null).Status!;
        Assert.Equal(MessageStatus.Read, status.Status);
        Assert.Equal("wamid.OUT1", status.ProviderMessageId);
        Assert.Equal(2, result.Events.Select(e => e.DedupKey).Distinct().Count());
    }

    [Fact]
    public void Meta_verify_token_handshake_echoes_the_challenge_only_for_the_right_token()
    {
        var account = Account(ChannelProviders.MetaCloud, ChannelType.WhatsApp, "+918040001234", new { verifyToken = "my-verify-token" }, new());
        var provider = new WhatsAppCloudProvider(new HttpClient());
        WebhookRequest Get(string token) => new("GET", "https://x", new Dictionary<string, string>
        {
            ["hub.mode"] = "subscribe", ["hub.verify_token"] = token, ["hub.challenge"] = "1158201444"
        }, new Dictionary<string, string>(), [], null);
        Assert.Equal("1158201444", provider.VerifyHandshake(account, Get("my-verify-token")));
        Assert.Null(provider.VerifyHandshake(account, Get("wrong")));
    }

    [Fact]
    public void Twilio_inbound_and_status_callbacks_require_a_valid_signature()
    {
        var account = Account(ChannelProviders.Twilio, ChannelType.Sms, "+15005550006", new { accountSid = "AC123" }, new() { ["authToken"] = "twilio-token" });
        const string url = "https://desk.example.com/api/v1/channels/webhooks/twilio/abc";
        var form = new Dictionary<string, string> { ["MessageSid"] = "SM1", ["From"] = "+919810010003", ["To"] = "+15005550006", ["Body"] = "Hello", ["SmsStatus"] = "received", ["NumMedia"] = "0" };
        var body = Encoding.UTF8.GetBytes(string.Join("&", form.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")));
        var provider = new TwilioSmsProvider(new HttpClient());

        Assert.Throws<WebhookRejectedException>(() => provider.ParseWebhook(account, Post(body, new() { ["X-Twilio-Signature"] = "bad" }, url)));
        var ok = provider.ParseWebhook(account, Post(body, new() { ["X-Twilio-Signature"] = WebhookSignatures.TwilioSignature("twilio-token", url, form) }, url));
        var msg = Assert.Single(ok.Events).Message!;
        Assert.Equal("SM1", msg.ExternalId);
        Assert.Equal("Hello", msg.Text);
        Assert.Equal("text/xml", ok.ResponseContentType);
    }

    [Fact]
    public void Telephony_webhook_is_signed_and_parsed_into_call_events()
    {
        var account = Account(ChannelProviders.GenericVoice, ChannelType.Voice, "+918040001000", new { }, new() { ["webhookSecret"] = "voice-secret" });
        var body = Encoding.UTF8.GetBytes("""{"callId":"c-1","event":"voicemail","from":"+919810020002","recordingUrl":"https://rec.example/vm.mp3","durationSeconds":42}""");
        var provider = new GenericVoiceProvider();
        Assert.Throws<WebhookRejectedException>(() => provider.ParseWebhook(account, Post(body, new() { ["X-Signature"] = "00" })));
        var parsed = provider.ParseWebhook(account, Post(body, new() { ["X-Signature"] = WebhookSignatures.HmacSha256Hex("voice-secret", body) }));
        var call = Assert.Single(parsed.Events).Call!;
        Assert.Equal("c-1", call.CallId);
        Assert.Equal("voicemail", call.EventType);
        Assert.Equal(42, call.DurationSeconds);
        Assert.Equal("https://rec.example/vm.mp3", call.RecordingUrl);
    }

    [Fact]
    public void Chat_visitor_tokens_are_signed_and_bound_to_conversation_and_tenant()
    {
        var tokens = new ChatTokens(new AesGcmSecretProtector(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()));
        var tenant = Guid.NewGuid();
        var conversation = Guid.NewGuid();
        var token = tokens.Create(tenant, conversation);
        Assert.Equal((tenant, conversation), tokens.Validate(token));
        var parts = token.Split('.');
        Assert.Null(tokens.Validate($"{Guid.NewGuid():N}.{parts[1]}.{parts[2]}.{parts[3]}"));
        Assert.Null(tokens.Validate(token + "x"));
        Assert.Null(new ChatTokens(new AesGcmSecretProtector(new byte[32])).Validate(token));
    }
}
