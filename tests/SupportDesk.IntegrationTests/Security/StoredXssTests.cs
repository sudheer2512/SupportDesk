using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Contracts.Knowledge;
using SupportDesk.Contracts.Portal;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain.Customers;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.IntegrationTests.Portal;
using SupportDesk.Persistence;
using static SupportDesk.IntegrationTests.Security.SecurityTestKit;

namespace SupportDesk.IntegrationTests.Security;

/// <summary>
/// Stored XSS: hostile HTML submitted through every rich-text entry point (agent replies, notes, ticket descriptions,
/// resolutions, signatures, KB articles, portal tickets/replies and the anonymous web form) is sanitised before storage,
/// so what the API hands back to the desk, the portal and other customers is inert.
/// </summary>
[Collection("api")]
public sealed class StoredXssTests(DeskApiFactory factory)
{
    [Fact]
    public async Task Agent_rich_text_is_sanitised_in_descriptions_replies_notes_resolutions_and_signatures()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var ticket = await admin.CreateTicketAsync("XSS description", XssPayload);
        AssertResponseHtmlSafe(await admin.GetJsonAsync($"/api/v1/tickets/{ticket.Id}"), "ticket description");

        Assert.True((await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/replies", new ReplyRequest { BodyHtml = XssPayload, Force = true }, J)).IsSuccessStatusCode);
        Assert.True((await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/notes", new NoteRequest { BodyHtml = XssPayload }, J)).IsSuccessStatusCode);
        Assert.True((await admin.PutAsJsonAsync($"/api/v1/tickets/{ticket.Id}/draft", new { kind = "reply", bodyHtml = XssPayload }, J)).StatusCode
            is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.BadRequest);
        var resolve = await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/resolve", new ResolveTicketRequest(null, null, XssPayload, NotifyCustomer: false), J);
        Assert.True(resolve.IsSuccessStatusCode, await resolve.Content.ReadAsStringAsync());

        var timeline = await admin.GetJsonAsync($"/api/v1/tickets/{ticket.Id}/timeline");
        var threads = timeline!.AsArray().Where(i => i?["thread"] is JsonObject).Select(i => i!["thread"]!).ToList();
        Assert.True(threads.Count >= 2, "the reply and the note are on the timeline");
        foreach (var thread in threads) AssertResponseHtmlSafe(thread, $"thread {thread["kind"]}");
        AssertResponseHtmlSafe(await admin.GetJsonAsync($"/api/v1/tickets/{ticket.Id}"), "resolution");

        // What was stored is clean too (not only the API projection).
        var stored = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().TicketThreads.AsNoTracking()
            .Where(x => x.TicketId == ticket.Id).Select(x => x.BodyHtml).ToListAsync());
        foreach (var html in stored) AssertSafeHtml(html, "stored thread");

        var me = await admin.PutAsJsonAsync("/api/v1/me/profile", new { signature = XssPayload }, J);
        AssertResponseHtmlSafe(await me.JsonAsync(HttpStatusCode.OK), "signature");
    }

    [Fact]
    public async Task Knowledge_base_articles_are_sanitised_for_agents_and_the_public_portal()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var article = await admin.PublishedArticleAsync("XSS hardening article", XssPayload);
        AssertResponseHtmlSafe(await admin.GetJsonAsync($"/api/v1/kb/articles/{article.Id}"), "article (desk)");

        // Editing keeps it clean as well.
        var current = await admin.GetJsonAsync($"/api/v1/kb/articles/{article.Id}");
        var update = await admin.PutAsJsonAsync($"/api/v1/kb/articles/{article.Id}", new SaveArticleRequest
        {
            Title = "XSS hardening article", BodyHtml = XssPayload + "<p>Edited</p>", Visibility = "Public", ArticleType = "HowTo",
            RowVersion = current!["rowVersion"]?.GetValue<string>()
        }, J);
        AssertResponseHtmlSafe(await update.JsonAsync(HttpStatusCode.OK), "article update");

        var anonymous = factory.CreateClient();
        var portal = await anonymous.GetAsync($"/api/v1/portal/{t.Slug}/kb/articles/{article.Slug}");
        AssertResponseHtmlSafe(await portal.JsonAsync(HttpStatusCode.OK), "article (portal)");
    }

    [Fact]
    public async Task Portal_submissions_and_replies_are_sanitised()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var customer = await factory.CustomerAsync(t, "Xss Retail");
        var (_, contact) = await factory.ContactAsync(t, customer.Id, customer.HeadOffice, PortalAccessScope.Own, "Xss Contact");

        var created = await contact.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/tickets", new PortalCreateTicketRequest { Subject = "Portal XSS", DescriptionHtml = XssPayload }, J);
        var ticketId = (await created.JsonAsync(HttpStatusCode.OK))!["id"]!.GetValue<string>();
        AssertResponseHtmlSafe(await contact.GetJsonAsync($"/api/v1/portal/{t.Slug}/tickets/{ticketId}"), "portal ticket (customer view)");
        AssertResponseHtmlSafe(await admin.GetJsonAsync($"/api/v1/tickets/{ticketId}"), "portal ticket (agent view)");

        var reply = await contact.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/tickets/{ticketId}/replies", new PortalReplyRequest(XssPayload), J);
        Assert.True(reply.IsSuccessStatusCode, await reply.Content.ReadAsStringAsync());
        var messages = await contact.GetJsonAsync($"/api/v1/portal/{t.Slug}/tickets/{ticketId}/messages");
        foreach (var m in messages!.AsArray()) AssertResponseHtmlSafe(m, "portal message");
        var timeline = await admin.GetJsonAsync($"/api/v1/tickets/{ticketId}/timeline");
        foreach (var thread in timeline!.AsArray().Select(i => i?["thread"]).OfType<JsonObject>()) AssertResponseHtmlSafe(thread, "customer reply (agent view)");

        // Anonymous web form (brands that allow anonymous tickets).
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var brand = await db.Brands.FirstAsync(b => b.IsDefault);
            brand.AllowAnonymousTickets = true;
            await db.SaveChangesAsync();
        });
        var anonymous = factory.CreateClient();
        var submitted = await anonymous.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/submit", new PortalAnonymousTicketRequest
        {
            Name = "Mallory", Email = $"mallory.{Guid.NewGuid():N}@evil.test", Subject = "Anonymous XSS", DescriptionHtml = XssPayload
        }, J);
        var number = (await submitted.JsonAsync(HttpStatusCode.OK))!["displayNumber"]!.GetValue<string>().TrimStart('#');
        AssertResponseHtmlSafe(await admin.GetJsonAsync($"/api/v1/tickets/by-number/{number}"), "anonymous submission");
    }
}
