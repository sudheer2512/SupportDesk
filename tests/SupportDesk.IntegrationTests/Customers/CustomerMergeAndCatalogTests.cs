using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Customers;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Customers;
using SupportDesk.Contracts.Products;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Products;
using SupportDesk.Domain.Service;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;

namespace SupportDesk.IntegrationTests.Customers;

/// <summary>Organisation merge (data moved, idempotency, permission, tenant isolation) and catalog delete semantics.</summary>
[Collection("api")]
public sealed class CustomerMergeAndCatalogTests(DeskApiFactory factory)
{
    private static readonly JsonSerializerOptions J = DeskApiFactory.Json;

    private static async Task<T> Ok<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(J))!;
    }

    private static async Task<CustomerDetailDto> CustomerAsync(HttpClient client, string name, string? code = null, params string[] domains) =>
        await Ok<CustomerDetailDto>(await client.PostAsJsonAsync("/api/v1/customers", new SaveCustomerRequest { Name = name, Code = code, Tier = "Gold", Domains = domains }, J));

    private Task<T> Db<T>(TestTenant t, Func<DeskDbContext, Task<T>> query) => factory.InTenantAsync(t.Id, sp => query(sp.GetRequiredService<DeskDbContext>()));

    /// <summary>Gives the merged customer one of everything that must move.</summary>
    private Task SeedRecordsAsync(TestTenant t, Guid customerId) => factory.InTenantAsync(t.Id, async sp =>
    {
        var db = sp.GetRequiredService<DeskDbContext>();
        var product = new Product { Name = "POS", Code = $"P{Guid.NewGuid():N}"[..8] };
        db.Products.Add(product);
        var location = new CustomerLocation { CustomerId = customerId, Name = "Pune branch" };
        db.CustomerLocations.Add(location);
        db.CustomerDepartments.Add(new CustomerDepartment { CustomerId = customerId, Name = "Accounts" });
        var installation = new Installation { CustomerId = customerId, ProductId = product.Id, Name = "Pune POS", LocationId = location.Id };
        db.Installations.Add(installation);
        db.Licences.Add(new Licence { CustomerId = customerId, InstallationId = installation.Id, ProductId = product.Id, KeyMasked = "XXXX-1234", Seats = 5, IssuedOn = new DateOnly(2026, 1, 1) });
        db.CustomerAssets.Add(new CustomerAsset { CustomerId = customerId, Name = "Receipt printer", InstallationId = installation.Id });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        db.Contracts.Add(new Contract { CustomerId = customerId, Number = $"AMC-{Guid.NewGuid():N}"[..12], Name = "AMC", StartDate = today.AddMonths(-1), EndDate = today.AddMonths(11) });
        db.ServiceActivities.Add(new ServiceActivity { CustomerId = customerId, Title = "Onsite visit", ScheduledStart = DateTime.UtcNow.AddDays(2), ScheduledEnd = DateTime.UtcNow.AddDays(2).AddHours(2) });
        db.LegalHolds.Add(new LegalHold { Name = "Dispute", Reason = "Audit", CustomerId = customerId });
        await db.SaveChangesAsync();
    });

    [Fact]
    public async Task Merging_customers_moves_their_records_keeps_history_and_is_idempotent()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var keep = await CustomerAsync(admin, "ABC Retail", "ABC", "abcretail.example");
        var dup = await CustomerAsync(admin, "ABC Retail Pvt Ltd", "ABC-DUP", "abc-retail.example");
        var subsidiary = await Ok<CustomerDetailDto>(await admin.PostAsJsonAsync("/api/v1/customers",
            new SaveCustomerRequest { Name = "ABC Retail Kochi", ParentCustomerId = dup.Id }, J));
        var contact = await Ok<ContactDetailDto>(await admin.PostAsJsonAsync("/api/v1/contacts",
            new SaveContactRequest { FullName = "Lata Menon", Email = $"lata.{Guid.NewGuid():N}"[..14] + "@abc-retail.example", CustomerId = dup.Id }, J));
        var ticket = await Ok<TicketDetailDto>(await admin.PostAsJsonAsync("/api/v1/tickets",
            new CreateTicketRequest { Subject = "Printer offline", Channel = "Phone", CustomerId = dup.Id, ContactId = contact.Id, SkipRouting = true }, J));
        await SeedRecordsAsync(t, dup.Id);

        var result = await Ok<CustomerMergeResultDto>(await admin.PostAsJsonAsync("/api/v1/customers/merge",
            new MergeCustomersRequest(keep.Id, dup.Id, ["code"]), J));
        Assert.False(result.AlreadyMerged);
        Assert.Equal(1, result.Moved["contacts"]);
        Assert.Equal(1, result.Moved["tickets"]);
        foreach (var kind in new[] { "locations", "customerDepartments", "contracts", "installations", "licences", "assets", "serviceActivities", "subsidiaries", "legalHolds" })
            Assert.True(result.Moved[kind] == 1, $"{kind}: {result.Moved[kind]}");
        Assert.Contains("code", result.FieldsCopied);
        Assert.Contains("domains", result.FieldsCopied);

        // Everything now belongs to the survivor.
        var survivor = await Ok<CustomerDetailDto>(await admin.GetAsync($"/api/v1/customers/{keep.Id}"));
        Assert.Equal("ABC-DUP", survivor.Code);
        Assert.Contains("abc-retail.example", survivor.Domains);
        Assert.Contains(survivor.Children, c => c.Id == subsidiary.Id);
        Assert.Equal(keep.Id, (await Ok<TicketDetailDto>(await admin.GetAsync($"/api/v1/tickets/{ticket.Id}"))).CustomerId);
        Assert.Equal(keep.Id, (await Ok<ContactDetailDto>(await admin.GetAsync($"/api/v1/contacts/{contact.Id}"))).CustomerId);
        var leftovers = await Db(t, async db => (
            await db.Installations.CountAsync(i => i.CustomerId == dup.Id) + await db.Contracts.CountAsync(c => c.CustomerId == dup.Id) +
            await db.Licences.CountAsync(l => l.CustomerId == dup.Id) + await db.CustomerAssets.CountAsync(a => a.CustomerId == dup.Id) +
            await db.LegalHolds.CountAsync(h => h.CustomerId == dup.Id) + await db.CustomerLocations.CountAsync(l => l.CustomerId == dup.Id)));
        Assert.Equal(0, leftovers);

        // The merged customer is hidden, read-only and cannot be restored.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/customers/{dup.Id}")).StatusCode);
        var list = await admin.GetFromJsonAsync<PagedResult<CustomerListItemDto>>("/api/v1/customers?limit=200", J);
        Assert.DoesNotContain(list!.Items, c => c.Id == dup.Id);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PutAsJsonAsync($"/api/v1/customers/{dup.Id}", new SaveCustomerRequest { Name = "Edited", RowVersion = dup.RowVersion }, J)).StatusCode);
        var restore = await admin.PostAsync($"/api/v1/customers/{dup.Id}/restore", null);
        Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
        Assert.Equal("Merged", await Db(t, db => db.Customers.IgnoreQueryFilters().Where(c => c.Id == dup.Id).Select(c => c.Status.ToString()).FirstAsync()));

        // History, audit and ticket activity.
        var history = await admin.GetFromJsonAsync<List<CustomerMergeLogDto>>($"/api/v1/customers/{keep.Id}/merge-history", J);
        var entry = Assert.Single(history!);
        Assert.Equal(dup.Id, entry.MergedCustomerId);
        Assert.Equal("ABC Retail Pvt Ltd", entry.MergedCustomerName);
        var fromMerged = await admin.GetFromJsonAsync<List<CustomerMergeLogDto>>($"/api/v1/customers/{dup.Id}/merge-history", J);
        Assert.Equal(keep.Id, Assert.Single(fromMerged!).SurvivorCustomerId);
        Assert.True(await Db(t, db => db.AuditLogs.AnyAsync(a => a.Action == CustomerMergeService.AuditAction && a.EntityId == keep.Id)));
        var timeline = await admin.GetFromJsonAsync<List<TimelineItemDto>>($"/api/v1/tickets/{ticket.Id}/timeline", J);
        Assert.Contains(timeline!, i => i.Event?.Summary?.Contains("merged into ABC Retail") == true);

        // Repeating the merge returns the original outcome; merging it elsewhere is refused.
        var again = await Ok<CustomerMergeResultDto>(await admin.PostAsJsonAsync("/api/v1/customers/merge", new MergeCustomersRequest(keep.Id, dup.Id), J));
        Assert.True(again.AlreadyMerged);
        Assert.Equal(result.LogId, again.LogId);
        Assert.Equal(1, again.Moved["tickets"]);
        var other = await CustomerAsync(admin, "Another customer");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/v1/customers/merge", new MergeCustomersRequest(other.Id, dup.Id), J)).StatusCode);
    }

    [Fact]
    public async Task Customer_merge_needs_customers_merge_and_stays_inside_the_tenant()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var a = await CustomerAsync(admin, "Keep Co");
        var b = await CustomerAsync(admin, "Dup Co");

        var (_, agentEmail) = await t.CreateAgentAsync("Merge Agent");
        var agent = await t.AgentClientAsync(agentEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync("/api/v1/customers/merge", new MergeCustomersRequest(a.Id, b.Id), J)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/customers/merge", new MergeCustomersRequest(a.Id, a.Id), J)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/v1/customers/merge", new MergeCustomersRequest(a.Id, b.Id, ["isDeleted"]), J)).StatusCode);

        // Supervisors get customers.merge.
        var (_, supervisorEmail) = await t.CreateAgentAsync("Merge Supervisor", "Supervisor");
        var supervisor = await t.AgentClientAsync(supervisorEmail);
        var c = await CustomerAsync(admin, "Dup Co 2");
        await Ok<CustomerMergeResultDto>(await supervisor.PostAsJsonAsync("/api/v1/customers/merge", new MergeCustomersRequest(a.Id, c.Id), J));

        // Another tenant's customer is invisible (404), and nothing of it moves.
        var other = await factory.CreateTenantAsync();
        var otherAdmin = await other.AdminClientAsync();
        var foreign = await CustomerAsync(otherAdmin, "Foreign Co");
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/v1/customers/merge", new MergeCustomersRequest(a.Id, foreign.Id), J)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherAdmin.PostAsJsonAsync("/api/v1/customers/merge", new MergeCustomersRequest(foreign.Id, b.Id), J)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await otherAdmin.GetAsync($"/api/v1/customers/{foreign.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/customers/{b.Id}")).StatusCode);
    }

    [Fact]
    public async Task Referenced_catalog_items_are_retired_instead_of_deleted()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var family = await Ok<ProductFamilyDto>(await admin.PostAsJsonAsync("/api/v1/product-families", new SaveProductFamilyRequest("Retail", null), J));
        var used = await Ok<ProductDto>(await admin.PostAsJsonAsync("/api/v1/products",
            new SaveProductRequest { Name = "Used product", Code = "USED", FamilyId = family.Id, IsActive = true }, J));
        var unused = await Ok<ProductDto>(await admin.PostAsJsonAsync("/api/v1/products", new SaveProductRequest { Name = "Unused product", Code = "UNUSED", IsActive = true }, J));
        var v1 = await Ok<ProductVersionDto>(await admin.PostAsJsonAsync($"/api/v1/products/{used.Id}/versions", new SaveProductVersionRequest("1.0", null, null, "Released", null), J));
        var v2 = await Ok<ProductVersionDto>(await admin.PostAsJsonAsync($"/api/v1/products/{used.Id}/versions", new SaveProductVersionRequest("2.0", null, null, "Released", null), J));
        await Ok<TicketDetailDto>(await admin.PostAsJsonAsync("/api/v1/tickets",
            new CreateTicketRequest { Subject = "Version issue", Channel = "Phone", ProductId = used.Id, VersionId = v1.Id, SkipRouting = true }, J));

        // Families in use cannot be deleted.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/v1/product-families/{family.Id}")).StatusCode);
        // A version referenced by a ticket is retired (End of life); an unused one is deleted.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/products/{used.Id}/versions/{v1.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/products/{used.Id}/versions/{v2.Id}")).StatusCode);
        var product = await Ok<ProductDto>(await admin.GetAsync($"/api/v1/products/{used.Id}"));
        Assert.Equal("EndOfLife", Assert.Single(product.Versions).Status);
        // A product referenced by a ticket is deactivated; an unused one is deleted.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/products/{used.Id}")).StatusCode);
        Assert.False((await Ok<ProductDto>(await admin.GetAsync($"/api/v1/products/{used.Id}"))).IsActive);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/products/{unused.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/products/{unused.Id}")).StatusCode);
    }
}
