using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SupportDesk.Domain;
using SupportDesk.Domain.Integration;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Automation;

/// <summary>Configuration section "Automation:Webhooks".</summary>
public sealed class WebhookTransportOptions
{
    /// <summary>Allow calls to loopback/private (RFC 1918) addresses. Off by default (SSRF protection); enable for on-premises ERP endpoints. Link-local/metadata addresses are always blocked.</summary>
    public bool AllowPrivateNetworks { get; set; }
    public int DefaultTimeoutSeconds { get; set; } = 10;
    public string UserAgent { get; set; } = "SupportDesk-Webhooks/1.0";
}

public sealed record OutboundHttpRequest(string Method, string Url, IReadOnlyDictionary<string, string> Headers, string? Body, TimeSpan Timeout,
    string LogSystem, string LogOperation, string? ContentType = "application/json");

public sealed record OutboundHttpResult(bool Success, int? StatusCode, string? ResponseExcerpt, int DurationMs, string? Error, bool Transient);

/// <summary>
/// Sends outbound webhook/API calls with an SSRF guard (blocked address ranges are checked at connect time, redirects
/// are not followed), a hard timeout, and a redacted <see cref="IntegrationLog"/> entry per call (added to the caller's unit of work).
/// </summary>
public sealed partial class OutboundHttpSender(IHttpClientFactory factory, DeskDbContext db, RequestContext ctx, ILogger<OutboundHttpSender> logger)
{
    public const string ClientName = "automation-webhooks";
    private const int ExcerptLength = 1000;

    [GeneratedRegex("(\"(?:password|passwd|secret|token|access_token|refresh_token|api[_-]?key|authorization|client_secret)\"\\s*:\\s*)\"[^\"]*\"", RegexOptions.IgnoreCase)]
    private static partial Regex SecretJsonRegex();

    public async Task<OutboundHttpResult> SendAsync(OutboundHttpRequest r, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int? status = null;
        string? excerpt = null, error = null;
        var transient = false;
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(r.Method), r.Url);
            if (r.Body is not null) request.Content = new StringContent(r.Body, Encoding.UTF8, r.ContentType ?? "application/json");
            foreach (var (name, value) in r.Headers)
            {
                if (name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                {
                    if (request.Content is not null) { request.Content.Headers.Remove(name); request.Content.Headers.TryAddWithoutValidation(name, value); }
                }
                else request.Headers.TryAddWithoutValidation(name, value);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(r.Timeout);
            var client = factory.CreateClient(ClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            status = (int)response.StatusCode;
            excerpt = await ReadExcerptAsync(response, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                error = $"HTTP {status} {response.ReasonPhrase}";
                transient = status is 408 or 425 or 429 or >= 500;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            error = $"Timed out after {r.Timeout.TotalSeconds:0} s";
            transient = true;
        }
        catch (HttpRequestException ex)
        {
            error = ex.InnerException is BlockedAddressException blocked ? blocked.Message : $"Connection failed: {ex.Message}";
            transient = ex.InnerException is not BlockedAddressException;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException or FormatException or ArgumentException)
        {
            error = $"Invalid request: {ex.Message}";
        }
        sw.Stop();
        var success = error is null;
        db.Set<IntegrationLog>().Add(new IntegrationLog
        {
            System = r.LogSystem, Direction = MessageDirection.Outbound, Operation = Truncate(r.LogOperation, 200) ?? "", Url = Truncate(ActionExecutor.RedactUrl(r.Url), 450),
            StatusCode = status, Success = success, DurationMs = (int)sw.ElapsedMilliseconds, RequestExcerpt = Redact(r.Body),
            ResponseExcerpt = Redact(error is null ? excerpt : $"{error}{(excerpt is null ? "" : " — " + excerpt)}"), CorrelationId = ctx.CorrelationId,
            CreatedAt = db.Clock.UtcNow
        });
        if (!success) logger.LogInformation("Outbound call {Method} {Url} failed: {Error}", r.Method, ActionExecutor.RedactUrl(r.Url), error);
        return new OutboundHttpResult(success, status, Truncate(excerpt, ExcerptLength), (int)sw.ElapsedMilliseconds, error, transient);
    }

    private static async Task<string?> ReadExcerptAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new char[ExcerptLength];
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
            return read == 0 ? null : new string(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return null;
        }
    }

    public static string? Redact(string? text) => text is null ? null : Truncate(SecretJsonRegex().Replace(text, "$1\"***\""), ExcerptLength);

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    /// <summary>"sha256=" + lowercase hex HMAC-SHA256 of the exact request body.</summary>
    public static string Signature(string secret, string body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    /// <summary>Primary handler for the webhook HttpClient: connect-time address checks, no redirects, no cookies.</summary>
    public static SocketsHttpHandler CreateHandler(WebhookTransportOptions options) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
            var allowed = addresses.Where(a => IsAllowed(a, options.AllowPrivateNetworks)).ToArray();
            if (allowed.Length == 0) throw new BlockedAddressException(host);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };

    public static bool IsAllowed(IPAddress address, bool allowPrivate)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 169 && b[1] == 254) return false; // link-local incl. cloud metadata 169.254.169.254
            if (b[0] == 0 || b[0] >= 224) return false;     // "this network", multicast, reserved
            if (allowPrivate) return true;
            return !(IPAddress.IsLoopback(address) || b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) ||
                     (b[0] == 100 && b[1] is >= 64 and <= 127));
        }
        if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return false;
        if (allowPrivate) return true;
        var bytes = address.GetAddressBytes();
        return !(IPAddress.IsLoopback(address) || (bytes[0] & 0xFE) == 0xFC);
    }
}

public sealed class BlockedAddressException(string host) : Exception($"Calls to '{host}' are not allowed (blocked network address).");
