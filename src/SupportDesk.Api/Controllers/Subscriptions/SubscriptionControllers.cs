using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Application.Subscriptions;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Platform;
using SupportDesk.Contracts.Subscriptions;

namespace SupportDesk.Api.Controllers.Subscriptions;

/// <summary>
/// The tenant administrator's subscription and billing (admin.billing). Routed under /api/v1/subscription, which stays
/// writable while the tenant is suspended so the plan can be changed and invoices paid.
/// </summary>
[Route("api/v1/subscription")]
public sealed class SubscriptionController(SubscriptionService subscriptions) : DeskControllerBase
{
    [HttpGet]
    public Task<SubscriptionDto> Get(CancellationToken ct) => subscriptions.GetAsync(ct);

    [HttpGet("plans")]
    public Task<List<PlanDto>> Plans(CancellationToken ct) => subscriptions.PlansAsync(ct);

    [HttpGet("features")]
    public IReadOnlyList<PlanFeatureDto> Features() => subscriptions.Features();

    [HttpGet("usage-history")]
    public Task<IReadOnlyList<UsageHistoryDto>> UsageHistory([FromQuery] int days = 30, CancellationToken ct = default) => subscriptions.HistoryAsync(days, ct);

    [HttpPost("plan-change/preview")]
    public Task<PlanChangePreviewDto> Preview(ChangePlanRequest request, CancellationToken ct) => subscriptions.PreviewAsync(request, ct);

    [HttpPost("plan-change")]
    public Task<ChangePlanResultDto> ChangePlan(ChangePlanRequest request, CancellationToken ct) => subscriptions.ChangePlanAsync(request, ct);

    [HttpDelete("plan-change")]
    public Task<SubscriptionDto> CancelPendingChange(CancellationToken ct) => subscriptions.CancelPendingChangeAsync(ct);

    [HttpPut("billing-contact")]
    public Task<SubscriptionDto> BillingContact(UpdateBillingContactRequest request, CancellationToken ct) => subscriptions.UpdateBillingContactAsync(request, ct);

    [HttpGet("invoices")]
    public Task<PagedResult<InvoiceSummaryDto>> Invoices([FromQuery] string? cursor, [FromQuery] int limit = 25, CancellationToken ct = default) =>
        subscriptions.InvoicesAsync(cursor, limit, ct);

    [HttpGet("invoices/{id:guid}")]
    public Task<InvoiceDto> Invoice(Guid id, CancellationToken ct) => subscriptions.InvoiceAsync(id, ct);

    [HttpGet("invoices/{id:guid}/pdf")]
    [Produces("application/pdf")]
    public async Task<IActionResult> InvoicePdf(Guid id, CancellationToken ct)
    {
        var (content, fileName) = await subscriptions.InvoicePdfAsync(id, ct);
        return File(content, "application/pdf", fileName);
    }
}

/// <summary>
/// Platform support-access requests as seen by the tenant (admin.security or admin.compliance): approve, reject, revoke.
/// There is no impersonation; an approved grant only unlocks read-only diagnostics for the operator, until it expires.
/// </summary>
[Route("api/v1/admin/support-access")]
public sealed class SupportAccessController(SupportAccessService supportAccess) : DeskControllerBase
{
    [HttpGet]
    public Task<List<SupportAccessDto>> List(CancellationToken ct) => supportAccess.TenantListAsync(ct);

    [HttpPost("{id:guid}/decision")]
    public Task<SupportAccessDto> Decide(Guid id, DecideSupportAccessRequest request, CancellationToken ct) => supportAccess.DecideAsync(id, request, ct);

    [HttpPost("{id:guid}/revoke")]
    public Task<SupportAccessDto> Revoke(Guid id, CancellationToken ct) => supportAccess.RevokeAsync(id, ct);
}

/// <summary>Public trial sign-up: slug availability, public plans, start (e-mails a code) and verify (provisions the workspace).</summary>
[ApiController]
[Route("api/v1/signup")]
[AllowAnonymous]
[EnableRateLimiting(HostingSetup.AuthLimiter)]
[Produces("application/json")]
public sealed class SignupController(SignupService signup) : ControllerBase
{
    [HttpGet("slug-availability")]
    [EnableRateLimiting(HostingSetup.PublicLimiter)]
    public Task<SlugAvailabilityDto> Slug([FromQuery] string? slug, CancellationToken ct) => signup.CheckSlugAsync(slug, ct);

    [HttpGet("plans")]
    [EnableRateLimiting(HostingSetup.PublicLimiter)]
    public Task<List<PlanDto>> Plans(CancellationToken ct) => signup.PlansAsync(ct);

    [HttpPost]
    [RequestSizeLimit(16 * 1024)]
    public async Task<ActionResult<SignupStartedDto>> Start(SignupRequest request, CancellationToken ct) =>
        Accepted(await signup.StartAsync(request, ct));

    [HttpPost("{id:guid}/verify")]
    [RequestSizeLimit(4 * 1024)]
    public Task<SignupCompletedDto> Verify(Guid id, SignupVerifyRequest request, CancellationToken ct) => signup.VerifyAsync(id, request, ct);
}
