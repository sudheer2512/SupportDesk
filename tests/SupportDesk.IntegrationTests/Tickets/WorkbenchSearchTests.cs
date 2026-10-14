using System.Net;
using System.Net.Http.Json;
using SupportDesk.Contracts.Tickets;
using SupportDesk.IntegrationTests.Infrastructure;

namespace SupportDesk.IntegrationTests.Tickets;

/// <summary>Workbench look-ups (GET /api/v1/search…): results are tenant-scoped and need an agent principal.</summary>
[Collection("api")]
public sealed class WorkbenchSearchTests(DeskApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions J = DeskApiFactory.Json;

    [Fact]
    public async Task Global_search_finds_tickets_and_contacts_of_the_own_tenant_only()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var clientA = await a.AdminClientAsync();
        var clientB = await b.AdminClientAsync();
        var marker = Guid.NewGuid().ToString("N")[..10];

        var created = await clientA.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest
        {
            Subject = $"Printer jam {marker}", Channel = "Phone",
            NewContact = new NewContactDto($"Search Person {marker}", $"search.{marker}@example.test", null, null)
        }, J);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ticket = (await created.Content.ReadFromJsonAsync<TicketDetailDto>(J))!;

        var found = await clientA.GetFromJsonAsync<GlobalSearchResultDto>($"/api/v1/search?q={marker}", J);
        Assert.Contains(found!.Tickets, t => t.Id == ticket.Id);
        Assert.Contains(found.Contacts, c => c.Id == ticket.ContactId);

        var contacts = await clientA.GetFromJsonAsync<List<ContactLookupDto>>($"/api/v1/search/contacts?q=search.{marker}", J);
        Assert.Single(contacts!);

        var other = await clientB.GetFromJsonAsync<GlobalSearchResultDto>($"/api/v1/search?q={marker}", J);
        Assert.Empty(other!.Tickets);
        Assert.Empty(other.Contacts);
        Assert.Empty((await clientB.GetFromJsonAsync<List<ContactLookupDto>>($"/api/v1/search/contacts?q=search.{marker}", J))!);
    }

    [Fact]
    public async Task Search_requires_authentication_and_short_queries_return_nothing()
    {
        var a = await factory.CreateTenantAsync();
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/search?q=printer")).StatusCode);

        var client = await a.AdminClientAsync();
        var result = await client.GetFromJsonAsync<GlobalSearchResultDto>("/api/v1/search?q=a", J);
        Assert.Empty(result!.Tickets);
        Assert.Empty(result.Customers);
        Assert.Empty(result.Contacts);
    }
}
