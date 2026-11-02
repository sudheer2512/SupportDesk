using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Compliance;
using SupportDesk.Contracts.Platform;
using SupportDesk.Contracts.Subscriptions;
using SupportDesk.Domain;
using SupportDesk.Domain.Ai;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Tickets;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;

namespace SupportDesk.IntegrationTests.Subscriptions;

[Collection("api")]
public sealed class ComplianceTests(DeskApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions J = SaasTestKit.J;

    private Task CloseAsync(Guid tenantId, Guid ticketId, int daysAgo) => factory.InTenantAsync(tenantId, sp =>
        sp.GetRequiredService<DeskDbContext>().Tickets.Where(t => t.Id == ticketId).ExecuteUpdateAsync(u => u
            .SetProperty(t => t.StatusCategory, StatusCategory.Closed).SetProperty(t => t.ClosedAt, DateTime.UtcNow.AddDays(-daysAgo))));

    [Fact]
    public async Task Retention_purges_only_eligible_tickets_and_honours_legal_holds()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var (_, _, old) = await factory.ContactWithTicketAsync(t, admin, "Old Owen");
        var (_, _, held) = await factory.ContactWithTicketAsync(t, admin, "Held Hana");
        var (_, _, recent) = await factory.ContactWithTicketAsync(t, admin, "Recent Rita");
        await CloseAsync(t.Id, old.Id, 400);
        await CloseAsync(t.Id, held.Id, 400);
        await CloseAsync(t.Id, recent.Id, 10);

        var hold = await (await admin.PostAsJsonAsync("/api/v1/admin/compliance/legal-holds",
            new SaveLegalHoldRequest("Matter 7", "Litigation hold for matter 7", TicketNumbers: [held.Number]), J)).ReadAsync<LegalHoldDto>();
        Assert.Equal(1, hold.CoveredTickets);

        var policy = await (await admin.PostAsJsonAsync("/api/v1/admin/compliance/retention",
            new SaveRetentionPolicyRequest("Delete closed tickets after a year", "ClosedTickets", "Delete", 365), J)).ReadAsync<RetentionPolicyDto>();
        var dry = await (await admin.PostAsync($"/api/v1/admin/compliance/retention/{policy.Id}/dry-run", null)).ReadAsync<RetentionDryRunDto>();
        Assert.Equal(1, dry.Eligible);
        Assert.Equal(1, dry.HeldBack);
        var tooShort = await admin.PostAsJsonAsync("/api/v1/admin/compliance/retention", new SaveRetentionPolicyRequest("x", "AuditLogs", "Delete", 30), J);
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        var run = await (await admin.PostAsync($"/api/v1/admin/compliance/retention/{policy.Id}/run", null)).ReadAsync<RetentionRunResultDto>();
        Assert.Equal(1, run.Affected);
        Assert.Equal(1, run.HeldBack);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/tickets/{old.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/tickets/{held.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/tickets/{recent.Id}")).StatusCode);
        var remaining = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().Tickets.IgnoreQueryFilters().CountAsync(x => x.TenantId == t.Id));
        Assert.Equal(2, remaining);

        // Releasing the hold makes the held ticket eligible.
        await (await admin.PostAsync($"/api/v1/admin/compliance/legal-holds/{hold.Id}/release", null)).ReadAsync<LegalHoldDto>();
        var again = await (await admin.PostAsync($"/api/v1/admin/compliance/retention/{policy.Id}/dry-run", null)).ReadAsync<RetentionDryRunDto>();
        Assert.Equal(1, again.Eligible);
        Assert.Equal(0, again.HeldBack);
    }

    [Fact]
    public async Task Ai_log_and_message_retention_counts_and_runs_while_a_legal_hold_exists()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var (_, _, held) = await factory.ContactWithTicketAsync(t, admin, "Held Hugo");
        var (_, _, free) = await factory.ContactWithTicketAsync(t, admin, "Free Fiona");
        await (await admin.PostAsJsonAsync("/api/v1/admin/compliance/legal-holds",
            new SaveLegalHoldRequest("Matter 9", "Litigation hold for matter 9", TicketNumbers: [held.Number]), J)).ReadAsync<LegalHoldDto>();
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            AiExecution Log(string? entityType, Guid? entityId) => new()
            {
                Feature = "summary", Provider = "Test", Model = "test", EntityType = entityType, EntityId = entityId,
                Status = AiExecutionStatus.Succeeded, OutputJson = "{}", CreatedAt = DateTime.UtcNow.AddDays(-200)
            };
            db.AiExecutions.AddRange(Log("Ticket", held.Id), Log("Ticket", free.Id), Log(null, null));
            await db.SaveChangesAsync();
        });

        var aiPolicy = await (await admin.PostAsJsonAsync("/api/v1/admin/compliance/retention",
            new SaveRetentionPolicyRequest("Purge AI execution logs after 180 days", "AiLogs", "Delete", 180), J)).ReadAsync<RetentionPolicyDto>();
        var dry = await (await admin.PostAsync($"/api/v1/admin/compliance/retention/{aiPolicy.Id}/dry-run", null)).ReadAsync<RetentionDryRunDto>();
        Assert.Equal(2, dry.Eligible);
        Assert.Equal(1, dry.HeldBack);
        var run = await (await admin.PostAsync($"/api/v1/admin/compliance/retention/{aiPolicy.Id}/run", null)).ReadAsync<RetentionRunResultDto>();
        Assert.Equal(2, run.Affected);
        Assert.Equal(1, run.HeldBack);
        var left = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().AiExecutions.Select(a => a.EntityId).ToListAsync());
        Assert.Equal([held.Id], left);

        // Message and attachment dry runs filter on the ticket id too.
        foreach (var type in new[] { "Messages", "Attachments" })
        {
            var policy = await (await admin.PostAsJsonAsync("/api/v1/admin/compliance/retention",
                new SaveRetentionPolicyRequest($"{type} after a month", type, "Delete", 30), J)).ReadAsync<RetentionPolicyDto>();
            var response = await admin.PostAsync($"/api/v1/admin/compliance/retention/{policy.Id}/dry-run", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Erasure_is_refused_under_legal_hold_then_approved_anonymises_and_keeps_metrics()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var (customerId, contactId, ticket) = await factory.ContactWithTicketAsync(t, admin, "Erasable Eve");
        var hold = await (await admin.PostAsJsonAsync("/api/v1/admin/compliance/legal-holds",
            new SaveLegalHoldRequest("Customer audit", "Regulatory audit of the customer", CustomerId: customerId), J)).ReadAsync<LegalHoldDto>();
        var refused = await admin.PostAsJsonAsync("/api/v1/admin/compliance/requests", new CreateDataRequestRequest("Erasure", contactId, "Customer asked by e-mail"), J);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("legal_hold", await refused.CodeAsync());
        await (await admin.PostAsync($"/api/v1/admin/compliance/legal-holds/{hold.Id}/release", null)).ReadAsync<LegalHoldDto>();

        // Four eyes: a second compliance administrator must approve the requester's erasure.
        var (_, secondEmail) = await t.CreateAgentAsync("Second Admin", "Administrator");
        var second = await t.AgentClientAsync(secondEmail);
        var request = await (await admin.PostAsJsonAsync("/api/v1/admin/compliance/requests", new CreateDataRequestRequest("Erasure", contactId, "Verified identity"), J))
            .ReadAsync<DataRequestDto>();
        Assert.Equal("Pending", request.Status);
        var self = await admin.PostAsync($"/api/v1/admin/compliance/requests/{request.Id}/approve", null);
        Assert.Equal("four_eyes_required", await self.CodeAsync());
        var approved = await (await second.PostAsync($"/api/v1/admin/compliance/requests/{request.Id}/approve", null)).ReadAsync<DataRequestDto>();
        Assert.Equal("Processing", approved.Status);
        await factory.DrainJobsAsync();
        await factory.DrainOutboxAsync();

        var done = await (await admin.GetAsync($"/api/v1/admin/compliance/requests/{request.Id}")).ReadAsync<DataRequestDto>();
        Assert.Equal("Completed", done.Status);
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var contact = await db.Contacts.IgnoreQueryFilters().FirstAsync(c => c.Id == contactId);
            Assert.Equal(ContactStatus.Erased, contact.Status);
            Assert.StartsWith("Erased contact", contact.FullName);
            Assert.Null(contact.Email);
            Assert.Null(contact.Phone);
            var kept = await db.Tickets.FirstAsync(x => x.Id == ticket.Id);
            Assert.Equal(ticket.Number, kept.Number);
            Assert.Equal(contactId, kept.ContactId);
            Assert.DoesNotContain("98450", kept.DescriptionText ?? "");
            Assert.Equal(1, await db.Tickets.CountAsync(x => x.ContactId == contactId));
        });
    }

    [Fact]
    public async Task Data_export_job_completes_and_the_link_works_once()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var (_, contactId, ticket) = await factory.ContactWithTicketAsync(t, admin, "Exporting Ezra");
        var request = await (await admin.PostAsJsonAsync("/api/v1/admin/compliance/requests", new CreateDataRequestRequest("Export", contactId), J)).ReadAsync<DataRequestDto>();
        Assert.Equal("Processing", request.Status);
        await factory.DrainJobsAsync();
        var done = await (await admin.GetAsync($"/api/v1/admin/compliance/requests/{request.Id}")).ReadAsync<DataRequestDto>();
        Assert.Equal("Completed", done.Status);
        Assert.True(done.CanDownload);

        var link = await (await admin.PostAsync($"/api/v1/admin/compliance/requests/{request.Id}/download-link", null)).ReadAsync<DownloadLinkDto>();
        var anonymous = factory.CreateClient();
        var download = await anonymous.GetAsync(link.Url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        using (var zip = new ZipArchive(await download.Content.ReadAsStreamAsync()))
        {
            Assert.Contains(zip.Entries, e => e.FullName == "profile.json");
            using var reader = new StreamReader(zip.GetEntry("tickets.json")!.Open());
            Assert.Contains(ticket.Subject, await reader.ReadToEndAsync());
        }
        Assert.Equal(HttpStatusCode.Gone, (await anonymous.GetAsync(link.Url)).StatusCode);
        var after = await (await admin.GetAsync($"/api/v1/admin/compliance/requests/{request.Id}")).ReadAsync<DataRequestDto>();
        Assert.False(after.CanDownload);
        Assert.NotNull(after.DownloadedAt);
    }

    [Fact]
    public async Task Sign_ins_are_attributed_to_the_person_and_stay_out_of_platform_activity()
    {
        var t = await factory.CreateTenantAsync();
        await t.AdminClientAsync();
        var platform = await factory.PlatformClientAsync();
        var logins = await factory.InCatalogAsync(db => db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.Action == "auth.login" && (a.TenantId == t.Id || a.EntityType == "PlatformOperator")).ToListAsync());

        var desk = Assert.Single(logins, a => a.TenantId == t.Id);
        Assert.Equal(ActorType.User, desk.ActorType);
        Assert.Equal(desk.EntityId, desk.ActorId);
        Assert.False(string.IsNullOrEmpty(desk.ActorName));
        Assert.All(logins.Where(a => a.EntityType == "PlatformOperator"), a =>
        {
            Assert.Equal(ActorType.Platform, a.ActorType);
            Assert.Equal(a.EntityId, a.ActorId);
        });
        var detail = await (await platform.GetAsync($"/api/v1/platform/tenants/{t.Id}")).ReadAsync<PlatformTenantDetailDto>();
        Assert.DoesNotContain(detail.RecentActivity, a => a.Action.StartsWith("auth.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Audit_chain_verification_detects_tampering_with_sealed_entries()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        await (await admin.PutAsJsonAsync("/api/v1/subscription/billing-contact", new UpdateBillingContactRequest("Ops", "ops@example.test", null, null), J))
            .ReadAsync<SubscriptionDto>();
        await (await admin.PutAsJsonAsync("/api/v1/subscription/billing-contact", new UpdateBillingContactRequest("Finance", "fin@example.test", null, null), J))
            .ReadAsync<SubscriptionDto>();
        await factory.RunRecurringAsync("audit-sealer");
        var ok = await (await admin.PostAsync("/api/v1/admin/audit/verify", null)).ReadAsync<AuditChainVerificationDto>();
        Assert.True(ok.Valid, ok.Message);
        Assert.True(ok.Checked >= 3);

        var entries = await (await admin.GetAsync("/api/v1/admin/audit?action=subscription.changed")).ReadAsync<PagedResult<AuditEntryDto>>();
        var detail = await (await admin.GetAsync($"/api/v1/admin/audit/{entries.Items[0].Id}")).ReadAsync<AuditEntryDetailDto>();
        Assert.Contains(detail.Diff, d => d.Path.Contains("billingContactName", StringComparison.OrdinalIgnoreCase) && d.Before == "Ops" && d.Kind == "changed");
        var csv = await admin.GetAsync("/api/v1/admin/audit/export");
        Assert.Contains("Sequence,OccurredAtUtc", await csv.Content.ReadAsStringAsync());

        // Tamper with sealed entry #2 through a privileged bypass of the protection trigger.
        await using (var conn = new SqlConnection(factory.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXEC sp_set_session_context N'audit_purge', 1; UPDATE dbo.AuditLogs SET Summary = N'nothing to see' WHERE TenantId = @t AND [Sequence] = 2";
            cmd.Parameters.AddWithValue("@t", t.Id);
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }
        var broken = await (await admin.PostAsync("/api/v1/admin/audit/verify", null)).ReadAsync<AuditChainVerificationDto>();
        Assert.False(broken.Valid);
        Assert.Equal(2, broken.BrokenAtSequence);
    }
}
