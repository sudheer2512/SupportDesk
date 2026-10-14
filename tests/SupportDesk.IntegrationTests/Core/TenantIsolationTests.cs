using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain.Tickets;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.IntegrationTests.Core;

[Collection("api")]
public sealed class TenantIsolationTests(DeskApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions J = DeskApiFactory.Json;

    private static async Task<TicketDetailDto> CreateTicketAsync(HttpClient client, string subject)
    {
        var response = await client.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest
        {
            Subject = subject, DescriptionHtml = "<p>Details</p>", Channel = "Phone",
            NewContact = new NewContactDto("Jane Customer", $"jane.{Guid.NewGuid():N}@example.test", null, null)
        }, J);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketDetailDto>(J))!;
    }

    [Fact]
    public async Task Tickets_of_one_tenant_are_invisible_to_another()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var clientA = await a.AdminClientAsync();
        var clientB = await b.AdminClientAsync();

        var ticketA = await CreateTicketAsync(clientA, "Tenant A private issue");

        // Direct fetch by id from tenant B → 404 (no existence leak).
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/api/v1/tickets/{ticketA.Id}")).StatusCode);
        // Mutations by id are equally blocked.
        var reply = await clientB.PostAsJsonAsync($"/api/v1/tickets/{ticketA.Id}/replies", new ReplyRequest { BodyHtml = "<p>hi</p>" }, J);
        Assert.Equal(HttpStatusCode.NotFound, reply.StatusCode);
        // Listing and search never include tenant A's ticket.
        var list = await clientB.GetFromJsonAsync<PagedResult<TicketListItemDto>>("/api/v1/tickets?q=Tenant%20A&limit=200", J);
        Assert.DoesNotContain(list!.Items, t => t.Id == ticketA.Id);
        // Same ticket number space is independent per tenant.
        var ticketB = await CreateTicketAsync(clientB, "Tenant B issue");
        Assert.Equal(ticketA.Number, ticketB.Number);
    }

    [Fact]
    public async Task Persistence_layer_blocks_cross_tenant_writes_even_for_buggy_code()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var clientA = await a.AdminClientAsync();
        var ticketA = await CreateTicketAsync(clientA, "Guarded ticket");

        await Assert.ThrowsAsync<CrossTenantWriteException>(() => factory.InTenantAsync(b.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            // Simulate a bug that bypasses the query filter and tries to modify another tenant's row.
            var foreign = await db.Tickets.IgnoreQueryFilters().FirstAsync(t => t.Id == ticketA.Id);
            foreign.Subject = "hijacked";
            await db.SaveChangesAsync();
        }));
        // And a context without a tenant sees nothing at all (fails closed).
        using var scope = factory.Services.CreateScope();
        var empty = await scope.ServiceProvider.GetRequiredService<DeskDbContext>().Tickets.CountAsync();
        Assert.Equal(0, empty);
        _ = scope.ServiceProvider.GetRequiredService<RequestContext>();
    }

    [Fact]
    public async Task Tokens_cannot_be_replayed_against_another_tenant_by_header_tricks()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var clientA = await a.AdminClientAsync();
        await CreateTicketAsync(clientA, "Only in A");
        // Client-supplied tenant hints are ignored: the tenant comes from the signed token.
        clientA.DefaultRequestHeaders.Add("X-Tenant", b.Slug);
        var list = await clientA.GetFromJsonAsync<PagedResult<TicketListItemDto>>("/api/v1/tickets?limit=50", J);
        Assert.All(list!.Items, t => Assert.NotEqual(Guid.Empty, t.Id));
        Assert.Contains(list.Items, t => t.Subject == "Only in A");
    }

    [Fact]
    public async Task Ticket_lifecycle_records_history_and_enforces_concurrency()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var ticket = await CreateTicketAsync(admin, "Lifecycle");

        var update = await admin.PatchAsJsonAsync($"/api/v1/tickets/{ticket.Id}", new UpdateTicketRequest { RowVersion = ticket.RowVersion, Priority = "High" }, J);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        // Stale rowVersion → 409 with the current ticket in the body.
        var stale = await admin.PatchAsJsonAsync($"/api/v1/tickets/{ticket.Id}", new UpdateTicketRequest { RowVersion = ticket.RowVersion, Priority = "Low" }, J);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("concurrency_conflict", await stale.Content.ReadAsStringAsync());

        var resolved = await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/resolve", new ResolveTicketRequest("fixed", "configuration", "<p>Done</p>"), J);
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var detail = await resolved.Content.ReadFromJsonAsync<TicketDetailDto>(J);
        Assert.Equal("Resolved", detail!.StatusCategory);

        var timeline = await admin.GetFromJsonAsync<List<TimelineItemDto>>($"/api/v1/tickets/{ticket.Id}/timeline", J);
        Assert.Contains(timeline!, i => i.Event?.Type == "Created");
        Assert.Contains(timeline!, i => i.Event?.Type == "FieldsChanged");
        Assert.Contains(timeline!, i => i.Event?.Type == "Resolved");
    }
}
