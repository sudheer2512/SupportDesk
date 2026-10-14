using System.Text.RegularExpressions;
using SupportDesk.E2ETests.Infrastructure;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace SupportDesk.E2ETests;

public sealed class TicketWorkspaceTests(DeskServerFixture server, ITestOutputHelper output) : E2ETestBase(server, output)
{
    private const string Guid36 = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";

    [E2EFact]
    public Task Ticket_number_url_resolves_to_the_ticket() => RunAsync(async () =>
    {
        var token = await Api.AgentTokenAsync(Demo.Supervisor);
        var t1000 = await Api.GetOkAsync(token, "/api/v1/tickets/by-number/1000");

        var page = await NewPageAsync();
        await SignInToDeskAsync(page, Demo.Supervisor);
        await page.GotoAsync("/tickets/1000");

        // Replaced by the id route (no extra history entry) and the ticket opens.
        await Expect(page).ToHaveURLAsync(new Regex($"/tickets/{t1000["id"]!.GetValue<string>()}$"));
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Copy link to #1000" })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = t1000["subject"]!.GetValue<string>(), Level = 1 })).ToBeVisibleAsync();

        // "#1000" (shared as text) works too.
        await page.GotoAsync("/tickets/%231000");
        await Expect(page).ToHaveURLAsync(new Regex($"/tickets/{Guid36}$"));
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Copy link to #1000" })).ToBeVisibleAsync();
    });

    [E2EFact]
    public Task Second_agent_replying_to_the_same_ticket_gets_the_collision_review() => RunAsync(async () =>
    {
        var token = await Api.AgentTokenAsync(Demo.Supervisor);
        var ticketId = (await Api.GetOkAsync(token, "/api/v1/tickets/by-number/1000"))["id"]!.GetValue<string>();
        var first = Marker("First reply from Priya");
        var second = Marker("Second reply from Arjun");

        var priya = await NewPageAsync();
        await SignInToDeskAsync(priya, Demo.Supervisor);
        var arjun = await NewPageAsync();
        await SignInToDeskAsync(arjun, Demo.Agent);

        // Both open the ticket and start writing a reply.
        foreach (var (page, text) in new[] { (priya, first), (arjun, second) })
        {
            await page.GotoAsync($"/tickets/{ticketId}");
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Copy link to #1000" })).ToBeVisibleAsync();
            await ClickPastToastsAsync(page, page.GetByRole(AriaRole.Button, new() { Name = "Reply", Exact = true }));
            await page.GetByRole(AriaRole.Textbox, new() { Name = "Reply to customer" }).FillAsync(text);
        }

        // Priya sends first.
        await ClickPastToastsAsync(priya, priya.GetByRole(AriaRole.Button, new() { Name = "Send", Exact = true }));
        await Expect(priya.GetByText("Reply sent").First).ToBeVisibleAsync();

        // Arjun's send is held back: the composer shows what was added meanwhile.
        await ClickPastToastsAsync(arjun, arjun.GetByRole(AriaRole.Button, new() { Name = "Send", Exact = true }));
        var review = arjun.GetByRole(AriaRole.Alert).Filter(new() { HasText = "added while you were writing" });
        await Expect(review).ToBeVisibleAsync();
        await Expect(review).ToContainTextAsync(first);
        await Expect(arjun.GetByText("Reply sent")).ToHaveCountAsync(0);

        // After reviewing, Arjun can still send deliberately.
        await review.GetByRole(AriaRole.Button, new() { Name = "Send anyway" }).ClickAsync();
        await Expect(arjun.GetByText("Reply sent").First).ToBeVisibleAsync();
        var timeline = (await Api.GetOkAsync(token, $"/api/v1/tickets/{ticketId}/timeline")).ToJsonString();
        Assert.Contains(first, timeline);
        Assert.Contains(second, timeline);
    });
}
