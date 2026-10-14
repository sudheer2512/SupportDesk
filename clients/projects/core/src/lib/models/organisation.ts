import { Guid, IdName, IsoDate, IsoDateTime } from './common';

/** Mirrors SupportDesk.Contracts.Organisation (GET/POST /org/…). */

export const ROUTING_MODES = ['Manual', 'RoundRobin', 'LoadBalanced', 'SkillBased', 'Territory'] as const;
export type RoutingMode = (typeof ROUTING_MODES)[number];

export const LEAVE_TYPES = ['Leave', 'Sick', 'Training', 'PublicHoliday', 'Other'] as const;
export type LeaveType = (typeof LEAVE_TYPES)[number];

export const LEAVE_STATUSES = ['Requested', 'Approved', 'Rejected', 'Cancelled'] as const;
export type LeaveStatus = (typeof LEAVE_STATUSES)[number];

export const ROUTING_ATTRIBUTE_KINDS = ['Territory', 'Product', 'Customer', 'Language', 'Category', 'CustomerTier'] as const;
export type RoutingAttributeKind = (typeof ROUTING_ATTRIBUTE_KINDS)[number];

export const AGENT_STATUSES = ['Active', 'Invited', 'Deactivated'] as const;
export type AgentStatus = (typeof AGENT_STATUSES)[number];

export const WEEK_DAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'] as const;
export type WeekDay = (typeof WEEK_DAYS)[number];

// ───── Departments ─────

export interface DepartmentDto {
  id: Guid;
  name: string;
  code: string;
  description: string | null;
  email: string | null;
  isActive: boolean;
  archivedAt: IsoDateTime | null;
  parentDepartmentId: Guid | null;
  defaultTeamId: Guid | null;
  defaultTeamName: string | null;
  businessCalendarId: Guid | null;
  businessCalendarName: string | null;
  defaultSlaPolicyId: Guid | null;
  defaultSlaPolicyName: string | null;
  headUserId: Guid | null;
  headUserName: string | null;
  visibleInPortal: boolean;
  transferRequiresApproval: boolean;
  sortOrder: number;
  teamCount: number;
  agentCount: number;
  openTickets: number;
  rowVersion: string;
}

export interface SaveDepartmentRequest {
  name: string;
  code?: string | null;
  description?: string | null;
  email?: string | null;
  parentDepartmentId?: Guid | null;
  defaultTeamId?: Guid | null;
  businessCalendarId?: Guid | null;
  defaultSlaPolicyId?: Guid | null;
  headUserId?: Guid | null;
  visibleInPortal: boolean;
  transferRequiresApproval: boolean;
  sortOrder: number;
  rowVersion?: string | null;
}

export interface ArchiveDepartmentRequest {
  reassignToDepartmentId: Guid | null;
  rowVersion?: string | null;
}

export interface ArchiveDepartmentResultDto {
  department: DepartmentDto;
  ticketsToMove: number;
  reassignToDepartmentId: Guid | null;
}

// ───── Teams & routing ─────

export interface TeamMemberDto {
  userId: Guid;
  name: string;
  email: string | null;
  isLead: boolean;
  availability: string;
  isActive: boolean;
  openTickets: number;
  capacity: number;
}

export interface TeamRoutingDto {
  mode: RoutingMode | string;
  onlyOnlineAgents: boolean;
  considerShifts: boolean;
  maxOpenTicketsPerAgent: number | null;
  fallbackTeamId: Guid | null;
  fallbackTeamName: string | null;
}

export interface TeamDto {
  id: Guid;
  departmentId: Guid;
  departmentName: string;
  name: string;
  description: string | null;
  leadUserId: Guid | null;
  leadName: string | null;
  isActive: boolean;
  isDepartmentDefault: boolean;
  routing: TeamRoutingDto;
  members: TeamMemberDto[];
  openTickets: number;
  unassignedTickets: number;
  rowVersion: string;
}

export interface TeamRoutingRequest {
  mode: RoutingMode | string;
  onlyOnlineAgents: boolean;
  considerShifts: boolean;
  maxOpenTicketsPerAgent: number | null;
  fallbackTeamId: Guid | null;
}

export interface SaveTeamRequest {
  departmentId: Guid;
  name: string;
  description?: string | null;
  leadUserId?: Guid | null;
  isActive: boolean;
  memberUserIds: Guid[];
  routing?: TeamRoutingRequest | null;
  rowVersion?: string | null;
}

export interface RoutingPreviewRequest {
  departmentId: Guid | null;
  teamId?: Guid | null;
  customerId?: Guid | null;
  productId?: Guid | null;
  categoryId?: Guid | null;
  language?: string | null;
  priority?: string | null;
  ticketId?: Guid | null;
}

export interface RoutingCandidateDto {
  userId: Guid;
  name: string;
  availability: string;
  openTickets: number;
  capacity: number;
  onShift: boolean;
  onLeave: boolean;
  delegated: boolean;
  eligible: boolean;
  excludedReason: string | null;
  score: number | null;
}

export interface RoutingPreviewDto {
  teamId: Guid | null;
  teamName: string | null;
  assigneeId: Guid | null;
  assigneeName: string | null;
  strategy: string;
  reason: string;
  usedFallback: boolean;
  candidates: RoutingCandidateDto[];
}

export interface RoleOptionDto {
  id: Guid;
  name: string;
  description: string | null;
  ticketScope: string;
  isSystem: boolean;
  isLightAgentRole: boolean;
}

// ───── Skills ─────

export interface SkillDto {
  id: Guid;
  name: string;
  category: string | null;
  description: string | null;
  agentCount: number;
}

export interface SaveSkillRequest {
  name: string;
  category: string | null;
  description: string | null;
}

// ───── Agents ─────

export interface AgentListItemDto {
  userId: Guid;
  name: string;
  email: string;
  title: string | null;
  avatarUrl: string | null;
  status: AgentStatus | string;
  isLightAgent: boolean;
  availability: string;
  roles: IdName[];
  departments: IdName[];
  openTickets: number;
  capacity: number;
  onLeave: boolean;
  lastLoginAt: IsoDateTime | null;
}

export interface AgentQuery {
  q?: string | null;
  /** Active, Invited, Deactivated (comma separated). */
  status?: string | null;
  departmentId?: Guid | null;
  teamId?: Guid | null;
  light?: boolean | null;
  cursor?: string | null;
  limit?: number | null;
}

export interface AgentDepartmentDto {
  id: Guid;
  name: string;
  isSupervisor: boolean;
}

export interface AgentSkillDto {
  skillId: Guid;
  name: string;
  category: string | null;
  level: number;
}

export interface RoutingAttributeDto {
  id: Guid;
  kind: RoutingAttributeKind | string;
  value: string | null;
  refId: Guid | null;
  label: string | null;
}

export interface AgentShiftDto {
  assignmentId: Guid;
  shiftId: Guid;
  shiftName: string;
  effectiveFrom: IsoDate;
  effectiveTo: IsoDate | null;
}

export interface AgentInvitationDto {
  sentAt: IsoDateTime;
  expiresAt: IsoDateTime;
  expired: boolean;
}

export interface AgentDetailDto {
  userId: Guid;
  email: string;
  name: string;
  firstName: string | null;
  lastName: string | null;
  title: string | null;
  phone: string | null;
  timeZone: string;
  locale: string;
  signature: string | null;
  avatarUrl: string | null;
  status: AgentStatus | string;
  kind: string;
  isLightAgent: boolean;
  availability: string;
  availabilityChangedAt: IsoDateTime | null;
  lastLoginAt: IsoDateTime | null;
  mfaEnabled: boolean;
  roles: IdName[];
  departments: AgentDepartmentDto[];
  teams: IdName[];
  reportsToUserId: Guid | null;
  reportsToName: string | null;
  maxOpenTickets: number;
  maxConcurrentChats: number;
  languages: string[];
  skills: AgentSkillDto[];
  routingAttributes: RoutingAttributeDto[];
  shifts: AgentShiftDto[];
  openTickets: number;
  accountManagedCustomers: IdName[];
  invitation: AgentInvitationDto | null;
  rowVersion: string;
}

export interface InviteAgentRequest {
  email: string;
  name: string;
  title?: string | null;
  isLightAgent: boolean;
  roleIds: Guid[];
  departmentIds: Guid[];
  supervisorDepartmentIds: Guid[];
  teamIds: Guid[];
  timeZone?: string | null;
  locale?: string | null;
}

export interface InviteResultDto {
  agent: AgentDetailDto;
  sentTo: string;
  expiresAt: IsoDateTime;
  acceptUrl: string;
  /** Only in demo mode (like the MFA code on the login screen). */
  demoCode: string | null;
}

export interface UpdateAgentProfileRequest {
  displayName: string;
  firstName?: string | null;
  lastName?: string | null;
  title?: string | null;
  phone?: string | null;
  timeZone?: string | null;
  locale?: string | null;
  signature?: string | null;
  rowVersion: string;
}

export interface UpdateAgentAccessRequest {
  roleIds: Guid[];
  departmentIds: Guid[];
  supervisorDepartmentIds: Guid[];
  teamIds: Guid[];
  isLightAgent: boolean;
  reportsToUserId: Guid | null;
  rowVersion: string;
}

export interface SkillLevelRequest {
  skillId: Guid;
  level: number;
}

export interface RoutingAttributeRequest {
  kind: RoutingAttributeKind | string;
  value: string | null;
  refId: Guid | null;
}

export interface UpdateRoutingProfileRequest {
  maxOpenTickets: number;
  maxConcurrentChats: number;
  languages: string[];
  skills: SkillLevelRequest[];
  attributes: RoutingAttributeRequest[];
  rowVersion: string;
}

/** mode: reassign | unassign | route */
export interface DeactivateAgentRequest {
  mode: 'reassign' | 'unassign' | 'route';
  reassignToUserId: Guid | null;
  reason: string | null;
}

export interface DeactivateAgentResultDto {
  agent: AgentDetailDto;
  openTickets: number;
  mode: string;
}

export interface SeatUsageDto {
  planName: string;
  agents: number;
  maxAgents: number | null;
  lightAgents: number;
  maxLightAgents: number | null;
  pendingInvitations: number;
}

export interface AcceptInvitationRequest {
  tenant: string;
  email: string;
  code: string;
  password: string;
  displayName?: string | null;
}

export interface AcceptInvitationResultDto {
  tenant: string;
  email: string;
  name: string;
}

// ───── Shifts, leave, delegation ─────

export interface WorkIntervalDto {
  start: string;
  end: string;
}

export type WeeklyHoursDto = Record<string, WorkIntervalDto[]>;

export interface ShiftDto {
  id: Guid;
  name: string;
  description: string | null;
  timeZone: string;
  color: string | null;
  isActive: boolean;
  weeklyHours: WeeklyHoursDto;
  weeklyHoursTotal: number;
  assignedAgents: number;
  rowVersion: string;
}

export interface SaveShiftRequest {
  name: string;
  description: string | null;
  timeZone: string;
  color: string | null;
  isActive: boolean;
  weeklyHours: WeeklyHoursDto;
  rowVersion?: string | null;
}

export interface ShiftAssignmentDto {
  id: Guid;
  userId: Guid;
  userName: string;
  shiftId: Guid;
  shiftName: string;
  effectiveFrom: IsoDate;
  effectiveTo: IsoDate | null;
  onShiftNow: boolean;
}

export interface AssignShiftRequest {
  shiftId: Guid;
  userIds: Guid[];
  effectiveFrom: IsoDate;
  effectiveTo: IsoDate | null;
}

export interface LeaveDto {
  id: Guid;
  userId: Guid;
  userName: string;
  startsAt: IsoDateTime;
  endsAt: IsoDateTime;
  type: LeaveType | string;
  status: LeaveStatus | string;
  reason: string | null;
  approvedById: Guid | null;
  approvedByName: string | null;
  decidedAt: IsoDateTime | null;
  decisionNote: string | null;
  approvalRequestId: Guid | null;
  createdAt: IsoDateTime;
  canDecide: boolean;
  canCancel: boolean;
}

export interface LeaveQuery {
  userId?: Guid | null;
  status?: string | null;
  from?: IsoDateTime | null;
  to?: IsoDateTime | null;
  departmentId?: Guid | null;
}

export interface CreateLeaveRequest {
  /** null = my own leave (needs approval); managers may record approved leave for others. */
  userId: Guid | null;
  startsAt: IsoDateTime;
  endsAt: IsoDateTime;
  type: LeaveType | string;
  reason: string | null;
}

export interface DelegationDto {
  id: Guid;
  userId: Guid;
  userName: string;
  delegateUserId: Guid;
  delegateName: string;
  startsAt: IsoDateTime;
  endsAt: IsoDateTime;
  reason: string | null;
  reassignOpenTickets: boolean;
  includeApprovals: boolean;
  isActive: boolean;
  /** Scheduled, Active, Ended */
  state: string;
}

export interface SaveDelegationRequest {
  userId: Guid | null;
  delegateUserId: Guid;
  startsAt: IsoDateTime;
  endsAt: IsoDateTime;
  reason: string | null;
  reassignOpenTickets: boolean;
  includeApprovals: boolean;
}

/** kind: leave | delegation | holiday */
export interface ScheduleEntryDto {
  kind: 'leave' | 'delegation' | 'holiday' | string;
  id: Guid;
  userId: Guid | null;
  title: string;
  startsAt: IsoDateTime;
  endsAt: IsoDateTime;
  status: string | null;
  allDay: boolean;
}

// ───── Supervisor live monitor ─────

export interface MonitorAgentDto {
  userId: Guid;
  name: string;
  avatarUrl: string | null;
  title: string | null;
  availability: string;
  availabilityChangedAt: IsoDateTime | null;
  onShift: boolean;
  onLeave: boolean;
  delegateUserId: Guid | null;
  openTickets: number;
  capacity: number;
  inProgressTickets: number;
  overdueTickets: number;
  activeChats: number;
  idleMinutes: number | null;
  lastActivityAt: IsoDateTime | null;
  departmentIds: Guid[];
  teamIds: Guid[];
}

export interface MonitorQueueDto {
  departmentId: Guid;
  departmentName: string;
  teamId: Guid | null;
  teamName: string | null;
  open: number;
  unassigned: number;
  atRisk: number;
  breached: number;
  oldestUnassignedAt: IsoDateTime | null;
}

export interface MonitorSlaItemDto {
  ticketId: Guid;
  number: number;
  subject: string;
  priority: string;
  slaState: string;
  milestone: string | null;
  dueAt: IsoDateTime | null;
  assigneeId: Guid | null;
  assigneeName: string | null;
  departmentId: Guid;
  departmentName: string | null;
}

export interface MonitorChatDto {
  conversationId: Guid;
  channel: string;
  visitorName: string | null;
  subject: string | null;
  status: string;
  waitingSince: IsoDateTime;
  waitingMinutes: number;
  departmentId: Guid | null;
  assignedUserId: Guid | null;
  ticketId: Guid | null;
}

export interface MonitorSnapshotDto {
  generatedAt: IsoDateTime;
  scope: string;
  departments: IdName[];
  agents: MonitorAgentDto[];
  queues: MonitorQueueDto[];
  openTickets: number;
  unassignedTickets: number;
  atRiskCount: number;
  breachedCount: number;
  slaItems: MonitorSlaItemDto[];
  chatsWaiting: number;
  chats: MonitorChatDto[];
  canChangeAvailability: boolean;
  canReassign: boolean;
}
