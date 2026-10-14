using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SupportDesk.E2ETests.Infrastructure;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace SupportDesk.E2ETests;

public sealed class KnowledgeBaseTests(DeskServerFixture server, ITestOutputHelper output) : E2ETestBase(server, output)
{
    private const string PublicTitle = "Wings POS store sync: troubleshooting a stuck sync queue";
    private const string InternalTitle = "Internal: SQL scripts to unblock the POS sync queue";
    private const string InternalSlug = "internal-sql-scripts-to-unblock-the-pos-sync-queue";

    [E2EFact]
    public Task Anonymous_portal_search_finds_published_articles_but_never_internal_ones() => RunAsync(async () =>
    {
        // Precondition from the seed: both articles exist and are published, one public, one internal.
        var agent = await Api.AgentTokenAsync(Demo.Admin);
        var articles = (await Api.GetOkAsync(agent, "/api/v1/kb/articles?limit=200")).ToJsonString();
        var all = JsonNode.Parse(articles)!["items"]!.AsArray();
        var internalArticle = all.Single(a => a!["title"]!.GetValue<string>() == InternalTitle)!;
        Assert.Equal("Internal", internalArticle["visibility"]!.GetValue<string>());
        Assert.Equal("Published", internalArticle["status"]!.GetValue<string>());

        var page = await NewPageAsync();
        await page.GotoAsync($"/portal/{Demo.Brand}");
        var search = page.GetByRole(AriaRole.Search).First.GetByRole(AriaRole.Combobox);
        await search.FillAsync("sync queue");
        await search.PressAsync("Enter");

        await Expect(page).ToHaveURLAsync(new Regex(@"/portal/wings/search\?q=sync(%20|\+)queue"));
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Search results" })).ToBeVisibleAsync();
        var hit = page.GetByRole(AriaRole.Link, new() { Name = PublicTitle });
        await Expect(hit).ToBeVisibleAsync();
        await Expect(page.GetByText(InternalTitle)).ToHaveCountAsync(0);

        // The public article opens anonymously…
        await hit.ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = PublicTitle, Level = 1 })).ToBeVisibleAsync();

        // …the internal one does not, even by its URL.
        await page.GotoAsync($"/portal/{Demo.Brand}/kb/{InternalSlug}");
        await Expect(page.GetByText("Article not found")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = InternalTitle })).ToHaveCountAsync(0);

        // Same answer from the public API (no token).
        using var anonymous = new HttpClient { BaseAddress = new Uri(Server.BaseUrl) };
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/portal/{Demo.Brand}/kb/articles/{InternalSlug}")).StatusCode);
        var results = await anonymous.GetStringAsync($"/api/v1/portal/{Demo.Brand}/search?q=sync%20queue");
        Assert.Contains(PublicTitle, results);
        Assert.DoesNotContain(InternalTitle, results);
    });
}
