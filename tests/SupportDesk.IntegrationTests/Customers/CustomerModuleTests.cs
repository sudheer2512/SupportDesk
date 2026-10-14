using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Identity;
using SupportDesk.Application.Integrations.Erp;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Service;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Customers;
using SupportDesk.Contracts.Erp;
using SupportDesk.Contracts.Products;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Products;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Service;
using SupportDesk.Domain.Tickets;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;

namespace SupportDesk.IntegrationTests.Customers;

[Collection("api")]
public sealed class CustomerModuleTests(DeskApiFactory factory)
{
    private static readonly JsonSerializerOptions J = DeskApiFactory.Json;

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(J))!;
    }

    private static async Task<CustomerDetailDto> CreateCustomerAsync(HttpClient client, string name, Guid? accountManager = null, string? code = null) =>
        await ReadAsync<CustomerDetailDto>(await client.PostAsJsonAsync("/api/v1/customers",
            new SaveCustomerRequest { Name = name, Code = code, AccountManagerUserId = accountManager, Tier = "Gold" }, J));

    private static async Task<ContactDetailDto> CreateContactAsync(HttpClient client, Guid customerId, string name, string? email, string? phone = null) =>
        await ReadAsync<ContactDetailDto>(await client.PostAsJsonAsync("/api/v1/contacts",
            new SaveContactRequest { FullName = name, Email = email, Phone = phone, CustomerId = customerId }, J));

    /// <summary>An agent whose only role has account-managed customer scope.</summary>
    private async Task<(Guid UserId, HttpClient Client)> AccountManagerAsync(TestTenant t)
    {
        var email = $"am.{Guid.NewGuid().ToString("N")[..6]}@{t.Slug}.test";
        var userId = await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var role = new Role { Name = $"Account Manager {Guid.NewGuid():N}"[..30], TicketScope = TicketScope.Departments, CustomerScope = CustomerScope.AccountManaged };
            db.Roles.Add(role);
            foreach (var p in Permissions.Templates.Agent.Append(Permissions.ContractsView).Distinct()) db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionCode = p });
            var user = new User
            {
                Email = email, EmailNormalized = email, DisplayName = "Account Manager", Kind = UserKind.Agent,
                PasswordHash = sp.GetRequiredService<PasswordService>().Hash(DeskApiFactory.Password)
            };
            db.Users.Add(user);
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
            db.Agents.Add(new Agent { UserId = user.Id, Availability = AgentAvailability.Online });
            db.AgentDepartments.Add(new AgentDepartment { UserId = user.Id, DepartmentId = t.SupportDepartmentId });
            await db.SaveChangesAsync();
            return user.Id;
        });
        return (userId, await t.AgentClientAsync(email));
    }

    // ───────────────────────────── Customer scope ─────────────────────────────

    [Fact]
    public async Task Account_managed_agents_only_see_their_own_customers()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var (amId, am) = await AccountManagerAsync(t);
        var mine = await CreateCustomerAsync(admin, "Managed Account Ltd", amId);
        var other = await CreateCustomerAsync(admin, "Someone Else's Account Ltd");
        var otherContact = await CreateContactAsync(admin, other.Id, "Other Person", $"other.{Guid.NewGuid():N}@else.test");

        var list = await am.GetFromJsonAsync<PagedResult<CustomerListItemDto>>("/api/v1/customers?limit=200", J);
        Assert.Contains(list!.Items, c => c.Id == mine.Id);
        Assert.DoesNotContain(list.Items, c => c.Id == other.Id);

        Assert.Equal(HttpStatusCode.OK, (await am.GetAsync($"/api/v1/customers/{mine.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await am.GetAsync($"/api/v1/customers/{mine.Id}/360")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await am.GetAsync($"/api/v1/customers/{other.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await am.GetAsync($"/api/v1/customers/{other.Id}/360")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await am.GetAsync($"/api/v1/customers/{other.Id}/locations")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await am.GetAsync($"/api/v1/contacts/{otherContact.Id}")).StatusCode);
        var contacts = await am.GetFromJsonAsync<PagedResult<ContactListItemDto>>($"/api/v1/contacts?customerId={other.Id}", J);
        Assert.Empty(contacts!.Items);
        // Writes are scoped too: the other customer cannot be edited, and new customers must be assigned to the agent.
        var edit = await am.PutAsJsonAsync($"/api/v1/customers/{other.Id}", new SaveCustomerRequest { Name = "Hijacked", RowVersion = other.RowVersion }, J);
        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        var unassigned = await am.PostAsJsonAsync("/api/v1/customers", new SaveCustomerRequest { Name = "Unassigned Ltd" }, J);
        Assert.Equal(HttpStatusCode.BadRequest, unassigned.StatusCode);
        // An administrator (all customers) sees both.
        var all = await admin.GetFromJsonAsync<PagedResult<CustomerListItemDto>>("/api/v1/customers?limit=200", J);
        Assert.Contains(all!.Items, c => c.Id == other.Id);
    }

    // ───────────────────────────── Duplicates & merge ─────────────────────────────

    [Fact]
    public async Task Duplicate_detection_finds_exact_and_fuzzy_duplicates()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var customer = await CreateCustomerAsync(admin, "Duplicate Detection Co");
        var otherCustomer = await CreateCustomerAsync(admin, "Unrelated Co");
        var ramesh = await CreateContactAsync(admin, customer.Id, "Ramesh Kumar", $"ramesh.{Guid.NewGuid():N}@dd.test", "+91 98450 12345");
        var samePhone = await CreateContactAsync(admin, customer.Id, "Ramesh Kumaar", $"rk.{Guid.NewGuid():N}@gmail.test", null);
        var fuzzy = await CreateContactAsync(admin, customer.Id, "Ramesh Kumar R", null, null);
        var unrelated = await CreateContactAsync(admin, otherCustomer.Id, "Ramesh Kumar", null, null);
        // Legacy rows: the same phone in trunk-prefix form, and an exact e-mail duplicate imported before identities existed.
        var legacyEmail = Guid.Empty;
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var c = await db.Contacts.FirstAsync(x => x.Id == samePhone.Id);
            c.Phone = "098450 12345";
            c.PhoneNormalized = TextNormalization.NormalizePhone(c.Phone);
            var copy = new Contact { CustomerId = otherCustomer.Id, FullName = "R. Kumar", Email = ramesh.Email, EmailNormalized = ramesh.Email };
            db.Contacts.Add(copy);
            legacyEmail = copy.Id;
            await db.SaveChangesAsync();
        });

        var candidates = await ReadAsync<List<DuplicateCandidateDto>>(await admin.GetAsync($"/api/v1/contacts/{ramesh.Id}/duplicates"));
        var byId = candidates.ToDictionary(c => c.Contact.Id);
        Assert.Contains(legacyEmail, byId.Keys);
        Assert.Contains("Same e-mail address", byId[legacyEmail].Reasons);
        Assert.Contains(samePhone.Id, byId.Keys);
        Assert.Contains("Same phone number", byId[samePhone.Id].Reasons);
        Assert.Equal("High", byId[samePhone.Id].Confidence);
        Assert.Contains(fuzzy.Id, byId.Keys);
        Assert.Contains(byId[fuzzy.Id].Reasons, r => r.StartsWith("Similar name", StringComparison.Ordinal));
        Assert.True(byId[samePhone.Id].Score > byId[fuzzy.Id].Score);
        Assert.DoesNotContain(unrelated.Id, byId.Keys); // same name, different customer, nothing else shared

        var pairs = await ReadAsync<List<DuplicatePairDto>>(await admin.GetAsync($"/api/v1/contacts/duplicates?customerId={customer.Id}"));
        Assert.Contains(pairs, p => (p.First.Id == ramesh.Id && p.Second.Id == samePhone.Id) || (p.First.Id == samePhone.Id && p.Second.Id == ramesh.Id));

        // New contacts cannot reuse an e-mail identity: the API points to the existing contact instead.
        var clash = await admin.PostAsJsonAsync("/api/v1/contacts", new SaveContactRequest { FullName = "Clash", Email = ramesh.Email, CustomerId = customer.Id }, J);
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
    }

    [Fact]
    public async Task Contact_merge_moves_tickets_and_is_idempotent()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var customer = await CreateCustomerAsync(admin, "Merge Co");
        var survivor = await CreateContactAsync(admin, customer.Id, "Kavya Reddy", $"kavya.{Guid.NewGuid():N}@merge.test", "+91 98000 11111");
        var mergedEmail = $"kavya.personal.{Guid.NewGuid():N}@mail.test";
        var merged = await CreateContactAsync(admin, customer.Id, "Kavya R", mergedEmail, null);
        await ReadAsync<ContactConsentDto>(await admin.PutAsJsonAsync($"/api/v1/contacts/{merged.Id}/consents",
            new SetConsentRequest("WhatsApp", "Marketing", "OptedIn", "test"), J));
        var t1 = await ReadAsync<TicketDetailDto>(await admin.PostAsJsonAsync("/api/v1/tickets",
            new CreateTicketRequest { Subject = "From the duplicate", Channel = "Email", ContactId = merged.Id, CustomerId = customer.Id, SkipRouting = true }, J));
        var t2 = await ReadAsync<TicketDetailDto>(await admin.PostAsJsonAsync("/api/v1/tickets",
            new CreateTicketRequest { Subject = "From the survivor", Channel = "Phone", ContactId = survivor.Id, CustomerId = customer.Id, SkipRouting = true }, J));

        // Merging requires contacts.merge: a regular agent cannot.
        var (_, agentEmail) = await t.CreateAgentAsync("Plain Agent");
        var agent = await t.AgentClientAsync(agentEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync("/api/v1/contacts/merge", new MergeContactsRequest(survivor.Id, merged.Id), J)).StatusCode);

        var result = await ReadAsync<ContactMergeResultDto>(await admin.PostAsJsonAsync("/api/v1/contacts/merge", new MergeContactsRequest(survivor.Id, merged.Id), J));
        Assert.False(result.AlreadyMerged);
        Assert.Equal(1, result.Tickets);
        Assert.True(result.Identities >= 1);

        var moved = await admin.GetFromJsonAsync<TicketDetailDto>($"/api/v1/tickets/{t1.Id}", J);
        Assert.Equal(survivor.Id, moved!.ContactId);
        var untouched = await admin.GetFromJsonAsync<TicketDetailDto>($"/api/v1/tickets/{t2.Id}", J);
        Assert.Equal(survivor.Id, untouched!.ContactId);
        var mergedNow = await ReadAsync<ContactDetailDto>(await admin.GetAsync($"/api/v1/contacts/{merged.Id}"));
        Assert.Equal("Merged", mergedNow.Status);
        Assert.Equal(survivor.Id, mergedNow.MergedIntoContactId);
        var survivorNow = await ReadAsync<ContactDetailDto>(await admin.GetAsync($"/api/v1/contacts/{survivor.Id}"));
        Assert.Contains(survivorNow.Identities, i => i.Channel == "Email" && i.Identifier == mergedEmail);
        Assert.Contains(survivorNow.Consents, c => c.Channel == "WhatsApp" && c.Purpose == "Marketing" && c.Status == "OptedIn");
        Assert.Equal(2, survivorNow.Stats.TotalTickets);

        // Repeating the merge (e.g. a retried request) changes nothing and reports the original outcome.
        var again = await ReadAsync<ContactMergeResultDto>(await admin.PostAsJsonAsync("/api/v1/contacts/merge", new MergeContactsRequest(survivor.Id, merged.Id), J));
        Assert.True(again.AlreadyMerged);
        Assert.Equal(1, again.Tickets);
        Assert.Equal(result.MergeLogId, again.MergeLogId);

        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            Assert.Equal(1, await db.ContactMergeLogs.CountAsync(l => l.MergedContactId == merged.Id));
            Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == AuditActions.ContactMerged && a.EntityId == survivor.Id));
            // Inbound mail from the merged address now resolves to the survivor.
            var resolved = await sp.GetRequiredService<SupportDesk.Application.Customers.ContactResolver>().FindAsync(Channel.Email, mergedEmail);
            Assert.Equal(survivor.Id, resolved!.Id);
        });
    }

    // ───────────────────────────── Contracts & eligibility ─────────────────────────────

    [Fact]
    public async Task Contract_expiry_flips_eligibility_of_new_and_open_tickets()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var customer = await CreateCustomerAsync(admin, "Expiring AMC Co");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var contract = await ReadAsync<ContractDetailDto>(await admin.PostAsJsonAsync("/api/v1/contracts", new SaveContractRequest
        {
            CustomerId = customer.Id, Name = "AMC ending soon", Type = "Amc", StartDate = today.AddMonths(-11), EndDate = today.AddDays(5), ContractValue = 100_000m
        }, J));
        Assert.StartsWith("AMC-", contract.Summary.Number);

        var preview = await ReadAsync<EligibilityPreviewDto>(await admin.GetAsync($"/api/v1/contracts/eligibility?customerId={customer.Id}"));
        Assert.Equal("Entitled", preview.Status);
        Assert.Equal(contract.Summary.Id, preview.ContractId);
        var before = await ReadAsync<TicketDetailDto>(await admin.PostAsJsonAsync("/api/v1/tickets",
            new CreateTicketRequest { Subject = "Covered by the AMC", Channel = "Phone", CustomerId = customer.Id, SkipRouting = true }, J));
        Assert.Equal("Entitled", before.EntitlementStatus);

        var clock = factory.Clock;
        try
        {
            clock.Pin(DateTime.UtcNow.AddDays(10));
            await factory.RunRecurringAsync(ContractLifecycleTask.TaskName);
            await factory.DrainJobsAsync();

            await factory.InTenantAsync(t.Id, async sp =>
            {
                var db = sp.GetRequiredService<DeskDbContext>();
                Assert.Equal(ContractStatus.Expired, (await db.Contracts.FirstAsync(c => c.Id == contract.Summary.Id)).Status);
                // The open ticket was re-evaluated by the background job.
                var open = await db.Tickets.FirstAsync(x => x.Id == before.Id);
                Assert.Equal(EntitlementStatus.Expired, open.EntitlementStatus);
                // New tickets are no longer entitled.
                var created = await sp.GetRequiredService<TicketService>().CreateAsync(new CreateTicketRequest
                {
                    Subject = "After expiry", Channel = "Email", CustomerId = customer.Id, SkipRouting = true
                }, new TicketCreationSource(EnforceRequiredFields: false), default);
                Assert.Equal(EntitlementStatus.Expired, created.EntitlementStatus);
                var later = await sp.GetRequiredService<ContractService>().PreviewEligibilityAsync(customer.Id, null, null, null, default);
                Assert.Equal("Expired", later.Status);
            });
        }
        finally
        {
            clock.Unpin();
        }
    }

    // ───────────────────────────── Wings ERP ─────────────────────────────

    private async Task<(Guid ErpCustomerId, Guid CustomerId)> ErpTenantAsync(TestTenant t)
    {
        var erpId = Guid.NewGuid();
        var customerId = await factory.InTenantAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<ErpIntegrationService>().SaveSettingsAsync(ErpSystems.WingsErp, new SaveErpIntegrationRequest { Mode = "simulator" }, default);
            var db = sp.GetRequiredService<DeskDbContext>();
            var c = new Customer { Name = "ERP Linked Retail", Code = "C-7001", ErpCustomerId = erpId, ErpCode = "C-7001", Tier = "Gold" };
            db.Customers.Add(c);
            db.Contacts.Add(new Contact { CustomerId = c.Id, FullName = "Finance Head", Email = $"fin.{Guid.NewGuid():N}@erp.test", IsPrimary = true });
            await db.SaveChangesAsync();
            return c.Id;
        });
        return (erpId, customerId);
    }

    [Fact]
    public async Task Erp_ticket_creation_requires_tickets_write_and_keeps_the_origin_reference()
    {
        var t = await factory.CreateTenantAsync();
        var (erpCustomerId, customerId) = await ErpTenantAsync(t);
        var writer = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<ApiKeyService>().CreateAsync("Wings ERP", ["tickets:write"], null, null, default));
        var reader = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<ApiKeyService>().CreateAsync("BI", ["tickets:read"], null, null, default));
        var invoiceId = Guid.NewGuid();
        var request = new CreateErpTicketRequest
        {
            System = "WingsErp", RecordType = "SalesInvoice", RecordId = invoiceId, RecordNumber = "SI/26-27/04512", CustomerErpId = erpCustomerId,
            Subject = "GST total does not match", Description = "CGST + SGST shows 18,450.36 but the grand total is off by 0.64.", Priority = "High",
            ContactEmail = $"ramesh.{Guid.NewGuid():N}@erp.test", ContactName = "Ramesh Kumar",
            Context = JsonSerializer.SerializeToElement(new { screen = "Sales / Invoices", branch = "Chennai HO", total = 120_450.36m }), ClientRequestId = "inv-04512-1"
        };

        var readOnly = factory.CreateClient();
        readOnly.DefaultRequestHeaders.Add("X-Api-Key", reader.Key);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync("/api/v1/integrations/erp/tickets", request, J)).StatusCode);

        var erp = factory.CreateClient();
        erp.DefaultRequestHeaders.Add("X-Api-Key", writer.Key);
        var response = await erp.PostAsJsonAsync("/api/v1/integrations/erp/tickets", request, J);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadAsync<ErpTicketCreatedDto>(response);
        Assert.Equal(customerId, created.CustomerId);
        Assert.Contains($"/tickets/{created.TicketId}", created.PortalUrl);

        // A retry with the same client request id returns the same ticket.
        var retry = await erp.PostAsJsonAsync("/api/v1/integrations/erp/tickets", request, J);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(created.TicketId, (await ReadAsync<ErpTicketCreatedDto>(retry)).TicketId);

        // Unknown customers are rejected (the simulator does not know this id either).
        var unknown = await erp.PostAsJsonAsync("/api/v1/integrations/erp/tickets", request with { CustomerErpId = Guid.NewGuid(), ClientRequestId = null }, J);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var ticket = await db.Tickets.FirstAsync(x => x.Id == created.TicketId);
            Assert.Equal(Channel.Erp, ticket.Channel);
            Assert.Equal(TicketPriority.High, ticket.Priority);
            Assert.NotNull(ticket.ContactId);
            var origin = await db.TicketExternalReferences.SingleAsync(r => r.TicketId == ticket.Id && r.IsOrigin);
            Assert.Equal("WingsErp", origin.System);
            Assert.Equal("SalesInvoice", origin.RecordType);
            Assert.Equal(invoiceId, origin.RecordId);
            Assert.Equal("SI/26-27/04512", origin.RecordNumber);
            Assert.Contains("Sales / Invoices", origin.ContextJson);
            Assert.Equal(1, await db.Tickets.CountAsync(x => x.Subject == "GST total does not match"));
            Assert.True(await db.IntegrationLogs.AnyAsync(l => l.Operation == "ticket.create" && l.Success));
        });
    }

    [Fact]
    public async Task Erp_sync_upserts_without_duplicates_on_rerun()
    {
        var t = await factory.CreateTenantAsync();
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var erpProduct = new Product { Name = "Wings ERP", Code = "WERP" };
            db.Products.Add(erpProduct);
            foreach (var v in new[] { "7.2", "7.3", "8.0" }) db.ProductVersions.Add(new ProductVersion { ProductId = erpProduct.Id, Version = v });
            foreach (var m in new[] { "Finance", "Inventory", "Sales" }) db.ProductModules.Add(new ProductModule { ProductId = erpProduct.Id, Name = m });
            // An existing desk customer with the ERP code but no ERP id must be linked, not duplicated.
            db.Customers.Add(new Customer { Name = "Horizon Agro (desk)", Code = "C-1009", Tier = "Gold" });
            await db.SaveChangesAsync();
            await sp.GetRequiredService<ErpIntegrationService>().SaveSettingsAsync(ErpSystems.WingsErp, new SaveErpIntegrationRequest { Mode = "simulator" }, default);
        });
        var admin = await t.AdminClientAsync();

        var first = await ReadAsync<ErpSyncResultDto>(await admin.PostAsync("/api/v1/integrations/erp/sync", null));
        Assert.True(first.Success, first.Error);
        var customers1 = first.Entities.Single(e => e.Entity == "Customers");
        Assert.Equal(1, customers1.Created); // Vista Builders is new
        Assert.Equal(1, customers1.Updated); // Horizon Agro was linked by its code
        Assert.True(first.Entities.Single(e => e.Entity == "Installations").Created >= 2);
        Assert.True(first.Entities.Single(e => e.Entity == "Contracts").Created >= 4);

        async Task<(int Customers, int Installations, int Licences, int Contracts, int Locations)> CountsAsync() =>
            await factory.InTenantAsync(t.Id, async sp =>
            {
                var db = sp.GetRequiredService<DeskDbContext>();
                return (await db.Customers.CountAsync(), await db.Installations.CountAsync(), await db.Licences.CountAsync(), await db.Contracts.CountAsync(),
                    await db.CustomerLocations.CountAsync());
            });
        var afterFirst = await CountsAsync();
        Assert.Equal(2, afterFirst.Customers);

        var second = await ReadAsync<ErpSyncResultDto>(await admin.PostAsync("/api/v1/integrations/erp/sync", null));
        Assert.True(second.Success, second.Error);
        Assert.All(second.Entities, e => Assert.Equal(0, e.Created));
        Assert.All(second.Entities, e => Assert.Equal(0, e.Updated));
        Assert.Equal(afterFirst, await CountsAsync());

        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var horizon = await db.Customers.SingleAsync(c => c.Code == "C-1009");
            Assert.Equal("Horizon Agro (desk)", horizon.Name); // desk-owned field untouched
            Assert.NotNull(horizon.ErpCustomerId);
            Assert.Equal(2, await db.IntegrationLogs.CountAsync(l => l.Operation == "sync" && l.Success));
        });

        var logs = await ReadAsync<List<IntegrationLogDto>>(await admin.GetAsync("/api/v1/integrations/erp/logs?operation=sync"));
        Assert.Equal(2, logs.Count);
        // The integration settings endpoint is admin-only.
        var (_, agentEmail) = await t.CreateAgentAsync("No Admin");
        var agent = await t.AgentClientAsync(agentEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsync("/api/v1/integrations/erp/sync", null)).StatusCode);
    }
}
