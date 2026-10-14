namespace SupportDesk.Domain.Organisation;

/// <summary>A support department (technical support, implementation, billing…) with its own queues, SLAs and schedule.</summary>
public class Department : AuditedEntity, IConcurrencyVersioned
{
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? ParentDepartmentId { get; set; }
    public Guid? BusinessCalendarId { get; set; }
    public Guid? DefaultSlaPolicyId { get; set; }
    public Guid? DefaultTeamId { get; set; }
    public Guid? HeadUserId { get; set; }
    public string? Email { get; set; }
    public bool VisibleInPortal { get; set; } = true;
    /// <summary>Transfers out of this department need supervisor approval.</summary>
    public bool TransferRequiresApproval { get; set; }
    public int SortOrder { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Territory = agents whose territory/customer specialisation matches the customer's service region win (then load).</summary>
public enum RoutingStrategy { Manual, RoundRobin, LoadBalanced, SkillBased, Territory }

public class Team : AuditedEntity, IConcurrencyVersioned
{
    public Guid DepartmentId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Guid? LeadUserId { get; set; }
    public RoutingStrategy RoutingStrategy { get; set; } = RoutingStrategy.RoundRobin;
    /// <summary>Last agent picked by round-robin routing (cursor).</summary>
    public Guid? RoundRobinCursorUserId { get; set; }
    /// <summary>Only agents whose availability is Online receive automatic assignments.</summary>
    public bool OnlyAssignToOnlineAgents { get; set; } = true;
    /// <summary>Skip agents who are off shift (agents without a shift assignment are always considered on shift).</summary>
    public bool ConsiderShifts { get; set; }
    /// <summary>Team-level cap on open tickets per agent (the lower of this and the agent's own capacity applies).</summary>
    public int? MaxOpenTicketsPerAgent { get; set; }
    /// <summary>Queue (team of the same department) that receives tickets when no member is eligible.</summary>
    public Guid? FallbackTeamId { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[] RowVersion { get; set; } = [];
}

public class TeamMember : TenantEntity
{
    public Guid TeamId { get; set; }
    public Guid UserId { get; set; }
    public bool IsLead { get; set; }
}

public enum AgentAvailability { Online, Away, Busy, Offline }

/// <summary>Agent profile for a <see cref="Identity.User"/> (1:1 by UserId).</summary>
public class Agent : AuditedEntity, IConcurrencyVersioned
{
    public Guid UserId { get; set; }
    public string? Title { get; set; }
    public bool IsLightAgent { get; set; }
    /// <summary>Maximum concurrently open tickets for load balancing.</summary>
    public int MaxOpenTickets { get; set; } = 25;
    public int MaxConcurrentChats { get; set; } = 3;
    public AgentAvailability Availability { get; set; } = AgentAvailability.Offline;
    public DateTime? AvailabilityChangedAt { get; set; }
    public Guid? ReportsToUserId { get; set; }
    public Guid? DefaultShiftId { get; set; }
    public string LanguagesJson { get; set; } = "[\"en\"]";
    public decimal? HourlyCost { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public class AgentDepartment : TenantEntity
{
    public Guid UserId { get; set; }
    public Guid DepartmentId { get; set; }
    public bool IsSupervisor { get; set; }
}

public class Skill : AuditedEntity
{
    public string Name { get; set; } = "";
    public string? Category { get; set; }
    public string? Description { get; set; }
}

public class AgentSkill : TenantEntity
{
    public Guid UserId { get; set; }
    public Guid SkillId { get; set; }
    /// <summary>1 (basic) to 5 (expert).</summary>
    public int Level { get; set; } = 3;
}

public enum RoutingAttributeKind { Territory, Product, Customer, Language, Category, CustomerTier }

/// <summary>Territory / product / customer specialisations used by routing rules.</summary>
public class AgentRoutingAttribute : TenantEntity
{
    public Guid UserId { get; set; }
    public RoutingAttributeKind Kind { get; set; }
    public string? Value { get; set; }
    public Guid? RefId { get; set; }
}

/// <summary>A work shift pattern. Weekly hours JSON: {"Monday":[{"start":"09:00","end":"18:00"}],…}</summary>
public class Shift : AuditedEntity, IConcurrencyVersioned
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Display colour for the schedule calendar (#rrggbb).</summary>
    public string? Color { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public string TimeZone { get; set; } = "UTC";
    public string WeeklyHoursJson { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
}

public class AgentShiftAssignment : TenantEntity
{
    public Guid UserId { get; set; }
    public Guid ShiftId { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
}

public enum LeaveType { Leave, Sick, Training, PublicHoliday, Other }
public enum LeaveStatus { Requested, Approved, Rejected, Cancelled }

public class AgentLeave : AuditedEntity
{
    public Guid UserId { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public LeaveType Type { get; set; }
    public LeaveStatus Status { get; set; } = LeaveStatus.Requested;
    public string? Reason { get; set; }
    public Guid? ApprovedById { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
    /// <summary>Approval request raised for this leave (Approvals inbox), if approval was needed.</summary>
    public Guid? ApprovalRequestId { get; set; }
}

/// <summary>Out-of-office coverage: new assignments for UserId go to DelegateUserId during the window.</summary>
public class AgentDelegation : AuditedEntity
{
    public Guid UserId { get; set; }
    public Guid DelegateUserId { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public string? Reason { get; set; }
    /// <summary>Also move the absent agent's open tickets to the delegate when the window starts.</summary>
    public bool ReassignOpenTickets { get; set; }
    /// <summary>Pending approvals addressed to the absent agent are routed to the delegate.</summary>
    public bool IncludeApprovals { get; set; } = true;
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Working calendar for SLA business-hour calculations.
/// WeeklyHoursJson: {"Monday":[{"start":"09:00","end":"18:00"}], "Saturday":[], …} in the calendar time zone.
/// </summary>
public class BusinessCalendar : AuditedEntity, IConcurrencyVersioned
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public string TimeZone { get; set; } = "UTC";
    public bool Is24x7 { get; set; }
    public string WeeklyHoursJson { get; set; } = "{}";
    public bool IsDefault { get; set; }
}

public class CalendarHoliday : TenantEntity
{
    public Guid CalendarId { get; set; }
    public DateOnly Date { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Repeats on the same month/day every year.</summary>
    public bool Recurring { get; set; }
}
