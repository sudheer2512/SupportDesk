using System.Net;
using SupportDesk.IntegrationTests.Infrastructure;

namespace SupportDesk.IntegrationTests.Security;

/// <summary>
/// Response security headers: nosniff, referrer policy, framing protection and CSP on the SPAs (tuned for the Angular
/// apps, SignalR and the KB's YouTube/Vimeo embeds), on API data and on downloads (sandboxed).
/// </summary>
[Collection("api")]
public sealed class SecurityHeaderTests(DeskApiFactory factory)
{
    private static string Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? string.Join(", ", v) :
        r.Content.Headers.TryGetValues(name, out var c) ? string.Join(", ", c) : "";

    private static Dictionary<string, string> Directives(string csp) =>
        csp.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Split(' ', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "", StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("/")]
    [InlineData("/tickets/1000")]
    [InlineData("/portal/acme/kb")]
    public async Task Spa_pages_get_a_strict_csp_that_still_allows_the_app(string path)
    {
        var r = await factory.CreateClient().GetAsync(path);
        Assert.Equal("nosniff", Header(r, "X-Content-Type-Options"));
        Assert.Equal("strict-origin-when-cross-origin", Header(r, "Referrer-Policy"));
        Assert.Equal("DENY", Header(r, "X-Frame-Options"));
        var csp = Directives(Header(r, "Content-Security-Policy"));
        Assert.Equal("'self'", csp["default-src"]);
        Assert.Equal("'self'", csp["script-src"]);                       // own bundles only: no inline or eval
        Assert.Contains("'self'", csp["style-src"]);
        Assert.Contains("data:", csp["img-src"]);                          // inline images in articles and replies
        Assert.Contains("ws:", csp["connect-src"]);                        // SignalR WebSockets
        Assert.Contains("wss:", csp["connect-src"]);
        Assert.Contains("https://www.youtube-nocookie.com", csp["frame-src"]);
        Assert.Contains("https://player.vimeo.com", csp["frame-src"]);
        Assert.Equal("'none'", csp["frame-ancestors"]);
        Assert.Equal("'none'", csp["object-src"]);
        Assert.Equal("'self'", csp["base-uri"]);
        Assert.Equal("'self'", csp["form-action"]);
    }

    [Fact]
    public async Task Api_responses_are_nosniff_unframeable_and_carry_a_deny_all_csp()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        foreach (var url in new[] { "/api/v1/me", "/api/v1/tickets?limit=1", "/api/v1/does-not-exist" })
        {
            var r = await admin.GetAsync(url);
            Assert.Equal("nosniff", Header(r, "X-Content-Type-Options"));
            Assert.Equal("strict-origin-when-cross-origin", Header(r, "Referrer-Policy"));
            Assert.Equal("DENY", Header(r, "X-Frame-Options"));
            var csp = Directives(Header(r, "Content-Security-Policy"));
            Assert.Equal("'none'", csp["default-src"]);
            Assert.Equal("'none'", csp["frame-ancestors"]);
        }
        // Errors too (problem details), including unauthenticated ones.
        var anonymous = await factory.CreateClient().GetAsync("/api/v1/tickets");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("nosniff", Header(anonymous, "X-Content-Type-Options"));
        Assert.Contains("frame-ancestors 'none'", Header(anonymous, "Content-Security-Policy"));
    }

    [Fact]
    public async Task Downloads_without_their_own_policy_are_still_covered()
    {
        // KB export is an attachment built by the knowledge module; it relies on the API-wide policy.
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var r = await admin.GetAsync("/api/v1/kb/export?format=json");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("attachment", r.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", Header(r, "X-Content-Type-Options"));
        Assert.Contains("default-src 'none'", Header(r, "Content-Security-Policy"));
    }

    [Fact]
    public async Task Hsts_is_sent_outside_development()
    {
        // The test host runs as "Testing" (not Development).
        var r = await factory.CreateClient().GetAsync("/health/live");
        Assert.Contains("max-age=", Header(r, "Strict-Transport-Security"));
    }
}
