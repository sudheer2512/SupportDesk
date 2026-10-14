using Microsoft.AspNetCore.Mvc;
using SupportDesk.Application.Integrations.Erp;
using SupportDesk.Contracts.Erp;

namespace SupportDesk.Api.Controllers.Erp;

/// <summary>
/// Wings ERP / Wings Books integration. ERP screens call POST /tickets (OAuth client or API key with tickets:write);
/// agents read ERP records for the ticket context panel; administrators (admin.integrations) configure the
/// connection, test it, run a sync and read the integration log.
/// </summary>
[Route("api/v1/integrations/erp")]
public sealed class ErpController(ErpTicketService tickets, ErpIntegrationService integration, ErpSyncService sync) : DeskControllerBase
{
    /// <summary>Creates a ticket from an ERP record (201), or returns the ticket created earlier with the same clientRequestId (200).</summary>
    [HttpPost("tickets")]
    [ProducesResponseType<ErpTicketCreatedDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ErpTicketCreatedDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ErpTicketCreatedDto>> CreateTicket(CreateErpTicketRequest request, CancellationToken ct)
    {
        var (ticket, created) = await tickets.CreateAsync(request, ct);
        return created ? Created($"/api/v1/tickets/{ticket.TicketId}", ticket) : Ok(ticket);
    }

    [HttpGet("records/{type}/{id:guid}")]
    public Task<ErpRecordViewDto> Record(string type, Guid id, [FromQuery] string? system, CancellationToken ct) => integration.RecordAsync(type, id, system, ct);

    [HttpGet("settings")]
    public Task<ErpIntegrationDto> Settings([FromQuery] string? system, CancellationToken ct) => integration.GetSettingsAsync(system, ct);

    [HttpPut("settings")]
    public Task<ErpIntegrationDto> SaveSettings([FromQuery] string? system, SaveErpIntegrationRequest request, CancellationToken ct) =>
        integration.SaveSettingsAsync(system, request, ct);

    [HttpPost("test")]
    public Task<ErpConnectionTestDto> Test([FromQuery] string? system, CancellationToken ct) => integration.TestAsync(system, ct);

    /// <summary>Runs a full sync now (customer masters, installations, licences, AMC contracts) and returns its summary.</summary>
    [HttpPost("sync")]
    public Task<ErpSyncResultDto> Sync([FromQuery] string? system, CancellationToken ct) => sync.SyncAsync(system, ErpSyncService.ManualTrigger, ct);

    [HttpGet("logs")]
    public Task<List<IntegrationLogDto>> Logs([FromQuery] string? system, [FromQuery] string? operation, [FromQuery] int limit = 100, CancellationToken ct = default) =>
        integration.LogsAsync(system, operation, limit, ct);
}
