using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Identity;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Auth;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain.Customers;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;

namespace SupportDesk.IntegrationTests.Core;

[Collection("api")]
public sealed class PermissionBoundaryTests(DeskApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions J = DeskApiFactory.Json;

    private static async Task<TicketDetailDto> CreateAsync(HttpClient client, Guid? departmentId = null)
    {
        var r = await client.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest { Subject = "Permission test", Channel = "Phone", DepartmentId = departmentId, SkipRouting = true }, J);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<TicketDetailDto>(J))!;
    }

    [Fact]
    public async Task Unauthenticated_requests_are_rejected()
    {
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Light_agents_can_add_notes_but_not_reply_or_assign()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var ticket = await CreateAsync(admin);
        var (lightId, lightEmail) = await t.CreateAgentAsync("Light One", "Light Agent");
        var light = await t.AgentClientAsync(lightEmail);

        Assert.Equal(HttpStatusCode.OK, (await light.GetAsync($"/api/v1/tickets/{ticket.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await light.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/replies", new ReplyRequest { BodyHtml = "<p>x</p>" }, J)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await light.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/notes", new NoteRequest { BodyHtml = "<p>internal</p>" }, J)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await light.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/assign", new AssignTicketRequest(lightId, null, null), J)).StatusCode);
        // Light agents can never own tickets either.
        var assign = await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/assign", new AssignTicketRequest(lightId, null, null), J);
        Assert.Equal(HttpStatusCode.BadRequest, assign.StatusCode);
    }

    [Fact]
    public async Task Department_scope_hides_other_departments_tickets()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var billingTicket = await CreateAsync(admin, t.BillingDepartmentId);
        var (_, supportEmail) = await t.CreateAgentAsync("Support Agent", "Agent", t.SupportDepartmentId);
        var (_, billingEmail) = await t.CreateAgentAsync("Billing Agent", "Agent", t.BillingDepartmentId);
        var support = await t.AgentClientAsync(supportEmail);
        var billing = await t.AgentClientAsync(billingEmail);

        Assert.Equal(HttpStatusCode.NotFound, (await support.GetAsync($"/api/v1/tickets/{billingTicket.Id}")).StatusCode);
        var list = await support.GetFromJsonAsync<PagedResult<TicketListItemDto>>("/api/v1/tickets?limit=200", J);
        Assert.DoesNotContain(list!.Items, x => x.Id == billingTicket.Id);
        Assert.Equal(HttpStatusCode.OK, (await billing.GetAsync($"/api/v1/tickets/{billingTicket.Id}")).StatusCode);
        // Agents cannot assign tickets to agents outside the ticket's department.
        var supportUser = (await support.GetFromJsonAsync<MeDto>("/api/v1/me", J))!;
        var r = await admin.PostAsJsonAsync($"/api/v1/tickets/{billingTicket.Id}/assign", new AssignTicketRequest(supportUser.Id, null, null), J);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Portal_contact_tokens_cannot_use_agent_endpoints()
    {
        var t = await factory.CreateTenantAsync();
        var email = $"portal.{Guid.NewGuid():N}@cust.test";
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            db.Contacts.Add(new Contact
            {
                FullName = "Portal User", Email = email, EmailNormalized = email, PortalAccess = PortalAccessScope.Own,
                PortalPasswordHash = sp.GetRequiredService<PasswordService>().Hash(DeskApiFactory.Password)
            });
            await db.SaveChangesAsync();
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client", "mobile");
        var login = await client.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/auth/login", new PortalLoginRequest(email, DeskApiFactory.Password), J);
        var body = await login.Content.ReadFromJsonAsync<PortalLoginResponse>(J);
        Assert.NotNull(body?.AccessToken);
        var contactClient = factory.ClientWithToken(body!.AccessToken!);
        Assert.Equal(HttpStatusCode.Forbidden, (await contactClient.GetAsync("/api/v1/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await contactClient.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Api_key_scopes_limit_what_an_integration_can_do()
    {
        var t = await factory.CreateTenantAsync();
        var created = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<ApiKeyService>().CreateAsync("Read-only BI", ["tickets:read"], null, null, default));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", created.Key);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/tickets?limit=5")).StatusCode);
        var write = await client.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest { Subject = "Should fail", Channel = "Api" }, J);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        var bad = factory.CreateClient();
        bad.DefaultRequestHeaders.Add("X-Api-Key", created.Key[..^2] + "zz");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bad.GetAsync("/api/v1/tickets")).StatusCode);

        await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<ApiKeyService>().RevokeAsync(created.Id, default));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/tickets")).StatusCode);
    }

    [Fact]
    public async Task Refresh_token_reuse_revokes_the_session_family()
    {
        var t = await factory.CreateTenantAsync();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client", "mobile");
        var login = await (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(t.Slug, t.AdminEmail, DeskApiFactory.Password), J))
            .Content.ReadFromJsonAsync<LoginResponse>(J);
        var first = login!.RefreshToken!;
        var rotated = await (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first), J)).Content.ReadFromJsonAsync<LoginResponse>(J);
        Assert.NotNull(rotated?.RefreshToken);
        // Replaying the old token is treated as theft: rejected and the rotated token dies too.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first), J)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(rotated!.RefreshToken), J)).StatusCode);
        // The access token of the revoked session is rejected as well.
        var withToken = factory.ClientWithToken(rotated.AccessToken!);
        Assert.Equal(HttpStatusCode.Unauthorized, (await withToken.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Repeated_bad_passwords_lock_the_account()
    {
        var t = await factory.CreateTenantAsync();
        var client = factory.CreateClient();
        for (var i = 0; i < 5; i++)
            await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(t.Slug, t.AdminEmail, "wrong-password"), J);
        var locked = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(t.Slug, t.AdminEmail, DeskApiFactory.Password), J);
        Assert.Equal((HttpStatusCode)423, locked.StatusCode);
    }
}
