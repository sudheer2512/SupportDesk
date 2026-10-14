using System.Text.RegularExpressions;
using SupportDesk.E2ETests.Infrastructure;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace SupportDesk.E2ETests;

public sealed class CustomerRoundTripTests(DeskServerFixture server, ITestOutputHelper output) : E2ETestBase(server, output)
{
    [E2EFact]
    public Task Portal_ticket_gets_a_public_reply_never_the_internal_note_and_is_confirmed_and_rated() => RunAsync(async () =>
    {
        var subject = Marker("E2E printer jams on invoice batch");
        var publicReply = Marker("Public answer: please update the printer driver");
        var internalNote = Marker("INTERNAL ONLY: customer is on an unsupported driver");

        // ── 1. The customer raises a ticket in the portal. ──
        var customer = await NewPageAsync();
        var customerApiBodies = new List<Task<string>>();
        customer.Response += (_, r) =>
        {
            if (r.Url.Contains("/api/") && (r.Headers.GetValueOrDefault("content-type") ?? "").Contains("json"))
                customerApiBodies.Add(r.TextAsync());
        };
        await SignInToPortalAsync(customer, Demo.Customer);
        await customer.GotoAsync($"/portal/{Demo.Brand}/submit");
        await Expect(customer.GetByRole(AriaRole.Heading, new() { Name = "Submit a ticket" })).ToBeVisibleAsync();
        await customer.GetByLabel("Subject").FillAsync(subject);
        await customer.GetByRole(AriaRole.Textbox, new() { Name = "Description" }).FillAsync("Every second invoice in a batch jams the thermal printer.");
        await customer.GetByRole(AriaRole.Button, new() { Name = "Submit ticket" }).ClickAsync();

        var done = customer.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex(@"^Ticket #\d+ submitted$") });
        await Expect(done).ToBeVisibleAsync();
        var number = Regex.Match(await done.InnerTextAsync(), @"#(\d+)").Groups[1].Value;
        await customer.GetByRole(AriaRole.Link, new() { Name = "View your ticket" }).ClickAsync();
        await Expect(customer).ToHaveURLAsync(new Regex(@"/portal/wings/tickets/[0-9a-f-]{36}$"));
        var portalTicketUrl = new Uri(customer.Url).AbsolutePath;
        var ticketId = portalTicketUrl[(portalTicketUrl.LastIndexOf('/') + 1)..];
        await Expect(customer.GetByRole(AriaRole.Heading, new() { Name = subject })).ToBeVisibleAsync();

        // ── 2. A supervisor finds it, replies publicly, adds an internal note and resolves it asking for confirmation. ──
        var agent = await NewPageAsync();
        await SignInToDeskAsync(agent, Demo.Supervisor);
        await agent.GotoAsync($"/tickets/{number}");
        await Expect(agent).ToHaveURLAsync(new Regex($"/tickets/{ticketId}"));
        await Expect(agent.GetByRole(AriaRole.Heading, new() { Name = subject })).ToBeVisibleAsync();

        await ClickPastToastsAsync(agent, agent.GetByRole(AriaRole.Button, new() { Name = "Reply", Exact = true }));
        await agent.GetByRole(AriaRole.Textbox, new() { Name = "Reply to customer" }).FillAsync(publicReply);
        await ClickPastToastsAsync(agent, agent.GetByRole(AriaRole.Button, new() { Name = "Send", Exact = true }));
        await Expect(agent.GetByText("Reply sent").First).ToBeVisibleAsync();

        await ClickPastToastsAsync(agent, agent.GetByRole(AriaRole.Button, new() { Name = "Internal note", Exact = true }));
        await agent.GetByRole(AriaRole.Textbox, new() { Name = "Internal note" }).FillAsync(internalNote);
        await ClickPastToastsAsync(agent, agent.GetByRole(AriaRole.Button, new() { Name = "Add note", Exact = true }));
        await Expect(agent.GetByText("Note added").First).ToBeVisibleAsync();
        await Expect(agent.GetByText(publicReply)).ToBeVisibleAsync();
        await Expect(agent.GetByText(internalNote)).ToBeVisibleAsync();

        await agent.GetByRole(AriaRole.Button, new() { Name = "More actions" }).ClickAsync();
        await agent.GetByRole(AriaRole.Menuitem, new() { Name = "Resolve…" }).ClickAsync();
        var dialog = agent.GetByRole(AriaRole.Dialog, new() { Name = "Resolve ticket" });
        await Expect(dialog).ToBeVisibleAsync();
        var code = dialog.GetByRole(AriaRole.Combobox).First;
        await code.ClickAsync();
        await agent.GetByRole(AriaRole.Option).First.ClickAsync();
        await dialog.GetByRole(AriaRole.Checkbox, new() { Name = "Ask the customer to confirm the fix" }).CheckAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Resolve", Exact = true }).ClickAsync();
        await Expect(agent.GetByText("waiting for the customer to confirm").First).ToBeVisibleAsync();

        // ── 3. The customer sees the public reply but never the internal note. ──
        await customer.ReloadAsync();
        var conversation = customer.GetByRole(AriaRole.Region, new() { Name = "Conversation" });
        await Expect(conversation.GetByText(publicReply)).ToBeVisibleAsync();
        await Expect(customer.GetByText(internalNote)).ToHaveCountAsync(0);
        Assert.DoesNotContain(internalNote, await customer.ContentAsync());

        // ── 4. They confirm the resolution and rate the support. ──
        var confirm = customer.GetByRole(AriaRole.Region, new() { Name = "Is your issue resolved?" });
        await Expect(confirm).ToBeVisibleAsync();
        await confirm.GetByRole(AriaRole.Button, new() { Name = "Yes, it's resolved" }).ClickAsync();
        await Expect(customer.GetByText("Thanks for confirming!").First).ToBeVisibleAsync();

        var rate = customer.GetByRole(AriaRole.Region, new() { Name = "How did we do?" });
        await Expect(rate).ToBeVisibleAsync();
        await rate.GetByRole(AriaRole.Radio, new() { Name = "5 stars" }).CheckAsync(new() { Force = true });
        await rate.GetByLabel("Comment").FillAsync("Quick and clear, thank you.");
        await rate.GetByRole(AriaRole.Button, new() { Name = "Submit rating" }).ClickAsync();
        var rated = customer.GetByRole(AriaRole.Region, new() { Name = "Your rating" });
        await Expect(rated).ToContainTextAsync("5 out of 5 stars");
        await Expect(rated).ToContainTextAsync("Thank you for your feedback.");

        // Nothing the portal received from the API ever carried the internal note.
        foreach (var body in customerApiBodies)
        {
            string text;
            try { text = await body; } catch (PlaywrightException) { continue; }
            Assert.DoesNotContain(internalNote, text);
        }
        // Cross-check through the API as the contact (ticket detail and its message list).
        var contact = await Api.ContactTokenAsync(Demo.Customer);
        var detail = await Api.GetOkAsync(contact, $"/api/v1/portal/{Demo.Brand}/tickets/{ticketId}");
        var messages = await Api.GetOkAsync(contact, $"/api/v1/portal/{Demo.Brand}/tickets/{ticketId}/messages");
        Assert.Contains(publicReply, messages.ToJsonString());
        Assert.DoesNotContain(internalNote, detail.ToJsonString());
        Assert.DoesNotContain(internalNote, messages.ToJsonString());
        Assert.Equal(5, detail["csatScore"]!.GetValue<int>());

        // And the desk shows the customer's confirmation and rating in the ticket history.
        var supervisor = await Api.AgentTokenAsync(Demo.Supervisor);
        var timeline = (await Api.GetOkAsync(supervisor, $"/api/v1/tickets/{ticketId}/timeline")).ToJsonString();
        Assert.Contains("confirmed the resolution", timeline);
        Assert.Contains("5/5", timeline);
    });
}
