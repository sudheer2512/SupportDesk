import { Guid, IsoDate, IsoDateTime, JsonValue } from './common';
import { WeeklyHoursDto } from './organisation';

/** Mirrors SupportDesk.Contracts.Sla (GET/POST /sla/…): policies, calendars, live breaches. */

export const SLA_MILESTONES = ['FirstResponse', 'NextResponse', 'Resolution'] as const;
export type SlaMilestoneKind = (typeof SLA_MILESTONES)[number];

export const ESCALATION_ACTIONS = ['Notify', 'Reassign', 'RaisePriority', 'NotifyAndReassign'] as const;
export type EscalationAction = (typeof ESCALATION_ACTIONS)[number];

export interface SlaTargetDto {
  priority: string;
  firstResponseMinutes: number | null;
  nextResponseMinutes: number | null;
  resolutionMinutes: number | null;
  businessHours: boolean;
  warningPercent: number;
}

/** Notify JSON: {"userIds":[…],"roleIds":[…],"notifyAssignee":true,"notifySupervisors":true}. */
export interface SlaEscalationNotify {
  userIds?: Guid[];
  roleIds?: Guid[];
  notifyAssignee?: boolean;
  notifySupervisors?: boolean;
}

export interface SlaEscalationDto {
  milestone: SlaMilestoneKind | string;
  level: number;
  /** Negative = before the breach (ignored when atPercent is set). */
  minutesAfterBreach: number;
  /** Fire at this % of the target (before or after the breach). */
  atPercent: number | null;
  action: EscalationAction | string;
  notify: SlaEscalationNotify | JsonValue;
  reassignToUserId: Guid | null;
  reassignToTeamId: Guid | null;
}

export interface SlaPolicyListItemDto {
  id: Guid;
  name: string;
  description: string | null;
  isActive: boolean;
  isDefault: boolean;
  evaluationOrder: number;
  version: number;
  calendarId: Guid | null;
  calendarName: string | null;
  conditionCount: number;
  targetCount: number;
  escalationCount: number;
  activeTickets: number;
  updatedAt: IsoDateTime;
}

export interface SlaPolicyDto {
  id: Guid;
  name: string;
  description: string | null;
  isActive: boolean;
  isDefault: boolean;
  evaluationOrder: number;
  version: number;
  /** Condition group {"logic","conditions","groups"} (see FilterGroup). */
  conditions: JsonValue;
  calendarId: Guid | null;
  calendarName: string | null;
  useContractCalendar: boolean;
  pauseStatusIds: Guid[];
  targets: SlaTargetDto[];
  escalations: SlaEscalationDto[];
  activeTickets: number;
  updatedAt: IsoDateTime;
  rowVersion: string;
}

export interface SaveSlaPolicyRequest {
  name: string;
  description: string | null;
  isActive: boolean;
  isDefault: boolean;
  conditions: JsonValue | null;
  calendarId: Guid | null;
  useContractCalendar: boolean;
  pauseStatusIds: Guid[];
  targets: SlaTargetDto[];
  escalations: SlaEscalationDto[];
  rowVersion?: string | null;
}

export interface SaveSlaPolicyResultDto {
  policy: SlaPolicyDto;
  retargetQueued: number;
}

export interface SimulateSlaRequest {
  ticketId: Guid | null;
  ticketNumber: number | null;
  policyId?: Guid | null;
  draft?: SaveSlaPolicyRequest | null;
}

export interface SlaPolicyMatchDto {
  policyId: Guid | null;
  name: string;
  evaluationOrder: number;
  isDefault: boolean;
  isActive: boolean;
  matched: boolean;
  selected: boolean;
  note: string;
}

export interface SimulatedMilestoneDto {
  milestone: string;
  targetMinutes: number;
  businessHours: boolean;
  startAt: IsoDateTime;
  dueAt: IsoDateTime;
  warningAt: IsoDateTime | null;
  currentDueAt: IsoDateTime | null;
  currentStatus: string | null;
}

export interface SlaSimulationDto {
  ticketId: Guid;
  number: number;
  subject: string;
  priority: string;
  status: string;
  selectedPolicyId: Guid | null;
  selectedPolicyName: string | null;
  selectionReason: string;
  calendarSource: string;
  calendarName: string | null;
  evaluated: SlaPolicyMatchDto[];
  milestones: SimulatedMilestoneDto[];
  currentPolicyId: Guid | null;
  currentPolicyName: string | null;
  wouldChange: boolean;
}

// ───── Calendars ─────

export interface BusinessCalendarListItemDto {
  id: Guid;
  name: string;
  description: string | null;
  timeZone: string;
  is24x7: boolean;
  isDefault: boolean;
  holidayCount: number;
  weeklyHours: number;
  usageCount: number;
}

export interface CalendarHolidayDto {
  id: Guid;
  date: IsoDate;
  name: string;
  recurring: boolean;
}

export interface BusinessCalendarDto {
  id: Guid;
  name: string;
  description: string | null;
  timeZone: string;
  is24x7: boolean;
  isDefault: boolean;
  weeklyHours: WeeklyHoursDto;
  weeklyHoursTotal: number;
  holidays: CalendarHolidayDto[];
  usedBy: string[];
  rowVersion: string;
}

export interface SaveBusinessCalendarRequest {
  name: string;
  description: string | null;
  timeZone: string;
  is24x7: boolean;
  isDefault: boolean;
  weeklyHours: WeeklyHoursDto;
  rowVersion?: string | null;
}

export interface SaveHolidayRequest {
  date: IsoDate;
  name: string;
  recurring: boolean;
}

export interface ImportHolidaysRequest {
  /** Lines "date,name[,recurring]". */
  csv: string;
  replaceExisting: boolean;
}

export interface ImportHolidaysResultDto {
  added: number;
  updated: number;
  skipped: number;
  errors: string[];
}

export interface BusinessTimePreviewRequest {
  from: IsoDateTime;
  to: IsoDateTime;
  addMinutes: number | null;
}

export interface BusinessIntervalDto {
  start: IsoDateTime;
  end: IsoDateTime;
  minutes: number;
}

export interface BusinessTimePreviewDto {
  from: IsoDateTime;
  to: IsoDateTime;
  businessMinutes: number;
  calendarMinutes: number;
  dueAt: IsoDateTime | null;
  intervals: BusinessIntervalDto[];
  holidaysInRange: CalendarHolidayDto[];
  timeZone: string;
}

// ───── Live breaches ─────

export interface SlaBreachQuery {
  /** AtRisk, Breached or All */
  state?: string | null;
  departmentId?: Guid | null;
  milestone?: string | null;
  cursor?: string | null;
  limit?: number | null;
}

export interface SlaBreachItemDto {
  ticketId: Guid;
  milestoneId: Guid;
  number: number;
  subject: string;
  priority: string;
  ticketSlaState: string;
  milestone: string;
  milestoneStatus: 'AtRisk' | 'Breached' | string;
  dueAt: IsoDateTime;
  breachedAt: IsoDateTime | null;
  targetMinutes: number;
  elapsedMinutes: number;
  assigneeId: Guid | null;
  assigneeName: string | null;
  departmentId: Guid;
  departmentName: string | null;
  policyName: string | null;
  escalationLevel: number;
}

export interface SlaBreachSummaryDto {
  atRisk: number;
  breached: number;
  breachedToday: number;
  paused: number;
  dueNextHour: number;
}
