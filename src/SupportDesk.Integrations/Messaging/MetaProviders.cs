using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SupportDesk.Application.Communication;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Rules;

namespace SupportDesk.Integrations.Messaging;

/// <summary>Shared Meta Graph webhook handling: hub.challenge handshake and X-Hub-Signature-256 verification.</summary>
internal static class MetaWebhook
{
    public static string? Handshake(ChannelAccountContext account, WebhookRequest request)
    {
        request.Query.TryGetValue("hub.mode", out var mode);
        request.Query.TryGetValue("hub.verify_token", out var token);
        request.Query.TryGetValue("hub.challenge", out var challenge);
        var expected = account.Setting("verifyToken");
        if (mode != "subscribe" || string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(challenge)) return null;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(token)) ? challenge : null;
    }

    public static JsonElement VerifyAndParse(ChannelAccountContext account, WebhookRequest request)
    {
        var secret = account.Secret("appSecret") ?? throw new WebhookRejectedException("The channel has no app secret; unsigned webhooks are not accepted.");
        if (!WebhookSignatures.VerifyHmacSha256(secret, request.Body, request.Header("X-Hub-Signature-256")))
            throw new WebhookRejectedException("X-Hub-Signature-256 does not match the payload.");
        using var doc = JsonDocument.Parse(request.Body);
        return doc.RootElement.Clone();
    }

    public static DateTime Unix(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return DateTime.UtcNow;
        long seconds = v.ValueKind == JsonValueKind.Number ? v.GetInt64() : long.TryParse(v.GetString(), out var s) ? s : 0;
        if (seconds > 100_000_000_000) seconds /= 1000; // milliseconds (Messenger)
        return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : DateTime.UtcNow;
    }

    public static async Task<SendResult> ReadSendResultAsync(HttpResponseMessage res, Func<JsonElement, string?> id, CancellationToken ct)
    {
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(body);
            return SendResult.Ok(id(doc.RootElement));
        }
        string message = $"HTTP {(int)res.StatusCode}";
        int? code = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                message = err.TryGetProperty("message", out var m) ? m.GetString() ?? message : message;
                code = err.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : null;
            }
        }
        catch (JsonException) { }
        // 4xx (except throttling) are request/permission problems — retrying will not help. 131047 = outside the 24h window.
        var permanent = (int)res.StatusCode is >= 400 and < 500 && res.StatusCode != HttpStatusCode.TooManyRequests && code is not (4 or 80007 or 130429);
        return SendResult.Fail($"Meta API error{(code is null ? "" : $" {code}")}: {message}", permanent);
    }
}

/// <summary>
/// WhatsApp Business Cloud API (Meta): text, template and media messages; webhooks with messages and statuses
/// (sent/delivered/read/failed), X-Hub-Signature-256 verification and the GET verify-token handshake; media download.
/// </summary>
public sealed class WhatsAppCloudProvider(HttpClient http) : IMessagingProvider
{
    public string Provider => ChannelProviders.MetaCloud;
    public IReadOnlyCollection<ChannelType> ChannelTypes { get; } = [ChannelType.WhatsApp];

    private static string Base(ChannelAccountContext a) => $"https://graph.facebook.com/{a.SettingOr("graphVersion", "v21.0")}";

    public async Task<SendResult> SendAsync(ChannelAccountContext account, OutboundChannelMessage message, CancellationToken ct)
    {
        var to = new string(message.To.Where(char.IsDigit).ToArray());
        if (to.Length < 6) return SendResult.Fail("The recipient is not a valid WhatsApp number.", permanent: true);
        var payload = new JsonObject { ["messaging_product"] = "whatsapp", ["recipient_type"] = "individual", ["to"] = to };
        if (message.TemplateName is { Length: > 0 } template)
        {
            var parameters = new JsonArray((message.TemplateParameters ?? []).Select(p => (JsonNode)new JsonObject { ["type"] = "text", ["text"] = p }).ToArray());
            payload["type"] = "template";
            payload["template"] = new JsonObject
            {
                ["name"] = template, ["language"] = new JsonObject { ["code"] = message.TemplateLanguage ?? "en" },
                ["components"] = parameters.Count == 0 ? new JsonArray() : new JsonArray(new JsonObject { ["type"] = "body", ["parameters"] = parameters })
            };
        }
        else if (message.MediaUrl is { Length: > 0 } url)
        {
            var kind = (message.MediaType ?? "").StartsWith("image/") ? "image" : (message.MediaType ?? "").StartsWith("video/") ? "video"
                : (message.MediaType ?? "").StartsWith("audio/") ? "audio" : "document";
            payload["type"] = kind;
            var media = new JsonObject { ["link"] = url };
            if (kind is "image" or "video" or "document" && !string.IsNullOrWhiteSpace(message.Text)) media["caption"] = TextNormalization.Truncate(message.Text, 1024);
            if (kind == "document" && message.MediaFileName is { } fn) media["filename"] = fn;
            payload[kind] = media;
        }
        else
        {
            payload["type"] = "text";
            payload["text"] = new JsonObject { ["preview_url"] = true, ["body"] = TextNormalization.Truncate(message.Text ?? "", 4096) };
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base(account)}/{Uri.EscapeDataString(account.RequireSetting("phoneNumberId"))}/messages")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.RequireSecret("accessToken"));
        using var res = await http.SendAsync(req, ct);
        return await MetaWebhook.ReadSendResultAsync(res, r => r.TryGetProperty("messages", out var m) && m.GetArrayLength() > 0 ? m[0].GetProperty("id").GetString() : null, ct);
    }

    public string? VerifyHandshake(ChannelAccountContext account, WebhookRequest request) => MetaWebhook.Handshake(account, request);

    public WebhookParseResult ParseWebhook(ChannelAccountContext account, WebhookRequest request)
    {
        var root = MetaWebhook.VerifyAndParse(account, request);
        var events = new List<ChannelWebhookEvent>();
        var phoneNumberId = account.Setting("phoneNumberId");
        if (!root.TryGetProperty("entry", out var entries)) return new WebhookParseResult(events);
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes)) continue;
            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value)) continue;
                if (phoneNumberId is { Length: > 0 } && value.TryGetProperty("metadata", out var md) && md.TryGetProperty("phone_number_id", out var pid)
                    && pid.GetString() != phoneNumberId) continue; // another number of the same app
                var names = new Dictionary<string, string>();
                if (value.TryGetProperty("contacts", out var contacts))
                    foreach (var c in contacts.EnumerateArray())
                        if (c.TryGetProperty("wa_id", out var wa) && c.TryGetProperty("profile", out var p) && p.TryGetProperty("name", out var n))
                            names[wa.GetString()!] = n.GetString() ?? "";
                if (value.TryGetProperty("messages", out var messages))
                    foreach (var m in messages.EnumerateArray()) events.Add(ParseMessage(m, names, account.Account.Address));
                if (value.TryGetProperty("statuses", out var statuses))
                    foreach (var s in statuses.EnumerateArray())
                    {
                        var id = s.GetProperty("id").GetString()!;
                        var status = s.GetProperty("status").GetString() switch
                        {
                            "sent" => MessageStatus.Sent, "delivered" => MessageStatus.Delivered, "read" => MessageStatus.Read, "failed" => MessageStatus.Failed, _ => (MessageStatus?)null
                        };
                        if (status is null) continue;
                        string? error = null;
                        if (s.TryGetProperty("errors", out var errs) && errs.GetArrayLength() > 0)
                        {
                            var e = errs[0];
                            error = $"{(e.TryGetProperty("code", out var code) ? code.ToString() + " " : "")}{(e.TryGetProperty("title", out var t) ? t.GetString() : "")}" +
                                    (e.TryGetProperty("message", out var em) ? $": {em.GetString()}" : "");
                        }
                        events.Add(new ChannelWebhookEvent($"status:{id}:{status}", "status",
                            Status: new DeliveryStatusUpdate(id, status.Value, MetaWebhook.Unix(s, "timestamp"), error, s.TryGetProperty("recipient_id", out var r) ? r.GetString() : null)));
                    }
            }
        }
        return new WebhookParseResult(events);
    }

    private static ChannelWebhookEvent ParseMessage(JsonElement m, Dictionary<string, string> names, string to)
    {
        var id = m.GetProperty("id").GetString()!;
        var from = m.GetProperty("from").GetString()!;
        var type = m.TryGetProperty("type", out var t) ? t.GetString() ?? "text" : "text";
        string? text = null;
        var media = new List<InboundMedia>();
        switch (type)
        {
            case "text": text = m.GetProperty("text").GetProperty("body").GetString(); break;
            case "image" or "document" or "audio" or "video" or "sticker":
            {
                var part = m.GetProperty(type);
                var caption = part.TryGetProperty("caption", out var c) ? c.GetString() : null;
                text = caption;
                media.Add(new InboundMedia(part.TryGetProperty("mime_type", out var mt) ? mt.GetString() ?? "application/octet-stream" : "application/octet-stream",
                    part.TryGetProperty("filename", out var fn) ? fn.GetString() : null, ProviderMediaId: part.GetProperty("id").GetString(), Caption: caption));
                break;
            }
            case "location":
            {
                var l = m.GetProperty("location");
                text = $"📍 {(l.TryGetProperty("name", out var n) ? n.GetString() + " " : "")}{(l.TryGetProperty("address", out var a) ? a.GetString() + " " : "")}" +
                       $"https://maps.google.com/?q={l.GetProperty("latitude")},{l.GetProperty("longitude")}";
                break;
            }
            case "interactive":
            {
                var i = m.GetProperty("interactive");
                text = i.TryGetProperty("button_reply", out var b) ? b.GetProperty("title").GetString()
                    : i.TryGetProperty("list_reply", out var lr) ? lr.GetProperty("title").GetString() : "(interactive reply)";
                break;
            }
            case "button": text = m.GetProperty("button").GetProperty("text").GetString(); break;
            default: text = $"(unsupported {type} message)"; break;
        }
        var context = m.TryGetProperty("context", out var ctx) && ctx.TryGetProperty("id", out var cid) ? cid.GetString() : null;
        return new ChannelWebhookEvent($"msg:{id}", "message", new InboundChannelMessage(id, "+" + from, names.GetValueOrDefault(from), to, text, type, media,
            MetaWebhook.Unix(m, "timestamp"), JsonSerializer.Serialize(new { waType = type, replyTo = context })));
    }

    public async Task<byte[]?> DownloadMediaAsync(ChannelAccountContext account, InboundMedia media, CancellationToken ct)
    {
        if (media.ProviderMediaId is not { Length: > 0 } mediaId) return null;
        var token = account.RequireSecret("accessToken");
        using var meta = new HttpRequestMessage(HttpMethod.Get, $"{Base(account)}/{Uri.EscapeDataString(mediaId)}");
        meta.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var metaRes = await http.SendAsync(meta, ct);
        if (!metaRes.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await metaRes.Content.ReadAsStringAsync(ct));
        var url = doc.RootElement.GetProperty("url").GetString();
        if (url is null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        using var file = new HttpRequestMessage(HttpMethod.Get, url);
        file.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var fileRes = await http.SendAsync(file, ct);
        return fileRes.IsSuccessStatusCode ? await fileRes.Content.ReadAsByteArrayAsync(ct) : null;
    }

    public async Task<ConnectionTestResult> TestAsync(ChannelAccountContext account, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Base(account)}/{Uri.EscapeDataString(account.RequireSetting("phoneNumberId"))}?fields=display_phone_number,verified_name,quality_rating");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.RequireSecret("accessToken"));
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) return new ConnectionTestResult(false, $"Meta returned {(int)res.StatusCode}: {Email.OAuthTokenCache.Describe(body)}");
        using var doc = JsonDocument.Parse(body);
        var details = new Dictionary<string, string>();
        foreach (var p in new[] { "display_phone_number", "verified_name", "quality_rating" })
            if (doc.RootElement.TryGetProperty(p, out var v)) details[p] = v.ToString();
        if (account.Secret("appSecret") is null) details["warning"] = "Set the appSecret secret so webhook signatures can be verified.";
        return new ConnectionTestResult(true, "Connected to the WhatsApp Business Cloud API.", details);
    }
}

/// <summary>Facebook Messenger and Instagram messaging through the Graph Send API and page webhooks (PSID identities).</summary>
public sealed class MetaMessengerProvider(HttpClient http) : IMessagingProvider
{
    public string Provider => ChannelProviders.MetaGraph;
    public IReadOnlyCollection<ChannelType> ChannelTypes { get; } = [ChannelType.Facebook, ChannelType.Instagram];

    private static string Base(ChannelAccountContext a) => $"https://graph.facebook.com/{a.SettingOr("graphVersion", "v21.0")}";

    public async Task<SendResult> SendAsync(ChannelAccountContext account, OutboundChannelMessage message, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["recipient"] = new JsonObject { ["id"] = message.To }, ["messaging_type"] = "RESPONSE",
            ["message"] = message.MediaUrl is { Length: > 0 } url
                ? new JsonObject { ["attachment"] = new JsonObject { ["type"] = (message.MediaType ?? "").StartsWith("image/") ? "image" : "file", ["payload"] = new JsonObject { ["url"] = url } } }
                : new JsonObject { ["text"] = TextNormalization.Truncate(message.Text ?? "", 2000) }
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base(account)}/{Uri.EscapeDataString(account.RequireSetting("pageId"))}/messages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.RequireSecret("pageAccessToken"));
        using var res = await http.SendAsync(req, ct);
        return await MetaWebhook.ReadSendResultAsync(res, r => r.TryGetProperty("message_id", out var id) ? id.GetString() : null, ct);
    }

    public string? VerifyHandshake(ChannelAccountContext account, WebhookRequest request) => MetaWebhook.Handshake(account, request);

    public WebhookParseResult ParseWebhook(ChannelAccountContext account, WebhookRequest request)
    {
        var root = MetaWebhook.VerifyAndParse(account, request);
        var events = new List<ChannelWebhookEvent>();
        if (!root.TryGetProperty("entry", out var entries)) return new WebhookParseResult(events);
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("messaging", out var messaging)) continue;
            foreach (var item in messaging.EnumerateArray())
            {
                var sender = item.GetProperty("sender").GetProperty("id").GetString()!;
                if (item.TryGetProperty("message", out var msg))
                {
                    if (msg.TryGetProperty("is_echo", out var echo) && echo.ValueKind == JsonValueKind.True) continue; // our own outbound message
                    var mid = msg.GetProperty("mid").GetString()!;
                    var media = new List<InboundMedia>();
                    if (msg.TryGetProperty("attachments", out var atts))
                        foreach (var a in atts.EnumerateArray())
                        {
                            var type = a.TryGetProperty("type", out var t) ? t.GetString() : "file";
                            var url = a.TryGetProperty("payload", out var p) && p.TryGetProperty("url", out var u) ? u.GetString() : null;
                            if (url is not null) media.Add(new InboundMedia(type switch { "image" => "image/jpeg", "video" => "video/mp4", "audio" => "audio/mpeg", _ => "application/octet-stream" }, null, Url: url));
                        }
                    var text = msg.TryGetProperty("text", out var tx) ? tx.GetString() : null;
                    events.Add(new ChannelWebhookEvent($"msg:{mid}", "message", new InboundChannelMessage(mid, sender, null, account.Account.Address, text,
                        media.Count > 0 ? "attachment" : "text", media, MetaWebhook.Unix(item, "timestamp"))));
                }
                else if (item.TryGetProperty("delivery", out var delivery) && delivery.TryGetProperty("mids", out var mids))
                {
                    foreach (var mid in mids.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null))
                        events.Add(new ChannelWebhookEvent($"status:{mid}:Delivered", "status",
                            Status: new DeliveryStatusUpdate(mid!, MessageStatus.Delivered, MetaWebhook.Unix(item, "timestamp"), null, sender)));
                }
            }
        }
        return new WebhookParseResult(events);
    }

    public async Task<byte[]?> DownloadMediaAsync(ChannelAccountContext account, InboundMedia media, CancellationToken ct)
    {
        if (media.Url is not { } url || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        using var res = await http.GetAsync(url, ct);
        return res.IsSuccessStatusCode ? await res.Content.ReadAsByteArrayAsync(ct) : null;
    }

    public async Task<ConnectionTestResult> TestAsync(ChannelAccountContext account, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Base(account)}/{Uri.EscapeDataString(account.RequireSetting("pageId"))}?fields=name");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.RequireSecret("pageAccessToken"));
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) return new ConnectionTestResult(false, $"Meta returned {(int)res.StatusCode}: {Email.OAuthTokenCache.Describe(body)}");
        using var doc = JsonDocument.Parse(body);
        return new ConnectionTestResult(true, $"Connected to page '{doc.RootElement.GetProperty("name").GetString()}'.");
    }
}
