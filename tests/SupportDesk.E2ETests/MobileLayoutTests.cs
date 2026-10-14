using SupportDesk.E2ETests.Infrastructure;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace SupportDesk.E2ETests;

public sealed class MobileLayoutTests(DeskServerFixture server, ITestOutputHelper output) : E2ETestBase(server, output)
{
    [E2EFact]
    public Task Desk_ticket_view_has_no_horizontal_page_scroll_on_a_phone() => RunAsync(async () =>
    {
        var page = await NewPageAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 375, Height = 812 },
            IsMobile = true,
            HasTouch = true,
            DeviceScaleFactor = 3
        });
        await SignInToDeskAsync(page, Demo.Agent);
        await page.GotoAsync("/tickets/1000");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Copy link to #1000" })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Reply", Exact = true })).ToBeVisibleAsync();
        await AssertNoHorizontalScrollAsync(page, "ticket view");

        // With the composer open as well.
        await ClickPastToastsAsync(page, page.GetByRole(AriaRole.Button, new() { Name = "Reply", Exact = true }));
        await Expect(page.GetByRole(AriaRole.Textbox, new() { Name = "Reply to customer" })).ToBeVisibleAsync();
        await AssertNoHorizontalScrollAsync(page, "ticket view with the composer open");
    });

    private static async Task AssertNoHorizontalScrollAsync(IPage page, string what)
    {
        await page.WaitForTimeoutAsync(500); // late layout (fonts, async panels)
        var m = await page.EvaluateAsync<Metrics>(
            """
            () => {
              const before = window.scrollX;
              window.scrollTo(200, window.scrollY);
              const pushed = window.scrollX;
              window.scrollTo(before, window.scrollY);
              return { viewport: window.innerWidth, doc: document.documentElement.scrollWidth, body: document.body.scrollWidth, scrolled: pushed };
            }
            """);
        Assert.True(m.Doc <= m.Viewport && m.Body <= m.Viewport && m.Scrolled == 0,
            $"{what}: page scrolls horizontally (viewport {m.Viewport}px, document {m.Doc}px, body {m.Body}px, scrollX after scrollTo {m.Scrolled}).");
    }

    private sealed class Metrics
    {
        public double Viewport { get; set; }
        public double Doc { get; set; }
        public double Body { get; set; }
        public double Scrolled { get; set; }
    }
}
