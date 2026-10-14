namespace SupportDesk.Domain.Analytics;

/// <summary>
/// One analytical row per ticket, maintained from ticket/SLA events. Holds denormalised dimensions and the
/// metric definitions documented in docs/METRICS.md (business vs calendar minutes, reopen-aware resolution).
/// </summary>
public class TicketMetric : TenantEntity
{
    public Guid TicketId { get; set; }
    public long TicketNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateOnly CreatedDay { get; set; }
    public Channel Channel { get; set; }
    public TicketPriority Priority { get; set; }
    public StatusCategory StatusCategory { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid? TeamId { get; set; }
    public Guid? AssigneeId { get; set; }
    public Guid? CustomerId { get; set; }
    public string? CustomerTier { get; set; }
    public Guid? LocationId { get; set; }
    public Guid? ProductId { get; set; }
    public Guid? ModuleId { get; set; }
    public Guid? VersionId { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? IssueTypeId { get; set; }
    public string? RootCause { get; set; }
    public int? FirstResponseMinutes { get; set; }
    public int? FirstResponseBusinessMinutes { get; set; }
    /// <summary>Created → final resolution (last resolution if reopened).</summary>
    public int? ResolutionMinutes { get; set; }
    public int? ResolutionBusinessMinutes { get; set; }
    public int ReopenCount { get; set; }
    public int AgentReplies { get; set; }
    public int CustomerReplies { get; set; }
    public int Handoffs { get; set; }
    /// <summary>Resolved with exactly one agent reply and never reopened.</summary>
    public bool FirstContactResolution { get; set; }
    public bool FirstResponseBreached { get; set; }
    public bool ResolutionBreached { get; set; }
    public bool HasSla { get; set; }
    public bool IsEscalated { get; set; }
    public int? CsatScore { get; set; }
    public DateTime? FirstResponseAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateOnly? ResolvedDay { get; set; }
    public DateTime? ClosedAt { get; set; }
    public int TimeSpentMinutes { get; set; }
    public int BillableMinutes { get; set; }
    public decimal Cost { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Daily per-agent productivity roll-up.</summary>
public class AgentMetric : TenantEntity
{
    public Guid UserId { get; set; }
    public DateOnly Day { get; set; }
    public int TicketsAssigned { get; set; }
    public int TicketsResolved { get; set; }
    public int Replies { get; set; }
    public int Notes { get; set; }
    public int FirstResponses { get; set; }
    public long FirstResponseMinutesSum { get; set; }
    public long ResolutionMinutesSum { get; set; }
    public int HandleMinutes { get; set; }
    public int OnlineMinutes { get; set; }
    public int CsatCount { get; set; }
    public int CsatSum { get; set; }
    public int SlaBreaches { get; set; }
    public int Reopens { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>End-of-day backlog snapshot per department/priority (backlog and ageing trends).</summary>
public class BacklogSnapshot : TenantEntity
{
    public DateOnly Day { get; set; }
    public Guid DepartmentId { get; set; }
    public TicketPriority Priority { get; set; }
    public int OpenCount { get; set; }
    public int CreatedCount { get; set; }
    public int ResolvedCount { get; set; }
    public int Age0To1 { get; set; }
    public int Age1To3 { get; set; }
    public int Age3To7 { get; set; }
    public int Age7To30 { get; set; }
    public int Age30Plus { get; set; }
    public int BreachedOpen { get; set; }
}

public enum ReportFormat { Csv, Xlsx, Pdf }
public enum ReportFrequency { Daily, Weekly, Monthly }

public class ScheduledReport : AuditedEntity
{
    public string Name { get; set; } = "";
    public string ReportKey { get; set; } = "";
    public string ParametersJson { get; set; } = "{}";
    public ReportFormat Format { get; set; }
    public ReportFrequency Frequency { get; set; }
    public TimeOnly RunAtLocal { get; set; } = new(7, 0);
    public DayOfWeek? DayOfWeek { get; set; }
    public int? DayOfMonth { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public string RecipientsJson { get; set; } = "[]";
    public Guid OwnerUserId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string? LastStatus { get; set; }
}

/// <summary>Role-specific configurable dashboard. WidgetsJson: [{"id","type":"kpi|chart|table","metric","dimension","filters","x","y","w","h"}]</summary>
public class Dashboard : AuditedEntity
{
    public string Name { get; set; } = "";
    public Guid? OwnerUserId { get; set; }
    /// <summary>agent, supervisor, department_head, csm, admin</summary>
    public string Audience { get; set; } = "agent";
    public bool IsShared { get; set; }
    public bool IsDefault { get; set; }
    public string WidgetsJson { get; set; } = "[]";
}
