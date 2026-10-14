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

    [E2EFact]
    public Task Article_with_a_video_embed_and_inline_image_renders_under_the_portal_csp() => RunAsync(async () =>
    {
        // The SPA's CSP must allow what articles legitimately contain: YouTube/Vimeo embeds and data: images.
        var admin = await Api.AgentTokenAsync(Demo.Admin);
        var title = Marker("E2E video walkthrough");
        const string pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
        var created = await Api.PostOkAsync(admin, "/api/v1/kb/articles", new
        {
            title, articleType = "HowTo", visibility = "Public",
            bodyHtml = "<p>Watch the walkthrough.</p>" +
                       "<iframe src=\"https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ\" width=\"560\" height=\"315\" allowfullscreen></iframe>" +
                       $"<p><img src=\"data:image/png;base64,{pixel}\" alt=\"Inline pixel\"></p>"
        });
        var id = created["id"]!.GetValue<string>();
        var published = await Api.PostOkAsync(admin, $"/api/v1/kb/articles/{id}/transition", new { action = "publish" });
        Assert.Equal("Published", published["status"]!.GetValue<string>());

        var page = await NewPageAsync();
        await page.GotoAsync($"/portal/{Demo.Brand}/kb/{published["slug"]!.GetValue<string>()}");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = title, Level = 1 })).ToBeVisibleAsync();
        var frame = page.Locator("iframe[src^='https://www.youtube-nocookie.com/embed/']");
        await Expect(frame).ToHaveCountAsync(1);
        await Expect(frame).ToHaveAttributeAsync("sandbox", new Regex("allow-scripts"));
        await Expect(page.GetByRole(AriaRole.Img, new() { Name = "Inline pixel" })).ToBeVisibleAsync();
        await page.WaitForTimeoutAsync(1000); // let the frame request start; RunAsync then fails on any CSP violation
    });
}
