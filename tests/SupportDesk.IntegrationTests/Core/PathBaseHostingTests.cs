using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc.Testing;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Contracts.Auth;
using SupportDesk.IntegrationTests.Infrastructure;

namespace SupportDesk.IntegrationTests.Core;

/// <summary>
/// Hosting under a path base — an IIS virtual directory (the module sets the path base) or a reverse proxy sub-path
/// (Hosting:PathBase): routing, the SPAs' base href, refresh-cookie paths and OAuth discovery follow the path base.
/// </summary>
[Collection("api")]
public sealed class PathBaseHostingTests(DeskApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions J = DeskApiFactory.Json;

    [Theory]
    [InlineData("", "/", "<base href=\"/\" />", "content=\"/\"")]
    [InlineData("/supportdesk", "/", "<base href=\"/supportdesk/\" />", "content=\"/supportdesk/\"")]
    [InlineData("/supportdesk", "/portal/", "<base href=\"/supportdesk/portal/\" />", "content=\"/supportdesk/\"")]
    public void Index_html_gets_the_path_base(string pathBase, string appSuffix, string expectedBase, string expectedRoot)
    {
        const string html = "<!doctype html><html><head>\n<meta charset=\"utf-8\">\n<base href=\"/\" />\n</head><body></body></html>";
        var rewritten = SpaIndexHtml.Rewrite(html, pathBase, appSuffix);
        Assert.Contains(expectedBase, rewritten);
        Assert.Contains($"<meta name=\"sd-app-root\" {expectedRoot} />", rewritten);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(rewritten, "<base "));
    }

    [Fact]
    public void Web_config_uses_only_sections_iis_leaves_unlocked_for_sites()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SupportDesk.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var webServer = XDocument.Load(Path.Combine(dir.FullName, "src", "SupportDesk.Api", "web.config"))
            .Descendants("system.webServer").Single();

        // Sections such as modules, webSocket, httpLogging and serverRuntime are locked (overrideModeDefault="Deny"): using
        // one in web.config fails every request with 500.19. handlers is unlocked by IIS's default <location path="">.
        string[] allowed = ["handlers", "aspNetCore", "security", "httpErrors", "httpProtocol", "staticContent", "defaultDocument", "httpRedirect", "urlCompression", "rewrite"];
        Assert.All(webServer.Elements(), e => Assert.Contains(e.Name.LocalName, allowed));
        Assert.All(webServer.Elements("security").Elements(), e => Assert.Contains(e.Name.LocalName, new[] { "requestFiltering", "authorization" }));
    }

    [Fact]
    public async Task The_app_works_under_a_configured_path_base()
    {
        var t = await factory.CreateTenantAsync();
        var (_, agentEmail) = await t.CreateAgentAsync("Path Base Agent");
        await using var host = factory.WithWebHostBuilder(b => b.UseSetting("Hosting:PathBase", "/supportdesk"));
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("http://localhost/supportdesk/") });

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("health/live")).StatusCode);

        // The refresh cookie is scoped under the path base, or the browser would never send it back.
        var login = await client.PostAsJsonAsync("api/v1/auth/login", new LoginRequest(t.Slug, agentEmail, DeskApiFactory.Password), J);
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith("sd_rt=", StringComparison.Ordinal));
        Assert.Contains("path=/supportdesk/api/v1/auth", cookie, StringComparison.OrdinalIgnoreCase);

        // OAuth discovery advertises endpoints under the path base.
        var discovery = await client.GetStringAsync(".well-known/openid-configuration");
        Assert.Contains("http://localhost/supportdesk/oauth/token", discovery);

        // The SPAs (when built into wwwroot) are served with the path base applied.
        var page = await client.GetStringAsync("tickets/1000");
        if (page.Contains("<html", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Contains("<base href=\"/supportdesk/\" />", page);
            Assert.Contains("<meta name=\"sd-app-root\" content=\"/supportdesk/\" />", page);
        }
    }
}
