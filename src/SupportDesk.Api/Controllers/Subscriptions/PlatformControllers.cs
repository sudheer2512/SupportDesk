using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Subscriptions;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Platform;
using SupportDesk.Contracts.Subscriptions;

namespace SupportDesk.Api.Controllers.Subscriptions;

/// <summary>Base for the platform operator console: platform tokens only (tenant agents, contacts and API clients get 403).</summary>
[ApiController]
[Authorize(Policy = Policies.Platform)]
[EnableRateLimiting(HostingSetup.ApiLimiter)]
[Produces("application/json")]
public abstract class PlatformControllerBase : ControllerBase;

/// <summary>Tenants: overview, list/search/detail, suspend, reactivate, cancel, extend trial, change plan, dedicated database.</summary>
[Route("api/v1/platform")]
public sealed class PlatformTenantsController(PlatformTenantService tenants, DedicatedDatabaseProvisioner dedicated) : PlatformControllerBase
{
    [HttpGet("overview")]
    public Task<PlatformOverviewDto> Overview(CancellationToken ct) => tenants.OverviewAsync(ct);

    [HttpGet("tenants")]
    public Task<PagedResult<PlatformTenantDto>> List([FromQuery] string? search, [FromQuery] string? status, [FromQuery] string? cursor,
        [FromQuery] int limit = 50, CancellationToken ct = default) => tenants.ListAsync(search, status, cursor, limit, ct);

    [HttpGet("tenants/{id:guid}")]
    public Task<PlatformTenantDetailDto> Get(Guid id, CancellationToken ct) => tenants.GetAsync(id, ct);

    [HttpPost("tenants/{id:guid}/suspend")]
    public Task<PlatformTenantDetailDto> Suspend(Guid id, TenantActionRequest request, CancellationToken ct) => tenants.SuspendAsync(id, request, ct);

    [HttpPost("tenants/{id:guid}/reactivate")]
    public Task<PlatformTenantDetailDto> Reactivate(Guid id, TenantActionRequest request, CancellationToken ct) => tenants.ReactivateAsync(id, request, ct);

    [HttpPost("tenants/{id:guid}/cancel")]
    public Task<PlatformTenantDetailDto> Cancel(Guid id, CancelTenantRequest request, CancellationToken ct) => tenants.CancelAsync(id, request, ct);

    [HttpPost("tenants/{id:guid}/extend-trial")]
    public Task<PlatformTenantDetailDto> ExtendTrial(Guid id, ExtendTrialRequest request, CancellationToken ct) => tenants.ExtendTrialAsync(id, request, ct);

    [HttpPost("tenants/{id:guid}/change-plan")]
    public Task<PlatformTenantDetailDto> ChangePlan(Guid id, OperatorChangePlanRequest request, CancellationToken ct) => tenants.ChangePlanAsync(id, request, ct);

    [HttpPost("tenants/{id:guid}/dedicated-database")]
    public Task<DedicatedDatabaseResultDto> Dedicated(Guid id, ProvisionDedicatedRequest request, CancellationToken ct) => dedicated.ProvisionAsync(id, request, ct);
}

/// <summary>Plans CRUD.</summary>
[Route("api/v1/platform/plans")]
public sealed class PlatformPlansController(PlatformPlanService plans) : PlatformControllerBase
{
    [HttpGet]
    public Task<List<PlanDto>> List(CancellationToken ct) => plans.ListAsync(ct);

    [HttpGet("features")]
    public IReadOnlyList<PlanFeatureDto> Features() => plans.Features();

    [HttpPost]
    public Task<PlanDto> Create(SavePlanRequest request, CancellationToken ct) => plans.CreateAsync(request, ct);

    [HttpPut("{id:guid}")]
    public Task<PlanDto> Update(Guid id, SavePlanRequest request, CancellationToken ct) => plans.UpdateAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await plans.DeleteAsync(id, ct);
        return NoContent();
    }
}

/// <summary>Invoices across tenants: list, detail, PDF, mark paid (offline payments), void.</summary>
[Route("api/v1/platform/invoices")]
public sealed class PlatformInvoicesController(PlatformInvoiceService invoices) : PlatformControllerBase
{
    [HttpGet]
    public Task<PagedResult<InvoiceSummaryDto>> List([FromQuery] string? status, [FromQuery] Guid? tenantId, [FromQuery] string? cursor, [FromQuery] int limit = 50,
        CancellationToken ct = default) => invoices.ListAsync(status, tenantId, cursor, limit, ct);

    [HttpGet("{id:guid}")]
    public Task<InvoiceDto> Get(Guid id, CancellationToken ct) => invoices.GetAsync(id, ct);

    [HttpGet("{id:guid}/pdf")]
    [Produces("application/pdf")]
    public async Task<IActionResult> Pdf(Guid id, CancellationToken ct)
    {
        var (content, fileName) = await invoices.PdfAsync(id, ct);
        return File(content, "application/pdf", fileName);
    }

    [HttpPost("{id:guid}/mark-paid")]
    public Task<InvoiceDto> MarkPaid(Guid id, MarkInvoicePaidRequest request, CancellationToken ct) => invoices.MarkPaidAsync(id, request, ct);

    [HttpPost("{id:guid}/void")]
    public Task<InvoiceDto> Void(Guid id, VoidInvoiceRequest request, CancellationToken ct) => invoices.VoidAsync(id, request, ct);
}

/// <summary>Platform audit trail and its hash-chain verification.</summary>
[Route("api/v1/platform/audit")]
public sealed class PlatformAuditController(PlatformAuditService audit) : PlatformControllerBase
{
    [HttpGet]
    public Task<PagedResult<PlatformAuditEntryDto>> List([FromQuery] string? search, [FromQuery] string? action, [FromQuery] Guid? tenantId, [FromQuery] string? cursor,
        [FromQuery] int limit = 50, CancellationToken ct = default) => audit.ListAsync(search, action, tenantId, cursor, limit, ct);

    [HttpGet("actions")]
    public Task<List<string>> Actions(CancellationToken ct) => audit.ActionsAsync(ct);

    [HttpPost("verify")]
    public Task<AuditVerificationResult> Verify(CancellationToken ct) => audit.VerifyAsync(ct);
}

/// <summary>Support access requests (operator side) and read-only diagnostics while an approved grant is active.</summary>
[Route("api/v1/platform/support-access")]
public sealed class PlatformSupportAccessController(SupportAccessService supportAccess) : PlatformControllerBase
{
    [HttpGet]
    public Task<List<SupportAccessDto>> List([FromQuery] Guid? tenantId, CancellationToken ct) => supportAccess.OperatorListAsync(tenantId, ct);

    [HttpPost("tenants/{tenantId:guid}")]
    public Task<SupportAccessDto> RequestAccess(Guid tenantId, RequestSupportAccessRequest request, CancellationToken ct) => supportAccess.RequestAsync(tenantId, request, ct);

    [HttpGet("{id:guid}/diagnostics")]
    public Task<SupportDiagnosticsDto> Diagnostics(Guid id, CancellationToken ct) => supportAccess.DiagnosticsAsync(id, ct);
}
