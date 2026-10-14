using Microsoft.AspNetCore.Mvc;
using SupportDesk.Application.Integrations.Collaboration;
using SupportDesk.Contracts.Collaboration;

namespace SupportDesk.Api.Controllers.Engineering;

/// <summary>
/// Microsoft Teams / Slack incoming-webhook channels (admin.integrations: event mappings, send test, delivery log) and
/// sharing a ticket to a channel (tickets.note).
/// </summary>
[Route("api/v1/collaboration")]
public sealed class CollaborationController(CollaborationService collaboration) : DeskControllerBase
{
    [HttpGet("catalog")]
    public CollaborationCatalogDto Catalog() => collaboration.Catalog();

    [HttpGet("channels")]
    public Task<List<CollaborationChannelDto>> Channels(CancellationToken ct) => collaboration.ListAsync(ct);

    [HttpPost("channels")]
    public async Task<ActionResult<CollaborationChannelDto>> Create(SaveCollaborationChannelRequest request, CancellationToken ct)
    {
        var channel = await collaboration.CreateAsync(request, ct);
        return StatusCode(201, channel);
    }

    [HttpPut("channels/{id:guid}")]
    public Task<CollaborationChannelDto> Update(Guid id, SaveCollaborationChannelRequest request, CancellationToken ct) => collaboration.UpdateAsync(id, request, ct);

    [HttpDelete("channels/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await collaboration.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Posts a sample card now and returns the provider's answer.</summary>
    [HttpPost("channels/{id:guid}/test")]
    public Task<CollaborationSendResultDto> Test(Guid id, CancellationToken ct) => collaboration.TestAsync(id, ct);

    [HttpGet("channels/{id:guid}/logs")]
    public Task<List<CollaborationLogDto>> Logs(Guid id, [FromQuery] int limit = 50, CancellationToken ct = default) => collaboration.LogsAsync(id, limit, ct);

    [HttpGet("share-targets")]
    public Task<List<ShareTargetDto>> ShareTargets(CancellationToken ct) => collaboration.ShareTargetsAsync(ct);

    [HttpPost("tickets/{ticketId:guid}/share")]
    public Task<CollaborationSendResultDto> Share(Guid ticketId, ShareTicketRequest request, CancellationToken ct) => collaboration.ShareTicketAsync(ticketId, request, ct);
}
