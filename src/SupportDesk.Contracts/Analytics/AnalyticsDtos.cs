using SupportDesk.Contracts.Common;

namespace SupportDesk.Contracts.Analytics;

// ───────────────────────────── Catalogue ─────────────────────────────

/// <summary>One metric of the single metric catalogue (docs/METRICS.md). Unit: tickets, hours, minutes, percent, score.</summary>
public sealed record MetricDefinitionDto(
    string Key,
    string Label,
    string Description,
    string Group,
    string Unit,
    string Aggregation,
    string DateBasis,
    bool HigherIsBetter,
    string Source,
    IReadOnlyList<string> Dimensions,
    IReadOnlyList<string> Filters,
    bool Drillable);

public sealed record DimensionDto(string Key, string Label, bool IsTime);

/// <summary>Filter field: Kind is "entity" (ids), "enum" (names) or "text".</summary>
public sealed record FilterFieldDto(string Key, string Label, string Kind);

public sealed record PeriodDto(string Key, string Label);

public sealed record ReportDefinitionDto(string Key, string Name, string Description, string Group, string Icon, IReadOnlyList<string> Filters);

/// <summary>TeamScope: the caller sees team/department/organisation analytics; false = personal (own tickets) only.</summary>
public sealed record AnalyticsCatalogDto(
    IReadOnlyList<MetricDefinitionDto> Metrics,
    IReadOnlyList<DimensionDto> Dimensions,
    IReadOnlyList<FilterFieldDto> Filters,
    IReadOnlyList<PeriodDto> Periods,
    IReadOnlyList<ReportDefinitionDto> Reports,
    string TimeZone,
    bool TeamScope,
    bool CanExport,
    bool CanSchedule);

/// <summary>Values for the filter bar (ids → names). Customers are searched through the customers API.</summary>
public sealed record AnalyticsFilterOptionsDto(
    IReadOnlyList<OptionDto> Departments,
    IReadOnlyList<OptionDto> Teams,
    IReadOnlyList<OptionDto> Agents,
    IReadOnlyList<OptionDto> Products,
    IReadOnlyList<OptionDto> Modules,
    IReadOnlyList<OptionDto> Versions,
    IReadOnlyList<OptionDto> Categories,
    IReadOnlyList<OptionDto> Tiers,
    IReadOnlyList<OptionDto> Channels,
    IReadOnlyList<OptionDto> Priorities,
    IReadOnlyList<OptionDto> StatusCategories);

// ───────────────────────────── Query ─────────────────────────────

/// <summary>
/// Query string of every analytics read (GET). Metric/dimension/filter/period values are whitelisted against the
/// catalogue; filters accept comma-separated values. From/To are local dates (yyyy-MM-dd, inclusive) for period=custom.
/// </summary>
public sealed record AnalyticsQuery
{
    public string? Metric { get; init; }
    /// <summary>Comma-separated metric keys (breakdown tables).</summary>
    public string? Metrics { get; init; }
    public string? Dimension { get; init; }
    public string? Period { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    /// <summary>previous | last_year</summary>
    public string? Compare { get; init; }
    public int? Limit { get; init; }
    /// <summary>desc (default) | asc</summary>
    public string? Sort { get; init; }
    public string? Department { get; init; }
    public string? Team { get; init; }
    public string? Assignee { get; init; }
    public string? Channel { get; init; }
    public string? Priority { get; init; }
    public string? Customer { get; init; }
    public string? Product { get; init; }
    public string? Module { get; init; }
    public string? Version { get; init; }
    public string? Category { get; init; }
    public string? Tier { get; init; }
    public string? StatusCategory { get; init; }
    /// <summary>Drill-down: the dimension value (row key) to open.</summary>
    public string? Key { get; init; }
    /// <summary>Drill-down: the time bucket start (yyyy-MM-dd) to open.</summary>
    public string? Bucket { get; init; }
    public string? Cursor { get; init; }
    public int? PageSize { get; init; }
}

public sealed record PeriodRangeDto(string Key, string Label, DateTime From, DateTime To, string FromDay, string ToDay);

public sealed record AnalyticsRowDto(string? Key, string Label, double? Value, int Count, double? CompareValue, double? DeltaPercent);

public sealed record AnalyticsResultDto(
    MetricDefinitionDto Metric,
    string Dimension,
    PeriodRangeDto Period,
    PeriodRangeDto? ComparePeriod,
    double? Total,
    int TotalCount,
    double? CompareTotal,
    double? DeltaPercent,
    IReadOnlyList<AnalyticsRowDto> Rows,
    bool Truncated);

public sealed record BreakdownColumnDto(string Key, string Label, string Unit, bool HigherIsBetter);

public sealed record BreakdownRowDto(string? Key, string Label, IReadOnlyList<double?> Values);

public sealed record AnalyticsBreakdownDto(
    string Dimension,
    PeriodRangeDto Period,
    IReadOnlyList<BreakdownColumnDto> Columns,
    IReadOnlyList<BreakdownRowDto> Rows,
    IReadOnlyList<double?> Totals);

public sealed record ParetoRowDto(string? Key, string Label, double Value, double Share, double CumulativeShare);

/// <summary>Categories sorted by contribution with cumulative share; VitalFew = rows needed to reach 80 %.</summary>
public sealed record AnalyticsParetoDto(string Metric, string Dimension, PeriodRangeDto Period, double Total, IReadOnlyList<ParetoRowDto> Rows, int VitalFew);

public sealed record CohortRowDto(string Cohort, string Label, int Size, IReadOnlyList<double?> ResolvedPercent);

/// <summary>Created-week cohorts: share of each cohort resolved within N days of creation (null = not yet observable).</summary>
public sealed record AnalyticsCohortsDto(PeriodRangeDto Period, IReadOnlyList<int> OffsetsDays, IReadOnlyList<CohortRowDto> Rows);

public sealed record AgeingBucketDto(string Key, string Label);

public sealed record AgeingRowDto(string? Key, string Label, IReadOnlyList<int> Counts, int Total, int Breached);

/// <summary>Open tickets by age (calendar time since creation) at <see cref="AsOf"/>, per dimension value.</summary>
public sealed record AnalyticsAgeingDto(string Dimension, DateTime AsOf, IReadOnlyList<AgeingBucketDto> Buckets, IReadOnlyList<AgeingRowDto> Rows, IReadOnlyList<int> Totals);

public sealed record DrilldownTicketDto(
    Guid TicketId,
    long Number,
    string DisplayNumber,
    string Subject,
    string Status,
    string StatusCategory,
    string Priority,
    string Channel,
    string? Department,
    string? Assignee,
    string? Customer,
    DateTime CreatedAt,
    DateTime? FirstResponseAt,
    DateTime? ResolvedAt,
    int? FirstResponseMinutes,
    int? FirstResponseBusinessMinutes,
    int? ResolutionMinutes,
    int? ResolutionBusinessMinutes,
    bool SlaBreached,
    int ReopenCount,
    int? CsatScore);

/// <summary>The tickets behind a number (keyset paged, newest first).</summary>
public sealed record AnalyticsDrilldownDto(string Title, PeriodRangeDto Period, IReadOnlyList<DrilldownTicketDto> Items, string? NextCursor, int Total);

// ───────────────────────────── Reports ─────────────────────────────

public sealed record ReportKpiDto(string Metric, string Label, string Unit, double? Value, double? CompareValue, double? DeltaPercent, bool HigherIsBetter);

public sealed record ReportSeriesDto(string Key, string Label, string Unit, IReadOnlyList<double?> Values, IReadOnlyList<double?>? CompareValues);

/// <summary>Chart data. Type: line | bar | stacked-bar | pie | pareto. CategoryKeys/Dimension/DrillMetric enable drill-down.</summary>
public sealed record ReportChartDto(
    string Id,
    string Title,
    string Type,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string?> CategoryKeys,
    IReadOnlyList<ReportSeriesDto> Series,
    string? Dimension,
    string? DrillMetric);

/// <summary>Column type: text | number | percent | hours | minutes | score | date | datetime | ticket.</summary>
public sealed record ReportColumnDto(string Key, string Label, string Type);

/// <summary>Rows are keyed by column key; "_key" holds the dimension value and "_ticket" the ticket number for drill-down.</summary>
public sealed record ReportTableDto(
    string Id,
    string Title,
    IReadOnlyList<ReportColumnDto> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    string? Dimension,
    string? DrillMetric);

public sealed record ReportResultDto(
    string Key,
    string Name,
    string Description,
    PeriodRangeDto Period,
    PeriodRangeDto? ComparePeriod,
    IReadOnlyList<ReportKpiDto> Kpis,
    IReadOnlyList<ReportChartDto> Charts,
    IReadOnlyList<ReportTableDto> Tables,
    IReadOnlyList<string> AppliedFilters,
    string TimeZone,
    DateTime GeneratedAt);

// ───────────────────────────── Dashboards ─────────────────────────────

/// <summary>Widget on a 12-column grid. Type: kpi | line | bar | pie | table. Filters use the query field names.</summary>
public sealed record DashboardWidgetDto
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Type { get; init; } = "kpi";
    public string Metric { get; init; } = "ticket_count";
    /// <summary>Additional metric columns for table widgets.</summary>
    public IReadOnlyList<string>? Metrics { get; init; }
    public string? Dimension { get; init; }
    public int? Limit { get; init; }
    public IReadOnlyDictionary<string, string>? Filters { get; init; }
    /// <summary>Overrides the dashboard period.</summary>
    public string? Period { get; init; }
    public bool Compare { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int W { get; init; } = 3;
    public int H { get; init; } = 2;
}

public sealed record DashboardSummaryDto(
    Guid Id,
    string Name,
    string? Description,
    string Audience,
    bool IsShared,
    bool IsDefault,
    bool IsMine,
    bool CanEdit,
    int WidgetCount,
    DateTime UpdatedAt);

public sealed record DashboardDto(
    Guid Id,
    string Name,
    string? Description,
    string Audience,
    bool IsShared,
    bool IsDefault,
    bool IsMine,
    bool CanEdit,
    string Period,
    IReadOnlyList<DashboardWidgetDto> Widgets,
    string RowVersion,
    DateTime UpdatedAt);

/// <summary>Create (POST) or replace (PUT, requires RowVersion) a dashboard. Shared dashboards need analytics.team.</summary>
public sealed record SaveDashboardRequest
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public bool IsShared { get; init; }
    public string? Period { get; init; }
    public IReadOnlyList<DashboardWidgetDto> Widgets { get; init; } = [];
    public string? RowVersion { get; init; }
}

// ───────────────────────────── Exports ─────────────────────────────

/// <summary>Queue a server-side export of a report. Format: Csv | Xlsx | Pdf. Parameters use the query field names.</summary>
public sealed record CreateReportExportRequest
{
    public string ReportKey { get; init; } = "";
    public string Format { get; init; } = "Xlsx";
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
}

public sealed record ReportExportDto(
    Guid Id,
    string ReportKey,
    string Title,
    string Format,
    string Status,
    string? FileName,
    long SizeBytes,
    int RowCount,
    string? Error,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    string? DownloadUrl);

// ───────────────────────────── Scheduled reports ─────────────────────────────

public sealed record ScheduledReportDto(
    Guid Id,
    string Name,
    string ReportKey,
    string ReportName,
    IReadOnlyDictionary<string, string> Parameters,
    string Format,
    string Frequency,
    string RunAtLocal,
    string? DayOfWeek,
    int? DayOfMonth,
    string TimeZone,
    IReadOnlyList<string> Recipients,
    Guid OwnerUserId,
    string? OwnerName,
    bool IsActive,
    DateTime NextRunAt,
    DateTime? LastRunAt,
    string? LastStatus,
    bool CanEdit,
    string RowVersion);

/// <summary>Create (POST) or replace (PUT, requires RowVersion) a schedule. RunAtLocal "HH:mm"; DayOfWeek for weekly, DayOfMonth (1–28) for monthly.</summary>
public sealed record SaveScheduledReportRequest
{
    public string Name { get; init; } = "";
    public string ReportKey { get; init; } = "";
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
    public string Format { get; init; } = "Pdf";
    public string Frequency { get; init; } = "Weekly";
    public string RunAtLocal { get; init; } = "07:00";
    public string? DayOfWeek { get; init; }
    public int? DayOfMonth { get; init; }
    public string? TimeZone { get; init; }
    public IReadOnlyList<string> Recipients { get; init; } = [];
    public bool IsActive { get; init; } = true;
    public string? RowVersion { get; init; }
}

public sealed record ScheduledReportRunDto(
    Guid Id,
    DateTime ScheduledFor,
    bool Manual,
    string Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    IReadOnlyList<string> Recipients,
    int Delivered,
    string? FileName,
    long SizeBytes,
    int RowCount,
    string? Error);

// ───────────────────────────── Metrics maintenance ─────────────────────────────

public sealed record MetricsStatusDto(
    int Tickets,
    int Metrics,
    int Missing,
    int Stale,
    DateTime? LastMetricUpdate,
    DateOnly? LastAgentDay,
    DateOnly? LastBacklogDay,
    bool RebuildQueued);

public sealed record RebuildMetricsRequest
{
    /// <summary>Also rebuild daily agent and backlog roll-ups for this many days (default 120, max 400).</summary>
    public int? Days { get; init; }
}
