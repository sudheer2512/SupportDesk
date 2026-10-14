using Microsoft.AspNetCore.Mvc;
using SupportDesk.Application.Analytics;
using SupportDesk.Contracts.Analytics;

namespace SupportDesk.Api.Controllers.Analytics;

/// <summary>
/// Analytics queries over the metric catalogue (whitelisted metric × dimension × filter × period — never SQL from clients),
/// standard reports, drill-down to tickets and metrics maintenance. Results respect the caller's analytics scope.
/// </summary>
[Route("api/v1/analytics")]
public sealed class AnalyticsController(
    AnalyticsCatalogService catalog,
    AnalyticsEngine engine,
    ReportService reports,
    MetricsMaintenanceService maintenance) : DeskControllerBase
{
    [HttpGet("catalog")]
    public Task<AnalyticsCatalogDto> Catalog(CancellationToken ct) => catalog.CatalogAsync(ct);

    [HttpGet("filter-options")]
    public Task<AnalyticsFilterOptionsDto> FilterOptions(CancellationToken ct) => catalog.FilterOptionsAsync(ct);

    /// <summary>One metric: total (+ comparison) and rows by the dimension (time series or top-N breakdown).</summary>
    [HttpGet("query")]
    public Task<AnalyticsResultDto> Query([FromQuery] AnalyticsQuery query, CancellationToken ct) => engine.QueryAsync(query, ct);

    /// <summary>Several metrics side by side by one dimension (tables).</summary>
    [HttpGet("breakdown")]
    public Task<AnalyticsBreakdownDto> Breakdown([FromQuery] AnalyticsQuery query, CancellationToken ct) => engine.BreakdownAsync(query, ct);

    [HttpGet("pareto")]
    public Task<AnalyticsParetoDto> Pareto([FromQuery] AnalyticsQuery query, CancellationToken ct) => engine.ParetoAsync(query, ct);

    [HttpGet("cohorts")]
    public Task<AnalyticsCohortsDto> Cohorts([FromQuery] AnalyticsQuery query, CancellationToken ct) => engine.CohortsAsync(query, ct);

    [HttpGet("ageing")]
    public Task<AnalyticsAgeingDto> Ageing([FromQuery] AnalyticsQuery query, CancellationToken ct) => engine.AgeingAsync(query, ct);

    /// <summary>The tickets behind a number (metric population, optional dimension key or time bucket), keyset paged.</summary>
    [HttpGet("drilldown")]
    public Task<AnalyticsDrilldownDto> Drilldown([FromQuery] AnalyticsQuery query, CancellationToken ct) => engine.DrilldownAsync(query, ct);

    [HttpGet("reports")]
    public IReadOnlyList<ReportDefinitionDto> ReportLibrary() => catalog.Reports();

    [HttpGet("reports/{key}")]
    public Task<ReportResultDto> Report(string key, [FromQuery] AnalyticsQuery query, CancellationToken ct) => reports.RunAsync(key, query, ct);

    [HttpGet("metrics/status")]
    public Task<MetricsStatusDto> MetricsStatus(CancellationToken ct) => maintenance.StatusAsync(ct);

    /// <summary>Queues a full, idempotent recomputation of ticket metrics and daily roll-ups.</summary>
    [HttpPost("metrics/rebuild")]
    public Task<MetricsStatusDto> Rebuild(RebuildMetricsRequest request, CancellationToken ct) => maintenance.RequestRebuildAsync(request, ct);
}

[Route("api/v1/analytics/dashboards")]
public sealed class AnalyticsDashboardsController(DashboardService dashboards) : DeskControllerBase
{
    [HttpGet]
    public Task<List<DashboardSummaryDto>> List(CancellationToken ct) => dashboards.ListAsync(ct);

    [HttpGet("{id:guid}")]
    public Task<DashboardDto> Get(Guid id, CancellationToken ct) => dashboards.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<DashboardDto>> Create(SaveDashboardRequest request, CancellationToken ct)
    {
        var d = await dashboards.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = d.Id }, d);
    }

    [HttpPut("{id:guid}")]
    public Task<DashboardDto> Update(Guid id, SaveDashboardRequest request, CancellationToken ct) => dashboards.UpdateAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await dashboards.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/duplicate")]
    public Task<DashboardDto> Duplicate(Guid id, CancellationToken ct) => dashboards.DuplicateAsync(id, ct);
}

[Route("api/v1/analytics/exports")]
public sealed class AnalyticsExportsController(ReportExportService exports) : DeskControllerBase
{
    /// <summary>Queues a server-side export (202 + the export record; poll it until Status is Succeeded).</summary>
    [HttpPost]
    public async Task<ActionResult<ReportExportDto>> Create(CreateReportExportRequest request, CancellationToken ct) =>
        Accepted(await exports.CreateAsync(request, ct));

    [HttpGet]
    public Task<List<ReportExportDto>> List(CancellationToken ct) => exports.ListAsync(ct);

    [HttpGet("{id:guid}")]
    public Task<ReportExportDto> Get(Guid id, CancellationToken ct) => exports.GetAsync(id, ct);

    [HttpGet("{id:guid}/download")]
    [Produces("application/octet-stream")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var (content, fileName, contentType) = await exports.DownloadAsync(id, ct);
        Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
        return File(content, contentType, fileName);
    }
}

[Route("api/v1/analytics/schedules")]
public sealed class AnalyticsSchedulesController(ScheduledReportService schedules) : DeskControllerBase
{
    [HttpGet]
    public Task<List<ScheduledReportDto>> List(CancellationToken ct) => schedules.ListAsync(ct);

    [HttpGet("{id:guid}")]
    public Task<ScheduledReportDto> Get(Guid id, CancellationToken ct) => schedules.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<ScheduledReportDto>> Create(SaveScheduledReportRequest request, CancellationToken ct)
    {
        var s = await schedules.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = s.Id }, s);
    }

    [HttpPut("{id:guid}")]
    public Task<ScheduledReportDto> Update(Guid id, SaveScheduledReportRequest request, CancellationToken ct) => schedules.UpdateAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await schedules.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/run")]
    public async Task<ActionResult<ScheduledReportRunDto>> RunNow(Guid id, CancellationToken ct) => Accepted(await schedules.RunNowAsync(id, ct));

    [HttpGet("{id:guid}/runs")]
    public Task<List<ScheduledReportRunDto>> Runs(Guid id, CancellationToken ct) => schedules.RunsAsync(id, ct);
}
