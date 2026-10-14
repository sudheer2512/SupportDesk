using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Communication;
using SupportDesk.Domain.Communication;

namespace SupportDesk.Integrations.Messaging;

/// <summary>APNs provider-token settings (Push:Apns:*). The .p8 key comes inline (PrivateKey) or from a file (PrivateKeyFile).</summary>
public sealed record ApnsOptions(string KeyId, string TeamId, string BundleId, string PrivateKeyPem, bool Sandbox)
{
    public const string Section = "Push:Apns";

    public static ApnsOptions? From(IConfiguration config)
    {
        var s = config.GetSection(Section);
        var pem = s["PrivateKey"];
        if (string.IsNullOrWhiteSpace(pem) && s["PrivateKeyFile"] is { Length: > 0 } path && File.Exists(path)) pem = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(pem) || string.IsNullOrWhiteSpace(s["KeyId"]) || string.IsNullOrWhiteSpace(s["TeamId"]) ||
            string.IsNullOrWhiteSpace(s["BundleId"])) return null;
        var sandbox = string.Equals(s["Environment"], "sandbox", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(s["Environment"], "development", StringComparison.OrdinalIgnoreCase);
        return new ApnsOptions(s["KeyId"]!.Trim(), s["TeamId"]!.Trim(), s["BundleId"]!.Trim(), pem, sandbox);
    }

    public string Host => Sandbox ? "https://api.sandbox.push.apple.com" : "https://api.push.apple.com";
}

/// <summary>
/// Apple Push Notification service over HTTP/2 with token-based authentication: an ES256 JWT (kid = key id, iss = team id)
/// signed with the .p8 key, cached for 50 minutes (Apple accepts 20–60). No SDK — HttpClient only.
/// 410 Unregistered and 400 BadDeviceToken/DeviceTokenNotForTopic mean the token is dead (permanent failure).
/// </summary>
public sealed class ApnsPushSender(HttpClient http, IConfiguration config, TimeProvider? clock = null) : IPushSender
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(50);
    private static readonly object Gate = new();
    private static (string Key, string Jwt, DateTimeOffset IssuedAt)? _cached;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public bool IsConfigured => ApnsOptions.From(config) is not null;

    public async Task<SendResult> SendAsync(string deviceToken, string title, string body, IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        var o = ApnsOptions.From(config) ?? throw new ChannelConfigurationException("APNs is not configured.");
        if (deviceToken.Length is 0 or > 200 || !deviceToken.All(Uri.IsHexDigit))
            return SendResult.Dead("This is not an APNs device token.");

        var payload = new JsonObject
        {
            ["aps"] = new JsonObject
            {
                ["alert"] = new JsonObject { ["title"] = title, ["body"] = body },
                ["sound"] = "default",
                ["mutable-content"] = 1
            }
        };
        foreach (var (key, value) in data)
            if (key != "aps") payload[key] = value;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{o.Host}/3/device/{deviceToken}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("bearer", ProviderToken(o));
        req.Headers.TryAddWithoutValidation("apns-topic", o.BundleId);
        req.Headers.TryAddWithoutValidation("apns-push-type", "alert");
        req.Headers.TryAddWithoutValidation("apns-priority", "10");

        using var res = await http.SendAsync(req, ct);
        if (res.IsSuccessStatusCode)
            return SendResult.Ok(res.Headers.TryGetValues("apns-id", out var ids) ? ids.FirstOrDefault() : null);

        var reason = await ReasonAsync(res, ct);
        if (res.StatusCode == HttpStatusCode.Forbidden && reason is "ExpiredProviderToken" or "InvalidProviderToken")
            lock (Gate) _cached = null; // re-sign on the next attempt
        var message = $"APNs returned {(int)res.StatusCode}{(reason is null ? "" : $" ({reason})")}.";
        // 410 / BadDeviceToken / DeviceTokenNotForTopic: this token is dead. Other 400s are payload problems: permanent for this message only.
        if (res.StatusCode == HttpStatusCode.Gone || reason is "BadDeviceToken" or "DeviceTokenNotForTopic" or "Unregistered") return SendResult.Dead(message);
        return SendResult.Fail(message, permanent: res.StatusCode == HttpStatusCode.BadRequest || reason is "TopicDisallowed");
    }

    private static async Task<string?> ReasonAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text)) return null;
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The cached provider JWT for these credentials (re-signed after <see cref="TokenLifetime"/>).</summary>
    internal string ProviderToken(ApnsOptions o)
    {
        var now = _clock.GetUtcNow();
        var cacheKey = $"{o.TeamId}:{o.KeyId}";
        lock (Gate)
        {
            if (_cached is { } c && c.Key == cacheKey && now - c.IssuedAt < TokenLifetime) return c.Jwt;
            var jwt = CreateProviderToken(o, now);
            _cached = (cacheKey, jwt, now);
            return jwt;
        }
    }

    /// <summary>ES256 JWT: header {alg, kid}, claims {iss, iat}; the signature is the raw r‖s pair (IEEE P1363) as JWS requires.</summary>
    internal static string CreateProviderToken(ApnsOptions o, DateTimeOffset now)
    {
        var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = o.KeyId }));
        var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new { iss = o.TeamId, iat = now.ToUnixTimeSeconds() }));
        using var key = ECDsa.Create();
        key.ImportFromPem(o.PrivateKeyPem);
        var signature = key.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{header}.{claims}.{B64(signature)}";
    }

    internal static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Test hook: forget the cached provider token.</summary>
    internal static void ResetCache()
    {
        lock (Gate) _cached = null;
    }
}

/// <summary>
/// The <see cref="IPushSender"/> the application uses: routes each device to the provider that can reach it.
/// iOS → APNs when configured (tokens from @capacitor/push-notifications on iOS are APNs tokens), otherwise FCM (apps
/// that bundle the Firebase iOS SDK register FCM tokens); Android/Web → FCM. With no provider configured every push is
/// logged only, so development and tests never call the outside world.
/// </summary>
public sealed class PushRouter(FcmPushSender fcm, ApnsPushSender apns, LogOnlyPushSender log, ILogger<PushRouter> logger) : IPushSender
{
    public bool IsConfigured => fcm.IsConfigured || apns.IsConfigured;

    public Task<SendResult> SendAsync(string deviceToken, string title, string body, IReadOnlyDictionary<string, string> data, CancellationToken ct) =>
        Pick(null).SendAsync(deviceToken, title, body, data, ct);

    public Task<SendResult> SendAsync(DevicePlatform platform, string deviceToken, string title, string body, IReadOnlyDictionary<string, string> data,
        CancellationToken ct)
    {
        var sender = Pick(platform);
        logger.LogDebug("Push to {Platform} device via {Sender}", platform, sender.GetType().Name);
        return sender.SendAsync(deviceToken, title, body, data, ct);
    }

    internal IPushSender Pick(DevicePlatform? platform) => platform switch
    {
        DevicePlatform.iOS when apns.IsConfigured => apns,
        _ when fcm.IsConfigured => fcm,
        null when apns.IsConfigured => apns,
        _ => log
    };
}
