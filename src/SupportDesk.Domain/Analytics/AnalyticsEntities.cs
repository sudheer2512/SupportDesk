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
    /// <summary>Creation date on the tenant's local calendar.</summary>
    public DateOnly CreatedDay { get; set; }
    public Channel Channel { get; set; }
    public TicketPriority Priority { get; set; }
    public StatusCategory StatusCategory { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid? TeamId { get; set; }
    public Guid? AssigneeId { get; set; }
    public Guid? CustomerId { get; set; }
    public string? CustomerTier { get; set; }
    public Guid? ContactId { get; set; }
    public Guid? LocationId { get; set; }
    public Guid? ProductId { get; set; }
    public Guid? ModuleId { get; set; }
    public Guid? VersionId { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? SubcategoryId { get; set; }
    public Guid? IssueTypeId { get; set; }
    public string? RootCause { get; set; }
    public string? ResolutionCode { get; set; }
    public Tickets.Sentiment Sentiment { get; set; }
    /// <summary>Spam, merged into another ticket or deleted: kept for history but excluded from every metric.</summary>
    public bool Excluded { get; set; }

    /// <summary>Agent who sent the first public reply.</summary>
    public Guid? FirstResponderId { get; set; }
    public int? FirstResponseMinutes { get; set; }
    public int? FirstResponseBusinessMinutes { get; set; }
    /// <summary>Created → final resolution (last resolution if reopened).</summary>
    public int? ResolutionMinutes { get; set; }
    public int? ResolutionBusinessMinutes { get; set; }
    public int ReopenCount { get; set; }
    public int AgentReplies { get; set; }
    public int CustomerReplies { get; set; }
    /// <summary>Re-assignments after the first assignment.</summary>
    public int Handoffs { get; set; }
    /// <summary>Department transfers.</summary>
    public int Transfers { get; set; }
    /// <summary>Resolved with at most one public agent reply and never reopened.</summary>
    public bool FirstContactResolution { get; set; }

    // SLA per milestone (any SLA instance of the ticket)
    public bool HasSla { get; set; }
    public bool FirstResponseSlaMet { get; set; }
    public bool FirstResponseBreached { get; set; }
    public bool ResolutionSlaMet { get; set; }
    public bool ResolutionBreached { get; set; }
    public int NextResponseMet { get; set; }
    public int NextResponseBreaches { get; set; }
    /// <summary>Any milestone breached (or the ticket's SLA state is Breached).</summary>
    public bool SlaBreached { get; set; }

    public bool IsEscalated { get; set; }
    public int EscalationCount { get; set; }
    public int? CsatScore { get; set; }
    public DateTime? FirstResponseAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateOnly? ResolvedDay { get; set; }
    public DateTime? ClosedAt { get; set; }
    /// <summary>Handle time: minutes logged in time entries.</summary>
    public int TimeSpentMinutes { get; set; }
    public int BillableMinutes { get; set; }
    public decimal Cost { get; set; }
    /// <summary>The ticket's rowversion when this row was computed (stale-row detection).</summary>
    public byte[]? SourceVersion { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Daily per-agent productivity roll-up (tenant-local day).</summary>
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
    /// <summary>Minutes logged in time entries started that day.</summary>
    public int HandleMinutes { get; set; }
    /// <summary>Scheduled (available) minutes: shift assignment, otherwise the agent's department calendar.</summary>
    public int OnlineMinutes { get; set; }
    public int CsatCount { get; set; }
    public int CsatSum { get; set; }
    public int SlaBreaches { get; set; }
    public int Reopens { get; set; }
    /// <summary>Open tickets assigned at the time of the roll-up (end of day for past days).</summary>
    public int OpenAtEnd { get; set; }
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
    public DateTime UpdatedAt { get; set; }
}

public enum ReportFormat { Csv, Xlsx, Pdf }
public enum ReportFrequency { Daily, Weekly, Monthly }

/// <summary>A standard report delivered by e-mail on a schedule (run under the owner's data scope).</summary>
public class ScheduledReport : AuditedEntity, IConcurrencyVersioned
{
    public string Name { get; set; } = "";
    public string ReportKey { get; set; } = "";
    /// <summary>Report filters: {"period":"last_7_days","compare":"previous","department":["…"],…}.</summary>
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
    public byte[] RowVersion { get; set; } = [];
}

public enum ReportRunStatus { Queued, Running, Succeeded, Failed }

/// <summary>Delivery history of a scheduled report (one row per run).</summary>
public class ScheduledReportRun : TenantEntity
{
    public Guid ScheduledReportId { get; set; }
    /// <summary>The schedule slot this run covers (idempotency: one run per slot).</summary>
    public DateTime ScheduledFor { get; set; }
    public bool Manual { get; set; }
    public ReportRunStatus Status { get; set; } = ReportRunStatus.Queued;
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string RecipientsJson { get; set; } = "[]";
    public int Delivered { get; set; }
    public string? FileName { get; set; }
    public long SizeBytes { get; set; }
    public int RowCount { get; set; }
    public string? Error { get; set; }
}

/// <summary>A report export produced server-side by a durable job (CSV, Excel or PDF).</summary>
public class ReportExport : TenantEntity
{
    public Guid RequestedById { get; set; }
    public string ReportKey { get; set; } = "";
    public string Title { get; set; } = "";
    public string ParametersJson { get; set; } = "{}";
    public ReportFormat Format { get; set; }
    public ReportRunStatus Status { get; set; } = ReportRunStatus.Queued;
    public Guid? FileId { get; set; }
    public string? FileName { get; set; }
    public long SizeBytes { get; set; }
    public int RowCount { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Role-specific configurable dashboard. WidgetsJson: [{"id","title","type":"kpi|line|bar|pie|table","metric","metrics",
/// "dimension","limit","filters":{…},"period","x","y","w","h"}] — widgets reference catalogue metrics only.
/// </summary>
public class Dashboard : AuditedEntity, IConcurrencyVersioned
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Null for tenant role defaults.</summary>
    public Guid? OwnerUserId { get; set; }
    /// <summary>agent, supervisor, manager, executive (role defaults) or custom.</summary>
    public string Audience { get; set; } = "custom";
    public bool IsShared { get; set; }
    public bool IsDefault { get; set; }
    /// <summary>Default period of the dashboard's widgets (catalogue period key).</summary>
    public string Period { get; set; } = "last_30_days";
    public string WidgetsJson { get; set; } = "[]";
    public byte[] RowVersion { get; set; } = [];
}
