import { Guid, IdName, IsoDateTime, JsonValue } from './common';
import { FilterGroup } from './filter';

/**
 * Mirrors SupportDesk.Contracts.Automation — automation rules, executions, blueprints (state-transition workflows)
 * and outbound webhook subscriptions. Definitions travel as JSON (RuleDefinition / BlueprintDefinition below).
 */

// ───── Designer catalog ─────

export type RuleTrigger =
  | 'ticket.created'
  | 'ticket.updated'
  | 'ticket.status_changed'
  | 'ticket.assigned'
  | 'ticket.reply_added'
  | 'ticket.customer_replied'
  | 'ticket.note_added'
  | 'sla.warning'
  | 'sla.breached'
  | 'customer.activity'
  | 'scheduled'
  | 'webhook';

export interface TriggerInfoDto {
  key: RuleTrigger | string;
  label: string;
  /** event | scheduled | webhook */
  kind: string;
  description: string;
}

export interface ConditionFieldDto {
  field: string;
  label: string;
  /** text, number, datetime, bool, enum, priority, status, department, team, user, customer, contact, product, module, version, installation, category, issueType, brand, tags, blueprint */
  type: string;
}

export interface OperatorInfoDto {
  key: string;
  label: string;
  valueKind: string;
  appliesTo: string[];
}

export interface ActionParamDto {
  name: string;
  label: string;
  /** user, users, team, department, status, category, issueType, role, roles, replyTemplate, customField, enum, list, tags, bool, int, string, template, url, headers, json, secret, any */
  type: string;
  required: boolean;
  description: string | null;
  options: string[] | null;
}

export interface ActionTypeDto {
  type: string;
  label: string;
  category: string;
  description: string;
  params: ActionParamDto[];
}

export interface AutomationCatalogDto {
  triggers: TriggerInfoDto[];
  fields: ConditionFieldDto[];
  operators: OperatorInfoDto[];
  actions: ActionTypeDto[];
  templatePlaceholders: string[];
  blueprintRequiredFields: string[];
}

export interface AutomationLookupsDto {
  roles: IdName[];
  replyTemplates: IdName[];
}

// ───── Definitions (JSON stored in versions) ─────

export interface RuleActionDef {
  type: string;
  params?: Record<string, JsonValue>;
}

export interface RuleDefinition {
  trigger?: { fields?: string[] } | null;
  conditions: FilterGroup;
  actions: RuleActionDef[];
}

export interface BlueprintStateDef {
  key: string;
  name: string;
  statusId?: Guid | null;
  substatus?: string | null;
  x?: number | null;
  y?: number | null;
  isInitial?: boolean;
  isFinal?: boolean;
}

export interface BlueprintApprovalDef {
  roleId?: Guid | null;
  userId?: Guid | null;
  note?: string | null;
}

export interface BlueprintTransitionDef {
  key: string;
  name: string;
  /** Source state key(s); "*" = any non-final state. */
  from: string | string[];
  to: string;
  allowedRoleIds?: Guid[];
  requiredFields?: string[];
  validation?: FilterGroup | null;
  validationMessage?: string | null;
  approval?: BlueprintApprovalDef | null;
  actions?: RuleActionDef[];
  requireComment?: boolean;
}

export interface BlueprintDefinition {
  states: BlueprintStateDef[];
  transitions: BlueprintTransitionDef[];
}

// ───── Rules ─────

export interface RuleSummaryDto {
  id: Guid;
  name: string;
  description: string | null;
  trigger: string;
  isActive: boolean;
  executionOrder: number;
  stopProcessing: boolean;
  publishedVersionId: Guid | null;
  publishedVersionNumber: number | null;
  latestVersionNumber: number;
  hasUnpublishedDraft: boolean;
  scheduleIntervalMinutes: number | null;
  nextScheduledRunAt: IsoDateTime | null;
  webhookUrl: string | null;
  executionCount: number;
  lastExecutedAt: IsoDateTime | null;
  updatedAt: IsoDateTime;
  rowVersion: string;
}

export interface RuleVersionSummaryDto {
  id: Guid;
  versionNumber: number;
  /** Draft | Published | Superseded */
  state: string;
  changeNote: string | null;
  createdAt: IsoDateTime;
  createdByName: string | null;
  publishedAt: IsoDateTime | null;
  publishedByName: string | null;
}

export interface RuleVersionDto extends RuleVersionSummaryDto {
  definition: JsonValue;
}

export interface RuleDetailDto {
  rule: RuleSummaryDto;
  published: RuleVersionDto | null;
  latest: RuleVersionDto | null;
  versions: RuleVersionSummaryDto[];
  conflicts: RuleConflictDto[];
  /** Inbound webhook secret — only present right after creation/rotation. */
  webhookSecret: string | null;
}

export interface SaveRuleRequest {
  name: string;
  description?: string | null;
  trigger: string;
  definition?: RuleDefinition | null;
  stopProcessing?: boolean;
  executionOrder?: number | null;
  scheduleIntervalMinutes?: number | null;
  isActive?: boolean | null;
  publish?: boolean;
  changeNote?: string | null;
  rowVersion?: string | null;
}

export interface ValidationIssueDto {
  path: string;
  message: string;
}

export interface ValidationResultDto {
  valid: boolean;
  errors: ValidationIssueDto[];
  warnings: ValidationIssueDto[];
}

// ───── Simulation ─────

export interface SimulateRuleRequest {
  versionId?: Guid | null;
  definition?: RuleDefinition | null;
  trigger?: string | null;
  ticketId?: Guid | null;
  lastTickets?: number | null;
}

export interface ConditionTraceDto {
  field: string;
  op: string;
  expected: string | null;
  actual: string | null;
  result: boolean;
}

export interface SimulatedActionDto {
  index: number;
  type: string;
  description: string;
  wouldRun: boolean;
  note: string | null;
}

export interface SimulationTicketResultDto {
  ticketId: Guid;
  number: number;
  subject: string;
  matched: boolean;
  trace: ConditionTraceDto[];
  actions: SimulatedActionDto[];
  note: string | null;
}

export interface SimulationResultDto {
  ruleId: Guid | null;
  versionId: Guid | null;
  versionNumber: number | null;
  evaluated: number;
  matched: number;
  results: SimulationTicketResultDto[];
}

// ───── Conflicts ─────

export type RuleConflictKind = 'field_conflict' | 'assignment_conflict' | 'tag_conflict' | 'stop_processing';

export interface RuleConflictDto {
  kind: RuleConflictKind | string;
  trigger: string;
  ruleId: Guid;
  ruleName: string;
  otherRuleId: Guid;
  otherRuleName: string;
  field: string | null;
  explanation: string;
}

// ───── Executions ─────

export type RuleExecutionStatus = 'NotMatched' | 'Succeeded' | 'PartiallyFailed' | 'Failed' | 'SkippedLoop' | 'Simulated';

export interface ActionResultDto {
  index: number;
  type: string;
  /** succeeded | failed | skipped | queued (| would_run for simulations) */
  status: string;
  message: string | null;
  error: string | null;
  transient: boolean;
  attempts: number;
}

export interface RuleExecutionDto {
  id: Guid;
  ruleId: Guid;
  ruleName: string | null;
  ruleVersionId: Guid;
  versionNumber: number | null;
  eventId: Guid;
  trigger: string;
  entityType: string;
  entityId: Guid;
  ticketNumber: number | null;
  status: RuleExecutionStatus | string;
  trace: ConditionTraceDto[];
  actions: ActionResultDto[];
  error: string | null;
  chainDepth: number;
  attempt: number;
  startedAt: IsoDateTime;
  completedAt: IsoDateTime;
  correlationId: string | null;
}

export interface ExecutionQuery {
  ruleId?: Guid | null;
  ticketId?: Guid | null;
  status?: string | null;
  trigger?: string | null;
  page?: number;
  pageSize?: number;
}

// ───── Blueprints ─────

export interface BlueprintSummaryDto {
  id: Guid;
  name: string;
  description: string | null;
  departmentId: Guid | null;
  departmentName: string | null;
  categoryId: Guid | null;
  categoryName: string | null;
  isActive: boolean;
  publishedVersionId: Guid | null;
  publishedVersionNumber: number | null;
  latestVersionNumber: number;
  hasUnpublishedDraft: boolean;
  activeTickets: number;
  updatedAt: IsoDateTime;
  rowVersion: string;
}

export interface BlueprintDetailDto {
  blueprint: BlueprintSummaryDto;
  published: RuleVersionDto | null;
  latest: RuleVersionDto | null;
  versions: RuleVersionSummaryDto[];
}

export interface SaveBlueprintRequest {
  name: string;
  description?: string | null;
  departmentId?: Guid | null;
  categoryId?: Guid | null;
  definition?: BlueprintDefinition | null;
  isActive?: boolean | null;
  publish?: boolean;
  changeNote?: string | null;
  rowVersion?: string | null;
}

export interface BlueprintStateDto {
  key: string;
  name: string;
  statusId: Guid | null;
  statusName: string | null;
  substatus: string | null;
  isInitial: boolean;
  isFinal: boolean;
  x: number | null;
  y: number | null;
}

export interface RequiredFieldDto {
  field: string;
  label: string;
  missing: boolean;
}

export interface BlueprintTransitionDto {
  key: string;
  name: string;
  to: string;
  toName: string;
  requiresComment: boolean;
  requiresApproval: boolean;
  requiredFields: RequiredFieldDto[];
  missingFields: string[];
  validationPassed: boolean;
  canExecute: boolean;
  blockedReason: string | null;
}

export interface PendingTransitionDto {
  approvalId: Guid;
  transitionKey: string;
  transitionName: string;
  requestedById: Guid;
  requestedByName: string | null;
  requestedAt: IsoDateTime;
}

export interface TicketBlueprintDto {
  ticketId: Guid;
  workflowId: Guid;
  workflowName: string;
  versionId: Guid | null;
  versionNumber: number | null;
  currentState: BlueprintStateDto;
  transitions: BlueprintTransitionDto[];
  pendingApproval: PendingTransitionDto | null;
  states: BlueprintStateDto[];
}

export interface ExecuteTransitionRequest {
  comment?: string | null;
  /** Mandatory field values supplied with the transition ("severity", "versionId", "cf:key", …). */
  fields?: Record<string, JsonValue>;
}

export interface TransitionResultDto {
  /** completed | pending_approval */
  status: string;
  stateKey: string;
  stateName: string;
  approvalId: Guid | null;
  actionResults: ActionResultDto[];
}

// ───── Outbound webhooks ─────

export type WebhookStatus = 'Active' | 'Disabled' | 'Failing';

export interface WebhookDto {
  id: Guid;
  name: string;
  url: string;
  events: string[];
  status: WebhookStatus | string;
  consecutiveFailures: number;
  lastDeliveryAt: IsoDateTime | null;
  lastStatusCode: number | null;
  createdAt: IsoDateTime;
  updatedAt: IsoDateTime;
  /** Signing secret — only present right after creation or rotation. */
  secret: string | null;
}

export interface SaveWebhookRequest {
  name: string;
  url: string;
  events: string[];
  isActive: boolean;
  secret?: string | null;
}

export type WebhookDeliveryStatus = 'Pending' | 'Succeeded' | 'Failed' | 'DeadLetter';

export interface WebhookDeliveryDto {
  id: Guid;
  webhookId: Guid;
  eventId: Guid;
  eventType: string;
  status: WebhookDeliveryStatus | string;
  attempts: number;
  nextAttemptAt: IsoDateTime;
  responseCode: number | null;
  responseExcerpt: string | null;
  durationMs: number | null;
  createdAt: IsoDateTime;
  deliveredAt: IsoDateTime | null;
  payload: JsonValue | null;
}

export interface WebhookEventTypesDto {
  eventTypes: string[];
}

export interface InboundWebhookResultDto {
  ruleId: Guid;
  ticketId: Guid;
  ticketNumber: number;
  status: string;
  executionId: Guid | null;
  message: string | null;
}
