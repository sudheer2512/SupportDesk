using System.Net;
using SupportDesk.E2ETests.Infrastructure;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace SupportDesk.E2ETests;

/// <summary>Department scope and tenant isolation as an agent experiences them in the desk UI.</summary>
public sealed class AccessBoundaryTests(DeskServerFixture server, ITestOutputHelper output) : E2ETestBase(server, output)
{
    [E2EFact]
    public Task Agent_of_another_department_gets_not_found_for_an_out_of_scope_ticket() => RunAsync(async () =>
    {
        // #1000 belongs to Technical Support; Sana works in Billing.
        var admin = await Api.AgentTokenAsync(Demo.Admin);
        var ticket = await Api.GetOkAsync(admin, "/api/v1/tickets/by-number/1000");
        Assert.Equal("Technical Support", ticket["departmentName"]!.GetValue<string>());
        var id = ticket["id"]!.GetValue<string>();
        var sana = await Api.AgentTokenAsync(Demo.BillingAgent);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.GetAsync(sana, $"/api/v1/tickets/{id}")).Status);

        var page = await NewPageAsync();
        await SignInToDeskAsync(page, Demo.BillingAgent);

        await page.GotoAsync($"/tickets/{id}");
        await Expect(page.GetByText("Ticket not found")).ToBeVisibleAsync();
        await Expect(page.GetByText("outside what you can see")).ToBeVisibleAsync();
        await Expect(page.GetByText(ticket["subject"]!.GetValue<string>())).ToHaveCountAsync(0);

        // By number as well.
        await page.GotoAsync("/tickets/1000");
        await Expect(page.GetByText("Ticket not found")).ToBeVisibleAsync();
    });

    [E2EFact]
    public Task Other_tenant_admin_cannot_reach_wings_tickets_by_url_or_search() => RunAsync(async () =>
    {
        var wings = await Api.AgentTokenAsync(Demo.Admin);
        var wingsTicket = await Api.GetOkAsync(wings, "/api/v1/tickets/by-number/1000");
        var wingsId = wingsTicket["id"]!.GetValue<string>();
        var wingsSubject = wingsTicket["subject"]!.GetValue<string>(); // "Invoice calculation issue — GST total doesn't match"
        var northwind = await Api.AgentTokenAsync(Demo.OtherTenantAdmin, Demo.OtherTenant);
        var ownTicket = await Api.GetOkAsync(northwind, "/api/v1/tickets/by-number/1000"); // northwind has its own #1000
        Assert.NotEqual(wingsId, ownTicket["id"]!.GetValue<string>());
        var ownSubject = ownTicket["subject"]!.GetValue<string>();

        var page = await NewPageAsync();
        await SignInToDeskAsync(page, Demo.OtherTenantAdmin, Demo.OtherTenant);

        // By URL: the wings ticket id is simply not found.
        await page.GotoAsync($"/tickets/{wingsId}");
        await Expect(page.GetByText("Ticket not found")).ToBeVisibleAsync();
        await Expect(page.GetByText(wingsSubject)).ToHaveCountAsync(0);

        // By search (command palette): their own ticket is found, the wings one is not.
        await page.GotoAsync("/tickets");
        await SearchPaletteAsync(page, ownSubject[..12]);
        await Expect(page.GetByRole(AriaRole.Option).Filter(new() { HasText = ownSubject })).Not.ToHaveCountAsync(0);
        await page.Keyboard.PressAsync("Escape");

        var wingsQuery = "Invoice calculation";
        await SearchPaletteAsync(page, wingsQuery);
        await Expect(page.GetByRole(AriaRole.Option).Filter(new() { HasText = wingsSubject })).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Option).Filter(new() { HasText = "Invoice calculation issue" })).ToHaveCountAsync(0);

        // The API agrees (global search and ticket list search).
        var hits = await Api.GetOkAsync(northwind, $"/api/v1/search?q={Uri.EscapeDataString(wingsQuery)}");
        Assert.DoesNotContain(wingsId, hits.ToJsonString());
        var list = await Api.GetOkAsync(northwind, $"/api/v1/tickets?q={Uri.EscapeDataString(wingsQuery)}&limit=50");
        Assert.DoesNotContain(wingsId, list.ToJsonString());
    });

    /// <summary>Opens the command palette, types the query and waits for its ticket search (GET /api/v1/tickets?q=) to finish.</summary>
    private static async Task SearchPaletteAsync(IPage page, string query)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Search and commands" }).ClickAsync();
        var palette = page.GetByRole(AriaRole.Dialog, new() { Name = "Command palette" });
        await Expect(palette).ToBeVisibleAsync();
        var firstWord = Uri.EscapeDataString(query.Split(' ')[0]);
        var searched = page.WaitForResponseAsync(r => r.Url.Contains("/api/v1/tickets?") && r.Url.Contains("q=" + firstWord));
        await palette.GetByRole(AriaRole.Combobox).FillAsync(query);
        await searched;
        await page.WaitForTimeoutAsync(300); // let the results render
    }
}
