using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SupportDesk.Application.Communication;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Rules;

namespace SupportDesk.Integrations.Messaging;

/// <summary>
/// Twilio Programmable Messaging: REST send (basic auth, From number or MessagingServiceSid), inbound message and status
/// callbacks validated with X-Twilio-Signature (HMAC-SHA1 over the public URL + sorted form fields; set the webhookUrl
/// setting when a proxy rewrites the URL), media download with account credentials.
/// </summary>
public sealed class TwilioSmsProvider(HttpClient http) : IMessagingProvider
{
    public string Provider => ChannelProviders.Twilio;
    public IReadOnlyCollection<ChannelType> ChannelTypes { get; } = [ChannelType.Sms];

    private static AuthenticationHeaderValue Auth(ChannelAccountContext a) =>
        new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{a.RequireSetting("accountSid")}:{a.RequireSecret("authToken")}")));

    public async Task<SendResult> SendAsync(ChannelAccountContext account, OutboundChannelMessage message, CancellationToken ct)
    {
        var form = new Dictionary<string, string> { ["To"] = message.To, ["Body"] = message.Text ?? "" };
        if (account.Setting("messagingServiceSid") is { Length: > 0 } svc) form["MessagingServiceSid"] = svc;
        else form["From"] = account.Account.Address;
        if (message.MediaUrl is { Length: > 0 } media) form["MediaUrl"] = media;
        if (account.Setting("statusCallbackUrl") is { Length: > 0 } cb) form["StatusCallback"] = cb;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.twilio.com/2010-04-01/Accounts/{Uri.EscapeDataString(account.RequireSetting("accountSid"))}/Messages.json")
        {
            Content = new FormUrlEncodedContent(form)
        };
        req.Headers.Authorization = Auth(account);
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(body);
            return SendResult.Ok(doc.RootElement.GetProperty("sid").GetString());
        }
        var detail = Email.OAuthTokenCache.Describe(body);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var m)) detail = $"{(doc.RootElement.TryGetProperty("code", out var c) ? c + " " : "")}{m.GetString()}";
        }
        catch (JsonException) { }
        var permanent = res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound;
        return SendResult.Fail($"Twilio rejected the message: {detail}", permanent);
    }

    public string? VerifyHandshake(ChannelAccountContext account, WebhookRequest request) => null;

    public WebhookParseResult ParseWebhook(ChannelAccountContext account, WebhookRequest request)
    {
        var form = WebhookSignatures.ParseForm(request.BodyText);
        var url = account.Setting("webhookUrl") is { Length: > 0 } configured ? configured : request.Url;
        if (!WebhookSignatures.VerifyTwilio(account.Secret("authToken"), url, form, request.Header("X-Twilio-Signature")))
            throw new WebhookRejectedException("X-Twilio-Signature does not match.");
        var events = new List<ChannelWebhookEvent>();
        var sid = form.GetValueOrDefault("MessageSid") ?? form.GetValueOrDefault("SmsSid");
        if (sid is null) return new WebhookParseResult(events, "<Response/>", "text/xml");
        var status = form.GetValueOrDefault("MessageStatus") ?? form.GetValueOrDefault("SmsStatus");
        if (form.ContainsKey("Body") && status is null or "received" or "receiving")
        {
            var media = new List<InboundMedia>();
            var count = int.TryParse(form.GetValueOrDefault("NumMedia"), out var n) ? n : 0;
            for (var i = 0; i < Math.Min(count, 10); i++)
                if (form.GetValueOrDefault($"MediaUrl{i}") is { } mu)
                    media.Add(new InboundMedia(form.GetValueOrDefault($"MediaContentType{i}") ?? "application/octet-stream", null, Url: mu));
            events.Add(new ChannelWebhookEvent($"msg:{sid}", "message", new InboundChannelMessage(sid, form.GetValueOrDefault("From") ?? "",
                form.GetValueOrDefault("ProfileName"), form.GetValueOrDefault("To"), form.GetValueOrDefault("Body"), media.Count > 0 ? "media" : "text", media, DateTime.UtcNow)));
        }
        else if (status is not null)
        {
            var mapped = status switch
            {
                "sent" => MessageStatus.Sent, "delivered" => MessageStatus.Delivered, "read" => MessageStatus.Read,
                "failed" or "undelivered" => MessageStatus.Failed, _ => (MessageStatus?)null
            };
            if (mapped is { } s)
            {
                var error = form.GetValueOrDefault("ErrorCode") is { Length: > 0 } code ? $"Twilio error {code}{(form.GetValueOrDefault("ErrorMessage") is { } em ? ": " + em : "")}" : null;
                events.Add(new ChannelWebhookEvent($"status:{sid}:{s}", "status", Status: new DeliveryStatusUpdate(sid, s, DateTime.UtcNow, error, form.GetValueOrDefault("To"))));
            }
        }
        return new WebhookParseResult(events, "<Response/>", "text/xml");
    }

    public async Task<byte[]?> DownloadMediaAsync(ChannelAccountContext account, InboundMedia media, CancellationToken ct)
    {
        if (media.Url is not { } url || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = Auth(account);
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? await res.Content.ReadAsByteArrayAsync(ct) : null;
    }

    public async Task<ConnectionTestResult> TestAsync(ChannelAccountContext account, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.twilio.com/2010-04-01/Accounts/{Uri.EscapeDataString(account.RequireSetting("accountSid"))}.json");
        req.Headers.Authorization = Auth(account);
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) return new ConnectionTestResult(false, $"Twilio returned {(int)res.StatusCode}.");
        using var doc = JsonDocument.Parse(body);
        return new ConnectionTestResult(true, $"Connected to Twilio account '{doc.RootElement.GetProperty("friendly_name").GetString()}' ({doc.RootElement.GetProperty("status").GetString()}).");
    }
}

/// <summary>
/// Generic HTTP SMS gateway: the send request is built from settings (sendUrl, method, contentType, bodyTemplate with
/// {{to}} {{from}} {{text}} {{messageId}} {{apiKey}} placeholders, optional auth header) and the provider id is read from
/// the JSON response (messageIdPath). Inbound/status webhooks must carry X-Signature = hex HMAC-SHA256(webhookSecret, body);
/// JSON {"type":"message","id","from","to","text"} or {"type":"status","id","status","error"}.
/// </summary>
public sealed class GenericSmsProvider(HttpClient http) : IMessagingProvider
{
    public string Provider => ChannelProviders.GenericSms;
    public IReadOnlyCollection<ChannelType> ChannelTypes { get; } = [ChannelType.Sms];

    public async Task<SendResult> SendAsync(ChannelAccountContext account, OutboundChannelMessage message, CancellationToken ct)
    {
        var url = account.RequireSetting("sendUrl");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            return SendResult.Fail("The sendUrl setting must be an absolute http(s) URL.", permanent: true);
        var contentType = account.SettingOr("contentType", "application/json");
        var json = contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
        var values = new Dictionary<string, string>
        {
            ["to"] = message.To, ["from"] = account.Account.Address, ["text"] = message.Text ?? "", ["messageId"] = message.MessageId.ToString("N"),
            ["apiKey"] = account.Secret("apiKey") ?? ""
        };
        var template = account.SettingOr("bodyTemplate", json ? "{\"to\":\"{{to}}\",\"from\":\"{{from}}\",\"text\":\"{{text}}\",\"reference\":\"{{messageId}}\"}" : "to={{to}}&from={{from}}&text={{text}}");
        var body = Fill(template, values, v => json ? JsonEncodedText.Encode(v).ToString() : Uri.EscapeDataString(v));
        using var req = new HttpRequestMessage(new HttpMethod(account.SettingOr("method", "POST").ToUpperInvariant()), Fill(url, values, Uri.EscapeDataString))
        {
            Content = new StringContent(body, Encoding.UTF8, contentType)
        };
        if (account.Secret("apiKey") is not null)
            req.Headers.TryAddWithoutValidation(account.SettingOr("authHeaderName", "Authorization"), Fill(account.SettingOr("authHeaderValueTemplate", "Bearer {{apiKey}}"), values, v => v));
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            return SendResult.Fail($"SMS gateway returned {(int)res.StatusCode}: {TextNormalization.Truncate(text, 300)}", (int)res.StatusCode is >= 400 and < 500 and not 429);
        return SendResult.Ok(ReadPath(text, account.SettingOr("messageIdPath", "id")) ?? message.MessageId.ToString("N"));
    }

    public string? VerifyHandshake(ChannelAccountContext account, WebhookRequest request) => null;

    public WebhookParseResult ParseWebhook(ChannelAccountContext account, WebhookRequest request)
    {
        if (!WebhookSignatures.VerifyHmacSha256(account.Secret("webhookSecret"), request.Body, request.Header("X-Signature")))
            throw new WebhookRejectedException("X-Signature does not match (configure the webhookSecret secret on the channel).");
        var events = new List<ChannelWebhookEvent>();
        using var doc = JsonDocument.Parse(request.Body);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        foreach (var e in items)
        {
            string? S(string name) => e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;
            var id = S("id") ?? S("messageId");
            if (id is null) continue;
            if (string.Equals(S("type"), "status", StringComparison.OrdinalIgnoreCase))
            {
                var status = (S("status") ?? "").ToLowerInvariant() switch
                {
                    "sent" => MessageStatus.Sent, "delivered" => MessageStatus.Delivered, "read" => MessageStatus.Read, "failed" or "undelivered" or "rejected" => MessageStatus.Failed,
                    _ => (MessageStatus?)null
                };
                if (status is { } s) events.Add(new ChannelWebhookEvent($"status:{id}:{s}", "status", Status: new DeliveryStatusUpdate(id, s, DateTime.UtcNow, S("error"))));
            }
            else
            {
                events.Add(new ChannelWebhookEvent($"msg:{id}", "message", new InboundChannelMessage(id, S("from") ?? "", S("name"), S("to"), S("text"), "text", [], DateTime.UtcNow)));
            }
        }
        return new WebhookParseResult(events);
    }

    public Task<byte[]?> DownloadMediaAsync(ChannelAccountContext account, InboundMedia media, CancellationToken ct) => Task.FromResult<byte[]?>(null);

    public Task<ConnectionTestResult> TestAsync(ChannelAccountContext account, CancellationToken ct)
    {
        var url = account.Setting("sendUrl");
        var ok = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
        var details = new Dictionary<string, string> { ["sendUrl"] = ok ? uri!.GetLeftPart(UriPartial.Path) : "(missing)" };
        if (account.Secret("webhookSecret") is null) details["warning"] = "Set webhookSecret to accept inbound messages.";
        return Task.FromResult(new ConnectionTestResult(ok, ok ? "Gateway configuration is valid (no test message was sent)." : "Configure an absolute sendUrl.", details));
    }

    private static string Fill(string template, Dictionary<string, string> values, Func<string, string> encode)
    {
        var result = template;
        foreach (var (k, v) in values) result = result.Replace("{{" + k + "}}", encode(v), StringComparison.Ordinal);
        return result;
    }

    private static string? ReadPath(string json, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var current = doc.RootElement;
            foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.ValueKind == JsonValueKind.Array && int.TryParse(part, out var i) && i < current.GetArrayLength()) current = current[i];
                else if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(part, out var next)) current = next;
                else return null;
            }
            return current.ValueKind == JsonValueKind.String ? current.GetString() : current.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
