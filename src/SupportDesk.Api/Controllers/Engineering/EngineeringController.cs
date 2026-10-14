using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Application.Integrations.Engineering;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Engineering;
using SupportDesk.Contracts.Erp;

namespace SupportDesk.Api.Controllers.Engineering;

/// <summary>
/// Engineering escalation from tickets (engineering.link / tickets.view) and the built-in engineering board
/// (engineering.view / engineering.manage). Permissions and ticket scope are enforced in the services.
/// </summary>
[Route("api/v1/engineering")]
public sealed class EngineeringController(EngineeringTicketService tickets, EngineeringBoardService board) : DeskControllerBase
{
    [HttpGet("trackers/active")]
    public Task<List<ActiveTrackerDto>> ActiveTrackers(CancellationToken ct) => tickets.ActiveTrackersAsync(ct);

    // ───── Ticket panel ─────

    /// <summary>Links of a ticket with their last known state; refresh=true re-reads stale items from the trackers first.</summary>
    [HttpGet("tickets/{ticketId:guid}")]
    public Task<TicketEngineeringDto> Ticket(Guid ticketId, [FromQuery] bool refresh = false, CancellationToken ct = default) =>
        tickets.GetAsync(ticketId, refresh, ct);

    [HttpGet("tickets/{ticketId:guid}/prefill")]
    public Task<EscalationPrefillDto> Prefill(Guid ticketId, CancellationToken ct) => tickets.PrefillAsync(ticketId, ct);

    /// <summary>Creates a work item in the tracker from the ticket and links it.</summary>
    [HttpPost("tickets/{ticketId:guid}/escalate")]
    public Task<EscalationResultDto> Escalate(Guid ticketId, EscalateToEngineeringRequest request, CancellationToken ct) => tickets.EscalateAsync(ticketId, request, ct);

    /// <summary>Links an existing work item by id or URL.</summary>
    [HttpPost("tickets/{ticketId:guid}/links")]
    public Task<EscalationResultDto> Link(Guid ticketId, LinkWorkItemRequest request, CancellationToken ct) => tickets.LinkAsync(ticketId, request, ct);

    [HttpDelete("tickets/{ticketId:guid}/links/{linkId:guid}")]
    public async Task<IActionResult> Unlink(Guid ticketId, Guid linkId, CancellationToken ct)
    {
        await tickets.UnlinkAsync(ticketId, linkId, ct);
        return NoContent();
    }

    [HttpPost("tickets/{ticketId:guid}/links/{linkId:guid}/refresh")]
    public Task<EngineeringLinkViewDto> Refresh(Guid ticketId, Guid linkId, CancellationToken ct) => tickets.RefreshAsync(ticketId, linkId, ct);

    /// <summary>Other tickets linked to the same work item (scope-filtered).</summary>
    [HttpGet("tickets/{ticketId:guid}/links/{linkId:guid}/tickets")]
    public Task<EngineeringAffectedTicketsDto> Affected(Guid ticketId, Guid linkId, CancellationToken ct) => tickets.AffectedTicketsAsync(ticketId, linkId, ct);

    // ───── Built-in board ─────

    [HttpGet("board")]
    public Task<EngineeringBoardDto> Board([FromQuery] Guid? trackerId, CancellationToken ct) => board.BoardAsync(trackerId, ct);

    [HttpGet("assignees")]
    public Task<List<EngineeringAssigneeDto>> Assignees(CancellationToken ct) => board.AssigneesAsync(ct);

    /// <summary>Keyset-paged work items (most recently updated first).</summary>
    [HttpGet("work-items")]
    public Task<PagedResult<EngineeringWorkItemDto>> WorkItems([FromQuery] Guid? trackerId, [FromQuery] string? state, [FromQuery] Guid? assigneeId, [FromQuery] Guid? releaseId,
        [FromQuery] string? q, [FromQuery] bool includeClosed = false, [FromQuery] string? cursor = null, [FromQuery] int limit = 100, CancellationToken ct = default) =>
        board.ListAsync(trackerId, state, assigneeId, releaseId, q, includeClosed, cursor, limit, ct);

    [HttpGet("work-items/{id:guid}")]
    public Task<EngineeringWorkItemDetailDto> WorkItem(Guid id, CancellationToken ct) => board.GetAsync(id, ct);

    [HttpPost("work-items")]
    public async Task<ActionResult<EngineeringWorkItemDto>> CreateWorkItem(SaveEngineeringWorkItemRequest request, CancellationToken ct)
    {
        var item = await board.CreateAsync(request, ct);
        return CreatedAtAction(nameof(WorkItem), new { id = item.Id }, item);
    }

    [HttpPut("work-items/{id:guid}")]
    public Task<EngineeringWorkItemDto> UpdateWorkItem(Guid id, SaveEngineeringWorkItemRequest request, CancellationToken ct) => board.UpdateAsync(id, request, ct);

    [HttpPost("work-items/{id:guid}/move")]
    public Task<EngineeringWorkItemDto> Move(Guid id, MoveEngineeringWorkItemRequest request, CancellationToken ct) => board.MoveAsync(id, request, ct);

    [HttpDelete("work-items/{id:guid}")]
    public async Task<IActionResult> DeleteWorkItem(Guid id, CancellationToken ct)
    {
        await board.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpGet("releases")]
    public Task<List<EngineeringReleaseDto>> Releases([FromQuery] Guid? trackerId, CancellationToken ct) => board.ReleasesAsync(trackerId, ct);

    [HttpPost("releases")]
    public Task<EngineeringReleaseDto> CreateRelease(SaveEngineeringReleaseRequest request, CancellationToken ct) => board.SaveReleaseAsync(null, request, ct);

    [HttpPut("releases/{id:guid}")]
    public Task<EngineeringReleaseDto> UpdateRelease(Guid id, SaveEngineeringReleaseRequest request, CancellationToken ct) => board.SaveReleaseAsync(id, request, ct);

    /// <summary>Publishes the release (and optionally records the deployment): linked tickets are updated and notified.</summary>
    [HttpPost("releases/{id:guid}/publish")]
    public Task<EngineeringReleaseDto> PublishRelease(Guid id, PublishEngineeringReleaseRequest request, CancellationToken ct) => board.PublishReleaseAsync(id, request, ct);

    [HttpDelete("releases/{id:guid}")]
    public async Task<IActionResult> DeleteRelease(Guid id, CancellationToken ct)
    {
        await board.DeleteReleaseAsync(id, ct);
        return NoContent();
    }
}

/// <summary>Engineering administration (admin.integrations): trackers, mappings, webhook secrets, sync, logs and auto-notify settings.</summary>
[Route("api/v1/engineering/admin")]
public sealed class EngineeringAdminController(EngineeringAdminService admin) : DeskControllerBase
{
    [HttpGet("trackers")]
    public Task<List<EngineeringTrackerDto>> Trackers(CancellationToken ct) => admin.ListAsync(ct);

    /// <summary>Creates a tracker; the response carries the inbound webhook secret once.</summary>
    [HttpPost("trackers")]
    public async Task<ActionResult<EngineeringTrackerDto>> Create(SaveEngineeringTrackerRequest request, CancellationToken ct)
    {
        var tracker = await admin.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = tracker.Id }, tracker);
    }

    [HttpGet("trackers/{id:guid}")]
    public Task<EngineeringTrackerDto> Get(Guid id, CancellationToken ct) => admin.GetAsync(id, ct);

    [HttpPut("trackers/{id:guid}")]
    public Task<EngineeringTrackerDto> Update(Guid id, SaveEngineeringTrackerRequest request, CancellationToken ct) => admin.UpdateAsync(id, request, ct);

    [HttpDelete("trackers/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await admin.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("trackers/{id:guid}/test")]
    public Task<TrackerTestResultDto> Test(Guid id, CancellationToken ct) => admin.TestAsync(id, ct);

    [HttpGet("trackers/{id:guid}/projects")]
    public Task<IReadOnlyList<TrackerProjectDto>> Projects(Guid id, CancellationToken ct) => admin.ProjectsAsync(id, ct);

    [HttpPost("trackers/{id:guid}/sync")]
    public Task<TrackerSyncResultDto> Sync(Guid id, CancellationToken ct) => admin.SyncNowAsync(id, ct);

    /// <summary>Issues a new inbound webhook secret (returned once).</summary>
    [HttpPost("trackers/{id:guid}/rotate-webhook-secret")]
    public Task<EngineeringTrackerDto> RotateSecret(Guid id, CancellationToken ct) => admin.RotateWebhookSecretAsync(id, ct);

    [HttpGet("trackers/{id:guid}/deliveries")]
    public Task<List<EngineeringDeliveryDto>> Deliveries(Guid id, [FromQuery] int limit = 50, CancellationToken ct = default) => admin.DeliveriesAsync(id, limit, ct);

    [HttpGet("trackers/{id:guid}/logs")]
    public Task<List<IntegrationLogDto>> Logs(Guid id, [FromQuery] int limit = 100, CancellationToken ct = default) => admin.LogsAsync(id, limit, ct);

    [HttpGet("settings")]
    public Task<EngineeringSettingsDto> Settings(CancellationToken ct) => admin.GetSettingsAsync(ct);

    [HttpPut("settings")]
    public Task<EngineeringSettingsDto> SaveSettings(SaveEngineeringSettingsRequest request, CancellationToken ct) => admin.SaveSettingsAsync(request, ct);
}

/// <summary>
/// Inbound tracker webhooks (anonymous): Azure DevOps service hooks and GitHub webhooks. Authenticated by the tracker's
/// webhook secret (GitHub HMAC signature / X-SupportDesk-Secret or Basic auth); the tenant comes from the tracker.
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(HostingSetup.PublicLimiter)]
[Route("api/v1/engineering/hooks/{provider}/{trackerId:guid}")]
public sealed class EngineeringHooksController(EngineeringWebhookService webhooks) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(EngineeringWebhookService.MaxBody)]
    public async Task<IActionResult> Receive(string provider, Guid trackerId, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        if (ms.Length > EngineeringWebhookService.MaxBody) return StatusCode(413);
        var headers = Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var result = await webhooks.HandleAsync(provider, trackerId, new EngineeringWebhookRequest(headers, ms.ToArray()), ct);
        return StatusCode(result.StatusCode, new { status = result.Status, detail = result.Detail });
    }
}
