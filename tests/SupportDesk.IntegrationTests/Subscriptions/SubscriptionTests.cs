using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Subscriptions;
using SupportDesk.Application.Tenancy;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Communication;
using SupportDesk.Contracts.Platform;
using SupportDesk.Contracts.Subscriptions;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain.Platform;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;

namespace SupportDesk.IntegrationTests.Subscriptions;

[Collection("api")]
public sealed class SubscriptionTests(DeskApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions J = SaasTestKit.J;

    [Fact]
    public async Task Signup_provisions_an_isolated_trial_tenant_and_rejects_a_duplicate_slug()
    {
        var anonymous = factory.CreateClient();
        var slug = "su-" + Guid.NewGuid().ToString("N")[..8];
        var email = $"founder@{slug}.test";
        var availability = await (await anonymous.GetAsync($"/api/v1/signup/slug-availability?slug={slug}")).ReadAsync<SlugAvailabilityDto>();
        Assert.True(availability.Available);

        var request = new SignupRequest("Signup Test Ltd", slug, "Fiona Founder", email, "Signup@Pass2026");
        var started = await (await anonymous.PostAsJsonAsync("/api/v1/signup", request, J)).ReadAsync<SignupStartedDto>();
        Assert.NotNull(started.DemoCode);
        var wrong = await anonymous.PostAsJsonAsync($"/api/v1/signup/{started.SignupId}/verify", new SignupVerifyRequest("000000" == started.DemoCode ? "111111" : "000000"), J);
        Assert.Equal("invalid_code", await wrong.CodeAsync());
        var completed = await (await anonymous.PostAsJsonAsync($"/api/v1/signup/{started.SignupId}/verify", new SignupVerifyRequest(started.DemoCode!), J))
            .ReadAsync<SignupCompletedDto>();
        Assert.Equal(slug, completed.Workspace);
        Assert.NotNull(completed.TrialEndsAt);

        // The new administrator signs in with the password chosen at sign-up and sees an empty, isolated workspace on a trial.
        var admin = factory.ClientWithToken(await factory.LoginAsync(slug, email, "Signup@Pass2026"));
        var sub = await (await admin.GetAsync("/api/v1/subscription")).ReadAsync<SubscriptionDto>();
        Assert.Equal("Trialing", sub.Status);
        Assert.Equal(completed.TenantId, sub.TenantId);
        Assert.True(sub.TrialDaysLeft > 0);
        var other = await factory.CreateTenantAsync();
        await (await other.AdminClientAsync()).PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest { Subject = "Other tenant", SkipRouting = true }, J);
        var tickets = await (await admin.GetAsync("/api/v1/tickets")).ReadAsync<PagedResult<System.Text.Json.JsonElement>>();
        Assert.Empty(tickets.Items);

        // The slug is now taken.
        var duplicate = await anonymous.PostAsJsonAsync("/api/v1/signup", request with { AdminEmail = $"someone@{slug}.test" }, J);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Contains("slug", await duplicate.Content.ReadAsStringAsync());
        var taken = await (await anonymous.GetAsync($"/api/v1/signup/slug-availability?slug={slug}")).ReadAsync<SlugAvailabilityDto>();
        Assert.False(taken.Available);
        Assert.NotNull(taken.Suggestion);

        // Honeypot submissions are silently dropped.
        var bot = await (await anonymous.PostAsJsonAsync("/api/v1/signup", request with { Slug = slug + "x", Website = "http://spam.example" }, J)).ReadAsync<SignupStartedDto>();
        Assert.Null(bot.DemoCode);
        Assert.False(await factory.InCatalogAsync(db => db.Set<TrialSignup>().AnyAsync(s => s.Id == bot.SignupId)));
    }

    [Fact]
    public async Task Exceeding_a_limit_returns_402_and_a_downgrade_that_does_not_fit_returns_409()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var sub = await db.Subscriptions.FirstAsync();
            sub.LimitOverridesJson = "{\"MaxChannels\":0}";
            await db.SaveChangesAsync();
        });
        var channel = await admin.PostAsJsonAsync("/api/v1/channels", new CreateChannelAccountRequest { Type = "Chat", Provider = "Widget", Name = "Website chat" }, J);
        Assert.Equal(HttpStatusCode.PaymentRequired, channel.StatusCode);
        Assert.Equal("limit_exceeded", await channel.CodeAsync());

        // 12 agents do not fit the Starter plan (10 agents): the downgrade is refused with a clear code.
        for (var i = 0; i < 12; i++) await t.CreateAgentAsync($"Agent {i}");
        await factory.RunRecurringAsync(UsageMeteringTask.TaskName);
        var preview = await (await admin.PostAsJsonAsync("/api/v1/subscription/plan-change/preview", new ChangePlanRequest("starter", Seats: 15), J))
            .ReadAsync<PlanChangePreviewDto>();
        Assert.False(preview.Allowed);
        Assert.Contains(preview.Conflicts, c => c.Metric == "agents");
        var downgrade = await admin.PostAsJsonAsync("/api/v1/subscription/plan-change", new ChangePlanRequest("starter", Seats: 15), J);
        Assert.Equal(HttpStatusCode.Conflict, downgrade.StatusCode);
        Assert.Equal("plan_usage_exceeded", await downgrade.CodeAsync());
    }

    [Fact]
    public async Task Suspended_tenant_is_blocked_and_reactivation_unblocks()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var platform = await factory.PlatformClientAsync();
        var suspended = await (await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{t.Id}/suspend", new TenantActionRequest("Abuse report under review"), J))
            .ReadAsync<PlatformTenantDetailDto>();
        Assert.Equal("Suspended", suspended.Tenant.Status);

        var blocked = await admin.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest { Subject = "While suspended", SkipRouting = true }, J);
        Assert.Equal(HttpStatusCode.PaymentRequired, blocked.StatusCode);
        Assert.Equal("tenant_suspended", await blocked.CodeAsync());
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/tickets")).StatusCode);
        var billing = await (await admin.GetAsync("/api/v1/subscription")).ReadAsync<SubscriptionDto>();
        Assert.Equal("Suspended", billing.TenantStatus);

        await (await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{t.Id}/reactivate", new TenantActionRequest("Cleared"), J)).ReadAsync<PlatformTenantDetailDto>();
        var created = await admin.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest { Subject = "After reactivation", SkipRouting = true }, J);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task Trial_expiry_leads_to_grace_then_suspension_and_payment_reactivates()
    {
        var slug = "tr-" + Guid.NewGuid().ToString("N")[..8];
        Guid tenantId;
        using (var scope = factory.Services.CreateScope())
        {
            tenantId = (await scope.ServiceProvider.GetRequiredService<TenantProvisioningService>().ProvisionAsync(new ProvisionTenantRequest(
                slug, "Trial Co", "trial", $"admin@{slug}.test", "Admin", DeskApiFactory.Password, Trial: true, Seats: 3))).TenantId;
        }
        var admin = factory.ClientWithToken(await factory.LoginAsync(slug, $"admin@{slug}.test"));
        var start = DateTime.UtcNow;
        try
        {
            factory.Clock.Pin(start.AddDays(31));
            await factory.RunRecurringAsync(SubscriptionLifecycleTask.TaskName);
            var grace = await (await admin.GetAsync("/api/v1/subscription")).ReadAsync<SubscriptionDto>();
            Assert.Equal("PastDue", grace.Status);
            Assert.NotNull(grace.GraceEndsAt);
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest { Subject = "In grace", SkipRouting = true }, J)).StatusCode);

            factory.Clock.Pin(start.AddDays(40));
            await factory.RunRecurringAsync(SubscriptionLifecycleTask.TaskName);
        }
        finally
        {
            factory.Clock.Unpin();
        }
        Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest { Subject = "Blocked", SkipRouting = true }, J)).StatusCode);

        // Billing stays writable: choose a paid plan (first invoice issued), the operator records the payment, service resumes.
        var change = await (await admin.PostAsJsonAsync("/api/v1/subscription/plan-change", new ChangePlanRequest("starter", Seats: 3), J)).ReadAsync<ChangePlanResultDto>();
        Assert.NotNull(change.ProrationInvoice);
        Assert.Equal("Suspended", change.Subscription.TenantStatus);
        var platform = await factory.PlatformClientAsync();
        var paid = await (await platform.PostAsJsonAsync($"/api/v1/platform/invoices/{change.ProrationInvoice!.Id}/mark-paid", new MarkInvoicePaidRequest("BANK-REF-1"), J))
            .ReadAsync<InvoiceDto>();
        Assert.Equal("Paid", paid.Summary.Status);
        var sub = await (await admin.GetAsync("/api/v1/subscription")).ReadAsync<SubscriptionDto>();
        Assert.Equal("Active", sub.Status);
        Assert.Equal("Active", sub.TenantStatus);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest { Subject = "Paid", SkipRouting = true }, J)).StatusCode);
        _ = tenantId;
    }

    [Fact]
    public async Task Invoice_generation_bills_each_period_exactly_once()
    {
        var t = await factory.CreateTenantAsync();
        await factory.RunRecurringAsync(InvoiceGenerationTask.TaskName);
        await factory.RunRecurringAsync(InvoiceGenerationTask.TaskName);
        Assert.Equal(1, await factory.InvoiceCountAsync(t.Id));
        try
        {
            factory.Clock.Pin(DateTime.UtcNow.AddDays(35));
            await factory.RunRecurringAsync(InvoiceGenerationTask.TaskName);
            await factory.RunRecurringAsync(InvoiceGenerationTask.TaskName);
        }
        finally
        {
            factory.Clock.Unpin();
        }
        var periods = await factory.InCatalogAsync(db => db.AllTenants<BillingInvoice>().Where(i => i.TenantId == t.Id).Select(i => i.PeriodStart).ToListAsync());
        Assert.Equal(2, periods.Count);
        Assert.Equal(2, periods.Distinct().Count());

        // The tenant administrator sees the invoices and can download a PDF.
        var admin = await t.AdminClientAsync();
        var list = await (await admin.GetAsync("/api/v1/subscription/invoices")).ReadAsync<PagedResult<InvoiceSummaryDto>>();
        Assert.Equal(2, list.Items.Count);
        var pdf = await admin.GetAsync($"/api/v1/subscription/invoices/{list.Items[0].Id}/pdf");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        var bytes = await pdf.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public async Task Upgrades_are_prorated_and_downgrades_are_scheduled_for_period_end()
    {
        var t = await factory.CreateTenantAsync(plan: "professional");
        var admin = await t.AdminClientAsync();
        var up = await (await admin.PostAsJsonAsync("/api/v1/subscription/plan-change", new ChangePlanRequest("enterprise"), J)).ReadAsync<ChangePlanResultDto>();
        Assert.Equal("enterprise", up.Subscription.Plan.Code);
        Assert.NotNull(up.ProrationInvoice);
        Assert.Equal("Proration", up.ProrationInvoice!.Kind);
        Assert.True(up.ProrationInvoice.Total > 0);

        var down = await (await admin.PostAsJsonAsync("/api/v1/subscription/plan-change", new ChangePlanRequest("professional"), J)).ReadAsync<ChangePlanResultDto>();
        Assert.Equal("enterprise", down.Subscription.Plan.Code);
        Assert.Equal("professional", down.Subscription.PendingPlan?.Code);
        Assert.Null(down.ProrationInvoice);
    }

    [Fact]
    public async Task Platform_endpoints_refuse_tenant_users_and_tenant_endpoints_refuse_operators()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var platform = await factory.PlatformClientAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/platform/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{t.Id}/suspend", new TenantActionRequest("x"), J)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/v1/platform/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await platform.GetAsync("/api/v1/subscription")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await platform.GetAsync("/api/v1/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await platform.GetAsync("/api/v1/admin/audit")).StatusCode);

        var tenants = await (await platform.GetAsync($"/api/v1/platform/tenants?search={t.Slug}")).ReadAsync<PagedResult<PlatformTenantDto>>();
        Assert.Contains(tenants.Items, x => x.Id == t.Id);
        var overview = await (await platform.GetAsync("/api/v1/platform/overview")).ReadAsync<PlatformOverviewDto>();
        Assert.True(overview.Tenants > 0);
    }

    [Fact]
    public async Task Support_access_needs_tenant_approval_and_expires()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var platform = await factory.PlatformClientAsync();
        var grant = await (await platform.PostAsJsonAsync($"/api/v1/platform/support-access/tenants/{t.Id}",
            new RequestSupportAccessRequest("Investigate slow searches reported in case 42", 30), J)).ReadAsync<SupportAccessDto>();
        Assert.Equal("Pending", grant.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await platform.GetAsync($"/api/v1/platform/support-access/{grant.Id}/diagnostics")).StatusCode);

        var approved = await (await admin.PostAsJsonAsync($"/api/v1/admin/support-access/{grant.Id}/decision", new DecideSupportAccessRequest(true, "OK for 30 minutes"), J))
            .ReadAsync<SupportAccessDto>();
        Assert.True(approved.Active);
        var diagnostics = await (await platform.GetAsync($"/api/v1/platform/support-access/{grant.Id}/diagnostics")).ReadAsync<SupportDiagnosticsDto>();
        Assert.Equal(t.Id, diagnostics.TenantId);

        await (await admin.PostAsync($"/api/v1/admin/support-access/{grant.Id}/revoke", null)).ReadAsync<SupportAccessDto>();
        Assert.Equal(HttpStatusCode.Forbidden, (await platform.GetAsync($"/api/v1/platform/support-access/{grant.Id}/diagnostics")).StatusCode);
    }
}
