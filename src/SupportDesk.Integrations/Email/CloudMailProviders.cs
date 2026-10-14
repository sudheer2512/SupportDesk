using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SupportDesk.Application.Communication;
using SupportDesk.Domain.Rules;

namespace SupportDesk.Integrations.Email;

/// <summary>Short-lived OAuth access tokens cached per credential (keyed by a hash — secrets are never used as plain keys).</summary>
internal static class OAuthTokenCache
{
    private static readonly ConcurrentDictionary<string, (string Token, DateTime ExpiresAt)> Tokens = new();

    public static async Task<string> GetAsync(HttpClient http, string tokenUrl, IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        var key = TextNormalization.Sha256Hex(tokenUrl + "|" + string.Join("&", form.OrderBy(k => k.Key).Select(kv => $"{kv.Key}={kv.Value}")));
        if (Tokens.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTime.UtcNow.AddMinutes(2)) return cached.Token;
        using var response = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(form), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var permanent = response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            var message = $"Token request failed ({(int)response.StatusCode}): {Describe(body)}";
            if (permanent) throw new ChannelConfigurationException(message);
            throw new HttpRequestException(message);
        }
        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("access_token").GetString()!;
        var expires = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
        Tokens[key] = (token, DateTime.UtcNow.AddSeconds(expires));
        return token;
    }

    public static void Invalidate() => Tokens.Clear();

    /// <summary>Error description without echoing tokens (only the error/error_description fields).</summary>
    public static string Describe(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            if (r.TryGetProperty("error_description", out var d)) return TextNormalization.Truncate(d.GetString(), 300);
            if (r.TryGetProperty("error", out var err))
                return err.ValueKind == JsonValueKind.Object
                    ? TextNormalization.Truncate(err.TryGetProperty("message", out var m) ? m.GetString() : err.ToString(), 300)
                    : TextNormalization.Truncate(err.GetString(), 300);
        }
        catch (JsonException) { }
        return TextNormalization.Truncate(body, 200);
    }
}

/// <summary>
/// Microsoft 365 mailbox through Microsoft Graph: app-only (client credentials) token, incremental inbox delta query
/// (cursor = deltaLink/nextLink; the first sync starts at now − initialSyncHours), raw MIME download ($value) and raw MIME
/// sendMail (keeps Message-ID / In-Reply-To / References intact).
/// </summary>
public sealed class GraphEmailProvider(HttpClient http, IMimeCodec mime) : IEmailProvider
{
    private const string Graph = "https://graph.microsoft.com/v1.0";
    public string Provider => ChannelProviders.Microsoft365;
    public bool SupportsPolling => true;

    private async Task<string> TokenAsync(ChannelAccountContext a, CancellationToken ct) =>
        await OAuthTokenCache.GetAsync(http, $"https://login.microsoftonline.com/{Uri.EscapeDataString(a.RequireSetting("tenantId"))}/oauth2/v2.0/token",
            new Dictionary<string, string>
            {
                ["client_id"] = a.RequireSetting("clientId"), ["client_secret"] = a.RequireSecret("clientSecret"),
                ["scope"] = "https://graph.microsoft.com/.default", ["grant_type"] = "client_credentials"
            }, ct);

    private static string Mailbox(ChannelAccountContext a) => Uri.EscapeDataString(a.SettingOr("mailbox", a.Account.Address));

    public async Task<EmailFetchResult> FetchAsync(ChannelAccountContext account, string? cursor, int max, CancellationToken ct)
    {
        var token = await TokenAsync(account, ct);
        var url = cursor is { Length: > 0 } && cursor.StartsWith("https://graph.microsoft.com/", StringComparison.OrdinalIgnoreCase)
            ? cursor
            : $"{Graph}/users/{Mailbox(account)}/mailFolders('inbox')/messages/delta?$select=id,internetMessageId,receivedDateTime" +
              $"&$filter=receivedDateTime+ge+{DateTime.UtcNow.AddHours(-account.Int("initialSyncHours", 24)):yyyy-MM-ddTHH:mm:ssZ}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("Prefer", $"odata.maxpagesize={Math.Clamp(max, 1, 50)}");
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == HttpStatusCode.Gone) return new EmailFetchResult([], null); // delta token expired → resync next poll
        if (!res.IsSuccessStatusCode) throw Error(res.StatusCode, body);
        using var doc = JsonDocument.Parse(body);
        var items = new List<FetchedEmail>();
        foreach (var m in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            if (m.TryGetProperty("@removed", out _)) continue;
            var id = m.GetProperty("id").GetString()!;
            using var raw = new HttpRequestMessage(HttpMethod.Get, $"{Graph}/users/{Mailbox(account)}/messages/{Uri.EscapeDataString(id)}/$value");
            raw.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var rawRes = await http.SendAsync(raw, ct);
            if (rawRes.StatusCode == HttpStatusCode.NotFound) continue;
            if (!rawRes.IsSuccessStatusCode) throw Error(rawRes.StatusCode, await rawRes.Content.ReadAsStringAsync(ct));
            items.Add(new FetchedEmail(id, mime.Parse(await rawRes.Content.ReadAsByteArrayAsync(ct))));
        }
        var next = doc.RootElement.TryGetProperty("@odata.nextLink", out var n) ? n.GetString() : null;
        var delta = doc.RootElement.TryGetProperty("@odata.deltaLink", out var d) ? d.GetString() : null;
        return new EmailFetchResult(items, next ?? delta ?? cursor, next is not null);
    }

    public async Task AcknowledgeAsync(ChannelAccountContext account, IReadOnlyList<string> providerIds, CancellationToken ct)
    {
        if (!account.Bool("markRead", true) || providerIds.Count == 0) return;
        var token = await TokenAsync(account, ct);
        foreach (var id in providerIds)
        {
            using var req = new HttpRequestMessage(HttpMethod.Patch, $"{Graph}/users/{Mailbox(account)}/messages/{Uri.EscapeDataString(id)}")
            {
                Content = new StringContent("{\"isRead\":true}", Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var _ = await http.SendAsync(req, ct);
        }
    }

    public async Task<SendResult> SendAsync(ChannelAccountContext account, OutboundEmail email, CancellationToken ct)
    {
        byte[] raw;
        try { raw = mime.Compose(email); }
        catch (MimeKit.ParseException ex) { return SendResult.Fail($"Invalid recipient address: {ex.Message}", permanent: true); }
        var token = await TokenAsync(account, ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Graph}/users/{Mailbox(account)}/sendMail")
        {
            Content = new StringContent(Convert.ToBase64String(raw), Encoding.ASCII, "text/plain")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req, ct);
        if (res.IsSuccessStatusCode) return SendResult.Ok(email.MessageId);
        var body = await res.Content.ReadAsStringAsync(ct);
        return SendResult.Fail($"Graph sendMail failed ({(int)res.StatusCode}): {OAuthTokenCache.Describe(body)}", Permanent(res.StatusCode));
    }

    public async Task<ConnectionTestResult> TestAsync(ChannelAccountContext account, CancellationToken ct)
    {
        var token = await TokenAsync(account, ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Graph}/users/{Mailbox(account)}/mailFolders/inbox?$select=displayName,totalItemCount,unreadItemCount");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) return new ConnectionTestResult(false, $"Microsoft Graph returned {(int)res.StatusCode}: {OAuthTokenCache.Describe(body)}");
        using var doc = JsonDocument.Parse(body);
        return new ConnectionTestResult(true, "Connected to Microsoft 365.", new Dictionary<string, string>
        {
            ["inbox"] = $"{doc.RootElement.GetProperty("totalItemCount").GetInt32()} message(s), {doc.RootElement.GetProperty("unreadItemCount").GetInt32()} unread"
        });
    }

    private static bool Permanent(HttpStatusCode code) => code is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound;

    private static Exception Error(HttpStatusCode code, string body) => Permanent(code)
        ? new ChannelConfigurationException($"Microsoft Graph returned {(int)code}: {OAuthTokenCache.Describe(body)}")
        : new HttpRequestException($"Microsoft Graph returned {(int)code}: {OAuthTokenCache.Describe(body)}");
}

/// <summary>
/// Gmail through the Gmail API: refresh-token OAuth, history-id cursor (the first poll imports inbox mail from the last
/// day and remembers the profile's historyId), raw MIME download and messages.send with the raw MIME.
/// </summary>
public sealed class GmailEmailProvider(HttpClient http, IMimeCodec mime) : IEmailProvider
{
    private const string Api = "https://gmail.googleapis.com/gmail/v1/users";
    public string Provider => ChannelProviders.Gmail;
    public bool SupportsPolling => true;

    private async Task<string> TokenAsync(ChannelAccountContext a, CancellationToken ct) =>
        await OAuthTokenCache.GetAsync(http, "https://oauth2.googleapis.com/token", new Dictionary<string, string>
        {
            ["client_id"] = a.RequireSetting("clientId"), ["client_secret"] = a.RequireSecret("clientSecret"),
            ["refresh_token"] = a.RequireSecret("refreshToken"), ["grant_type"] = "refresh_token"
        }, ct);

    private static string User(ChannelAccountContext a) => Uri.EscapeDataString(a.SettingOr("userId", "me"));

    public async Task<EmailFetchResult> FetchAsync(ChannelAccountContext account, string? cursor, int max, CancellationToken ct)
    {
        var token = await TokenAsync(account, ct);
        var ids = new List<string>();
        string? nextCursor;
        if (cursor is { Length: > 0 })
        {
            var (status, history) = await GetAsync(token, $"{Api}/{User(account)}/history?startHistoryId={Uri.EscapeDataString(cursor)}&historyTypes=messageAdded&labelId=INBOX&maxResults=100", ct);
            if (status == HttpStatusCode.NotFound) return new EmailFetchResult([], null); // history expired → resync
            using var doc = JsonDocument.Parse(history!);
            if (doc.RootElement.TryGetProperty("history", out var h))
                foreach (var entry in h.EnumerateArray())
                    if (entry.TryGetProperty("messagesAdded", out var added))
                        foreach (var a in added.EnumerateArray())
                        {
                            var msg = a.GetProperty("message");
                            var labels = msg.TryGetProperty("labelIds", out var l) ? l.EnumerateArray().Select(x => x.GetString()).ToList() : [];
                            if (labels.Contains("INBOX") && !labels.Contains("SENT") && !labels.Contains("DRAFT")) ids.Add(msg.GetProperty("id").GetString()!);
                        }
            nextCursor = doc.RootElement.TryGetProperty("historyId", out var hid) ? hid.GetString() ?? hid.ToString() : cursor;
        }
        else
        {
            var (_, profile) = await GetAsync(token, $"{Api}/{User(account)}/profile", ct);
            using var p = JsonDocument.Parse(profile!);
            nextCursor = p.RootElement.GetProperty("historyId").ToString().Trim('"');
            var (_, list) = await GetAsync(token, $"{Api}/{User(account)}/messages?q={Uri.EscapeDataString("in:inbox newer_than:1d")}&maxResults={Math.Clamp(max, 1, 100)}", ct);
            using var l = JsonDocument.Parse(list!);
            if (l.RootElement.TryGetProperty("messages", out var msgs)) ids.AddRange(msgs.EnumerateArray().Select(m => m.GetProperty("id").GetString()!));
            ids.Reverse();
        }
        var items = new List<FetchedEmail>();
        foreach (var id in ids.Distinct().Take(Math.Max(max, 100)))
        {
            var (status, body) = await GetAsync(token, $"{Api}/{User(account)}/messages/{Uri.EscapeDataString(id)}?format=raw", ct);
            if (status == HttpStatusCode.NotFound) continue;
            using var doc = JsonDocument.Parse(body!);
            var raw = Base64Url(doc.RootElement.GetProperty("raw").GetString()!);
            items.Add(new FetchedEmail(id, mime.Parse(raw)));
        }
        return new EmailFetchResult(items, nextCursor);
    }

    public async Task AcknowledgeAsync(ChannelAccountContext account, IReadOnlyList<string> providerIds, CancellationToken ct)
    {
        if (!account.Bool("markRead", true) || providerIds.Count == 0) return;
        var token = await TokenAsync(account, ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Api}/{User(account)}/messages/batchModify")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { ids = providerIds, removeLabelIds = new[] { "UNREAD" } }), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var _ = await http.SendAsync(req, ct);
    }

    public async Task<SendResult> SendAsync(ChannelAccountContext account, OutboundEmail email, CancellationToken ct)
    {
        byte[] raw;
        try { raw = mime.Compose(email); }
        catch (MimeKit.ParseException ex) { return SendResult.Fail($"Invalid recipient address: {ex.Message}", permanent: true); }
        var token = await TokenAsync(account, ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Api}/{User(account)}/messages/send")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { raw = Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_') }), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            return SendResult.Fail($"Gmail send failed ({(int)res.StatusCode}): {OAuthTokenCache.Describe(body)}",
                res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        using var doc = JsonDocument.Parse(body);
        return SendResult.Ok(doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : email.MessageId);
    }

    public async Task<ConnectionTestResult> TestAsync(ChannelAccountContext account, CancellationToken ct)
    {
        var token = await TokenAsync(account, ct);
        var (status, body) = await GetAsync(token, $"{Api}/{User(account)}/profile", ct, throwOnError: false);
        if (status != HttpStatusCode.OK) return new ConnectionTestResult(false, $"Gmail returned {(int)status}: {OAuthTokenCache.Describe(body ?? "")}");
        using var doc = JsonDocument.Parse(body!);
        return new ConnectionTestResult(true, "Connected to Gmail.", new Dictionary<string, string>
        {
            ["emailAddress"] = doc.RootElement.GetProperty("emailAddress").GetString() ?? "", ["messagesTotal"] = doc.RootElement.GetProperty("messagesTotal").ToString()
        });
    }

    private async Task<(HttpStatusCode Status, string? Body)> GetAsync(string token, string url, CancellationToken ct, bool throwOnError = true)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.IsSuccessStatusCode || res.StatusCode == HttpStatusCode.NotFound || !throwOnError) return (res.StatusCode, body);
        var msg = $"Gmail returned {(int)res.StatusCode}: {OAuthTokenCache.Describe(body)}";
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest) throw new ChannelConfigurationException(msg);
        throw new HttpRequestException(msg);
    }

    private static byte[] Base64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }
}
