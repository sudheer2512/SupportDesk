using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Communication;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Communication;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Tickets;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;

namespace SupportDesk.IntegrationTests.Tickets;

/// <summary>
/// Ticket workbench lifecycle: close after resolve (can.close), "notify the customer" on resolve, clone permission,
/// entitlement override setting, recycle bin (deleted list + restore, legal holds) and the default saved view.
/// </summary>
[Collection("api")]
public sealed class TicketLifecycleFixTests(DeskApiFactory factory)
{
    private static readonly JsonSerializerOptions J = DeskApiFactory.Json;

    // ───────────────────────────── helpers ─────────────────────────────

    private static async Task<T> Ok<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(J))!;
    }

    private static async Task<TicketDetailDto> CreateTicketAsync(HttpClient client, string subject = "Lifecycle test") =>
        await Ok<TicketDetailDto>(await client.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest { Subject = subject, Channel = "Phone", SkipRouting = true }, J));

    private Task<T> Db<T>(TestTenant t, Func<DeskDbContext, Task<T>> query) => factory.InTenantAsync(t.Id, sp => query(sp.GetRequiredService<DeskDbContext>()));

    /// <summary>A user whose only role grants exactly <paramref name="permissions"/> (all tickets in scope).</summary>
    private async Task<HttpClient> UserWithPermissionsAsync(TestTenant t, params string[] permissions)
    {
        var email = $"custom.{Guid.NewGuid():N}"[..20] + $"@{t.Slug}.test";
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var role = new Role { Name = $"Custom {Guid.NewGuid():N}"[..20], TicketScope = TicketScope.All };
            role.Permissions.AddRange(permissions.Select(p => new RolePermission { PermissionCode = p }));
            db.Roles.Add(role);
            var user = new User
            {
                Email = email, EmailNormalized = email, DisplayName = "Custom user", Kind = UserKind.Agent,
                PasswordHash = sp.GetRequiredService<PasswordService>().Hash(DeskApiFactory.Password)
            };
            db.Users.Add(user);
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
            db.Agents.Add(new Agent { UserId = user.Id, Availability = AgentAvailability.Online });
            db.AgentDepartments.Add(new AgentDepartment { UserId = user.Id, DepartmentId = t.SupportDepartmentId });
            await db.SaveChangesAsync();
        });
        return await t.AgentClientAsync(email);
    }

    private static async Task<(Guid TicketId, Guid ChannelId)> EmailTicketAsync(HttpClient admin, TestTenant t)
    {
        var mailbox = await Ok<ChannelAccountDto>(await admin.PostAsJsonAsync("/api/v1/channels", new CreateChannelAccountRequest
        {
            Type = "Email", Provider = ChannelProviders.FileDrop, Name = "Support mailbox", Address = $"support@{t.Slug}.test",
            Settings = JsonSerializer.SerializeToElement(new { pollIntervalSeconds = 0 })
        }, J));
        var created = await Ok<InboundResultDto>(await admin.PostAsJsonAsync($"/api/v1/channels/{mailbox.Id}/simulator/email", new SimulateEmailRequest
        {
            From = $"buyer.{Guid.NewGuid():N}"[..14] + "@customer.example", FromName = "Asha Rao", Subject = "Receipt printer offline", Text = "It stopped printing."
        }, J));
        return (created.TicketId!.Value, mailbox.Id);
    }

    // ───────────────────────────── close after resolve ─────────────────────────────

    [Fact]
    public async Task Resolved_tickets_can_still_be_closed_and_the_close_flag_follows_the_state()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var ticket = await CreateTicketAsync(admin);
        Assert.True(ticket.Can.Close);
        Assert.True(ticket.Can.Resolve);

        var resolved = await Ok<TicketDetailDto>(await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/resolve",
            new ResolveTicketRequest("fixed", null, null, NotifyCustomer: false), J));
        Assert.Equal("Resolved", resolved.StatusCategory);
        Assert.False(resolved.Can.Resolve);
        Assert.True(resolved.Can.Close);

        var closed = await Ok<TicketDetailDto>(await admin.PostAsync($"/api/v1/tickets/{ticket.Id}/close", null));
        Assert.Equal("Closed", closed.StatusCategory);
        Assert.False(closed.Can.Close);
        Assert.True(closed.Can.Reopen);

        // Without tickets.resolve the flag is off and the API refuses.
        var viewer = await UserWithPermissionsAsync(t, "tickets.view", "tickets.edit");
        var other = await CreateTicketAsync(admin, "Viewer cannot close");
        Assert.False((await viewer.GetFromJsonAsync<TicketDetailDto>($"/api/v1/tickets/{other.Id}", J))!.Can.Close);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/v1/tickets/{other.Id}/close", null)).StatusCode);
    }

    // ───────────────────────────── notify the customer on resolve ─────────────────────────────

    [Fact]
    public async Task Resolve_with_notify_sends_the_resolution_notice_and_a_portal_entry()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var (ticketId, _) = await EmailTicketAsync(admin, t);
        await factory.DrainOutboxAsync();

        await Ok<TicketDetailDto>(await admin.PostAsJsonAsync($"/api/v1/tickets/{ticketId}/resolve",
            new ResolveTicketRequest("fixed", null, "<p>Reinstalled the <strong>printer driver</strong>.</p>", NotifyCustomer: true), J));
        await factory.DrainOutboxAsync();
        await factory.DrainOutboxAsync(); // idempotent: nothing new on a second pass

        var (notices, notice, threads, surveys) = await Db(t, async db => (
            await db.Messages.CountAsync(m => m.TicketId == ticketId && m.Direction == MessageDirection.Outbound && m.MetadataJson!.Contains("\"kind\":\"resolution\"")),
            await db.Messages.Where(m => m.TicketId == ticketId && m.MetadataJson!.Contains("\"kind\":\"resolution\"")).Select(m => m.BodyHtml).FirstOrDefaultAsync(),
            await db.TicketThreads.Where(x => x.TicketId == ticketId && x.Kind == ThreadKind.SystemMessage && x.Visibility == Visibility.Public).ToListAsync(),
            await db.SatisfactionResponses.CountAsync(r => r.TicketId == ticketId)));
        Assert.Equal(1, notices);
        Assert.Contains("printer driver", notice);
        var entry = Assert.Single(threads);
        Assert.Contains("has been resolved", entry.BodyHtml);
        Assert.Contains("printer driver", entry.BodyHtml);
        Assert.Equal(1, surveys);
    }

    [Fact]
    public async Task Resolve_without_notify_sends_nothing_to_the_customer()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var (ticketId, _) = await EmailTicketAsync(admin, t);
        await factory.DrainOutboxAsync();
        var outboundBefore = await Db(t, db => db.Messages.CountAsync(m => m.TicketId == ticketId && m.Direction == MessageDirection.Outbound));

        await Ok<TicketDetailDto>(await admin.PostAsJsonAsync($"/api/v1/tickets/{ticketId}/resolve",
            new ResolveTicketRequest("fixed", null, "<p>Fixed quietly.</p>", NotifyCustomer: false), J));
        await factory.DrainOutboxAsync();

        var (outboundAfter, systemEntries, surveys) = await Db(t, async db => (
            await db.Messages.CountAsync(m => m.TicketId == ticketId && m.Direction == MessageDirection.Outbound),
            await db.TicketThreads.CountAsync(x => x.TicketId == ticketId && x.Kind == ThreadKind.SystemMessage),
            await db.SatisfactionResponses.CountAsync(r => r.TicketId == ticketId)));
        Assert.Equal(outboundBefore, outboundAfter);
        Assert.Equal(0, systemEntries);
        Assert.Equal(0, surveys);
    }

    // ───────────────────────────── clone permission ─────────────────────────────

    [Fact]
    public async Task Clone_needs_tickets_create_not_tickets_split()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var source = await CreateTicketAsync(admin, "Clone me");

        var creator = await UserWithPermissionsAsync(t, "tickets.view", "tickets.create");
        var clone = await Ok<TicketDetailDto>(await creator.PostAsJsonAsync($"/api/v1/tickets/{source.Id}/clone", new { }, J));
        Assert.Equal("Clone me (copy)", clone.Subject);
        Assert.Contains(clone.Links, l => l.LinkType == "ClonedFrom" && l.TicketId == source.Id);

        var splitter = await UserWithPermissionsAsync(t, "tickets.view", "tickets.split");
        Assert.Equal(HttpStatusCode.Forbidden, (await splitter.PostAsJsonAsync($"/api/v1/tickets/{source.Id}/clone", new { }, J)).StatusCode);
    }

    // ───────────────────────────── entitlement override setting ─────────────────────────────

    [Fact]
    public async Task Agents_with_edit_may_override_entitlement_only_while_the_tenant_setting_allows_it()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var (_, agentEmail) = await t.CreateAgentAsync("Override Agent");
        var agent = await t.AgentClientAsync(agentEmail);
        var ticket = await CreateTicketAsync(admin);

        // Default: AllowAgentsToOverrideEntitlement = true → an agent with tickets.edit (no override permission) may override.
        Assert.True((await agent.GetFromJsonAsync<TicketDetailDto>($"/api/v1/tickets/{ticket.Id}", J))!.Can.OverrideEntitlement);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/override-entitlement", new OverrideEntitlementRequest(" ", null), J)).StatusCode);
        var overridden = await Ok<TicketDetailDto>(await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/override-entitlement",
            new OverrideEntitlementRequest("Goodwill for a key account", null), J));
        Assert.Equal("Overridden", overridden.EntitlementStatus);
        var audited = await Db(t, db => db.AuditLogs.AnyAsync(a => a.Action == AuditActions.EntitlementOverridden && a.EntityId == ticket.Id));
        Assert.True(audited);

        // Setting off → only tickets.override_entitlement allows it.
        await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<TenantSettingsService>()
            .SetAsync(SettingKeys.Tickets, new TicketSettings { AllowAgentsToOverrideEntitlement = false }));
        var second = await CreateTicketAsync(admin, "Second");
        Assert.False((await agent.GetFromJsonAsync<TicketDetailDto>($"/api/v1/tickets/{second.Id}", J))!.Can.OverrideEntitlement);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PostAsJsonAsync($"/api/v1/tickets/{second.Id}/override-entitlement", new OverrideEntitlementRequest("Please", null), J)).StatusCode);

        var (_, supervisorEmail) = await t.CreateAgentAsync("Override Supervisor", "Supervisor");
        var supervisor = await t.AgentClientAsync(supervisorEmail);
        Assert.True((await supervisor.GetFromJsonAsync<TicketDetailDto>($"/api/v1/tickets/{second.Id}", J))!.Can.OverrideEntitlement);
        await Ok<TicketDetailDto>(await supervisor.PostAsJsonAsync($"/api/v1/tickets/{second.Id}/override-entitlement",
            new OverrideEntitlementRequest("Approved by the supervisor", null), J));
    }

    // ───────────────────────────── recycle bin ─────────────────────────────

    [Fact]
    public async Task Deleted_tickets_are_listed_in_the_recycle_bin_and_can_be_restored()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var a = await CreateTicketAsync(admin, "Bulk delete A");
        var b = await CreateTicketAsync(admin, "Bulk delete B");
        var keep = await CreateTicketAsync(admin, "Stays");

        var bulk = await Ok<BulkResultDto>(await admin.PostAsJsonAsync("/api/v1/tickets/bulk", new BulkTicketRequest([a.Id, b.Id], BulkActions.Delete, null), J));
        Assert.Equal(2, bulk.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/tickets/{a.Id}")).StatusCode);

        var bin = await admin.GetFromJsonAsync<PagedResult<DeletedTicketDto>>("/api/v1/tickets/deleted?limit=1", J);
        Assert.Single(bin!.Items);
        Assert.NotNull(bin.NextCursor);
        var page2 = await admin.GetFromJsonAsync<PagedResult<DeletedTicketDto>>($"/api/v1/tickets/deleted?limit=1&cursor={Uri.EscapeDataString(bin.NextCursor!)}", J);
        var all = bin.Items.Concat(page2!.Items).ToList();
        Assert.Equal(new[] { a.Id, b.Id }.Order().ToList(), all.Select(x => x.Id).Order().ToList());
        Assert.DoesNotContain(all, x => x.Id == keep.Id);
        Assert.All(all, x => Assert.NotNull(x.DeletedAt));
        Assert.All(all, x => Assert.False(x.UnderLegalHold));

        // The recycle bin needs tickets.delete.
        var (_, agentEmail) = await t.CreateAgentAsync("Bin Agent");
        var agent = await t.AgentClientAsync(agentEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/v1/tickets/deleted")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsync($"/api/v1/tickets/{a.Id}/restore", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/tickets/{a.Id}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/tickets/{a.Id}")).StatusCode);
        var after = await admin.GetFromJsonAsync<PagedResult<DeletedTicketDto>>("/api/v1/tickets/deleted", J);
        Assert.Equal([b.Id], after!.Items.Select(x => x.Id).ToList());
        var timeline = await admin.GetFromJsonAsync<List<TimelineItemDto>>($"/api/v1/tickets/{a.Id}/timeline", J);
        Assert.Contains(timeline!, i => i.Event?.Type == "Restored");
    }

    [Fact]
    public async Task Legal_holds_block_deletion_and_are_flagged_in_the_recycle_bin()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var held = await CreateTicketAsync(admin, "Held");
        var later = await CreateTicketAsync(admin, "Held after deletion");
        var unrelated = await CreateTicketAsync(admin, "Not held");

        // A hold naming the ticket blocks deletion; a contact-only hold must not cover tickets without a contact.
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            db.LegalHolds.Add(new LegalHold { Name = "Audit 2026", Reason = "Regulator request", TicketIdsJson = JsonSerializer.Serialize(new[] { held.Id }) });
            db.LegalHolds.Add(new LegalHold { Name = "Contact hold", Reason = "Dispute", ContactId = Guid.NewGuid() });
            await db.SaveChangesAsync();
        });
        var refused = await admin.DeleteAsync($"/api/v1/tickets/{held.Id}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("legal_hold", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/tickets/{unrelated.Id}")).StatusCode);

        // Deleted first, put on hold afterwards: the recycle bin flags it (restore still works).
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/tickets/{later.Id}")).StatusCode);
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            db.LegalHolds.Add(new LegalHold { Name = "Late hold", Reason = "Litigation", TicketIdsJson = JsonSerializer.Serialize(new[] { later.Id }) });
            await db.SaveChangesAsync();
        });
        var bin = (await admin.GetFromJsonAsync<PagedResult<DeletedTicketDto>>("/api/v1/tickets/deleted", J))!.Items;
        Assert.True(bin.Single(x => x.Id == later.Id).UnderLegalHold);
        Assert.False(bin.Single(x => x.Id == unrelated.Id).UnderLegalHold);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/tickets/{later.Id}/restore", null)).StatusCode);
    }

    // ───────────────────────────── default saved view ─────────────────────────────

    [Fact]
    public async Task Only_one_personal_view_is_the_default()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var filter = JsonSerializer.SerializeToElement(new { logic = "and", conditions = Array.Empty<object>() });
        var first = await Ok<SavedViewDto>(await admin.PostAsJsonAsync("/api/v1/views", new SaveViewRequest("tickets", "First", "Personal", filter, null, null, true, 0), J));
        Assert.True(first.IsDefault);
        var second = await Ok<SavedViewDto>(await admin.PostAsJsonAsync("/api/v1/views", new SaveViewRequest("tickets", "Second", "Personal", filter, null, null, true, 1), J));
        Assert.True(second.IsDefault);
        var views = await admin.GetFromJsonAsync<List<SavedViewDto>>("/api/v1/views?scope=tickets", J);
        Assert.Equal([second.Id], views!.Where(v => v.IsDefault).Select(v => v.Id).ToList());
    }
}
