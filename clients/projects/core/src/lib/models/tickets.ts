import { AttachmentDto, FieldChangeDto, Guid, IsoDate, IsoDateTime, JsonValue } from './common';
import {
  BulkAction,
  Channel,
  EntitlementStatus,
  Priority,
  Sentiment,
  Severity,
  SlaState,
  SortDirection,
  StatusCategory,
  TicketSort,
} from './enums';
import { FilterGroup } from './filter';

/** Mirrors SupportDesk.Contracts.Tickets. Field names are exactly the backend's (camelCase). */

export interface TicketListItemDto {
  id: Guid;
  number: number;
  displayNumber: string;
  subject: string;
  channel: Channel;
  priority: Priority;
  severity: Severity | null;
  statusId: Guid;
  status: string;
  statusCategory: StatusCategory;
  statusColor: string;
  departmentId: Guid;
  departmentName: string;
  teamId: Guid | null;
  teamName: string | null;
  assigneeId: Guid | null;
  assigneeName: string | null;
  customerId: Guid | null;
  customerName: string | null;
  customerTier: string | null;
  contactId: Guid | null;
  contactName: string | null;
  productName: string | null;
  categoryName: string | null;
  slaState: SlaState;
  dueAt: IsoDateTime | null;
  firstResponseDueAt: IsoDateTime | null;
  createdAt: IsoDateTime;
  lastActivityAt: IsoDateTime;
  lastCustomerResponseAt: IsoDateTime | null;
  isEscalated: boolean;
  sentiment: Sentiment;
  agentReplyCount: number;
  customerReplyCount: number;
  tags: string[];
  hasUnreadCustomerReply: boolean;
}

export interface TicketDetailDto {
  id: Guid;
  number: number;
  displayNumber: string;
  subject: string;
  descriptionHtml: string | null;
  channel: Channel;
  brandId: Guid | null;
  brandName: string | null;
  customerId: Guid | null;
  customerName: string | null;
  customerTier: string | null;
  contactId: Guid | null;
  contactName: string | null;
  contactEmail: string | null;
  contactPhone: string | null;
  contactIsVip: boolean;
  locationId: Guid | null;
  locationName: string | null;
  departmentId: Guid;
  departmentName: string;
  teamId: Guid | null;
  teamName: string | null;
  assigneeId: Guid | null;
  assigneeName: string | null;
  productId: Guid | null;
  productName: string | null;
  moduleId: Guid | null;
  moduleName: string | null;
  versionId: Guid | null;
  versionName: string | null;
  installationId: Guid | null;
  installationName: string | null;
  assetId: Guid | null;
  assetName: string | null;
  categoryId: Guid | null;
  categoryName: string | null;
  subcategoryId: Guid | null;
  subcategoryName: string | null;
  issueTypeId: Guid | null;
  issueTypeName: string | null;
  priority: Priority;
  severity: Severity | null;
  statusId: Guid;
  status: string;
  statusCategory: StatusCategory;
  statusColor: string;
  substatus: string | null;
  language: string;
  createdAt: IsoDateTime;
  updatedAt: IsoDateTime;
  assignedAt: IsoDateTime | null;
  firstResponseAt: IsoDateTime | null;
  firstResponseDueAt: IsoDateTime | null;
  nextResponseDueAt: IsoDateTime | null;
  dueAt: IsoDateTime | null;
  resolvedAt: IsoDateTime | null;
  closedAt: IsoDateTime | null;
  lastActivityAt: IsoDateTime;
  slaPolicyId: Guid | null;
  slaPolicyName: string | null;
  slaState: SlaState;
  entitlementStatus: EntitlementStatus;
  contractId: Guid | null;
  contractName: string | null;
  entitlementNote: string | null;
  parentTicketId: Guid | null;
  parentTicketNumber: number | null;
  mergedIntoTicketId: Guid | null;
  resolutionCode: string | null;
  rootCause: string | null;
  resolutionHtml: string | null;
  awaitingCustomerConfirmation: boolean;
  csatScore: number | null;
  csatComment: string | null;
  sentiment: Sentiment;
  aiSummary: string | null;
  timeSpentMinutes: number;
  billableMinutes: number;
  supportCost: number | null;
  reopenCount: number;
  isEscalated: boolean;
  escalationLevel: number;
  lastThreadSeq: number;
  workflowId: Guid | null;
  workflowStateKey: string | null;
  customFields: Record<string, JsonValue>;
  tags: string[];
  watchers: WatcherDto[];
  links: TicketLinkDto[];
  children: ChildTicketDto[];
  externalReferences: ExternalReferenceDto[];
  lock: TicketLockDto | null;
  /** Base64 optimistic-concurrency token; send it back on PATCH / status changes. */
  rowVersion: string;
  can: TicketPermissionsDto;
}

/** What the current user may do on this ticket (computed server-side from permissions and scope). */
export interface TicketPermissionsDto {
  edit: boolean;
  reply: boolean;
  note: boolean;
  assign: boolean;
  transfer: boolean;
  escalate: boolean;
  resolve: boolean;
  reopen: boolean;
  merge: boolean;
  split: boolean;
  delete: boolean;
  logTime: boolean;
  viewCosts: boolean;
  overrideEntitlement: boolean;
}

export interface WatcherDto {
  userId: Guid;
  name: string;
  kind: string;
  addedAt: IsoDateTime;
  expiresAt: IsoDateTime | null;
}

export interface TicketLinkDto {
  id: Guid;
  linkType: string;
  direction: string;
  ticketId: Guid;
  number: number;
  subject: string;
  status: string;
}

export interface ChildTicketDto {
  id: Guid;
  number: number;
  subject: string;
  status: string;
  statusCategory: StatusCategory;
  assigneeName: string | null;
}

export interface ExternalReferenceDto {
  id: Guid;
  system: string;
  recordType: string;
  recordId: Guid | null;
  recordNumber: string | null;
  url: string | null;
  isOrigin: boolean;
  context: JsonValue | null;
}

export interface TicketLockDto {
  userId: Guid;
  userName: string;
  until: IsoDateTime;
}

export interface NewContactDto {
  fullName: string | null;
  email: string | null;
  phone: string | null;
  customerId: Guid | null;
}

export interface ExternalReferenceInput {
  system: string;
  recordType: string;
  recordId: Guid | null;
  recordNumber: string | null;
  url: string | null;
  context: JsonValue | null;
}

export interface CreateTicketRequest {
  subject: string;
  descriptionHtml?: string | null;
  channel?: Channel;
  brandId?: Guid | null;
  customerId?: Guid | null;
  contactId?: Guid | null;
  /** Creates or matches a contact by e-mail/phone when contactId is not given. */
  newContact?: NewContactDto | null;
  locationId?: Guid | null;
  departmentId?: Guid | null;
  teamId?: Guid | null;
  assigneeId?: Guid | null;
  productId?: Guid | null;
  moduleId?: Guid | null;
  versionId?: Guid | null;
  installationId?: Guid | null;
  assetId?: Guid | null;
  categoryId?: Guid | null;
  subcategoryId?: Guid | null;
  issueTypeId?: Guid | null;
  priority?: Priority | null;
  severity?: Severity | null;
  statusId?: Guid | null;
  parentTicketId?: Guid | null;
  tags?: string[] | null;
  customFields?: Record<string, JsonValue> | null;
  attachmentFileIds?: Guid[] | null;
  origin?: ExternalReferenceInput | null;
  /** Skip automatic routing (assignment rules). */
  skipRouting?: boolean;
  /** Idempotency key: the same key returns the same ticket. */
  clientRequestId?: string | null;
}

/** Partial update: only provided (non-null) properties change. `rowVersion` is required. */
export interface UpdateTicketRequest {
  rowVersion: string;
  subject?: string | null;
  descriptionHtml?: string | null;
  customerId?: Guid | null;
  contactId?: Guid | null;
  locationId?: Guid | null;
  productId?: Guid | null;
  moduleId?: Guid | null;
  versionId?: Guid | null;
  installationId?: Guid | null;
  assetId?: Guid | null;
  categoryId?: Guid | null;
  subcategoryId?: Guid | null;
  issueTypeId?: Guid | null;
  priority?: Priority | null;
  severity?: Severity | null;
  language?: string | null;
  customFields?: Record<string, JsonValue> | null;
  /** Explicitly clear these fields, e.g. ["productId","installationId"]. */
  clear?: string[] | null;
}

export interface AssignTicketRequest {
  assigneeId: Guid | null;
  teamId: Guid | null;
  note?: string | null;
}

export interface TransferTicketRequest {
  departmentId: Guid;
  teamId?: Guid | null;
  assigneeId?: Guid | null;
  reason?: string | null;
}

export interface ChangeStatusRequest {
  statusId: Guid;
  substatus?: string | null;
  comment?: string | null;
  rowVersion?: string | null;
}

export interface EscalateTicketRequest {
  reason: string;
  escalateToUserId?: Guid | null;
  raisePriority: boolean;
}

export interface ResolveTicketRequest {
  resolutionCode?: string | null;
  rootCause?: string | null;
  resolutionHtml?: string | null;
  notifyCustomer?: boolean;
  requestConfirmation?: boolean;
}

export interface ReopenTicketRequest {
  reason: string;
}

/** Body of POST /tickets/{id}/close (shape not fixed by the contract: an optional comment). */
export interface CloseTicketRequest {
  comment?: string | null;
}

export interface MergeTicketsRequest {
  sourceTicketIds: Guid[];
  note?: string | null;
}

export interface SplitTicketRequest {
  threadIds: Guid[];
  subject: string;
  linkAsChild?: boolean;
}

/** Body of POST /tickets/{id}/clone (shape not fixed by the contract). */
export interface CloneTicketRequest {
  subject?: string | null;
  includeAttachments?: boolean;
}

export interface LinkTicketRequest {
  targetTicketId: Guid;
  linkType: string;
}

export interface OverrideEntitlementRequest {
  reason: string;
  contractId?: Guid | null;
}

export interface SetTagsRequest {
  tags: string[];
}

export interface BulkTicketRequest {
  ticketIds: Guid[];
  action: BulkAction;
  /** assign {assigneeId,teamId}, status {statusId}, priority {priority}, tags_add/tags_remove {tags}, transfer {departmentId}, merge {targetId}. */
  parameters?: Record<string, JsonValue> | null;
}

// ───── Conversation ─────

export type ThreadKind = 'reply' | 'note' | 'handover' | 'description' | 'forward' | 'system' | string;
export type ThreadVisibility = 'Public' | 'Internal' | string;

export interface ThreadDto {
  id: Guid;
  seq: number;
  kind: ThreadKind;
  visibility: ThreadVisibility;
  channel: Channel;
  direction: string;
  authorType: string;
  authorUserId: Guid | null;
  authorContactId: Guid | null;
  authorName: string | null;
  fromAddress: string | null;
  to: string[];
  cc: string[];
  subject: string | null;
  bodyHtml: string | null;
  bodyText: string | null;
  deliveryStatus: string | null;
  sentiment: Sentiment;
  channelMetadata: JsonValue | null;
  attachments: AttachmentDto[];
  createdAt: IsoDateTime;
  editedAt: IsoDateTime | null;
}

export interface TicketEventDto {
  id: Guid;
  type: string;
  actorType: string;
  actorId: Guid | null;
  actorName: string | null;
  occurredAt: IsoDateTime;
  summary: string | null;
  changes: FieldChangeDto[];
  details: JsonValue | null;
}

/** One chronological timeline item: either a conversation entry or an activity event. */
export interface TimelineItemDto {
  itemType: 'thread' | 'event';
  at: IsoDateTime;
  thread: ThreadDto | null;
  event: TicketEventDto | null;
}

export interface ReplyRequest {
  bodyHtml: string;
  to?: string[] | null;
  cc?: string[] | null;
  bcc?: string[] | null;
  attachmentFileIds?: Guid[] | null;
  /** Change status together with the reply (e.g. Waiting / Resolved). */
  statusId?: Guid | null;
  /** Last thread seq the agent had seen; newer entries → 409 reply_collision unless `force`. */
  basedOnSeq?: number | null;
  force?: boolean;
  /** Send through this channel (defaults to the ticket's channel). */
  channel?: Channel | null;
  templateId?: Guid | null;
  clientRequestId?: string | null;
}

export type NoteKind = 'note' | 'handover';

export interface NoteRequest {
  bodyHtml: string;
  kind: NoteKind;
  mentionUserIds?: Guid[] | null;
  attachmentFileIds?: Guid[] | null;
  clientRequestId?: string | null;
}

/** Payload of a 409 `reply_collision` problem (extensions). */
export interface ReplyCollisionDto {
  currentSeq: number;
  newEntries: ThreadDto[];
}

export type DraftKind = 'reply' | 'note';

export interface DraftDto {
  kind: DraftKind;
  bodyHtml: string | null;
  to: string[];
  cc: string[];
  templateId: Guid | null;
  basedOnSeq: number;
  updatedAt: IsoDateTime;
}

export interface SaveDraftRequest {
  kind: DraftKind;
  bodyHtml: string | null;
  to?: string[] | null;
  cc?: string[] | null;
  templateId?: Guid | null;
  basedOnSeq: number;
}

// ───── Collaboration items ─────

export interface AddWatcherRequest {
  userId: Guid;
  kind?: string;
  expiresAt?: IsoDateTime | null;
}

export interface TaskDto {
  id: Guid;
  ticketId: Guid;
  parentTaskId: Guid | null;
  title: string;
  description: string | null;
  isChecklistItem: boolean;
  assigneeId: Guid | null;
  assigneeName: string | null;
  dueAt: IsoDateTime | null;
  status: string;
  completedAt: IsoDateTime | null;
  sortOrder: number;
  subtasks: TaskDto[];
}

export interface SaveTaskRequest {
  title: string;
  description?: string | null;
  isChecklistItem: boolean;
  parentTaskId?: Guid | null;
  assigneeId?: Guid | null;
  dueAt?: IsoDateTime | null;
  status?: string | null;
  sortOrder?: number | null;
}

export interface TimeEntryDto {
  id: Guid;
  userId: Guid;
  userName: string;
  startedAt: IsoDateTime;
  minutes: number;
  isBillable: boolean;
  activityType: string | null;
  notes: string | null;
  costAmount: number | null;
  billableAmount: number | null;
  contractNumber: string | null;
}

export interface LogTimeRequest {
  minutes: number;
  startedAt?: IsoDateTime | null;
  isBillable: boolean;
  activityType?: string | null;
  notes?: string | null;
}

export interface FollowUpDto {
  id: Guid;
  userId: Guid;
  userName: string;
  remindAt: IsoDateTime;
  note: string | null;
  status: string;
}

export interface CreateFollowUpRequest {
  remindAt: IsoDateTime;
  note?: string | null;
  userId?: Guid | null;
}

// ───── Views ─────

/** Personal (owner only) or Shared (everyone; creating shared views needs supervisor.monitor). */
export type ViewVisibility = 'Personal' | 'Shared' | string;

export interface SavedViewDto {
  id: Guid;
  scope: string;
  name: string;
  visibility: ViewVisibility;
  isMine: boolean;
  /** Condition tree (see FilterGroup). */
  filter: FilterGroup;
  columns: JsonValue | null;
  sort: JsonValue | null;
  isDefault: boolean;
  sortOrder: number;
  count: number | null;
}

export interface SaveViewRequest {
  scope: string;
  name: string;
  visibility: ViewVisibility;
  filter: FilterGroup;
  columns?: JsonValue | null;
  sort?: JsonValue | null;
  isDefault: boolean;
  sortOrder: number;
}

/** Query parameters for GET /tickets (all optional; list-valued params are sent comma-separated). */
export interface TicketQuery {
  viewId?: Guid | null;
  q?: string | null;
  statusId?: string | string[] | null;
  /** Exact categories, or the lower-case groups "open"/"unresolved" (every open category) and "done" (resolved or closed). */
  statusCategory?: StatusCategory | StatusCategory[] | 'open' | 'unresolved' | 'done' | null;
  priority?: string | string[] | null;
  assigneeId?: string | string[] | null;
  unassigned?: boolean | null;
  mine?: boolean | null;
  watching?: boolean | null;
  departmentId?: string | string[] | null;
  teamId?: string | string[] | null;
  customerId?: Guid | null;
  contactId?: Guid | null;
  productId?: Guid | null;
  channel?: string | string[] | null;
  tag?: string | string[] | null;
  slaState?: SlaState | SlaState[] | null;
  overdue?: boolean | null;
  escalated?: boolean | null;
  createdFrom?: IsoDateTime | null;
  createdTo?: IsoDateTime | null;
  sort?: TicketSort | null;
  dir?: SortDirection | null;
  cursor?: string | null;
  limit?: number | null;
  includeTotal?: boolean | null;
}

// ───── Right-hand context panel ─────

export interface TicketContextDto {
  customer: CustomerSummaryDto | null;
  contact: ContactSummaryDto | null;
  previousTickets: TicketListItemDto[];
  sla: SlaStatusDto | null;
  contracts: ContractSummaryDto[];
  installations: InstallationSummaryDto[];
  relatedTransactions: ExternalReferenceDto[];
  suggestedArticles: ArticleSuggestionDto[];
  aiRecommendations: AiRecommendationDto[];
  engineeringLinks: EngineeringLinkDto[];
}

export interface CustomerSummaryDto {
  id: Guid;
  name: string;
  code: string | null;
  tier: string;
  industry: string | null;
  accountManagerName: string | null;
  serviceRegion: string | null;
  openTickets: number;
  ticketsLast90Days: number;
  csatAverage: number | null;
  slaBreachesLast90Days: number;
  status: string;
}

export interface ContactSummaryDto {
  id: Guid;
  fullName: string;
  email: string | null;
  phone: string | null;
  jobTitle: string | null;
  isVip: boolean;
  preferredLanguage: string;
  preferredChannel: string | null;
  locationName: string | null;
  openTickets: number;
}

export interface SlaStatusDto {
  instanceId: Guid;
  policyName: string;
  status: string;
  milestones: SlaMilestoneDto[];
}

export interface SlaMilestoneDto {
  id: Guid;
  kind: string;
  cycle: number;
  status: string;
  targetMinutes: number;
  businessHours: boolean;
  startedAt: IsoDateTime;
  dueAt: IsoDateTime;
  completedAt: IsoDateTime | null;
  breachedAt: IsoDateTime | null;
  elapsedMinutes: number;
  remainingMinutes: number;
  percentElapsed: number;
  escalationLevel: number;
}

export interface ContractSummaryDto {
  id: Guid;
  number: string;
  name: string;
  type: string;
  status: string;
  startDate: IsoDate;
  endDate: IsoDate;
  prepaidHours: number | null;
  consumedHours: number;
  isBillable: boolean;
  coversTicket: boolean;
}

export interface InstallationSummaryDto {
  id: Guid;
  name: string;
  productName: string;
  version: string | null;
  environment: string;
  deploymentMode: string;
  operatingSystem: string | null;
  databaseVersion: string | null;
  status: string;
}

export interface ArticleSuggestionDto {
  id: Guid;
  title: string;
  summary: string | null;
  visibility: string;
  score: number;
  url: string;
}

export interface AiRecommendationDto {
  kind: string;
  title: string;
  detail: string;
  confidence: number | null;
  executionId: Guid | null;
  payload: JsonValue | null;
}

export interface EngineeringLinkDto {
  id: Guid;
  provider: string;
  itemType: string;
  externalId: string;
  url: string | null;
  title: string;
  state: string | null;
  assignedTo: string | null;
  fixVersion: string | null;
  isResolved: boolean;
  lastSyncedAt: IsoDateTime | null;
}
