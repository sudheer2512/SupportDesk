namespace SupportDesk.Domain.Sla;

/// <summary>
/// An SLA policy. Policies are evaluated in <see cref="EvaluationOrder"/>; the first whose <see cref="ConditionsJson"/>
/// matches the ticket applies. Conditions use the shared condition model (customer tier, contract, priority,
/// department, product, category, channel).
/// </summary>
public class SlaPolicy : AuditedEntity, IConcurrencyVersioned
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; }
    public int EvaluationOrder { get; set; }
    /// <summary>Condition group (see Rules/Conditions.cs). Empty group matches every ticket.</summary>
    public string ConditionsJson { get; set; } = "{\"logic\":\"and\",\"conditions\":[]}";
    /// <summary>Business calendar; null = 24x7.</summary>
    public Guid? CalendarId { get; set; }
    /// <summary>Use the customer's contract calendar when the ticket is covered by a contract with support hours.</summary>
    public bool UseContractCalendar { get; set; } = true;
    /// <summary>Status ids whose time is excluded (in addition to statuses flagged PausesSla).</summary>
    public string PauseStatusIdsJson { get; set; } = "[]";
    public int Version { get; set; } = 1;
    public byte[] RowVersion { get; set; } = [];
    public List<SlaTarget> Targets { get; set; } = [];
    public List<SlaEscalationLevel> Escalations { get; set; } = [];
}

public class SlaTarget : TenantEntity
{
    public Guid PolicyId { get; set; }
    public TicketPriority Priority { get; set; }
    public int? FirstResponseMinutes { get; set; }
    public int? NextResponseMinutes { get; set; }
    public int? ResolutionMinutes { get; set; }
    /// <summary>Count only business hours of the policy/contract calendar.</summary>
    public bool BusinessHours { get; set; } = true;
    /// <summary>Raise an approaching-breach warning when this % of the target has elapsed.</summary>
    public int WarningPercent { get; set; } = 75;
}

public enum EscalationAction { Notify, Reassign, RaisePriority, NotifyAndReassign }

/// <summary>Escalation step relative to a milestone breach (negative minutes = before the breach).</summary>
public class SlaEscalationLevel : TenantEntity
{
    public Guid PolicyId { get; set; }
    public SlaMilestoneKind Milestone { get; set; }
    public int Level { get; set; }
    public int MinutesAfterBreach { get; set; }
    public EscalationAction Action { get; set; } = EscalationAction.Notify;
    /// <summary>{"userIds":[…],"roleIds":[…],"notifySupervisors":true,"notifyAssignee":true}</summary>
    public string NotifyJson { get; set; } = "{}";
    public Guid? ReassignToUserId { get; set; }
    public Guid? ReassignToTeamId { get; set; }
}

public enum SlaInstanceStatus { Active, Paused, Completed, Cancelled }

/// <summary>
/// The SLA applied to one ticket. Holds an immutable snapshot of the policy, targets and calendar used, so
/// calculations are reproducible even if the policy is edited later. Re-targeting (priority/contract change)
/// writes a Recalculated event and updates milestone due dates.
/// </summary>
public class SlaInstance : TenantEntity, IConcurrencyVersioned
{
    public Guid TicketId { get; set; }
    public Guid PolicyId { get; set; }
    public int PolicyVersion { get; set; }
    public string PolicySnapshotJson { get; set; } = "{}";
    public string CalendarSnapshotJson { get; set; } = "{}";
    public TicketPriority Priority { get; set; }
    public DateTime StartedAt { get; set; }
    public SlaInstanceStatus Status { get; set; } = SlaInstanceStatus.Active;
    public DateTime? PausedSince { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public List<SlaMilestone> Milestones { get; set; } = [];
}

public enum MilestoneStatus { Active, Paused, Met, Breached, Cancelled }

/// <summary>
/// One SLA clock (first response, a next-response cycle, or resolution). Elapsed business minutes are tracked
/// across pauses; DueAt is recomputed on resume. A breached milestone stays Breached even after completion (BreachedAt set; CompletedAt records the late completion).
/// </summary>
public class SlaMilestone : TenantEntity
{
    public Guid InstanceId { get; set; }
    public Guid TicketId { get; set; }
    public SlaMilestoneKind Kind { get; set; }
    /// <summary>Cycle number for next-response milestones (1, 2, …).</summary>
    public int Cycle { get; set; } = 1;
    public int TargetMinutes { get; set; }
    public bool BusinessHours { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime? WarningAt { get; set; }
    public bool WarningRaised { get; set; }
    /// <summary>Business (or calendar) minutes consumed before the current running segment.</summary>
    public double ElapsedMinutesBeforeSegment { get; set; }
    /// <summary>Start of the current running segment (null while paused).</summary>
    public DateTime? SegmentStartedAt { get; set; }
    public MilestoneStatus Status { get; set; } = MilestoneStatus.Active;
    public DateTime? CompletedAt { get; set; }
    public DateTime? BreachedAt { get; set; }
    public int EscalationLevelReached { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class SlaEventTypes
{
    public const string Started = "Started";
    public const string Paused = "Paused";
    public const string Resumed = "Resumed";
    public const string Recalculated = "Recalculated";
    public const string Warning = "Warning";
    public const string Breached = "Breached";
    public const string Met = "Met";
    public const string Escalated = "Escalated";
    public const string Cancelled = "Cancelled";
    public const string PolicyChanged = "PolicyChanged";
    public const string Completed = "Completed";
}

/// <summary>Append-only SLA history (reproducibility and escalation audit).</summary>
public class SlaEvent : TenantEntity, IAppendOnly
{
    public Guid InstanceId { get; set; }
    public Guid? MilestoneId { get; set; }
    public Guid TicketId { get; set; }
    public string Type { get; set; } = "";
    public SlaMilestoneKind? Milestone { get; set; }
    public DateTime OccurredAt { get; set; }
    public string? DetailsJson { get; set; }
}
