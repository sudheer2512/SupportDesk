using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Contracts.Administration;
using SupportDesk.Contracts.Auth;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Organisation;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain.Rules;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;

namespace SupportDesk.IntegrationTests.Administration;

/// <summary>Administration console: roles &amp; permissions, ticket configuration, security policies, API credentials, isolation.</summary>
[Collection("api")]
public sealed class AdministrationTests(DeskApiFactory factory)
{
    private static readonly JsonSerializerOptions J = DeskApiFactory.Json;

    private static async Task<T> Ok<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<T>(J))!;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body)) return null;
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : doc.RootElement.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
    }

    private static async Task<TicketDetailDto> CreateTicketAsync(HttpClient client, Guid? departmentId = null)
    {
        var r = await client.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest { Subject = "Admin console test", Channel = "Phone", DepartmentId = departmentId, SkipRouting = true }, J);
        return await Ok<TicketDetailDto>(r);
    }

    private static async Task<AdminRoleDto> CreateRoleAsync(HttpClient client, string name, IEnumerable<string> permissions, string ticketScope = "Departments")
    {
        var r = await client.PostAsJsonAsync("/api/v1/admin/roles", new SaveRoleRequest(name, null, ticketScope, "All", false, permissions.ToList()), J);
        return await Ok<AdminRoleDto>(r);
    }

    private static Task<HttpResponseMessage> SetRolesAsync(HttpClient client, Guid userId, params Guid[] roleIds) =>
        client.PutAsJsonAsync($"/api/v1/admin/users/{userId}/roles", new SetUserRolesRequest(roleIds), J);

    private HttpClient Anonymous(string? forwardedFor = null)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Client", "mobile");
        if (forwardedFor is not null) client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        return client;
    }

    private HttpClient WithToken(string token, string? forwardedFor = null)
    {
        var client = factory.ClientWithToken(token);
        if (forwardedFor is not null) client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        return client;
    }

    // ───────────────────────────── Roles & permissions ─────────────────────────────

    [Fact]
    public async Task Custom_role_without_reply_permission_gets_403_and_changes_apply_immediately()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var ticket = await CreateTicketAsync(admin, t.SupportDepartmentId);
        var (agentId, agentEmail) = await t.CreateAgentAsync("Role Tester");
        var agent = await t.AgentClientAsync(agentEmail);

        // With the system Agent role the agent may reply.
        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/replies", new ReplyRequest { BodyHtml = "<p>first</p>" }, J)).StatusCode);

        // A custom role: the Agent template without tickets.reply.
        var noReply = await CreateRoleAsync(admin, "Agent without replies", Permissions.Templates.Agent.Where(p => p != Permissions.TicketsReply));
        Assert.DoesNotContain(Permissions.TicketsReply, noReply.Permissions);
        await Ok<UserRolesDto>(await SetRolesAsync(admin, agentId, noReply.Id));

        // The same session loses the permission at once (principal cache invalidated): reply → 403, note → 200.
        var reply = await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/replies", new ReplyRequest { BodyHtml = "<p>second</p>" }, J);
        Assert.Equal(HttpStatusCode.Forbidden, reply.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/notes", new NoteRequest { BodyHtml = "<p>internal</p>" }, J)).StatusCode);
        var me = await agent.GetFromJsonAsync<MeDto>("/api/v1/me", J);
        Assert.DoesNotContain(Permissions.TicketsReply, me!.Permissions);

        // The role list reports the member; the agent cannot open role administration.
        var roles = await admin.GetFromJsonAsync<List<AdminRoleListItemDto>>("/api/v1/admin/roles", J);
        Assert.Equal(1, roles!.Single(r => r.Id == noReply.Id).UserCount);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/v1/admin/roles")).StatusCode);

        // The light-agent flag on a role strips reply even when granted.
        var updated = await admin.PutAsJsonAsync($"/api/v1/admin/roles/{noReply.Id}",
            new SaveRoleRequest(noReply.Name, null, "Departments", "All", true, [.. Permissions.Templates.Agent], noReply.RowVersion), J);
        var light = await Ok<AdminRoleDto>(updated);
        Assert.True(light.IsLightAgent);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/replies", new ReplyRequest { BodyHtml = "<p>third</p>" }, J)).StatusCode);

        // Stale rowVersion → 409; system roles are read-only but can be cloned.
        var stale = await admin.PutAsJsonAsync($"/api/v1/admin/roles/{noReply.Id}",
            new SaveRoleRequest(noReply.Name, null, "Departments", "All", false, [.. Permissions.Templates.Agent], noReply.RowVersion), J);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var system = roles.Single(r => r.Name == "Supervisor");
        var sys = await admin.PutAsJsonAsync($"/api/v1/admin/roles/{system.Id}", new SaveRoleRequest("Supervisor", null, "All", "All", false, [], "AAAAAAAAAAA="), J);
        Assert.Equal("system_role_read_only", await CodeAsync(sys));
        var clone = await Ok<AdminRoleDto>(await admin.PostAsJsonAsync($"/api/v1/admin/roles/{system.Id}/clone", new CloneRoleRequest("Supervisor (copy)"), J));
        Assert.False(clone.IsSystem);
        Assert.Equal(Permissions.Templates.Supervisor.Distinct().Count(), clone.Permissions.Count);
    }

    [Fact]
    public async Task The_last_administrator_cannot_be_removed()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var adminId = t.Provisioned.AdminUserId;
        var agentRole = t.Provisioned.Roles["Agent"];

        // Demoting the only administrator is refused.
        var demote = await SetRolesAsync(admin, adminId, agentRole);
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Equal("last_administrator", await CodeAsync(demote));

        // A custom administrative role held by a second user makes the demotion possible…
        var custom = await CreateRoleAsync(admin, "Deputy admin", Permissions.AllCodes, "All");
        var (deputyId, deputyEmail) = await t.CreateAgentAsync("Deputy");
        await Ok<UserRolesDto>(await SetRolesAsync(admin, deputyId, custom.Id));
        await Ok<UserRolesDto>(await SetRolesAsync(admin, adminId, agentRole)); // the deputy remains an administrator
        var deputy = await t.AgentClientAsync(deputyEmail);

        // Now the deputy is the last administrator: stripping admin.roles from the role, deleting it or dropping it is refused.
        var role = (await deputy.GetFromJsonAsync<AdminRoleDto>($"/api/v1/admin/roles/{custom.Id}", J))!;
        var strip = await deputy.PutAsJsonAsync($"/api/v1/admin/roles/{custom.Id}",
            new SaveRoleRequest(role.Name, null, "All", "All", false, Permissions.AllCodes.Where(p => p != Permissions.AdminRoles).ToList(), role.RowVersion), J);
        Assert.Equal("last_administrator", await CodeAsync(strip));
        var delete = await deputy.DeleteAsync($"/api/v1/admin/roles/{custom.Id}?reassignTo={agentRole}");
        Assert.Equal("last_administrator", await CodeAsync(delete));
        var removeSelf = await SetRolesAsync(deputy, deputyId, agentRole);
        Assert.Equal("last_administrator", await CodeAsync(removeSelf));

        // Restoring the original administrator, then the deputy role can go (members moved to Agent).
        await Ok<UserRolesDto>(await SetRolesAsync(deputy, adminId, t.Provisioned.Roles["Administrator"]));
        Assert.Equal(HttpStatusCode.NoContent, (await deputy.DeleteAsync($"/api/v1/admin/roles/{custom.Id}?reassignTo={agentRole}")).StatusCode);
    }

    [Fact]
    public async Task Role_managers_cannot_escalate_privileges()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var managerRole = await CreateRoleAsync(admin, "Role manager", [Permissions.AdminRoles, Permissions.AdminUsers, Permissions.TicketsView, Permissions.AdminIntegrations]);
        var (managerId, managerEmail) = await t.CreateAgentAsync("Role Manager");
        await Ok<UserRolesDto>(await SetRolesAsync(admin, managerId, managerRole.Id));
        var manager = await t.AgentClientAsync(managerEmail);

        // Permissions the manager lacks, or a broader data scope, cannot be granted.
        var tooMuch = await manager.PostAsJsonAsync("/api/v1/admin/roles", new SaveRoleRequest("Deleters", null, "Departments", "All", false, [Permissions.TicketsView, Permissions.TicketsDelete]), J);
        Assert.Equal(HttpStatusCode.Forbidden, tooMuch.StatusCode);
        Assert.Equal("privilege_escalation", await CodeAsync(tooMuch));
        var broader = await manager.PostAsJsonAsync("/api/v1/admin/roles", new SaveRoleRequest("Everything viewers", null, "All", "All", false, [Permissions.TicketsView]), J);
        Assert.Equal("privilege_escalation", await CodeAsync(broader));
        var fine = await manager.PostAsJsonAsync("/api/v1/admin/roles", new SaveRoleRequest("Viewers", null, "Departments", "All", false, [Permissions.TicketsView]), J);
        var viewers = await Ok<AdminRoleDto>(fine);

        // Nor can the manager hand out the Administrator role, change an administrator's roles, or edit a stronger role.
        var (otherId, _) = await t.CreateAgentAsync("Other Agent");
        var promote = await SetRolesAsync(manager, otherId, t.Provisioned.Roles["Administrator"]);
        Assert.Equal(HttpStatusCode.Forbidden, promote.StatusCode);
        var self = await SetRolesAsync(manager, managerId, managerRole.Id, t.Provisioned.Roles["Supervisor"]);
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetRolesAsync(manager, t.Provisioned.AdminUserId, viewers.Id)).StatusCode);
        var adminRole = await admin.GetFromJsonAsync<AdminRoleDto>($"/api/v1/admin/roles/{managerRole.Id}", J);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.DeleteAsync($"/api/v1/admin/roles/{(await CreateRoleAsync(admin, "Supers", Permissions.Templates.Supervisor)).Id}")).StatusCode);
        Assert.NotNull(adminRole);

        // API credentials cannot carry permissions the creator lacks either.
        var key = await manager.PostAsJsonAsync("/api/v1/admin/api-keys", new CreateApiKeyRequest("Writer", ["tickets:write"], null, null), J);
        Assert.Equal("privilege_escalation", await CodeAsync(key));
        var catalog = await manager.GetFromJsonAsync<PermissionCatalogDto>("/api/v1/admin/roles/catalog", J);
        Assert.DoesNotContain(Permissions.TicketsDelete, catalog!.Grantable);
        Assert.Contains(Permissions.TicketsView, catalog.Grantable);

        // Every change was audited.
        var actions = await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            return await db.AuditLogs.Where(a => a.TenantId == t.Id).Select(a => a.Action).ToListAsync();
        });
        Assert.Contains("role.created", actions);
        Assert.Contains("user.roles_changed", actions);
    }

    [Fact]
    public async Task Agent_administration_cannot_escalate_privileges_either()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        // A user manager without administrator rights uses the organisation module's agent screens.
        var managerRole = await CreateRoleAsync(admin, "User manager", [Permissions.AdminUsers, Permissions.AgentsManage, Permissions.TicketsView]);
        var viewers = await CreateRoleAsync(admin, "Viewers", [Permissions.TicketsView]);
        var (managerId, managerEmail) = await t.CreateAgentAsync("User Manager");
        await Ok<UserRolesDto>(await SetRolesAsync(admin, managerId, managerRole.Id));
        var manager = await t.AgentClientAsync(managerEmail);
        var administrator = t.Provisioned.Roles["Administrator"];

        // Changing an agent's access to the Administrator role is refused; a role within the manager's own rights is fine.
        var (otherId, _) = await t.CreateAgentAsync("Other Agent");
        await Ok<UserRolesDto>(await SetRolesAsync(admin, otherId, viewers.Id));
        async Task<HttpResponseMessage> SetAccessAsync(Guid roleId)
        {
            var detail = await Ok<AgentDetailDto>(await manager.GetAsync($"/api/v1/org/agents/{otherId}"));
            return await manager.PutAsJsonAsync($"/api/v1/org/agents/{otherId}/access", new UpdateAgentAccessRequest
            {
                RoleIds = [roleId], DepartmentIds = [t.SupportDepartmentId], RowVersion = detail.RowVersion
            }, J);
        }
        var promote = await SetAccessAsync(administrator);
        Assert.Equal(HttpStatusCode.Forbidden, promote.StatusCode);
        Assert.Equal("privilege_escalation", await CodeAsync(promote));
        await Ok<AgentDetailDto>(await SetAccessAsync(viewers.Id));

        // Inviting a new administrator is refused.
        var invite = await manager.PostAsJsonAsync("/api/v1/org/agents", new InviteAgentRequest
        {
            Email = $"new.admin.{Guid.NewGuid():N}@{t.Slug}.test", Name = "New Admin", RoleIds = [administrator], DepartmentIds = [t.SupportDepartmentId]
        }, J);
        Assert.Equal(HttpStatusCode.Forbidden, invite.StatusCode);

        // Deactivating an administrator (a stronger user) is refused, and the administrator can still sign in.
        var deactivate = await manager.PostAsJsonAsync($"/api/v1/org/agents/{t.Provisioned.AdminUserId}/deactivate",
            new DeactivateAgentRequest("unassign", null, "Escalation attempt"), J);
        Assert.Equal(HttpStatusCode.Forbidden, deactivate.StatusCode);
        Assert.NotNull(await t.AdminClientAsync());
    }

    // ───────────────────────────── Ticket configuration ─────────────────────────────

    [Fact]
    public async Task A_field_required_by_status_blocks_the_status_change_until_filled()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var statuses = await admin.GetFromJsonAsync<List<AdminStatusDto>>("/api/v1/admin/ticket-config/statuses", J);
        var resolved = statuses!.Single(s => s.Name == "Resolved");
        var field = await Ok<AdminCustomFieldDto>(await admin.PostAsJsonAsync("/api/v1/admin/ticket-config/fields", new SaveCustomFieldRequest("Ticket", "root_cause", "Root cause",
            "Picklist", JsonSerializer.SerializeToElement(new[] { new { value = "config", label = "Configuration" }, new { value = "defect", label = "Product defect" } }),
            false, null, null, null, null, null, null, false, false, null, [resolved.Id], true), J));
        Assert.Equal([resolved.Id], field.RequiredForStatusIds);

        var ticket = await CreateTicketAsync(admin, t.SupportDepartmentId);
        var blocked = await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/status", new ChangeStatusRequest(resolved.Id, null, null, ticket.RowVersion), J);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Equal(ErrorCodes.MissingFields, await CodeAsync(blocked));

        var patched = await Ok<TicketDetailDto>(await admin.PatchAsJsonAsync($"/api/v1/tickets/{ticket.Id}", new UpdateTicketRequest
        {
            RowVersion = ticket.RowVersion, CustomFields = new Dictionary<string, JsonElement> { ["root_cause"] = JsonSerializer.SerializeToElement("config") }
        }, J));
        var done = await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/status", new ChangeStatusRequest(resolved.Id, null, null, patched.RowVersion), J);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);

        // Picklist values are validated; unknown keys and bad regexes are rejected.
        var badRegex = await admin.PostAsJsonAsync("/api/v1/admin/ticket-config/fields", new SaveCustomFieldRequest("Ticket", "erp_ref", "ERP reference", "Text", null,
            false, null, null, "([a-z", null, null, null, false, false, null, null, true), J);
        Assert.Equal(HttpStatusCode.BadRequest, badRegex.StatusCode);
    }

    [Fact]
    public async Task Status_transitions_are_restricted_to_the_configured_targets()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var statuses = (await admin.GetFromJsonAsync<List<AdminStatusDto>>("/api/v1/admin/ticket-config/statuses", J))!;
        var open = statuses.Single(s => s.Name == "Open");
        var inProgress = statuses.Single(s => s.Name == "In Progress");
        var waiting = statuses.Single(s => s.Name == "Waiting on Customer");

        var saved = await Ok<AdminStatusDto>(await admin.PutAsJsonAsync($"/api/v1/admin/ticket-config/statuses/{open.Id}",
            new SaveStatusRequest(open.Name, open.Category, open.Color, open.IsDefault, open.PausesSla, true, open.CustomerLabel, [inProgress.Id], open.Substatuses, open.RowVersion), J));
        Assert.Equal([inProgress.Id], saved.AllowedNext);

        var ticket = await CreateTicketAsync(admin, t.SupportDepartmentId);
        Assert.Equal(open.Id, ticket.StatusId);
        var direct = await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/status", new ChangeStatusRequest(waiting.Id, null, null, ticket.RowVersion), J);
        Assert.Equal(ErrorCodes.InvalidTransition, await CodeAsync(direct));
        var allowed = await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/status", new ChangeStatusRequest(inProgress.Id, null, null, ticket.RowVersion), J);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        // Every category must keep an active status; built-in statuses cannot be deleted.
        var deactivate = await admin.PutAsJsonAsync($"/api/v1/admin/ticket-config/statuses/{waiting.Id}",
            new SaveStatusRequest(waiting.Name, waiting.Category, waiting.Color, false, waiting.PausesSla, false, null, null, null, waiting.RowVersion), J);
        Assert.Equal(HttpStatusCode.BadRequest, deactivate.StatusCode); // built-in statuses stay active
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/v1/admin/ticket-config/statuses/{open.Id}")).StatusCode);

        // Numbering: the prefix changes the displayed number; the next number is read-only.
        var lifecycle = (await admin.GetFromJsonAsync<TicketLifecycleDto>("/api/v1/admin/ticket-config/lifecycle", J))!;
        Assert.True(lifecycle.NextNumber > 0);
        var newLifecycle = await Ok<TicketLifecycleDto>(await admin.PutAsJsonAsync("/api/v1/admin/ticket-config/lifecycle", new SaveTicketLifecycleRequest("SD-",
            lifecycle.ReopenWindowDays, lifecycle.AllowCustomerReopen, 3, lifecycle.RequireCustomerConfirmation, lifecycle.RequireResolutionCode, lifecycle.SoftLockMinutes,
            false, lifecycle.AllowMergeAcrossDepartments, lifecycle.MaxMergeSources, lifecycle.AllowSplit, lifecycle.CsatEnabled, lifecycle.CsatExpiryDays, lifecycle.AutoAcknowledge), J));
        Assert.Equal("SD-", newLifecycle.NumberPrefix);
        Assert.Equal(3, newLifecycle.AutoCloseResolvedAfterDays);
        var next = await CreateTicketAsync(admin, t.SupportDepartmentId);
        Assert.StartsWith("SD-", next.DisplayNumber);
    }

    // ───────────────────────────── Security ─────────────────────────────

    [Fact]
    public async Task The_ip_allow_list_blocks_agents_outside_it_but_not_allowed_addresses()
    {
        var t = await factory.CreateTenantAsync();
        var adminToken = await factory.LoginAsync(t.Slug, t.AdminEmail);
        var (_, agentEmail) = await t.CreateAgentAsync("Office Agent");
        var policy = (await WithToken(adminToken, "10.20.30.5").GetFromJsonAsync<SecurityPolicyDto>("/api/v1/admin/security/policy", J))!;
        Assert.Equal("10.20.30.5", policy.CurrentIp);

        // Anti-lockout: a list without the caller's own address is refused.
        var lockout = await WithToken(adminToken, "10.20.30.5").PutAsJsonAsync("/api/v1/admin/security/policy",
            new SaveSecurityPolicyRequest(policy.MfaPolicy, policy.Password, policy.Session, new IpAllowListDto(true, ["192.168.50.0/24"])), J);
        Assert.Equal(HttpStatusCode.BadRequest, lockout.StatusCode);

        await Ok<SecurityPolicyDto>(await WithToken(adminToken, "10.20.30.5").PutAsJsonAsync("/api/v1/admin/security/policy",
            new SaveSecurityPolicyRequest(policy.MfaPolicy, policy.Password, policy.Session, new IpAllowListDto(true, ["10.20.30.0/24", "2001:db8::/32"])), J));

        // Sign-in from outside the list is refused; from inside it works.
        var outside = await Anonymous("198.51.100.7").PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(t.Slug, agentEmail, DeskApiFactory.Password), J);
        Assert.Equal(HttpStatusCode.Forbidden, outside.StatusCode);
        Assert.Equal(LoginStatus.IpNotAllowed, (await outside.Content.ReadFromJsonAsync<LoginResponse>(J))!.Status);
        var inside = await Ok<LoginResponse>(await Anonymous("10.20.30.44").PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(t.Slug, agentEmail, DeskApiFactory.Password), J));
        Assert.Equal(LoginStatus.Ok, inside.Status);

        // A token used from another network is blocked on every request; from the office it works.
        var away = await WithToken(inside.AccessToken!, "203.0.113.9").GetAsync("/api/v1/tickets");
        Assert.Equal(HttpStatusCode.Forbidden, away.StatusCode);
        Assert.Equal("ip_not_allowed", await CodeAsync(away));
        Assert.Equal(HttpStatusCode.OK, (await WithToken(inside.AccessToken!, "10.20.30.44").GetAsync("/api/v1/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await WithToken(inside.AccessToken!, "2001:db8::17").GetAsync("/api/v1/tickets")).StatusCode);

        // Switching it off restores access immediately (cached rules are invalidated).
        await Ok<SecurityPolicyDto>(await WithToken(adminToken, "10.20.30.5").PutAsJsonAsync("/api/v1/admin/security/policy",
            new SaveSecurityPolicyRequest(policy.MfaPolicy, policy.Password, policy.Session, new IpAllowListDto(false, ["10.20.30.0/24"])), J));
        Assert.Equal(HttpStatusCode.OK, (await WithToken(inside.AccessToken!, "203.0.113.9").GetAsync("/api/v1/tickets")).StatusCode);
    }

    [Fact]
    public async Task Mfa_policy_and_session_revocation_are_enforced()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var (agentId, agentEmail) = await t.CreateAgentAsync("Mfa Agent");
        var agentToken = await factory.LoginAsync(t.Slug, agentEmail);
        var agent = factory.ClientWithToken(agentToken);
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/v1/me")).StatusCode);

        // The administrator sees the agent's session and revokes it: the token stops working at once.
        var sessions = await admin.GetFromJsonAsync<PagedResult<AdminSessionDto>>($"/api/v1/admin/security/sessions?userId={agentId}", J);
        var session = Assert.Single(sessions!.Items);
        Assert.Equal(agentEmail, session.Email);
        var revoked = await Ok<RevokeSessionsResultDto>(await admin.DeleteAsync($"/api/v1/admin/security/sessions/{session.Id}"));
        Assert.True(revoked.Revoked >= 1);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.GetAsync("/api/v1/me")).StatusCode);

        // MFA required for everyone: a password alone no longer signs the agent in.
        var policy = (await admin.GetFromJsonAsync<SecurityPolicyDto>("/api/v1/admin/security/policy", J))!;
        await Ok<SecurityPolicyDto>(await admin.PutAsJsonAsync("/api/v1/admin/security/policy",
            new SaveSecurityPolicyRequest("RequiredForAll", policy.Password with { MinLength = 12 }, policy.Session, policy.IpAllowList), J));
        var login = await Anonymous().PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(t.Slug, agentEmail, DeskApiFactory.Password), J);
        Assert.Equal(LoginStatus.MfaRequired, (await login.Content.ReadFromJsonAsync<LoginResponse>(J))!.Status);
        var saved = (await admin.GetFromJsonAsync<SecurityPolicyDto>("/api/v1/admin/security/policy", J))!;
        Assert.Equal("RequiredForAll", saved.MfaPolicy);
        Assert.Equal(12, saved.Password.MinLength);

        // SSO: the client secret is write-only and is dropped when the provider registration changes.
        var sso = await Ok<SsoConfigDto>(await admin.PutAsJsonAsync("/api/v1/admin/security/sso", new SaveSsoConfigRequest(false, "https://idp.example.com", "desk-app",
            "s3cret-value", false, "openid profile email", "email", "name", false, null, false), J));
        Assert.True(sso.HasClientSecret);
        Assert.DoesNotContain("s3cret-value", await admin.GetStringAsync("/api/v1/admin/security/sso"));
        var kept = await Ok<SsoConfigDto>(await admin.PutAsJsonAsync("/api/v1/admin/security/sso", new SaveSsoConfigRequest(false, "https://idp.example.com", "desk-app",
            null, false, "openid profile email", "email", "name", false, null, false), J));
        Assert.True(kept.HasClientSecret);
        var moved = await Ok<SsoConfigDto>(await admin.PutAsJsonAsync("/api/v1/admin/security/sso", new SaveSsoConfigRequest(false, "https://other-idp.example.net", "desk-app",
            null, false, "openid profile email", "email", "name", false, null, false), J));
        Assert.False(moved.HasClientSecret);
    }

    // ───────────────────────────── API credentials ─────────────────────────────

    [Fact]
    public async Task An_api_key_is_limited_to_its_scopes_and_can_be_revoked()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var created = await Ok<ApiKeySecretDto>(await admin.PostAsJsonAsync("/api/v1/admin/api-keys",
            new CreateApiKeyRequest("Read-only reporting", ["tickets:read"], null, factory.Clock.UtcNow.AddDays(30)), J));
        Assert.StartsWith($"sdk_{created.Key.Prefix}_", created.Secret);
        Assert.Equal("Active", created.Key.Status);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", created.Secret);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/tickets")).StatusCode);
        var write = await client.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest { Subject = "From the API", Channel = "Api", SkipRouting = true }, J);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/admin/roles")).StatusCode);

        // The secret is never listed; revocation takes effect on the next request.
        var keys = await admin.GetFromJsonAsync<List<AdminApiKeyDto>>("/api/v1/admin/api-keys", J);
        Assert.Contains(keys!, k => k.Id == created.Key.Id);
        Assert.DoesNotContain(created.Secret, await admin.GetStringAsync("/api/v1/admin/api-keys"));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/admin/api-keys/{created.Key.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/tickets")).StatusCode);

        // OAuth client: create, rotate and revoke.
        var oauth = await Ok<OAuthClientSecretDto>(await admin.PostAsJsonAsync("/api/v1/admin/oauth-clients", new CreateOAuthClientRequest("ERP sync", ["tickets:read"], null), J));
        Assert.EndsWith("/oauth/token", oauth.TokenEndpoint);
        var rotated = await Ok<OAuthClientSecretDto>(await admin.PostAsync($"/api/v1/admin/oauth-clients/{oauth.Client.Id}/rotate-secret", null));
        Assert.NotEqual(oauth.ClientSecret, rotated.ClientSecret);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/admin/oauth-clients/{oauth.Client.Id}")).StatusCode);
        var clients = await admin.GetFromJsonAsync<List<AdminOAuthClientDto>>("/api/v1/admin/oauth-clients", J);
        Assert.Equal("Revoked", clients!.Single(c => c.Id == oauth.Client.Id).Status);
    }

    // ───────────────────────────── Brands & notifications ─────────────────────────────

    [Fact]
    public async Task Brands_and_templates_are_validated_and_rendered()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var adminA = await a.AdminClientAsync();
        var adminB = await b.AdminClientAsync();
        var brandA = (await adminA.GetFromJsonAsync<List<AdminBrandListItemDto>>("/api/v1/admin/brands", J))!.Single();
        var full = (await adminA.GetFromJsonAsync<AdminBrandDto>($"/api/v1/admin/brands/{brandA.Id}", J))!;

        // Slugs are globally unique (another tenant's slug is taken); colours must be hex.
        var taken = await adminA.PutAsJsonAsync($"/api/v1/admin/brands/{brandA.Id}", ToSave(full) with { Slug = b.Slug }, J);
        Assert.Equal(HttpStatusCode.BadRequest, taken.StatusCode);
        var updated = await Ok<AdminBrandDto>(await adminA.PutAsJsonAsync($"/api/v1/admin/brands/{brandA.Id}",
            ToSave(full) with { PrimaryColor = "#7C3AED", CustomDomain = "help.tenant-a.example", EmailFooterHtml = "<p>Footer<script>alert(1)</script></p>" }, J));
        Assert.Equal("#7c3aed", updated.PrimaryColor);
        Assert.Equal("CNAME", updated.Dns!.RecordType);
        Assert.DoesNotContain("<script", updated.EmailFooterHtml);
        // The public portal configuration reflects the change (contract unchanged).
        var portal = await Anonymous().GetFromJsonAsync<JsonElement>($"/api/v1/portal/{a.Slug}/config", J);
        Assert.Equal("#7c3aed", portal.GetProperty("primaryColor").GetString());

        // Logos are public only once a brand uses them: a generic upload labelled "brand-asset" is never served.
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        MultipartFormDataContent Form()
        {
            var content = new ByteArrayContent(png);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return new MultipartFormDataContent { { content, "file", "logo.png" } };
        }
        var sneaky = await Ok<UploadedFileDto>(await adminA.PostAsync("/api/v1/files?purpose=brand-asset", Form()));
        Assert.Equal(HttpStatusCode.NotFound, (await Anonymous().GetAsync($"/api/v1/brand-assets/{sneaky.FileId}")).StatusCode);
        var withLogo = await Ok<AdminBrandDto>(await adminA.PostAsync($"/api/v1/admin/brands/{brandA.Id}/assets/logo", Form()));
        Assert.StartsWith("/api/v1/brand-assets/", withLogo.LogoUrl);
        Assert.Equal(HttpStatusCode.OK, (await Anonymous().GetAsync(withLogo.LogoUrl)).StatusCode);

        // Templates: unknown placeholders are rejected; preview renders with sample values and encodes them.
        var bad = await adminA.PutAsJsonAsync("/api/v1/admin/notifications/templates/customer.ticket_acknowledgement",
            new SaveNotificationTemplateRequest("Hi {{nope}}", "<p>{{contact.name}}</p>", true), J);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var saved = await Ok<NotificationTemplateDto>(await adminA.PutAsJsonAsync("/api/v1/admin/notifications/templates/customer.ticket_acknowledgement",
            new SaveNotificationTemplateRequest("Logged {{ticket.number}}", "<p>Hello {{contact.name}} from {{brand.name}}</p><script>x()</script>", true), J));
        Assert.True(saved.IsCustomized);
        var preview = await Ok<TemplatePreviewDto>(await adminA.PostAsJsonAsync("/api/v1/admin/notifications/templates/customer.ticket_acknowledgement/preview",
            new PreviewTemplateRequest(), J));
        Assert.Equal("Logged #1042", preview.Subject);
        Assert.Contains("Hello Ramesh Kumar from", preview.BodyHtml);
        Assert.DoesNotContain("<script", preview.BodyHtml);
        Assert.Contains("Footer", preview.BodyHtml);

        // Tenant B never sees tenant A's override.
        var bTemplate = await adminB.GetFromJsonAsync<NotificationTemplateDto>("/api/v1/admin/notifications/templates/customer.ticket_acknowledgement", J);
        Assert.False(bTemplate!.IsCustomized);

        // Notification defaults: the matrix feeds users without preferences.
        var defaults = (await adminA.GetFromJsonAsync<NotificationPolicyDto>("/api/v1/admin/notifications/defaults", J))!;
        var events = defaults.Events.Select(e => e.EventType == "ticket.assigned" ? e with { Email = false, Digest = true } : e).ToList();
        await Ok<NotificationPolicyDto>(await adminA.PutAsJsonAsync("/api/v1/admin/notifications/defaults",
            new SaveNotificationPolicyRequest(events, "22:00", "07:00", "Asia/Kolkata", "Hourly", "08:00"), J));
        var me = await adminA.GetFromJsonAsync<JsonElement>("/api/v1/me/notification-settings", J);
        var assigned = me.GetProperty("preferences").EnumerateArray().Single(p => p.GetProperty("eventType").GetString() == "ticket.assigned");
        Assert.Equal("Hourly", assigned.GetProperty("digest").GetString());
        Assert.Equal("22:00", me.GetProperty("quietHoursStart").GetString());
    }

    private static SaveBrandRequest ToSave(AdminBrandDto b) => new(b.Name, b.Slug, b.CustomDomain, b.PrimaryColor, b.AccentColor, b.SupportEmail, b.SupportPhone,
        b.PortalTitle, b.PortalWelcome, b.FooterHtml, b.EmailFooterHtml, b.DefaultDepartmentId, b.DefaultLocale, b.Locales, b.PortalEnabled, b.KnowledgeBaseEnabled,
        b.CommunityEnabled, b.ChatEnabled, b.AiAssistantEnabled, b.AllowAnonymousTickets, b.AllowSelfRegistration, b.PortalAuthMode, b.RowVersion);

    // ───────────────────────────── Tenant isolation ─────────────────────────────

    [Fact]
    public async Task Admin_endpoints_are_tenant_isolated()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var adminA = await a.AdminClientAsync();
        var adminB = await b.AdminClientAsync();

        var roleB = await CreateRoleAsync(adminB, "Tenant B custom", [Permissions.TicketsView]);
        var fieldB = await Ok<AdminCustomFieldDto>(await adminB.PostAsJsonAsync("/api/v1/admin/ticket-config/fields",
            new SaveCustomFieldRequest("Ticket", "b_only", "B only", "Text", null, false, null, null, null, null, null, null, false, false, null, null, true), J));
        var statusB = (await adminB.GetFromJsonAsync<List<AdminStatusDto>>("/api/v1/admin/ticket-config/statuses", J))!.First();
        var brandB = (await adminB.GetFromJsonAsync<List<AdminBrandListItemDto>>("/api/v1/admin/brands", J))!.Single();
        var keyB = await Ok<ApiKeySecretDto>(await adminB.PostAsJsonAsync("/api/v1/admin/api-keys", new CreateApiKeyRequest("B key", ["tickets:read"], null, null), J));
        var sessionsB = (await adminB.GetFromJsonAsync<PagedResult<AdminSessionDto>>("/api/v1/admin/security/sessions", J))!;

        // Lists never include the other tenant's rows.
        Assert.DoesNotContain((await adminA.GetFromJsonAsync<List<AdminRoleListItemDto>>("/api/v1/admin/roles", J))!, r => r.Id == roleB.Id);
        Assert.DoesNotContain((await adminA.GetFromJsonAsync<List<AdminCustomFieldDto>>("/api/v1/admin/ticket-config/fields", J))!, f => f.Id == fieldB.Id);
        Assert.DoesNotContain((await adminA.GetFromJsonAsync<List<AdminApiKeyDto>>("/api/v1/admin/api-keys", J))!, k => k.Id == keyB.Key.Id);
        Assert.DoesNotContain((await adminA.GetFromJsonAsync<PagedResult<AdminSessionDto>>("/api/v1/admin/security/sessions", J))!.Items, s => s.UserId == b.Provisioned.AdminUserId);

        // Direct access by id → 404 (no existence leak), and nothing changes.
        Assert.Equal(HttpStatusCode.NotFound, (await adminA.GetAsync($"/api/v1/admin/roles/{roleB.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminA.PutAsJsonAsync($"/api/v1/admin/roles/{roleB.Id}",
            new SaveRoleRequest("Hijacked", null, "All", "All", false, [Permissions.TicketsView], roleB.RowVersion), J)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminA.DeleteAsync($"/api/v1/admin/roles/{roleB.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetRolesAsync(adminA, b.Provisioned.AdminUserId, a.Provisioned.Roles["Agent"])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminA.GetAsync($"/api/v1/admin/users/{b.Provisioned.AdminUserId}/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminA.DeleteAsync($"/api/v1/admin/ticket-config/fields/{fieldB.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminA.PutAsJsonAsync($"/api/v1/admin/ticket-config/statuses/{statusB.Id}",
            new SaveStatusRequest("Hijacked", statusB.Category, statusB.Color, statusB.IsDefault, false, true, null, null, null, statusB.RowVersion), J)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminA.GetAsync($"/api/v1/admin/brands/{brandB.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminA.DeleteAsync($"/api/v1/admin/api-keys/{keyB.Key.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminA.DeleteAsync($"/api/v1/admin/security/sessions/{sessionsB.Items.First().Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminA.DeleteAsync($"/api/v1/admin/security/users/{b.Provisioned.AdminUserId}/sessions")).StatusCode);

        // Tenant B is untouched: its role, key and session still work.
        Assert.Equal("Tenant B custom", (await adminB.GetFromJsonAsync<AdminRoleDto>($"/api/v1/admin/roles/{roleB.Id}", J))!.Name);
        var keyClient = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        keyClient.DefaultRequestHeaders.Add("X-Api-Key", keyB.Secret);
        Assert.Equal(HttpStatusCode.OK, (await keyClient.GetAsync("/api/v1/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminB.GetAsync("/api/v1/admin/roles")).StatusCode);

        // Agents without administration permissions are refused (403) on every admin area.
        var (_, agentEmail) = await a.CreateAgentAsync("Plain Agent");
        var agent = await a.AgentClientAsync(agentEmail);
        foreach (var path in new[] { "/api/v1/admin/roles/catalog", "/api/v1/admin/ticket-config/statuses", "/api/v1/admin/brands", "/api/v1/admin/notifications/templates",
                     "/api/v1/admin/security/policy", "/api/v1/admin/api-keys", "/api/v1/admin/security/sessions" })
            Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/v1/admin/ticket-config/display")).StatusCode);
    }
}
