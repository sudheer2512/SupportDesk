using System.Text.Json;
using SupportDesk.Contracts.Organisation;

namespace SupportDesk.Contracts.Sla;

// ───────────────────────────── Policies ─────────────────────────────

/// <summary>Priority: Low, Medium, High, Critical. Minutes are business minutes when BusinessHours is set.</summary>
public sealed record SlaTargetDto(string Priority, int? FirstResponseMinutes, int? NextResponseMinutes, int? ResolutionMinutes, bool BusinessHours, int WarningPercent);

/// <summary>
/// Milestone: FirstResponse, NextResponse, Resolution. Action: Notify, Reassign, RaisePriority, NotifyAndReassign.
/// Trigger: AtPercent (of the target, before or after the breach) or MinutesAfterBreach (negative = before the breach).
/// Notify: {"userIds":[…],"roleIds":[…],"notifyAssignee":true,"notifySupervisors":true}.
/// </summary>
public sealed record SlaEscalationDto(
    string Milestone,
    int Level,
    int MinutesAfterBreach,
    int? AtPercent,
    string Action,
    JsonElement Notify,
    Guid? ReassignToUserId,
    Guid? ReassignToTeamId);

public sealed record SlaPolicyListItemDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsActive,
    bool IsDefault,
    int EvaluationOrder,
    int Version,
    Guid? CalendarId,
    string? CalendarName,
    int ConditionCount,
    int TargetCount,
    int EscalationCount,
    int ActiveTickets,
    DateTime UpdatedAt);

public sealed record SlaPolicyDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsActive,
    bool IsDefault,
    int EvaluationOrder,
    int Version,
    JsonElement Conditions,
    Guid? CalendarId,
    string? CalendarName,
    bool UseContractCalendar,
    IReadOnlyList<Guid> PauseStatusIds,
    IReadOnlyList<SlaTargetDto> Targets,
    IReadOnlyList<SlaEscalationDto> Escalations,
    int ActiveTickets,
    DateTime UpdatedAt,
    string RowVersion);

public sealed record SaveSlaPolicyRequest
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public bool IsActive { get; init; } = true;
    public bool IsDefault { get; init; }
    /// <summary>Condition group {"logic","conditions","groups"}; null/empty = matches every ticket.</summary>
    public JsonElement? Conditions { get; init; }
    public Guid? CalendarId { get; init; }
    public bool UseContractCalendar { get; init; } = true;
    public IReadOnlyList<Guid> PauseStatusIds { get; init; } = [];
    public IReadOnlyList<SlaTargetDto> Targets { get; init; } = [];
    public IReadOnlyList<SlaEscalationDto> Escalations { get; init; } = [];
    public string? RowVersion { get; init; }
}

/// <summary>RetargetQueued = open tickets whose SLA will be recalculated by the background job.</summary>
public sealed record SaveSlaPolicyResultDto(SlaPolicyDto Policy, int RetargetQueued);

/// <summary>Policy ids in evaluation order (first match wins; the default policy is the fallback).</summary>
public sealed record ReorderSlaPoliciesRequest(IReadOnlyList<Guid> PolicyIds);

/// <summary>Simulates policy selection and due dates for an existing ticket. With Draft (and PolicyId for an edited policy) unsaved changes are used.</summary>
public sealed record SimulateSlaRequest(Guid? TicketId, long? TicketNumber, Guid? PolicyId = null, SaveSlaPolicyRequest? Draft = null);

public sealed record SlaPolicyMatchDto(Guid? PolicyId, string Name, int EvaluationOrder, bool IsDefault, bool IsActive, bool Matched, bool Selected, string Note);

public sealed record SimulatedMilestoneDto(string Milestone, int TargetMinutes, bool BusinessHours, DateTime StartAt, DateTime DueAt, DateTime? WarningAt,
    DateTime? CurrentDueAt, string? CurrentStatus);

public sealed record SlaSimulationDto(
    Guid TicketId,
    long Number,
    string Subject,
    string Priority,
    string Status,
    Guid? SelectedPolicyId,
    string? SelectedPolicyName,
    string SelectionReason,
    string CalendarSource,
    string? CalendarName,
    IReadOnlyList<SlaPolicyMatchDto> Evaluated,
    IReadOnlyList<SimulatedMilestoneDto> Milestones,
    Guid? CurrentPolicyId,
    string? CurrentPolicyName,
    bool WouldChange);

// ───────────────────────────── Calendars ─────────────────────────────

public sealed record BusinessCalendarListItemDto(Guid Id, string Name, string? Description, string TimeZone, bool Is24x7, bool IsDefault, int HolidayCount,
    double WeeklyHours, int UsageCount);

public sealed record CalendarHolidayDto(Guid Id, DateOnly Date, string Name, bool Recurring);

public sealed record BusinessCalendarDto(
    Guid Id,
    string Name,
    string? Description,
    string TimeZone,
    bool Is24x7,
    bool IsDefault,
    IReadOnlyDictionary<string, IReadOnlyList<WorkIntervalDto>> WeeklyHours,
    double WeeklyHoursTotal,
    IReadOnlyList<CalendarHolidayDto> Holidays,
    IReadOnlyList<string> UsedBy,
    string RowVersion);

public sealed record SaveBusinessCalendarRequest
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string TimeZone { get; init; } = "UTC";
    public bool Is24x7 { get; init; }
    public bool IsDefault { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyList<WorkIntervalDto>> WeeklyHours { get; init; } = new Dictionary<string, IReadOnlyList<WorkIntervalDto>>();
    public string? RowVersion { get; init; }
}

public sealed record SaveHolidayRequest(DateOnly Date, string Name, bool Recurring);

/// <summary>CSV lines "date,name[,recurring]" (date yyyy-MM-dd or dd-MM-yyyy / dd/MM/yyyy; recurring yes/true/1). A header row is ignored.</summary>
public sealed record ImportHolidaysRequest(string Csv, bool ReplaceExisting = false);

public sealed record ImportHolidaysResultDto(int Added, int Updated, int Skipped, IReadOnlyList<string> Errors);

/// <summary>Business minutes between two instants; with AddMinutes also the due time From + AddMinutes business minutes.</summary>
public sealed record BusinessTimePreviewRequest(DateTime From, DateTime To, int? AddMinutes = null);

public sealed record BusinessIntervalDto(DateTime Start, DateTime End, double Minutes);

public sealed record BusinessTimePreviewDto(
    DateTime From,
    DateTime To,
    double BusinessMinutes,
    double CalendarMinutes,
    DateTime? DueAt,
    IReadOnlyList<BusinessIntervalDto> Intervals,
    IReadOnlyList<CalendarHolidayDto> HolidaysInRange,
    string TimeZone);

// ───────────────────────────── Live breaches ─────────────────────────────

public sealed record SlaBreachQuery
{
    /// <summary>AtRisk, Breached or All (default All).</summary>
    public string? State { get; init; }
    public Guid? DepartmentId { get; init; }
    public string? Milestone { get; init; }
    public string? Cursor { get; init; }
    public int? Limit { get; init; }
}

public sealed record SlaBreachItemDto(
    Guid TicketId,
    Guid MilestoneId,
    long Number,
    string Subject,
    string Priority,
    string TicketSlaState,
    string Milestone,
    string MilestoneStatus,
    DateTime DueAt,
    DateTime? BreachedAt,
    int TargetMinutes,
    double ElapsedMinutes,
    Guid? AssigneeId,
    string? AssigneeName,
    Guid DepartmentId,
    string? DepartmentName,
    string? PolicyName,
    int EscalationLevel);

public sealed record SlaBreachSummaryDto(int AtRisk, int Breached, int BreachedToday, int Paused, int DueNextHour);
