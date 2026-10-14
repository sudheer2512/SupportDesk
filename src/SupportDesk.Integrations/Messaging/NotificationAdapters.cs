using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Communication;

namespace SupportDesk.Integrations.Messaging;

/// <summary>Microsoft Teams incoming webhook (classic connector or Workflows): posts an Adaptive Card with an "Open" action.</summary>
public sealed class TeamsWebhookNotifier(HttpClient http) : ITeamsNotifier
{
    public async Task<SendResult> SendAsync(string webhookUrl, string title, string text, string? link, CancellationToken ct)
    {
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            return SendResult.Fail("The Teams webhook URL must be an https URL.", permanent: true);
        var body = new JsonArray(
            new JsonObject { ["type"] = "TextBlock", ["text"] = title, ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true },
            new JsonObject { ["type"] = "TextBlock", ["text"] = text, ["wrap"] = true });
        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json", ["type"] = "AdaptiveCard", ["version"] = "1.4", ["body"] = body,
            ["actions"] = link is null ? new JsonArray() : new JsonArray(new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = "Open in SupportDesk", ["url"] = link })
        };
        var payload = new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray(new JsonObject { ["contentType"] = "application/vnd.microsoft.card.adaptive", ["content"] = card })
        };
        using var res = await http.PostAsync(uri, new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        if (res.IsSuccessStatusCode) return SendResult.Ok(null);
        return SendResult.Fail($"Teams returned {(int)res.StatusCode}.", res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
    }
}

/// <summary>Fallback push sender when FCM is not configured: logs the notification (no device delivery).</summary>
public sealed class LogOnlyPushSender(ILogger<LogOnlyPushSender> logger) : IPushSender
{
    public bool IsConfigured => false;

    public Task<SendResult> SendAsync(string deviceToken, string title, string body, IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        logger.LogInformation("Push (log-only, FCM not configured) to device …{Suffix}: {Title}", deviceToken.Length > 6 ? deviceToken[^6..] : deviceToken, title);
        return Task.FromResult(SendResult.Ok("log-only"));
    }
}

/// <summary>
/// Firebase Cloud Messaging HTTP v1. Authenticates with a service account (Push:Fcm:ServiceAccountJson or
/// Push:Fcm:ServiceAccountFile): an RS256-signed JWT is exchanged for an OAuth token (cached), then
/// projects/{id}/messages:send. Unregistered tokens are permanent failures.
/// </summary>
public sealed class FcmPushSender(HttpClient http, IConfiguration config) : IPushSender
{
    private static (string Token, DateTime ExpiresAt)? _token;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public bool IsConfigured => ServiceAccount() is not null;

    private JsonElement? ServiceAccount()
    {
        var json = config["Push:Fcm:ServiceAccountJson"];
        if (string.IsNullOrWhiteSpace(json) && config["Push:Fcm:ServiceAccountFile"] is { Length: > 0 } path && File.Exists(path)) json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public async Task<SendResult> SendAsync(string deviceToken, string title, string body, IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        var sa = ServiceAccount() ?? throw new ChannelConfigurationException("FCM is not configured.");
        var project = sa.GetProperty("project_id").GetString()!;
        var token = await AccessTokenAsync(sa, ct);
        var message = new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["token"] = deviceToken,
                ["notification"] = new JsonObject { ["title"] = title, ["body"] = body },
                ["data"] = new JsonObject(data.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
                ["android"] = new JsonObject { ["priority"] = "high" },
                ["apns"] = new JsonObject { ["payload"] = new JsonObject { ["aps"] = new JsonObject { ["sound"] = "default" } } }
            }
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(project)}/messages:send")
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (res.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(text);
            return SendResult.Ok(doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null);
        }
        var unregistered = text.Contains("UNREGISTERED", StringComparison.Ordinal) || res.StatusCode == HttpStatusCode.NotFound;
        return SendResult.Fail($"FCM returned {(int)res.StatusCode}{(unregistered ? " (device token is no longer registered)" : "")}.",
            unregistered || res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden);
    }

    private async Task<string> AccessTokenAsync(JsonElement sa, CancellationToken ct)
    {
        if (_token is { } t && t.ExpiresAt > DateTime.UtcNow.AddMinutes(2)) return t.Token;
        await Gate.WaitAsync(ct);
        try
        {
            if (_token is { } again && again.ExpiresAt > DateTime.UtcNow.AddMinutes(2)) return again.Token;
            var tokenUri = sa.TryGetProperty("token_uri", out var tu) ? tu.GetString()! : "https://oauth2.googleapis.com/token";
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
            var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = sa.GetProperty("client_email").GetString(), scope = "https://www.googleapis.com/auth/firebase.messaging", aud = tokenUri, iat = now, exp = now + 3600
            }));
            using var rsa = RSA.Create();
            rsa.ImportFromPem(sa.GetProperty("private_key").GetString());
            var signature = B64(rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            using var res = await http.PostAsync(tokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = $"{header}.{claims}.{signature}"
            }), ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw new ChannelConfigurationException($"FCM token request failed ({(int)res.StatusCode}).");
            using var doc = JsonDocument.Parse(body);
            var token = doc.RootElement.GetProperty("access_token").GetString()!;
            _token = (token, DateTime.UtcNow.AddSeconds(doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600));
            return token;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
