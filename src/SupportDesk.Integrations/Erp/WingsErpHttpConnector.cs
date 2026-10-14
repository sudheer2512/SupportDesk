using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Integrations.Erp;
using SupportDesk.Contracts.Erp;
using SupportDesk.Domain;

namespace SupportDesk.Integrations.Erp;

/// <summary>Caches OAuth2 client-credentials access tokens per integration and credential (in memory, refreshed a minute early).</summary>
public sealed class ErpTokenCache
{
    private readonly ConcurrentDictionary<string, (string Token, DateTime ExpiresAt)> _tokens = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<string> GetAsync(string key, Func<Task<(string Token, int ExpiresIn)>> fetch, CancellationToken ct)
    {
        if (_tokens.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTime.UtcNow) return cached.Token;
        await _lock.WaitAsync(ct);
        try
        {
            if (_tokens.TryGetValue(key, out cached) && cached.ExpiresAt > DateTime.UtcNow) return cached.Token;
            var (token, expiresIn) = await fetch();
            _tokens[key] = (token, DateTime.UtcNow.AddSeconds(Math.Max(30, expiresIn - 60)));
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Invalidate(string key) => _tokens.TryRemove(key, out _);
}

/// <summary>
/// Wings ERP / Wings Books REST connector (contract in docs/integrations/WINGS-ERP.md). Authenticates with OAuth 2.0
/// client credentials or an API key, pages through list endpoints, applies the per-integration timeout on top of the
/// shared resilience pipeline (retries for safe methods, circuit breaker), and logs every call to the IntegrationLog
/// (URL without query secrets, status, duration; response excerpts only on failure).
/// </summary>
public sealed class WingsErpHttpConnector(HttpClient http, ErpConnection connection, ErpTokenCache tokens, IErpCallLog callLog,
    ILogger<WingsErpHttpConnector> logger) : IErpConnector
{
    public const string ClientName = "wings-erp";
    private const int PageSize = 200;
    private const int MaxPages = 200;

    public string System => connection.System;
    public string Mode => ErpModes.Http;

    private string BaseUrl => connection.BaseUrl!.TrimEnd('/');

    public async Task<ErpPingResult> PingAsync(CancellationToken ct) =>
        await SendAsync<ErpPingResult>(HttpMethod.Get, "/api/v1/ping", null, "ping", ct) ?? new ErpPingResult(false, System, null, DateTime.UtcNow);

    public Task<IReadOnlyList<ErpCustomerMaster>> GetCustomersAsync(DateTime? modifiedSince, CancellationToken ct) =>
        PagedAsync<ErpCustomerMaster>("/api/v1/customers", Query(("modifiedSince", Iso(modifiedSince))), "customers.list", ct);

    public async Task<ErpCustomerMaster?> GetCustomerAsync(Guid? id, string? code, CancellationToken ct)
    {
        if (id is { } g) return await SendAsync<ErpCustomerMaster>(HttpMethod.Get, $"/api/v1/customers/{g}", null, "customers.get", ct, allowNotFound: true);
        if (!string.IsNullOrWhiteSpace(code))
            return await SendAsync<ErpCustomerMaster>(HttpMethod.Get, $"/api/v1/customers/by-code/{Uri.EscapeDataString(code)}", null, "customers.get", ct, allowNotFound: true);
        return null;
    }

    public Task<IReadOnlyList<ErpInstallationRecord>> GetInstallationsAsync(Guid? customerId, DateTime? modifiedSince, CancellationToken ct) =>
        PagedAsync<ErpInstallationRecord>(customerId is { } c ? $"/api/v1/customers/{c}/installations" : "/api/v1/installations",
            Query(("modifiedSince", Iso(modifiedSince))), "installations.list", ct);

    public Task<IReadOnlyList<ErpLicenceRecord>> GetLicencesAsync(Guid? customerId, DateTime? modifiedSince, CancellationToken ct) =>
        PagedAsync<ErpLicenceRecord>(customerId is { } c ? $"/api/v1/customers/{c}/licences" : "/api/v1/licences",
            Query(("modifiedSince", Iso(modifiedSince))), "licences.list", ct);

    public Task<IReadOnlyList<ErpAmcContractRecord>> GetAmcContractsAsync(Guid? customerId, DateTime? modifiedSince, CancellationToken ct) =>
        PagedAsync<ErpAmcContractRecord>(customerId is { } c ? $"/api/v1/customers/{c}/amc-contracts" : "/api/v1/amc-contracts",
            Query(("modifiedSince", Iso(modifiedSince))), "amc.list", ct);

    public async Task<IReadOnlyList<ErpTransaction>> GetTransactionsAsync(Guid customerId, string? recordType, DateOnly? from, int take, CancellationToken ct)
    {
        var path = recordType switch
        {
            ErpRecordTypes.SalesInvoice => $"/api/v1/customers/{customerId}/invoices",
            ErpRecordTypes.SalesOrder => $"/api/v1/customers/{customerId}/orders",
            _ => $"/api/v1/customers/{customerId}/transactions"
        };
        var query = Query(("type", recordType is ErpRecordTypes.SalesInvoice or ErpRecordTypes.SalesOrder ? null : recordType),
            ("from", from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("top", Math.Clamp(take, 1, 500).ToString(CultureInfo.InvariantCulture)));
        var page = await SendAsync<ErpPage<ErpTransaction>>(HttpMethod.Get, path + query, null, "transactions.list", ct);
        return page?.Items ?? [];
    }

    public Task<ErpTransaction?> GetRecordAsync(string recordType, Guid id, CancellationToken ct) =>
        SendAsync<ErpTransaction>(HttpMethod.Get, $"/api/v1/records/{Uri.EscapeDataString(recordType)}/{id}", null, "records.get", ct, allowNotFound: true);

    public async Task<ErpUpdateResult> UpdateRecordAsync(string recordType, Guid? id, string? number, ErpRecordUpdate update, CancellationToken ct)
    {
        var path = id is { } g
            ? $"/api/v1/records/{Uri.EscapeDataString(recordType)}/{g}"
            : $"/api/v1/records/{Uri.EscapeDataString(recordType)}/by-number/{Uri.EscapeDataString(number ?? "")}";
        return await SendAsync<ErpUpdateResult>(HttpMethod.Patch, path, update, "records.update", ct)
               ?? new ErpUpdateResult(false, recordType, id, number, "The ERP returned no result.", []);
    }

    // ───────────────────────────── Plumbing ─────────────────────────────

    private async Task<IReadOnlyList<T>> PagedAsync<T>(string path, string query, string operation, CancellationToken ct)
    {
        var all = new List<T>();
        int? page = 1;
        for (var i = 0; page is { } p && i < MaxPages; i++)
        {
            var separator = query.Length == 0 ? "?" : "&";
            var result = await SendAsync<ErpPage<T>>(HttpMethod.Get, $"{path}{query}{separator}page={p}&pageSize={PageSize}", null, operation, ct);
            if (result is null) break;
            all.AddRange(result.Items);
            page = result.NextPage;
        }
        return all;
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string pathAndQuery, object? body, string operation, CancellationToken ct, bool allowNotFound = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(connection.Settings.TimeoutSeconds, 5, 300)));
        var url = BaseUrl + pathAndQuery;
        var sw = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        string? failure = null;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var request = new HttpRequestMessage(method, url);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Add("X-Correlation-Id", Guid.NewGuid().ToString("N"));
                await AuthenticateAsync(request, timeout.Token);
                if (body is not null) request.Content = JsonContent.Create(body, options: Json.Options);
                response?.Dispose();
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                // An expired/revoked token: drop it and retry once with a fresh one.
                if (response.StatusCode == HttpStatusCode.Unauthorized && connection.Settings.AuthType == "OAuth2" && attempt == 0)
                {
                    tokens.Invalidate(TokenKey);
                    continue;
                }
                break;
            }
            if (allowNotFound && response!.StatusCode == HttpStatusCode.NotFound) return default;
            if (!response!.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(timeout.Token);
                failure = Excerpt(text);
                throw new HttpRequestException($"{System} returned {(int)response.StatusCode} {response.ReasonPhrase} for {operation}: {ProblemTitle(text)}", null, response.StatusCode);
            }
            if (response.StatusCode == HttpStatusCode.NoContent) return default;
            return await response.Content.ReadFromJsonAsync<T>(Json.Options, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            failure ??= ex.Message;
            if (ex is TaskCanceledException && !ct.IsCancellationRequested)
                throw new HttpRequestException($"{System} did not answer within {connection.Settings.TimeoutSeconds} s ({operation}).", ex);
            throw;
        }
        finally
        {
            var status = response is null ? (int?)null : (int)response.StatusCode;
            response?.Dispose();
            try
            {
                await callLog.RecordAsync(new ErpCallRecord(connection.TenantId, connection.IntegrationId, System, MessageDirection.Outbound, operation,
                    method.Method + " " + StripQuery(url), status, failure is null, (int)sw.ElapsedMilliseconds, null, failure, null), CancellationToken.None);
            }
            catch (Exception logEx) when (logEx is not OperationCanceledException)
            {
                logger.LogWarning(logEx, "Could not write the ERP integration log");
            }
        }
    }

    private string TokenKey => $"{connection.IntegrationId:N}:{connection.Settings.ClientId}:{Hash(connection.Secrets.ClientSecret)}";

    private async Task AuthenticateAsync(HttpRequestMessage request, CancellationToken ct)
    {
        switch (connection.Settings.AuthType)
        {
            case "ApiKey":
                if (string.IsNullOrEmpty(connection.Secrets.ApiKey)) throw new DomainException("integration_unavailable", $"{System} API key is not configured.");
                request.Headers.Add("X-Api-Key", connection.Secrets.ApiKey);
                break;
            case "OAuth2":
            {
                var token = await tokens.GetAsync(TokenKey, () => FetchTokenAsync(ct), ct);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                break;
            }
        }
    }

    private async Task<(string Token, int ExpiresIn)> FetchTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(connection.Settings.ClientId) || string.IsNullOrEmpty(connection.Secrets.ClientSecret))
            throw new DomainException("integration_unavailable", $"{System} OAuth client id/secret are not configured.");
        var tokenUrl = connection.Settings.TokenUrl ?? $"{BaseUrl}/oauth/token";
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = connection.Settings.ClientId, ["client_secret"] = connection.Secrets.ClientSecret
        };
        if (!string.IsNullOrWhiteSpace(connection.Settings.Scope)) form["scope"] = connection.Settings.Scope;
        var sw = Stopwatch.StartNew();
        using var response = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(form), ct);
        var ok = response.IsSuccessStatusCode;
        await callLog.RecordAsync(new ErpCallRecord(connection.TenantId, connection.IntegrationId, System, MessageDirection.Outbound, "oauth.token",
            "POST " + StripQuery(tokenUrl), (int)response.StatusCode, ok, (int)sw.ElapsedMilliseconds, null, ok ? null : $"HTTP {(int)response.StatusCode}", null), ct);
        if (!ok) throw new HttpRequestException($"{System} rejected the client credentials ({(int)response.StatusCode}).", null, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var token = doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        var expires = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : 3600;
        return (token ?? throw new HttpRequestException($"{System} token response has no access_token."), expires);
    }

    private static string Query(params (string Key, string? Value)[] pairs)
    {
        var parts = pairs.Where(p => !string.IsNullOrEmpty(p.Value)).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}").ToList();
        return parts.Count == 0 ? "" : "?" + string.Join('&', parts);
    }

    private static string? Iso(DateTime? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string StripQuery(string url) => url.Split('?')[0];

    private static string Excerpt(string text) => text.Length <= 1000 ? text : text[..1000] + "…";

    private static string ProblemTitle(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var key in new[] { "title", "message", "error_description", "error" })
                    if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString()!;
        }
        catch (JsonException) { }
        return Excerpt(text);
    }

    private static string Hash(string? secret) =>
        secret is null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))[..12];
}
