using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace SupportDesk.E2ETests.Infrastructure;

/// <summary>
/// Direct REST access to the running API for test set-up and for cross-checking what the UI shows (for example that an
/// out-of-scope ticket really is a 404, not just hidden by the UI). Tokens are cached per identity.
/// </summary>
public sealed class DeskApi(string baseUrl) : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(60) };
    private readonly ConcurrentDictionary<string, string> _tokens = new();

    /// <summary>Agent access token (completes the demo e-mail MFA with the code the demo environment returns).</summary>
    public async Task<string> AgentTokenAsync(string email, string tenant = Demo.Tenant, string password = Demo.DeskPassword)
    {
        var key = $"agent:{tenant}:{email}";
        if (_tokens.TryGetValue(key, out var cached)) return cached;
        var login = await PostAnonymousAsync("/api/v1/auth/login", new { tenant, email, password });
        if (login["status"]?.GetValue<string>() == "mfa_required")
            login = await PostAnonymousAsync("/api/v1/auth/mfa", new { challengeId = login["challengeId"]!.GetValue<string>(), code = login["demoCode"]!.GetValue<string>(), method = "email" });
        var token = login["accessToken"]?.GetValue<string>() ?? throw new InvalidOperationException($"Login failed for {email}: {login.ToJsonString()}");
        _tokens[key] = token;
        return token;
    }

    public async Task<string> ContactTokenAsync(string email, string brand = Demo.Brand, string password = Demo.PortalPassword)
    {
        var key = $"contact:{brand}:{email}";
        if (_tokens.TryGetValue(key, out var cached)) return cached;
        var login = await PostAnonymousAsync($"/api/v1/portal/{brand}/auth/login", new { email, password });
        var token = login["accessToken"]?.GetValue<string>() ?? throw new InvalidOperationException($"Portal login failed for {email}: {login.ToJsonString()}");
        _tokens[key] = token;
        return token;
    }

    public async Task<(HttpStatusCode Status, JsonNode? Body)> GetAsync(string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 && response.Content.Headers.ContentType?.MediaType?.Contains("json") == true ? JsonNode.Parse(text) : null);
    }

    public async Task<JsonNode> GetOkAsync(string token, string path)
    {
        var (status, body) = await GetAsync(token, path);
        if (status != HttpStatusCode.OK || body is null) throw new InvalidOperationException($"GET {path} returned {(int)status}.");
        return body;
    }

    public async Task<JsonNode> PostOkAsync(string token, string path, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"POST {path} returned {(int)response.StatusCode}: {text}");
        return text.Length > 0 ? JsonNode.Parse(text)! : new JsonObject();
    }

    private async Task<JsonNode> PostAnonymousAsync(string path, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload) };
        request.Headers.Add("X-Client", "mobile"); // tokens in the body, no cookies
        using var response = await _http.SendAsync(request);
        return await response.Content.ReadFromJsonAsync<JsonNode>() ?? new JsonObject();
    }

    public void Dispose() => _http.Dispose();
}
