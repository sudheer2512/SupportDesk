using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Domain.Customers;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.IntegrationTests.Portal;
using SupportDesk.Persistence;
using static SupportDesk.IntegrationTests.Security.SecurityTestKit;

namespace SupportDesk.IntegrationTests.Security;

/// <summary>
/// Mass assignment / over-posting: unknown or server-owned JSON fields (tenantId, id, number, timestamps, ratings,
/// permissions, portal scope…) are ignored, and ids that belong to another tenant are never linked.
/// </summary>
[Collection("api")]
public sealed class MassAssignmentTests(DeskApiFactory factory)
{
    [Fact]
    public async Task Ticket_creation_ignores_server_owned_fields_and_never_lands_in_another_tenant()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var adminA = await a.AdminClientAsync();
        var adminB = await b.AdminClientAsync();
        var forcedId = Guid.NewGuid();

        var r = await adminA.PostAsJsonAsync("/api/v1/tickets", new JsonObject
        {
            ["subject"] = "Over-posted ticket", ["channel"] = "Phone", ["skipRouting"] = true,
            ["tenantId"] = b.Id.ToString(), ["TenantId"] = b.Id.ToString(), ["id"] = forcedId.ToString(), ["number"] = 1,
            ["createdAt"] = "2001-01-01T00:00:00Z", ["csatScore"] = 5, ["isDeleted"] = true, ["reopenCount"] = 99,
            ["supportCost"] = 0, ["slaState"] = "Met", ["statusCategory"] = "Closed", ["awaitingCustomerConfirmation"] = true
        });
        var created = await r.JsonAsync(HttpStatusCode.Created);
        var id = created!["id"]!.GetValue<string>();
        Assert.NotEqual(forcedId.ToString(), id);

        var detail = await adminA.GetJsonAsync($"/api/v1/tickets/{id}");
        Assert.True(detail!["createdAt"]!.GetValue<DateTime>() > DateTime.UtcNow.AddDays(-1));
        Assert.Null(detail["csatScore"]?.GetValue<int?>());
        Assert.Equal(0, detail["reopenCount"]!.GetValue<int>());
        Assert.Equal("Open", detail["statusCategory"]!.GetValue<string>());
        Assert.False(detail["awaitingCustomerConfirmation"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/v1/tickets/{id}")).StatusCode);
        var tenantOfRow = await factory.InTenantAsync(a.Id, sp => sp.GetRequiredService<DeskDbContext>().Tickets.AsNoTracking()
            .Where(t => t.Id == Guid.Parse(id)).Select(t => t.TenantId).SingleAsync());
        Assert.Equal(a.Id, tenantOfRow);
    }

    [Fact]
    public async Task Ids_of_another_tenant_are_never_linked_to_a_ticket()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var adminA = await a.AdminClientAsync();
        var adminB = await b.AdminClientAsync();
        var foreignTicket = await adminB.CreateTicketAsync("Tenant B ticket");
        var customerB = await factory.CustomerAsync(b, "Tenant B Customer");
        var (foreignContact, _) = await factory.ContactAsync(b, customerB.Id, customerB.HeadOffice, PortalAccessScope.Own, "Tenant B Contact");
        var (foreignTeam, foreignBrand) = await factory.InTenantAsync(b.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            return (await db.Teams.OrderBy(t => t.Id).Select(t => t.Id).FirstAsync(), await db.Brands.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync());
        });

        foreach (var (field, value) in new (string, Guid)[]
                 {
                     ("customerId", customerB.Id), ("contactId", foreignContact), ("locationId", customerB.HeadOffice), ("teamId", foreignTeam),
                     ("brandId", foreignBrand), ("parentTicketId", foreignTicket.Id), ("departmentId", b.SupportDepartmentId),
                     ("assigneeId", b.Provisioned.AdminUserId)
                 })
        {
            var r = await adminA.PostAsJsonAsync("/api/v1/tickets", new JsonObject
            {
                ["subject"] = $"Foreign {field}", ["channel"] = "Phone", ["skipRouting"] = true, [field] = value.ToString()
            });
            if (!r.IsSuccessStatusCode)
            {
                await AssertClientErrorAsync(r, HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.UnprocessableEntity);
                continue;
            }
            var id = Guid.Parse((await r.JsonAsync())!["id"]!.GetValue<string>());
            Assert.DoesNotContain((Guid?)value, await LinkedIdsAsync(a, id));
        }

        // Editing an existing ticket cannot point it at another tenant's records either.
        var ticket = await adminA.CreateTicketAsync("Tenant A ticket");
        foreach (var (field, value) in new (string, Guid)[]
                 {
                     ("contactId", foreignContact), ("locationId", customerB.HeadOffice), ("customerId", customerB.Id)
                 })
        {
            var current = await adminA.GetJsonAsync($"/api/v1/tickets/{ticket.Id}");
            var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/tickets/{ticket.Id}")
            {
                Content = JsonContent.Create(new JsonObject { ["rowVersion"] = current!["rowVersion"]!.GetValue<string>(), [field] = value.ToString() })
            };
            await AssertClientErrorAsync(await adminA.SendAsync(patch), HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.UnprocessableEntity);
            Assert.DoesNotContain((Guid?)value, await LinkedIdsAsync(a, ticket.Id));
        }
    }

    private Task<Guid?[]> LinkedIdsAsync(TestTenant tenant, Guid ticketId) =>
        factory.InTenantAsync(tenant.Id, sp => sp.GetRequiredService<DeskDbContext>().Tickets.AsNoTracking().Where(t => t.Id == ticketId)
            .Select(t => new[] { t.CustomerId, t.ContactId, t.LocationId, t.TeamId, t.BrandId, t.ParentTicketId, (Guid?)t.DepartmentId, t.AssigneeId })
            .SingleAsync());

    [Fact]
    public async Task Profile_updates_cannot_change_identity_or_permissions()
    {
        var t = await factory.CreateTenantAsync();
        var other = await factory.CreateTenantAsync();
        var (_, email) = await t.CreateAgentAsync("Plain Agent", "Agent");
        var agent = await t.AgentClientAsync(email);
        var before = await agent.GetJsonAsync("/api/v1/me");

        var r = await agent.PutAsJsonAsync("/api/v1/me/profile", new JsonObject
        {
            ["displayName"] = "Renamed Agent", ["email"] = "attacker@evil.test", ["tenantId"] = other.Id.ToString(), ["isActive"] = false,
            ["permissions"] = new JsonArray("admin.security", "admin.roles"), ["roles"] = new JsonArray("Administrator"), ["kind"] = "Admin",
            ["passwordHash"] = "x", ["mfaEnabled"] = false, ["ticketScope"] = "All"
        });
        var after = await r.JsonAsync(HttpStatusCode.OK);
        Assert.Equal("Renamed Agent", after!["displayName"]!.GetValue<string>());
        Assert.Equal(email, after["email"]!.GetValue<string>());
        Assert.Equal(t.Id.ToString(), after["tenantId"]!.GetValue<string>());
        Assert.Equal(before!["permissions"]!.ToJsonString(), after["permissions"]!.ToJsonString());
        Assert.Equal(before["ticketScope"]!.GetValue<string>(), after["ticketScope"]!.GetValue<string>());
        // Still signed in (not disabled) and still a plain agent on the next request.
        var again = await agent.GetJsonAsync("/api/v1/me");
        Assert.Equal(before["permissions"]!.ToJsonString(), again!["permissions"]!.ToJsonString());
    }

    [Fact]
    public async Task Portal_contacts_cannot_widen_their_own_access_or_switch_customer()
    {
        var t = await factory.CreateTenantAsync();
        var mine = await factory.CustomerAsync(t, "Own Customer");
        var theirs = await factory.CustomerAsync(t, "Another Customer");
        var (contactId, contact) = await factory.ContactAsync(t, mine.Id, mine.Branch, PortalAccessScope.Own, "Scoped Contact");
        var foreign = await (await factory.ContactAsync(t, theirs.Id, theirs.HeadOffice, PortalAccessScope.Own, "Other Contact")).Client
            .CreatePortalTicketAsync(t.Slug, "Other customer's ticket");

        var r = await contact.PutAsJsonAsync($"/api/v1/portal/{t.Slug}/account/profile", new JsonObject
        {
            ["fullName"] = "Scoped Contact", ["preferredLanguage"] = "en", ["portalAccess"] = "Organisation", ["PortalAccess"] = 2,
            ["customerId"] = theirs.Id.ToString(), ["locationId"] = theirs.HeadOffice.ToString(), ["email"] = "attacker@evil.test",
            ["isVip"] = true, ["tenantId"] = Guid.NewGuid().ToString()
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var stored = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().Contacts.AsNoTracking().SingleAsync(c => c.Id == contactId));
        Assert.Equal(PortalAccessScope.Own, stored.PortalAccess);
        Assert.Equal(mine.Id, stored.CustomerId);
        Assert.Equal(mine.Branch, stored.LocationId);
        Assert.False(stored.IsVip);
        Assert.DoesNotContain("evil.test", stored.Email);
        Assert.Equal(HttpStatusCode.NotFound, (await contact.GetAsync($"/api/v1/portal/{t.Slug}/tickets/{foreign.Id}")).StatusCode);

        // Creating a ticket "for" another customer's contact or customer is ignored as well.
        var created = await contact.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/tickets", new JsonObject
        {
            ["subject"] = "Over-posted portal ticket", ["descriptionHtml"] = "<p>x</p>", ["customerId"] = theirs.Id.ToString(),
            ["contactId"] = Guid.NewGuid().ToString(), ["priority"] = "Urgent", ["assigneeId"] = Guid.NewGuid().ToString()
        });
        if (created.IsSuccessStatusCode)
        {
            var ticketId = Guid.Parse((await created.JsonAsync())!["id"]!.GetValue<string>());
            var row = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().Tickets.AsNoTracking().SingleAsync(x => x.Id == ticketId));
            Assert.Equal(contactId, row.ContactId);
            Assert.Equal(mine.Id, row.CustomerId);
            Assert.Null(row.AssigneeId);
        }
        else await AssertClientErrorAsync(created, HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);
    }
}
