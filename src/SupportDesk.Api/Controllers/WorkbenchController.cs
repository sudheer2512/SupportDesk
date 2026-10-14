using Microsoft.AspNetCore.Mvc;
using SupportDesk.Application.Approvals;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Meta;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Api.Controllers;

/// <summary>Saved views (personal and shared).</summary>
[Route("api/v1/views")]
public sealed class ViewsController(TicketCollaborationService collaboration) : DeskControllerBase
{
    [HttpGet]
    public Task<List<SavedViewDto>> List([FromQuery] string scope = "tickets", [FromQuery] bool counts = false, CancellationToken ct = default) =>
        collaboration.ListViewsAsync(scope, counts, ct);

    [HttpPost]
    public Task<SavedViewDto> Create(SaveViewRequest request, CancellationToken ct) => collaboration.SaveViewAsync(null, request, ct);

    [HttpPut("{id:guid}")]
    public Task<SavedViewDto> Update(Guid id, SaveViewRequest request, CancellationToken ct) => collaboration.SaveViewAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await collaboration.DeleteViewAsync(id, ct);
        return NoContent();
    }
}

/// <summary>Reply templates / macros.</summary>
[Route("api/v1/templates")]
public sealed class TemplatesController(TicketCollaborationService collaboration) : DeskControllerBase
{
    public sealed record TemplateDto(Guid Id, string Name, string? Category, string? Subject, string BodyHtml, string Visibility, Guid? OwnerUserId,
        Guid? DepartmentId, Guid? TeamId, System.Text.Json.JsonElement Actions, int UsageCount);

    public sealed record SaveTemplateRequest(string Name, string? Category, string? Subject, string BodyHtml, string Visibility, Guid? DepartmentId,
        Guid? TeamId, System.Text.Json.JsonElement? Actions);

    private static TemplateDto Map(ReplyTemplate t) => new(t.Id, t.Name, t.Category, t.Subject, t.BodyHtml, t.Visibility.ToString(), t.OwnerUserId,
        t.DepartmentId, t.TeamId, Application.Common.Json.Element(t.ActionsJson, "[]"), t.UsageCount);

    [HttpGet]
    public async Task<List<TemplateDto>> List(CancellationToken ct) => (await collaboration.ListTemplatesAsync(ct)).Select(Map).ToList();

    [HttpPost]
    public async Task<TemplateDto> Create(SaveTemplateRequest r, CancellationToken ct) =>
        Map(await collaboration.SaveTemplateAsync(null, r.Name, r.Category, r.Subject, r.BodyHtml, r.Visibility, r.DepartmentId, r.TeamId, r.Actions?.GetRawText(), ct));

    [HttpPut("{id:guid}")]
    public async Task<TemplateDto> Update(Guid id, SaveTemplateRequest r, CancellationToken ct) =>
        Map(await collaboration.SaveTemplateAsync(id, r.Name, r.Category, r.Subject, r.BodyHtml, r.Visibility, r.DepartmentId, r.TeamId, r.Actions?.GetRawText(), ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await collaboration.DeleteTemplateAsync(id, ct);
        return NoContent();
    }
}

/// <summary>Form/filter metadata for the workbench.</summary>
[Route("api/v1/meta")]
public sealed class MetaController(MetaService meta) : DeskControllerBase
{
    [HttpGet("ticket-form")]
    public Task<TicketFormMetaDto> TicketForm(CancellationToken ct) => meta.TicketFormAsync(ct);

    [HttpGet("condition-fields")]
    public object ConditionFields() => new
    {
        fields = TicketConditionContext.FieldCatalog.Select(f => new { field = f.Field, label = f.Label, type = f.Type }),
        operators = ConditionOps.All
    };

    [HttpGet("permissions")]
    public object PermissionCatalog() => Permissions.Catalog.Select(p => new { p.Code, p.Module, p.Description, p.Sensitive });
}

/// <summary>File uploads (validated, scanned) and attachment downloads (authorised through the owning ticket).</summary>
[Route("api/v1")]
public sealed class FilesController(FileService files, TicketCollaborationService collaboration) : DeskControllerBase
{
    [HttpPost("files")]
    [RequestSizeLimit(110 * 1024 * 1024)]
    public async Task<ActionResult<UploadedFileDto>> Upload(IFormFile file, [FromQuery] string purpose = "attachment", CancellationToken ct = default)
    {
        await using var stream = file.OpenReadStream();
        var stored = await files.UploadAsync(stream, file.FileName, file.ContentType, purpose, ct);
        return new UploadedFileDto(stored.Id, stored.FileName, stored.ContentType, stored.SizeBytes, stored.ScanStatus.ToString());
    }

    [HttpGet("attachments/{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, [FromQuery] bool inline = false, CancellationToken ct = default)
    {
        var (attachment, stream) = await collaboration.OpenAttachmentAsync(id, ct);
        Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
        var safeInline = inline && FileService.IsInlineSafe(attachment.ContentType);
        var result = File(stream, safeInline ? attachment.ContentType : "application/octet-stream", safeInline ? null : attachment.FileName, enableRangeProcessing: true);
        if (safeInline) Response.Headers.ContentDisposition = $"inline; filename=\"{attachment.FileName.Replace("\"", "")}\"";
        return result;
    }
}

/// <summary>Approval requests (transfers, blueprint transitions, AI actions, custom).</summary>
[Route("api/v1/approvals")]
public sealed class ApprovalsController(ApprovalService approvals) : DeskControllerBase
{
    [HttpGet]
    public Task<List<ApprovalDto>> List([FromQuery] string? status, [FromQuery] bool mine = false, CancellationToken ct = default) =>
        approvals.ListAsync(status, mine, ct);

    [HttpPost]
    public async Task<IActionResult> Create(CreateApprovalRequest request, CancellationToken ct)
    {
        var a = await approvals.CreateAsync(request, ct);
        return Ok(new { id = a.Id });
    }

    [HttpPost("{id:guid}/decide")]
    public Task<ApprovalDto> Decide(Guid id, DecideApprovalRequest request, CancellationToken ct) => approvals.DecideAsync(id, request, ct);
}
