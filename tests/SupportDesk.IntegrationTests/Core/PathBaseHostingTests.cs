using System.Net;
using System.Net.Http.Json;
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
