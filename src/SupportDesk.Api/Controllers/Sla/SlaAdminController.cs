using Microsoft.AspNetCore.Mvc;
using SupportDesk.Application.Sla;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Sla;

namespace SupportDesk.Api.Controllers.Sla;

/// <summary>SLA policies (targets, escalations, order, simulation), business calendars and holidays, live breaches.</summary>
[Route("api/v1/sla")]
public sealed class SlaAdminController(SlaPolicyAdminService policies, BusinessCalendarService calendars, SlaBreachService breaches) : DeskControllerBase
{
    [HttpGet("policies")]
    public Task<List<SlaPolicyListItemDto>> Policies(CancellationToken ct) => policies.ListAsync(ct);

    [HttpGet("policies/{id:guid}")]
    public Task<SlaPolicyDto> Policy(Guid id, CancellationToken ct) => policies.GetAsync(id, ct);

    [HttpPost("policies")]
    public Task<SaveSlaPolicyResultDto> CreatePolicy(SaveSlaPolicyRequest r, CancellationToken ct) => policies.CreateAsync(r, ct);

    [HttpPut("policies/{id:guid}")]
    public Task<SaveSlaPolicyResultDto> UpdatePolicy(Guid id, SaveSlaPolicyRequest r, CancellationToken ct) => policies.UpdateAsync(id, r, ct);

    [HttpDelete("policies/{id:guid}")]
    public async Task<IActionResult> DeletePolicy(Guid id, CancellationToken ct)
    {
        await policies.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPut("policies/order")]
    public Task<List<SlaPolicyListItemDto>> Reorder(ReorderSlaPoliciesRequest r, CancellationToken ct) => policies.ReorderAsync(r, ct);

    [HttpPost("simulate")]
    public Task<SlaSimulationDto> Simulate(SimulateSlaRequest r, CancellationToken ct) => policies.SimulateAsync(r, ct);

    [HttpGet("calendars")]
    public Task<List<BusinessCalendarListItemDto>> Calendars(CancellationToken ct) => calendars.ListAsync(ct);

    [HttpGet("calendars/{id:guid}")]
    public Task<BusinessCalendarDto> Calendar(Guid id, CancellationToken ct) => calendars.GetAsync(id, ct);

    [HttpPost("calendars")]
    public Task<BusinessCalendarDto> CreateCalendar(SaveBusinessCalendarRequest r, CancellationToken ct) => calendars.SaveAsync(null, r, ct);

    [HttpPut("calendars/{id:guid}")]
    public Task<BusinessCalendarDto> UpdateCalendar(Guid id, SaveBusinessCalendarRequest r, CancellationToken ct) => calendars.SaveAsync(id, r, ct);

    [HttpDelete("calendars/{id:guid}")]
    public async Task<IActionResult> DeleteCalendar(Guid id, CancellationToken ct)
    {
        await calendars.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("calendars/{id:guid}/holidays")]
    public Task<CalendarHolidayDto> AddHoliday(Guid id, SaveHolidayRequest r, CancellationToken ct) => calendars.SaveHolidayAsync(id, null, r, ct);

    [HttpPut("calendars/{id:guid}/holidays/{holidayId:guid}")]
    public Task<CalendarHolidayDto> UpdateHoliday(Guid id, Guid holidayId, SaveHolidayRequest r, CancellationToken ct) => calendars.SaveHolidayAsync(id, holidayId, r, ct);

    [HttpDelete("calendars/{id:guid}/holidays/{holidayId:guid}")]
    public async Task<IActionResult> DeleteHoliday(Guid id, Guid holidayId, CancellationToken ct)
    {
        await calendars.DeleteHolidayAsync(id, holidayId, ct);
        return NoContent();
    }

    [HttpPost("calendars/{id:guid}/holidays/import")]
    public Task<ImportHolidaysResultDto> ImportHolidays(Guid id, ImportHolidaysRequest r, CancellationToken ct) => calendars.ImportAsync(id, r, ct);

    [HttpPost("calendars/{id:guid}/preview")]
    public Task<BusinessTimePreviewDto> Preview(Guid id, BusinessTimePreviewRequest r, CancellationToken ct) => calendars.PreviewAsync(id, r, ct);

    [HttpGet("breaches")]
    public Task<PagedResult<SlaBreachItemDto>> Breaches([FromQuery] SlaBreachQuery q, CancellationToken ct) => breaches.ListAsync(q, ct);

    [HttpGet("breaches/summary")]
    public Task<SlaBreachSummaryDto> BreachSummary([FromQuery] Guid? departmentId, CancellationToken ct) => breaches.SummaryAsync(departmentId, ct);
}
