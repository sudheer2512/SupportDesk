using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Portal;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Tickets;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;

namespace SupportDesk.IntegrationTests.Portal;

[Collection("api")]
public sealed class PortalTicketTests(DeskApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions J = PortalTestKit.J;

    private static async Task<List<Guid>> TicketIdsAsync(HttpClient client, string slug, string? scope = null)
    {
        var url = $"/api/v1/portal/{slug}/tickets?limit=100" + (scope is null ? "" : $"&scope={scope}");
        var page = await client.GetFromJsonAsync<PagedResult<PortalTicketListItemDto>>(url, J);
        return page!.Items.Select(i => i.Id).ToList();
    }

    [Fact]
    public async Task Own_location_and_organisation_contacts_see_exactly_their_scope()
    {
        var t = await factory.CreateTenantAsync();
        var acme = await factory.CustomerAsync(t, "Acme");
        var other = await factory.CustomerAsync(t, "Other Co");
        var (_, own) = await factory.ContactAsync(t, acme.Id, acme.HeadOffice, PortalAccessScope.Own, "Own User");
        var (_, loc) = await factory.ContactAsync(t, acme.Id, acme.Branch, PortalAccessScope.Location, "Branch Lead");
        var (_, org) = await factory.ContactAsync(t, acme.Id, acme.HeadOffice, PortalAccessScope.Organisation, "Org Admin");
        var (_, stranger) = await factory.ContactAsync(t, other.Id, other.HeadOffice, PortalAccessScope.Organisation, "Stranger");

        var t1 = await own.CreatePortalTicketAsync(t.Slug, "Own ticket at head office");
        var t2 = await loc.CreatePortalTicketAsync(t.Slug, "Branch ticket");
        var t3 = await org.CreatePortalTicketAsync(t.Slug, "Org admin ticket at head office");
        var t4 = await stranger.CreatePortalTicketAsync(t.Slug, "Other customer ticket");

        Assert.Equal([t1.Id], await TicketIdsAsync(own, t.Slug));
        Assert.Equal([t2.Id], await TicketIdsAsync(loc, t.Slug));
        Assert.Equal(new[] { t1.Id, t2.Id, t3.Id }.OrderBy(x => x), (await TicketIdsAsync(org, t.Slug)).OrderBy(x => x));
        Assert.Equal([t4.Id], await TicketIdsAsync(stranger, t.Slug));
        // Narrowing the scope works; widening beyond the contact's access is capped.
        Assert.Equal([t3.Id], await TicketIdsAsync(org, t.Slug, "own"));
        Assert.Equal([t1.Id], await TicketIdsAsync(own, t.Slug, "organisation"));
        Assert.Equal([t2.Id], await TicketIdsAsync(loc, t.Slug, "organisation"));

        // Single-ticket access follows the same scope (404, no existence leak).
        Assert.Equal(HttpStatusCode.NotFound, (await own.GetAsync($"/api/v1/portal/{t.Slug}/tickets/{t2.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await loc.GetAsync($"/api/v1/portal/{t.Slug}/tickets/{t1.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/portal/{t.Slug}/tickets/{t3.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await org.GetAsync($"/api/v1/portal/{t.Slug}/tickets/{t2.Id}")).StatusCode);
        var reply = await loc.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/tickets/{t1.Id}/replies", new PortalReplyRequest("<p>sneaky</p>"), J);
        Assert.Equal(HttpStatusCode.NotFound, reply.StatusCode);

        var scope = await org.GetFromJsonAsync<PortalTicketScopeDto>($"/api/v1/portal/{t.Slug}/tickets/scope", J);
        Assert.Equal("organisation", scope!.Max);
        Assert.Equal(["own"], (await own.GetFromJsonAsync<PortalTicketScopeDto>($"/api/v1/portal/{t.Slug}/tickets/scope", J))!.Available);
    }

    [Fact]
    public async Task Portal_never_returns_internal_notes_events_internal_attachments_or_status_names()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var acme = await factory.CustomerAsync(t, "Acme");
        var (_, contact) = await factory.ContactAsync(t, acme.Id, acme.HeadOffice, PortalAccessScope.Own, "Customer");
        var created = await contact.CreatePortalTicketAsync(t.Slug, "Printer offline");

        // Agent: internal note with an internal attachment, a public reply, and an internal status with a customer label.
        var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent("secret diagnostic dump"u8.ToArray());
        bytes.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(bytes, "file", "internal-dump.txt");
        var uploaded = await (await admin.PostAsync("/api/v1/files", form)).Content.ReadFromJsonAsync<UploadedFileDto>(J);
        var note = await admin.PostAsJsonAsync($"/api/v1/tickets/{created.Id}/notes",
            new NoteRequest { BodyHtml = "<p>INTERNAL-ONLY: customer is on an unsupported driver</p>", AttachmentFileIds = [uploaded!.FileId] }, J);
        Assert.True(note.IsSuccessStatusCode, await note.Content.ReadAsStringAsync());
        var reply = await admin.PostAsJsonAsync($"/api/v1/tickets/{created.Id}/replies", new ReplyRequest { BodyHtml = "<p>Please restart the spooler.</p>", Force = true }, J);
        Assert.True(reply.IsSuccessStatusCode, await reply.Content.ReadAsStringAsync());
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var status = new TicketStatus { Name = "Escalated to Vendor XYZ", Category = StatusCategory.Waiting, CustomerLabel = "With our engineering team", SortOrder = 9 };
            db.TicketStatuses.Add(status);
            await db.SaveChangesAsync();
            var tickets = sp.GetRequiredService<TicketService>();
            var ticket = await db.Tickets.FirstAsync(x => x.Id == created.Id);
            await tickets.ApplyStatusAsync(ticket, status, null, null, bypassRules: true, default);
            await db.SaveChangesAsync();
        });

        var detailJson = await contact.GetStringAsync($"/api/v1/portal/{t.Slug}/tickets/{created.Id}");
        var messagesJson = await contact.GetStringAsync($"/api/v1/portal/{t.Slug}/tickets/{created.Id}/messages");
        foreach (var json in new[] { detailJson, messagesJson })
        {
            Assert.DoesNotContain("INTERNAL-ONLY", json);
            Assert.DoesNotContain("internal-dump.txt", json);
            Assert.DoesNotContain("Escalated to Vendor", json);
            Assert.DoesNotContain("InternalNote", json);
            Assert.DoesNotContain("assignee", json, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("restart the spooler", messagesJson);
        var detail = System.Text.Json.JsonSerializer.Deserialize<PortalTicketDetailDto>(detailJson, J)!;
        Assert.Equal("With our engineering team", detail.Status);

        // The internal attachment cannot be fetched through the portal either.
        var internalAttachmentId = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().TicketAttachments
            .Where(a => a.TicketId == created.Id && a.Visibility == Visibility.Internal).Select(a => a.Id).FirstAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await contact.GetAsync($"/api/v1/portal/{t.Slug}/tickets/{created.Id}/attachments/{internalAttachmentId}")).StatusCode);
    }

    [Fact]
    public async Task Reopen_is_allowed_inside_the_window_and_refused_after_it()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var acme = await factory.CustomerAsync(t, "Acme");
        var (_, contact) = await factory.ContactAsync(t, acme.Id, acme.HeadOffice, PortalAccessScope.Own, "Customer");
        await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<TenantSettingsService>()
            .SetAsync(SettingKeys.Tickets, new TicketSettings { ReopenWindowDays = 5, AllowCustomerReopen = true }));

        var created = await contact.CreatePortalTicketAsync(t.Slug, "Report totals wrong");
        var resolve = await admin.PostAsJsonAsync($"/api/v1/tickets/{created.Id}/resolve", new ResolveTicketRequest("fixed", null, "<p>Fixed the report filter.</p>"), J);
        Assert.True(resolve.IsSuccessStatusCode, await resolve.Content.ReadAsStringAsync());

        var detail = await contact.GetFromJsonAsync<PortalTicketDetailDto>($"/api/v1/portal/{t.Slug}/tickets/{created.Id}", J);
        Assert.True(detail!.Actions.Reopen);
        var reopened = await contact.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/tickets/{created.Id}/reopen", new PortalReopenRequest("Still wrong for March"), J);
        Assert.True(reopened.IsSuccessStatusCode, await reopened.Content.ReadAsStringAsync());
        Assert.True((await reopened.Content.ReadFromJsonAsync<PortalTicketDetailDto>(J))!.IsOpen);

        // Resolve and close again, then move the closure outside the window.
        Assert.True((await admin.PostAsJsonAsync($"/api/v1/tickets/{created.Id}/resolve", new ResolveTicketRequest("fixed", null, null), J)).IsSuccessStatusCode);
        Assert.True((await admin.PostAsync($"/api/v1/tickets/{created.Id}/close", null)).IsSuccessStatusCode);
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var old = db.Clock.UtcNow.AddDays(-6);
            await db.Tickets.Where(x => x.Id == created.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ClosedAt, old).SetProperty(x => x.ResolvedAt, old));
        });
        detail = await contact.GetFromJsonAsync<PortalTicketDetailDto>($"/api/v1/portal/{t.Slug}/tickets/{created.Id}", J);
        Assert.False(detail!.Actions.Reopen);
        Assert.False(detail.Actions.Reply);
        var late = await contact.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/tickets/{created.Id}/reopen", new PortalReopenRequest("Again"), J);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, late.StatusCode);
        var lateReply = await contact.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/tickets/{created.Id}/replies", new PortalReplyRequest("<p>hello?</p>"), J);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, lateReply.StatusCode);
        Assert.False((await contact.GetFromJsonAsync<PortalTicketDetailDto>($"/api/v1/portal/{t.Slug}/tickets/{created.Id}", J))!.IsOpen);
    }

    [Fact]
    public async Task Customer_confirms_resolution_and_rates_support()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var acme = await factory.CustomerAsync(t, "Acme");
        var (_, contact) = await factory.ContactAsync(t, acme.Id, acme.HeadOffice, PortalAccessScope.Organisation, "Customer");
        var (_, colleague) = await factory.ContactAsync(t, acme.Id, acme.HeadOffice, PortalAccessScope.Organisation, "Colleague");
        var created = await contact.CreatePortalTicketAsync(t.Slug, "Cannot post journal");
        Assert.True((await admin.PostAsJsonAsync($"/api/v1/tickets/{created.Id}/resolve",
            new ResolveTicketRequest("fixed", null, null, RequestConfirmation: true), J)).IsSuccessStatusCode);

        var detail = await contact.GetFromJsonAsync<PortalTicketDetailDto>($"/api/v1/portal/{t.Slug}/tickets/{created.Id}", J);
        Assert.True(detail!.AwaitingConfirmation);
        Assert.True(detail.Actions.Confirm);
        // A colleague can see the ticket but cannot confirm it for the requester.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await colleague.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/tickets/{created.Id}/confirm", new PortalConfirmRequest(true), J)).StatusCode);

        var confirmed = await contact.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/tickets/{created.Id}/confirm", new PortalConfirmRequest(true, "Works now"), J);
        Assert.True(confirmed.IsSuccessStatusCode, await confirmed.Content.ReadAsStringAsync());
        var after = (await confirmed.Content.ReadFromJsonAsync<PortalTicketDetailDto>(J))!;
        Assert.Equal("Closed", after.StatusCategory);
        Assert.True(after.Actions.Rate);

        var rated = await contact.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/tickets/{created.Id}/satisfaction", new PortalSatisfactionRequest(5, "Quick help"), J);
        Assert.True(rated.IsSuccessStatusCode, await rated.Content.ReadAsStringAsync());
        Assert.Equal(5, (await rated.Content.ReadFromJsonAsync<PortalTicketDetailDto>(J))!.CsatScore);
        Assert.Equal(HttpStatusCode.Conflict,
            (await contact.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/tickets/{created.Id}/satisfaction", new PortalSatisfactionRequest(4), J)).StatusCode);
    }

    [Fact]
    public async Task Contact_tokens_only_work_for_brands_of_their_own_tenant()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var acme = await factory.CustomerAsync(a, "Acme");
        var (_, contact) = await factory.ContactAsync(a, acme.Id, acme.HeadOffice, PortalAccessScope.Organisation, "Customer");
        Assert.Equal(HttpStatusCode.NotFound, (await contact.GetAsync($"/api/v1/portal/{b.Slug}/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await contact.GetAsync($"/api/v1/portal/{a.Slug}/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/v1/portal/{a.Slug}/tickets")).StatusCode);
    }
}
