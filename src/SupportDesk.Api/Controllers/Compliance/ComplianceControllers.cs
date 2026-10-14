using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Application.Administration.Compliance;
using SupportDesk.Application.Administration.ConfigTransfer;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Compliance;
using SupportDesk.Contracts.ConfigTransfer;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Api.Controllers.Compliance;

/// <summary>Audit viewer (admin.audit): search/filter, detail with before/after diff, CSV export and hash-chain verification.</summary>
[Route("api/v1/admin/audit")]
public sealed class AuditController(AuditQueryService audit) : DeskControllerBase
{
    [HttpGet]
    public Task<PagedResult<AuditEntryDto>> Search([FromQuery] string? actor, [FromQuery] string? entityType, [FromQuery] Guid? entityId, [FromQuery] string? action,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? ip, [FromQuery] string? search, [FromQuery] string? cursor,
        [FromQuery] int limit = 50, CancellationToken ct = default) =>
        audit.SearchAsync(new AuditQuery(actor, entityType, entityId, action, from, to, ip, search, cursor, limit), ct);

    [HttpGet("facets")]
    public Task<AuditFacetsDto> Facets(CancellationToken ct) => audit.FacetsAsync(ct);

    [HttpGet("{id:guid}")]
    public Task<AuditEntryDetailDto> Get(Guid id, CancellationToken ct) => audit.GetAsync(id, ct);

    [HttpGet("export")]
    [Produces("text/csv")]
    public async Task<IActionResult> Export([FromQuery] string? actor, [FromQuery] string? entityType, [FromQuery] Guid? entityId, [FromQuery] string? action,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? ip, [FromQuery] string? search, CancellationToken ct)
    {
        var (content, fileName) = await audit.ExportCsvAsync(new AuditQuery(actor, entityType, entityId, action, from, to, ip, search), ct);
        return File(content, "text/csv; charset=utf-8", fileName);
    }

    [HttpPost("verify")]
    public Task<AuditChainVerificationDto> Verify(CancellationToken ct) => audit.VerifyAsync(ct);
}

/// <summary>Compliance (admin.compliance): retention policies, legal holds and data-subject requests.</summary>
[Route("api/v1/admin/compliance")]
public sealed class ComplianceController(RetentionService retention, LegalHoldService holds, DataRequestService requests) : DeskControllerBase
{
    // Retention
    [HttpGet("retention/data-types")]
    public IReadOnlyList<RetentionDataTypeDto> DataTypes() => RetentionDataTypes.All;

    [HttpGet("retention")]
    public Task<List<RetentionPolicyDto>> Policies(CancellationToken ct) => retention.ListAsync(ct);

    [HttpPost("retention")]
    public Task<RetentionPolicyDto> CreatePolicy(SaveRetentionPolicyRequest request, CancellationToken ct) => retention.CreateAsync(request, ct);

    [HttpPut("retention/{id:guid}")]
    public Task<RetentionPolicyDto> UpdatePolicy(Guid id, SaveRetentionPolicyRequest request, CancellationToken ct) => retention.UpdateAsync(id, request, ct);

    [HttpDelete("retention/{id:guid}")]
    public async Task<IActionResult> DeletePolicy(Guid id, CancellationToken ct)
    {
        await retention.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Counts what a proposed (unsaved) policy would affect now.</summary>
    [HttpPost("retention/dry-run")]
    public Task<RetentionDryRunDto> DryRunProposed(SaveRetentionPolicyRequest request, CancellationToken ct) => retention.DryRunAsync(request, null, ct);

    [HttpPost("retention/{id:guid}/dry-run")]
    public Task<RetentionDryRunDto> DryRun(Guid id, CancellationToken ct) => retention.DryRunAsync(id, ct);

    [HttpPost("retention/{id:guid}/run")]
    public Task<RetentionRunResultDto> Run(Guid id, CancellationToken ct) => retention.RunNowAsync(id, ct);

    // Legal holds
    [HttpGet("legal-holds")]
    public Task<List<LegalHoldDto>> Holds([FromQuery] bool includeReleased = false, CancellationToken ct = default) => holds.ListAsync(includeReleased, ct);

    [HttpPost("legal-holds")]
    public Task<LegalHoldDto> CreateHold(SaveLegalHoldRequest request, CancellationToken ct) => holds.CreateAsync(request, ct);

    [HttpPost("legal-holds/{id:guid}/release")]
    public Task<LegalHoldDto> Release(Guid id, CancellationToken ct) => holds.ReleaseAsync(id, ct);

    // Data subject requests
    [HttpGet("requests")]
    public Task<List<DataRequestDto>> Requests([FromQuery] string? status, CancellationToken ct) => requests.ListAsync(status, ct);

    [HttpGet("requests/{id:guid}")]
    public Task<DataRequestDto> GetRequest(Guid id, CancellationToken ct) => requests.GetAsync(id, ct);

    [HttpPost("requests")]
    public Task<DataRequestDto> CreateRequest(CreateDataRequestRequest request, CancellationToken ct) => requests.CreateAsync(request, ct);

    [HttpPost("requests/{id:guid}/approve")]
    public Task<DataRequestDto> Approve(Guid id, CancellationToken ct) => requests.ApproveAsync(id, ct);

    [HttpPost("requests/{id:guid}/reject")]
    public Task<DataRequestDto> Reject(Guid id, RejectDataRequestRequest request, CancellationToken ct) => requests.RejectAsync(id, request, ct);

    [HttpPost("requests/{id:guid}/download-link")]
    public Task<DownloadLinkDto> DownloadLink(Guid id, CancellationToken ct) => requests.CreateDownloadLinkAsync(id, ct);
}

/// <summary>
/// One-time download of a data-subject export. Anonymous: the unguessable token is the credential (single use, expiring);
/// the tenant id in the path selects the tenant (and its database) and must match the token's request.
/// </summary>
[ApiController]
[Route("api/v1/compliance/downloads")]
[AllowAnonymous]
[EnableRateLimiting(HostingSetup.PublicLimiter)]
public sealed class ComplianceDownloadsController(DataRequestService requests, RequestContext ctx) : ControllerBase
{
    [HttpGet("{tenantId:guid}/{token}")]
    public async Task<IActionResult> Download(Guid tenantId, string token, CancellationToken ct)
    {
        if (token.Length is < 20 or > 100) return NotFound();
        ctx.SetSystem(tenantId, "Export download");
        var (stream, fileName) = await requests.DownloadAsync(token, ct);
        Response.Headers.CacheControl = "no-store";
        return File(stream, "application/zip", fileName);
    }
}

/// <summary>Configuration transfer (admin.config_transfer): export, import into drafts, resolve, publish, snapshots and rollback.</summary>
[Route("api/v1/admin/config")]
public sealed class ConfigTransferController(ConfigTransferService config) : DeskControllerBase
{
    [HttpGet("sections")]
    public Task<List<ConfigSectionInfoDto>> Sections(CancellationToken ct) => config.SectionsAsync(ct);

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] string? sections, CancellationToken ct)
    {
        var list = string.IsNullOrWhiteSpace(sections) ? null : sections.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var (content, fileName) = await config.ExportAsync(list, ct);
        return File(content, "application/json", fileName);
    }

    [HttpPost("imports")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public Task<ConfigDraftDto> Import(ImportConfigRequest request, CancellationToken ct) => config.ImportAsync(request, ct);

    [HttpGet("imports")]
    public Task<List<ConfigDraftDto>> Drafts(CancellationToken ct) => config.DraftsAsync(ct);

    [HttpGet("imports/{id:guid}")]
    public Task<ConfigDraftDto> Draft(Guid id, CancellationToken ct) => config.DraftAsync(id, ct);

    [HttpPut("imports/{id:guid}/resolutions")]
    public Task<ConfigDraftDto> Resolutions(Guid id, SaveResolutionsRequest request, CancellationToken ct) => config.SaveResolutionsAsync(id, request, ct);

    [HttpPost("imports/{id:guid}/publish")]
    public Task<ConfigPublishResultDto> Publish(Guid id, CancellationToken ct) => config.PublishAsync(id, ct);

    [HttpDelete("imports/{id:guid}")]
    public async Task<IActionResult> Discard(Guid id, CancellationToken ct)
    {
        await config.DiscardAsync(id, ct);
        return NoContent();
    }

    [HttpGet("snapshots")]
    public Task<List<ConfigSnapshotDto>> Snapshots(CancellationToken ct) => config.SnapshotsAsync(ct);

    [HttpPost("snapshots")]
    public Task<ConfigSnapshotDto> Capture(CreateSnapshotRequest request, CancellationToken ct) => config.CreateSnapshotAsync(request, ct);

    [HttpGet("snapshots/{id:guid}/download")]
    public async Task<IActionResult> DownloadSnapshot(Guid id, CancellationToken ct)
    {
        var (content, fileName) = await config.DownloadSnapshotAsync(id, ct);
        return File(content, "application/json", fileName);
    }

    [HttpPost("snapshots/{id:guid}/rollback")]
    public Task<ConfigPublishResultDto> Rollback(Guid id, RollbackRequest request, CancellationToken ct) => config.RollbackAsync(id, request, ct);
}
