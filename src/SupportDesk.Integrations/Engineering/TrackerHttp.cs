using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SupportDesk.Application.Integrations.Engineering;

namespace SupportDesk.Integrations.Engineering;

/// <summary>
/// Shared HTTP plumbing for tracker adapters: per-call timeout, redacted request/response excerpts in the integration
/// log, and mapping of HTTP failures to <see cref="TrackerException"/> (transient for 408/429/5xx and network errors).
/// </summary>
internal sealed partial class TrackerHttp(HttpClient http, TrackerConnection connection, IEngineeringCallLog log, string system)
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    public sealed record Response(HttpStatusCode Status, string Body, HttpResponseHeaders Headers)
    {
        public JsonElement Json()
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(Body) ? "{}" : Body);
            return doc.RootElement.Clone();
        }
    }

    public sealed record HttpResponseHeaders(string? Location);

    /// <summary>Sends a request; returns the response for 2xx and the statuses in <paramref name="accept"/>, throws <see cref="TrackerException"/> otherwise.</summary>
    public async Task<Response> SendAsync(HttpMethod method, string url, Action<HttpRequestMessage> configure, string operation, CancellationToken ct,
        string? requestExcerpt = null, params HttpStatusCode[] accept)
    {
        var sw = Stopwatch.StartNew();
        int? status = null;
        string? body = null;
        try
        {
            using var request = new HttpRequestMessage(method, url);
            configure(request);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CallTimeout);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(timeout.Token);
            var ok = response.IsSuccessStatusCode || accept.Contains(response.StatusCode);
            await LogAsync(operation, url, status, ok, sw, requestExcerpt, body);
            if (ok) return new Response(response.StatusCode, body, new HttpResponseHeaders(response.Headers.Location?.ToString()));
            throw Failure(response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await LogAsync(operation, url, status, false, sw, requestExcerpt, "timeout");
            throw new TrackerException($"{system} did not answer within {CallTimeout.TotalSeconds:0} s.", transient: true);
        }
        catch (HttpRequestException ex)
        {
            await LogAsync(operation, url, status, false, sw, requestExcerpt, ex.Message);
            var blocked = ex.InnerException is SupportDesk.Application.Automation.BlockedAddressException;
            throw new TrackerException(blocked ? ex.InnerException!.Message : $"Could not reach {system}: {ex.Message}", transient: !blocked);
        }
    }

    private TrackerException Failure(HttpStatusCode code, string body)
    {
        var detail = ErrorDetail(body);
        var n = (int)code;
        return code switch
        {
            HttpStatusCode.Unauthorized => new TrackerException($"{system} rejected the credentials (HTTP 401). Check the token and its scopes.", false, n),
            HttpStatusCode.Forbidden => new TrackerException($"{system} denied access (HTTP 403){Suffix(detail)}. Check the token's scopes/permissions.", false, n),
            HttpStatusCode.NotFound => new TrackerException($"{system} returned 404 (project, repository or item not found){Suffix(detail)}.", false, n),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => new TrackerException($"{system} rejected the request (HTTP {n}){Suffix(detail)}.", false, n),
            _ => new TrackerException($"{system} answered HTTP {n}{Suffix(detail)}.", n is 408 or 429 or >= 500, n)
        };
    }

    private static string Suffix(string? detail) => string.IsNullOrWhiteSpace(detail) ? "" : ": " + detail;

    private static string? ErrorDetail(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            return message is null ? null : message.Length > 300 ? message[..300] : message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task LogAsync(string operation, string url, int? status, bool success, Stopwatch sw, string? request, string? response) =>
        log.RecordAsync(connection.TenantId, connection.IntegrationId, system, operation, Redact(url), status, success, (int)sw.ElapsedMilliseconds,
            Redact(request), Redact(response is { Length: > 2000 } ? response[..2000] : response));

    [GeneratedRegex("(\"(?:token|password|secret|private_key|access_token|authorization)\"\\s*:\\s*)\"[^\"]*\"", RegexOptions.IgnoreCase)]
    private static partial Regex SecretJson();

    private static string? Redact(string? text) => text is null ? null : SecretJson().Replace(text, "$1\"***\"");

    public static string Basic(string user, string password) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
}
