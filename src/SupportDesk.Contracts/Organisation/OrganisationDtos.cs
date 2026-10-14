using SupportDesk.Contracts.Common;

namespace SupportDesk.Contracts.Organisation;

// ───────────────────────────── Departments ─────────────────────────────

public sealed record DepartmentDto(
    Guid Id,
    string Name,
    string Code,
    string? Description,
    string? Email,
    bool IsActive,
    DateTime? ArchivedAt,
    Guid? ParentDepartmentId,
    Guid? DefaultTeamId,
    string? DefaultTeamName,
    Guid? BusinessCalendarId,
    string? BusinessCalendarName,
    Guid? DefaultSlaPolicyId,
    string? DefaultSlaPolicyName,
    Guid? HeadUserId,
    string? HeadUserName,
    bool VisibleInPortal,
    bool TransferRequiresApproval,
    int SortOrder,
    int TeamCount,
    int AgentCount,
    int OpenTickets,
    string RowVersion);

public sealed record SaveDepartmentRequest
{
    public string Name { get; init; } = "";
    public string? Code { get; init; }
    public string? Description { get; init; }
    public string? Email { get; init; }
    public Guid? ParentDepartmentId { get; init; }
    public Guid? DefaultTeamId { get; init; }
    public Guid? BusinessCalendarId { get; init; }
    public Guid? DefaultSlaPolicyId { get; init; }
    public Guid? HeadUserId { get; init; }
    public bool VisibleInPortal { get; init; } = true;
    public bool TransferRequiresApproval { get; init; }
    public int SortOrder { get; init; }
    /// <summary>Required for updates (optimistic concurrency).</summary>
    public string? RowVersion { get; init; }
}

/// <summary>Archiving a department with open tickets requires a target department; the tickets are moved by a background job.</summary>
public sealed record ArchiveDepartmentRequest(Guid? ReassignToDepartmentId, string? RowVersion);

public sealed record ArchiveDepartmentResultDto(DepartmentDto Department, int TicketsToMove, Guid? ReassignToDepartmentId);

// ───────────────────────────── Teams & routing ─────────────────────────────

public sealed record TeamMemberDto(Guid UserId, string Name, string? Email, bool IsLead, string Availability, bool IsActive, int OpenTickets, int Capacity);

/// <summary>Routing mode: Manual, RoundRobin, LoadBalanced, SkillBased or Territory.</summary>
public sealed record TeamRoutingDto(string Mode, bool OnlyOnlineAgents, bool ConsiderShifts, int? MaxOpenTicketsPerAgent, Guid? FallbackTeamId, string? FallbackTeamName);

public sealed record TeamDto(
    Guid Id,
    Guid DepartmentId,
    string DepartmentName,
    string Name,
    string? Description,
    Guid? LeadUserId,
    string? LeadName,
    bool IsActive,
    bool IsDepartmentDefault,
    TeamRoutingDto Routing,
    IReadOnlyList<TeamMemberDto> Members,
    int OpenTickets,
    int UnassignedTickets,
    string RowVersion);

public sealed record TeamRoutingRequest(string Mode, bool OnlyOnlineAgents, bool ConsiderShifts, int? MaxOpenTicketsPerAgent, Guid? FallbackTeamId);

public sealed record SaveTeamRequest
{
    public Guid DepartmentId { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public Guid? LeadUserId { get; init; }
    public bool IsActive { get; init; } = true;
    public IReadOnlyList<Guid> MemberUserIds { get; init; } = [];
    public TeamRoutingRequest? Routing { get; init; }
    public string? RowVersion { get; init; }
}

/// <summary>A sample ticket for the routing preview (nothing is saved). With TicketId the real ticket's attributes are used.</summary>
public sealed record RoutingPreviewRequest(
    Guid? DepartmentId,
    Guid? TeamId = null,
    Guid? CustomerId = null,
    Guid? ProductId = null,
    Guid? CategoryId = null,
    string? Language = null,
    string? Priority = null,
    Guid? TicketId = null);

public sealed record RoutingCandidateDto(
    Guid UserId,
    string Name,
    string Availability,
    int OpenTickets,
    int Capacity,
    bool OnShift,
    bool OnLeave,
    bool Delegated,
    bool Eligible,
    string? ExcludedReason,
    double? Score);

public sealed record RoutingPreviewDto(
    Guid? TeamId,
    string? TeamName,
    Guid? AssigneeId,
    string? AssigneeName,
    string Strategy,
    string Reason,
    bool UsedFallback,
    IReadOnlyList<RoutingCandidateDto> Candidates);

/// <summary>Minimal read-only role list for pickers (until the roles administration API is merged).</summary>
public sealed record RoleOptionDto(Guid Id, string Name, string? Description, string TicketScope, bool IsSystem, bool IsLightAgentRole);

// ───────────────────────────── Skills ─────────────────────────────

public sealed record SkillDto(Guid Id, string Name, string? Category, string? Description, int AgentCount);

public sealed record SaveSkillRequest(string Name, string? Category, string? Description);

// ───────────────────────────── Agents ─────────────────────────────

/// <summary>Status: Active, Invited (no password yet) or Deactivated.</summary>
public sealed record AgentListItemDto(
    Guid UserId,
    string Name,
    string Email,
    string? Title,
    string? AvatarUrl,
    string Status,
    bool IsLightAgent,
    string Availability,
    IReadOnlyList<IdName> Roles,
    IReadOnlyList<IdName> Departments,
    int OpenTickets,
    int Capacity,
    bool OnLeave,
    DateTime? LastLoginAt);

public sealed record AgentQuery
{
    public string? Q { get; init; }
    /// <summary>Active, Invited, Deactivated (comma separated); default all.</summary>
    public string? Status { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? TeamId { get; init; }
    public bool? Light { get; init; }
    public string? Cursor { get; init; }
    public int? Limit { get; init; }
}

public sealed record AgentDepartmentDto(Guid Id, string Name, bool IsSupervisor);

public sealed record AgentSkillDto(Guid SkillId, string Name, string? Category, int Level);

/// <summary>Kind: Territory, Product, Customer, Language, Category, CustomerTier.</summary>
public sealed record RoutingAttributeDto(Guid Id, string Kind, string? Value, Guid? RefId, string? Label);

public sealed record AgentShiftDto(Guid AssignmentId, Guid ShiftId, string ShiftName, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

public sealed record AgentInvitationDto(DateTime SentAt, DateTime ExpiresAt, bool Expired);

public sealed record AgentDetailDto(
    Guid UserId,
    string Email,
    string Name,
    string? FirstName,
    string? LastName,
    string? Title,
    string? Phone,
    string TimeZone,
    string Locale,
    string? Signature,
    string? AvatarUrl,
    string Status,
    string Kind,
    bool IsLightAgent,
    string Availability,
    DateTime? AvailabilityChangedAt,
    DateTime? LastLoginAt,
    bool MfaEnabled,
    IReadOnlyList<IdName> Roles,
    IReadOnlyList<AgentDepartmentDto> Departments,
    IReadOnlyList<IdName> Teams,
    Guid? ReportsToUserId,
    string? ReportsToName,
    int MaxOpenTickets,
    int MaxConcurrentChats,
    IReadOnlyList<string> Languages,
    IReadOnlyList<AgentSkillDto> Skills,
    IReadOnlyList<RoutingAttributeDto> RoutingAttributes,
    IReadOnlyList<AgentShiftDto> Shifts,
    int OpenTickets,
    IReadOnlyList<IdName> AccountManagedCustomers,
    AgentInvitationDto? Invitation,
    string RowVersion);

public sealed record InviteAgentRequest
{
    public string Email { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Title { get; init; }
    public bool IsLightAgent { get; init; }
    public IReadOnlyList<Guid> RoleIds { get; init; } = [];
    public IReadOnlyList<Guid> DepartmentIds { get; init; } = [];
    public IReadOnlyList<Guid> SupervisorDepartmentIds { get; init; } = [];
    public IReadOnlyList<Guid> TeamIds { get; init; } = [];
    public string? TimeZone { get; init; }
    public string? Locale { get; init; }
}

/// <summary>DemoCode is only returned when the tenant runs in demo mode (like the MFA code on the login screen).</summary>
public sealed record InviteResultDto(AgentDetailDto Agent, string SentTo, DateTime ExpiresAt, string AcceptUrl, string? DemoCode);

public sealed record UpdateAgentProfileRequest
{
    public string? DisplayName { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Title { get; init; }
    public string? Phone { get; init; }
    public string? TimeZone { get; init; }
    public string? Locale { get; init; }
    public string? Signature { get; init; }
    public string? RowVersion { get; init; }
}

public sealed record UpdateAgentAccessRequest
{
    public IReadOnlyList<Guid> RoleIds { get; init; } = [];
    public IReadOnlyList<Guid> DepartmentIds { get; init; } = [];
    public IReadOnlyList<Guid> SupervisorDepartmentIds { get; init; } = [];
    public IReadOnlyList<Guid> TeamIds { get; init; } = [];
    public bool IsLightAgent { get; init; }
    public Guid? ReportsToUserId { get; init; }
    public string? RowVersion { get; init; }
}

public sealed record SkillLevelRequest(Guid SkillId, int Level);

public sealed record RoutingAttributeRequest(string Kind, string? Value, Guid? RefId);

public sealed record UpdateRoutingProfileRequest
{
    public int MaxOpenTickets { get; init; } = 25;
    public int MaxConcurrentChats { get; init; } = 3;
    public IReadOnlyList<string> Languages { get; init; } = ["en"];
    public IReadOnlyList<SkillLevelRequest> Skills { get; init; } = [];
    public IReadOnlyList<RoutingAttributeRequest> Attributes { get; init; } = [];
    public string? RowVersion { get; init; }
}

/// <summary>Mode: reassign (to ReassignToUserId where eligible, else unassigned), unassign (back to the team queue) or route (automatic routing).</summary>
public sealed record DeactivateAgentRequest(string Mode, Guid? ReassignToUserId, string? Reason);

public sealed record DeactivateAgentResultDto(AgentDetailDto Agent, int OpenTickets, string Mode);

/// <summary>Availability: Online, Away, Busy, Offline.</summary>
public sealed record SetAvailabilityRequest(string Availability);

public sealed record SeatUsageDto(string PlanName, int Agents, int? MaxAgents, int LightAgents, int? MaxLightAgents, int PendingInvitations);

public sealed record AcceptInvitationRequest(string Tenant, string Email, string Code, string Password, string? DisplayName = null);

public sealed record AcceptInvitationResultDto(string Tenant, string Email, string Name);

// ───────────────────────────── Shifts, leave, delegation ─────────────────────────────

public sealed record WorkIntervalDto(string Start, string End);

public sealed record ShiftDto(
    Guid Id,
    string Name,
    string? Description,
    string TimeZone,
    string? Color,
    bool IsActive,
    IReadOnlyDictionary<string, IReadOnlyList<WorkIntervalDto>> WeeklyHours,
    double WeeklyHoursTotal,
    int AssignedAgents,
    string RowVersion);

public sealed record SaveShiftRequest
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string TimeZone { get; init; } = "UTC";
    public string? Color { get; init; }
    public bool IsActive { get; init; } = true;
    public IReadOnlyDictionary<string, IReadOnlyList<WorkIntervalDto>> WeeklyHours { get; init; } = new Dictionary<string, IReadOnlyList<WorkIntervalDto>>();
    public string? RowVersion { get; init; }
}

public sealed record ShiftAssignmentDto(Guid Id, Guid UserId, string UserName, Guid ShiftId, string ShiftName, DateOnly EffectiveFrom, DateOnly? EffectiveTo, bool OnShiftNow);

public sealed record AssignShiftRequest(Guid ShiftId, IReadOnlyList<Guid> UserIds, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

/// <summary>Type: Leave, Sick, Training, PublicHoliday, Other. Status: Requested, Approved, Rejected, Cancelled.</summary>
public sealed record LeaveDto(
    Guid Id,
    Guid UserId,
    string UserName,
    DateTime StartsAt,
    DateTime EndsAt,
    string Type,
    string Status,
    string? Reason,
    Guid? ApprovedById,
    string? ApprovedByName,
    DateTime? DecidedAt,
    string? DecisionNote,
    Guid? ApprovalRequestId,
    DateTime CreatedAt,
    bool CanDecide,
    bool CanCancel);

public sealed record LeaveQuery
{
    public Guid? UserId { get; init; }
    public string? Status { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public Guid? DepartmentId { get; init; }
}

/// <summary>UserId null = the caller's own leave. Managers (agents.manage) may record leave for others (approved immediately).</summary>
public sealed record CreateLeaveRequest(Guid? UserId, DateTime StartsAt, DateTime EndsAt, string Type, string? Reason);

public sealed record DecideLeaveRequest(string? Note);

/// <summary>State: Scheduled, Active, Ended.</summary>
public sealed record DelegationDto(
    Guid Id,
    Guid UserId,
    string UserName,
    Guid DelegateUserId,
    string DelegateName,
    DateTime StartsAt,
    DateTime EndsAt,
    string? Reason,
    bool ReassignOpenTickets,
    bool IncludeApprovals,
    bool IsActive,
    string State);

public sealed record SaveDelegationRequest(Guid? UserId, Guid DelegateUserId, DateTime StartsAt, DateTime EndsAt, string? Reason, bool ReassignOpenTickets, bool IncludeApprovals = true);

/// <summary>Kind: leave, delegation, holiday.</summary>
public sealed record ScheduleEntryDto(string Kind, Guid Id, Guid? UserId, string Title, DateTime StartsAt, DateTime EndsAt, string? Status, bool AllDay);

// ───────────────────────────── Supervisor live monitor ─────────────────────────────

public sealed record MonitorAgentDto(
    Guid UserId,
    string Name,
    string? AvatarUrl,
    string? Title,
    string Availability,
    DateTime? AvailabilityChangedAt,
    bool OnShift,
    bool OnLeave,
    Guid? DelegateUserId,
    int OpenTickets,
    int Capacity,
    int InProgressTickets,
    int OverdueTickets,
    int ActiveChats,
    int? IdleMinutes,
    DateTime? LastActivityAt,
    IReadOnlyList<Guid> DepartmentIds,
    IReadOnlyList<Guid> TeamIds);

public sealed record MonitorQueueDto(Guid DepartmentId, string DepartmentName, Guid? TeamId, string? TeamName, int Open, int Unassigned, int AtRisk, int Breached,
    DateTime? OldestUnassignedAt);

public sealed record MonitorSlaItemDto(Guid TicketId, long Number, string Subject, string Priority, string SlaState, string? Milestone, DateTime? DueAt,
    Guid? AssigneeId, string? AssigneeName, Guid DepartmentId, string? DepartmentName);

public sealed record MonitorChatDto(Guid ConversationId, string Channel, string? VisitorName, string? Subject, string Status, DateTime WaitingSince, int WaitingMinutes,
    Guid? DepartmentId, Guid? AssignedUserId, Guid? TicketId);

public sealed record MonitorSnapshotDto(
    DateTime GeneratedAt,
    string Scope,
    IReadOnlyList<IdName> Departments,
    IReadOnlyList<MonitorAgentDto> Agents,
    IReadOnlyList<MonitorQueueDto> Queues,
    int OpenTickets,
    int UnassignedTickets,
    int AtRiskCount,
    int BreachedCount,
    IReadOnlyList<MonitorSlaItemDto> SlaItems,
    int ChatsWaiting,
    IReadOnlyList<MonitorChatDto> Chats,
    bool CanChangeAvailability,
    bool CanReassign);
