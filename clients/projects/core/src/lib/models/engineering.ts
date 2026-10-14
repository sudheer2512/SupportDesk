import { Guid, IdName, IsoDate, IsoDateTime } from './common';

/** Mirrors SupportDesk.Contracts.Engineering / SupportDesk.Contracts.Collaboration. */

export type EngineeringTrackerProvider = 'AzureDevOps' | 'GitHub' | 'Local';
export type EngineeringItemTypeName = 'Bug' | 'Feature' | 'Task';
export type LocalWorkItemState = 'New' | 'Active' | 'Resolved' | 'Closed';
export const LOCAL_WORK_ITEM_STATES: readonly LocalWorkItemState[] = ['New', 'Active', 'Resolved', 'Closed'];

// ───── Tracker connections (admin.integrations) ─────

export interface TrackerFieldMapDto {
  reproStepsField: string | null;
  descriptionField: string | null;
  severityField: string | null;
  priorityField: string | null;
  fixVersionField: string | null;
  customerImpactField: string | null;
  defaultAreaPath: string | null;
  defaultIterationPath: string | null;
  defaultTags: string[];
  severityLabelPrefix: string | null;
  priorityLabelPrefix: string | null;
}

export interface EngineeringTrackerDto {
  id: Guid;
  provider: EngineeringTrackerProvider;
  name: string;
  isActive: boolean;
  status: string;
  baseUrl: string | null;
  project: string | null;
  owner: string | null;
  repository: string | null;
  authType: string;
  hasToken: boolean;
  hasAppPrivateKey: boolean;
  appId: string | null;
  installationId: string | null;
  defaultItemType: string;
  typeMap: Record<string, string>;
  fields: TrackerFieldMapDto;
  resolvedStates: string[];
  releasedStates: string[];
  pollIntervalMinutes: number;
  webhookUrl: string | null;
  hasWebhookSecret: boolean;
  /** Returned once, right after create / rotate. */
  webhookSecret: string | null;
  lastSyncAt: IsoDateTime | null;
  lastError: string | null;
  linkedItemCount: number;
  rowVersion: string;
}

export interface SaveEngineeringTrackerRequest {
  provider: EngineeringTrackerProvider;
  name?: string | null;
  isActive: boolean;
  baseUrl?: string | null;
  project?: string | null;
  owner?: string | null;
  repository?: string | null;
  authType?: string | null;
  /** Write-only: null/undefined keeps the stored value, "" clears it. */
  token?: string | null;
  appPrivateKey?: string | null;
  appId?: string | null;
  installationId?: string | null;
  defaultItemType?: string | null;
  typeMap?: Record<string, string> | null;
  fields?: TrackerFieldMapDto | null;
  resolvedStates?: string[] | null;
  releasedStates?: string[] | null;
  pollIntervalMinutes: number;
  rowVersion?: string | null;
}

export interface TrackerProjectDto {
  id: string;
  name: string;
  url: string | null;
}

export interface TrackerTestResultDto {
  ok: boolean;
  message: string;
  durationMs: number;
  projects: TrackerProjectDto[];
}

export interface TrackerSyncResultDto {
  checked: number;
  updated: number;
  orphaned: number;
  failed: number;
  error: string | null;
}

export interface EngineeringDeliveryDto {
  id: Guid;
  provider: string;
  deliveryId: string;
  eventType: string;
  externalId: string | null;
  status: 'Processed' | 'Ignored' | 'Failed' | string;
  detail: string | null;
  receivedAt: IsoDateTime;
}

export interface EngineeringSettingsDto {
  addNoteOnResolved: boolean;
  addNoteOnReleased: boolean;
  notifyAssignee: boolean;
  notifyCustomerOnRelease: boolean;
  releaseReplyTemplateId: Guid | null;
  setEngineeringSubstatus: boolean;
  escalationStatusId: Guid | null;
  defaultTrackerId: Guid | null;
  replyTemplates: IdName[];
  engineeringStatuses: IdName[];
}

export interface SaveEngineeringSettingsRequest {
  addNoteOnResolved: boolean;
  addNoteOnReleased: boolean;
  notifyAssignee: boolean;
  notifyCustomerOnRelease: boolean;
  releaseReplyTemplateId: Guid | null;
  setEngineeringSubstatus: boolean;
  escalationStatusId: Guid | null;
  defaultTrackerId: Guid | null;
}

// ───── Ticket panel ─────

export interface ActiveTrackerDto {
  id: Guid;
  provider: EngineeringTrackerProvider;
  name: string;
  project: string | null;
  itemTypes: string[];
  defaultItemType: string;
  supportsAssignee: boolean;
  isDefault: boolean;
}

export interface EngineeringArtifactDto {
  kind: 'PullRequest' | 'Changeset' | 'Commit' | 'Build' | 'Release' | string;
  id: string;
  title: string | null;
  url: string | null;
  state: string | null;
}

export interface EngineeringLinkViewDto {
  id: Guid;
  ticketId: Guid;
  itemId: Guid | null;
  trackerId: Guid | null;
  trackerName: string;
  provider: EngineeringTrackerProvider | 'Jira';
  itemType: string;
  externalId: string;
  displayId: string;
  url: string | null;
  title: string;
  state: string | null;
  assignedTo: string | null;
  iterationPath: string | null;
  fixVersion: string | null;
  releaseName: string | null;
  releasedAt: IsoDateTime | null;
  isResolved: boolean;
  isReleased: boolean;
  isOrphaned: boolean;
  orphanReason: string | null;
  lastSyncedAt: IsoDateTime | null;
  syncError: string | null;
  ticketCount: number;
  customerCount: number;
  customerNotified: boolean;
  linkedAt: IsoDateTime;
  artifacts: EngineeringArtifactDto[];
}

export interface TicketEngineeringDto {
  links: EngineeringLinkViewDto[];
  trackers: ActiveTrackerDto[];
  canLink: boolean;
  canManage: boolean;
}

export interface EscalationPrefillDto {
  title: string;
  reproStepsHtml: string;
  severity: string | null;
  priority: string;
  productName: string | null;
  moduleName: string | null;
  version: string | null;
  customerImpactCount: number;
  tags: string[];
}

export interface EscalateToEngineeringRequest {
  trackerId: Guid;
  itemType?: string | null;
  title: string;
  reproStepsHtml?: string | null;
  severity?: string | null;
  priority?: string | null;
  areaPath?: string | null;
  iterationPath?: string | null;
  tags?: string[] | null;
  assigneeUserId?: Guid | null;
  setSubstatus: boolean;
  clientRequestId?: string | null;
}

export interface LinkWorkItemRequest {
  trackerId: Guid;
  reference: string;
  setSubstatus: boolean;
}

export interface EscalationResultDto {
  link: EngineeringLinkViewDto;
  substatusApplied: boolean;
  message: string | null;
}

export interface EngineeringAffectedTicketDto {
  id: Guid;
  number: number;
  displayNumber: string;
  subject: string;
  status: string;
  customerName: string | null;
  createdAt: IsoDateTime;
}

export interface EngineeringAffectedTicketsDto {
  total: number;
  /** Linked tickets outside the caller's ticket scope (counted, not shown). */
  hidden: number;
  tickets: EngineeringAffectedTicketDto[];
}

// ───── Built-in board ─────

export interface EngineeringWorkItemDto {
  id: Guid;
  trackerId: Guid;
  number: number;
  key: string;
  type: EngineeringItemTypeName | string;
  title: string;
  descriptionHtml: string | null;
  state: LocalWorkItemState;
  severity: string | null;
  priority: string;
  assigneeUserId: Guid | null;
  assigneeName: string | null;
  areaPath: string | null;
  iterationPath: string | null;
  tags: string[];
  fixVersion: string | null;
  releaseId: Guid | null;
  releaseName: string | null;
  isReleased: boolean;
  productId: Guid | null;
  productName: string | null;
  linkedTicketCount: number;
  customerImpactCount: number;
  rank: number;
  createdAt: IsoDateTime;
  updatedAt: IsoDateTime;
  resolvedAt: IsoDateTime | null;
  closedAt: IsoDateTime | null;
  rowVersion: string;
}

export interface EngineeringWorkItemDetailDto {
  item: EngineeringWorkItemDto;
  tickets: EngineeringAffectedTicketsDto;
}

export interface SaveEngineeringWorkItemRequest {
  trackerId?: Guid | null;
  type?: string | null;
  title: string;
  descriptionHtml?: string | null;
  severity?: string | null;
  priority?: string | null;
  assigneeUserId?: Guid | null;
  areaPath?: string | null;
  iterationPath?: string | null;
  tags?: string[] | null;
  releaseId?: Guid | null;
  productId?: Guid | null;
  rowVersion?: string | null;
}

export interface MoveEngineeringWorkItemRequest {
  state: LocalWorkItemState;
  beforeId?: Guid | null;
  rowVersion?: string | null;
}

export interface EngineeringReleaseDto {
  id: Guid;
  trackerId: Guid;
  name: string;
  notes: string | null;
  productId: Guid | null;
  productName: string | null;
  plannedDate: IsoDate | null;
  releasedAt: IsoDateTime | null;
  deployedAt: IsoDateTime | null;
  itemCount: number;
  openItemCount: number;
  rowVersion: string;
}

export interface SaveEngineeringReleaseRequest {
  trackerId?: Guid | null;
  name: string;
  notes?: string | null;
  productId?: Guid | null;
  plannedDate?: IsoDate | null;
  rowVersion?: string | null;
}

export interface PublishEngineeringReleaseRequest {
  deployed: boolean;
  rowVersion?: string | null;
}

export interface EngineeringAssigneeDto {
  id: Guid;
  name: string;
  title: string | null;
  isLightAgent: boolean;
}

export interface EngineeringBoardDto {
  trackerId: Guid | null;
  trackerName: string | null;
  states: LocalWorkItemState[];
  releases: EngineeringReleaseDto[];
  assignees: EngineeringAssigneeDto[];
  products: IdName[];
  canManage: boolean;
}

export interface EngineeringWorkItemQuery {
  trackerId?: Guid | null;
  state?: LocalWorkItemState | null;
  assigneeId?: Guid | null;
  releaseId?: Guid | null;
  q?: string | null;
  includeClosed?: boolean;
  cursor?: string | null;
  limit?: number;
}

// ───── Microsoft Teams / Slack ─────

export type CollaborationProviderName = 'Teams' | 'Slack';

export interface CollaborationChannelDto {
  id: Guid;
  provider: CollaborationProviderName;
  name: string;
  urlHint: string;
  events: string[];
  departmentIds: Guid[];
  minPriority: string;
  isActive: boolean;
  lastSentAt: IsoDateTime | null;
  lastStatusCode: number | null;
  lastError: string | null;
  rowVersion: string;
}

export interface SaveCollaborationChannelRequest {
  provider: CollaborationProviderName;
  name: string;
  /** Required on create; null keeps the stored URL on update. */
  webhookUrl?: string | null;
  events: string[];
  departmentIds?: Guid[] | null;
  minPriority?: string | null;
  isActive: boolean;
  rowVersion?: string | null;
}

export interface CollaborationEventTypeDto {
  key: string;
  label: string;
  description: string;
}

export interface CollaborationCatalogDto {
  events: CollaborationEventTypeDto[];
  providers: CollaborationProviderName[];
  allowPrivateNetworks: boolean;
}

export interface CollaborationSendResultDto {
  success: boolean;
  statusCode: number | null;
  durationMs: number;
  error: string | null;
}

export interface CollaborationLogDto {
  id: Guid;
  operation: string;
  statusCode: number | null;
  success: boolean;
  durationMs: number;
  responseExcerpt: string | null;
  createdAt: IsoDateTime;
}

export interface CollaborationShareTargetDto {
  id: Guid;
  provider: CollaborationProviderName;
  name: string;
}

export interface ShareTicketToChannelRequest {
  channelId: Guid;
  comment: string | null;
}
