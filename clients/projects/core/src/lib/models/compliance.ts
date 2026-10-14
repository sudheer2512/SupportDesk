import { Guid, IsoDateTime, JsonValue } from './common';

/** Mirrors SupportDesk.Contracts.Compliance (audit viewer, retention, legal holds, data-subject requests). */

export interface AuditEntryDto {
  id: Guid;
  sequence: number;
  occurredAt: IsoDateTime;
  actorType: string;
  actorId: Guid | null;
  actorName: string | null;
  action: string;
  entityType: string | null;
  entityId: Guid | null;
  summary: string | null;
  ipAddress: string | null;
  sealed: boolean;
}

export interface AuditDiffDto {
  path: string;
  before: string | null;
  after: string | null;
  kind: 'added' | 'removed' | 'changed';
}

export interface AuditEntryDetailDto {
  entry: AuditEntryDto;
  changes: JsonValue | null;
  diff: AuditDiffDto[];
  userAgent: string | null;
  correlationId: string | null;
  hash: string;
  prevHash: string;
}

export interface AuditFacetsDto {
  actions: string[];
  entityTypes: string[];
  actorTypes: string[];
}

export interface AuditChainVerificationDto {
  valid: boolean;
  checked: number;
  brokenAtSequence: number | null;
  brokenEntryId: Guid | null;
  message: string;
  verifiedAt: IsoDateTime;
  unsealed: number;
  anchoredAfterSequence: number | null;
}

export interface AuditQuery {
  actor?: string | null;
  entityType?: string | null;
  entityId?: Guid | null;
  action?: string | null;
  from?: IsoDateTime | null;
  to?: IsoDateTime | null;
  ip?: string | null;
  search?: string | null;
  cursor?: string | null;
  limit?: number | null;
}

export interface RetentionDataTypeDto {
  key: string;
  label: string;
  description: string;
  actions: string[];
  minimumDays: number;
}

export interface RetentionPolicyDto {
  id: Guid;
  name: string;
  dataType: string;
  action: string;
  afterDays: number;
  isActive: boolean;
  lastRunAt: IsoDateTime | null;
  lastRunAffected: number;
  createdAt: IsoDateTime;
}

export interface SaveRetentionPolicyRequest {
  name: string;
  dataType: string;
  action: string;
  afterDays: number;
  isActive: boolean;
}

export interface RetentionDryRunDto {
  policyId: Guid | null;
  dataType: string;
  action: string;
  afterDays: number;
  cutoff: IsoDateTime;
  eligible: number;
  heldBack: number;
  evaluatedAt: IsoDateTime;
}

export interface RetentionRunResultDto {
  policyId: Guid;
  affected: number;
  heldBack: number;
  ranAt: IsoDateTime;
}

export interface LegalHoldDto {
  id: Guid;
  name: string;
  reason: string;
  customerId: Guid | null;
  customerName: string | null;
  contactId: Guid | null;
  contactName: string | null;
  ticketNumbers: number[];
  fromDate: IsoDateTime | null;
  toDate: IsoDateTime | null;
  active: boolean;
  createdAt: IsoDateTime;
  createdByName: string | null;
  releasedAt: IsoDateTime | null;
  coveredTickets: number;
}

export interface SaveLegalHoldRequest {
  name: string;
  reason: string;
  customerId?: Guid | null;
  contactId?: Guid | null;
  ticketNumbers?: number[] | null;
  fromDate?: IsoDateTime | null;
  toDate?: IsoDateTime | null;
}

export type DataRequestStatusName = 'Pending' | 'Processing' | 'Completed' | 'Rejected' | 'Failed';

export interface DataRequestDto {
  id: Guid;
  type: 'Export' | 'Erasure';
  status: DataRequestStatusName;
  contactId: Guid;
  contactName: string;
  notes: string | null;
  createdAt: IsoDateTime;
  requestedByName: string | null;
  approvedAt: IsoDateTime | null;
  approvedByName: string | null;
  completedAt: IsoDateTime | null;
  rejectionReason: string | null;
  error: string | null;
  canDownload: boolean;
  downloadExpiresAt: IsoDateTime | null;
  downloadedAt: IsoDateTime | null;
  resultFileName: string | null;
  resultSizeBytes: number | null;
  blockedByHold: boolean;
}

export interface CreateDataRequestRequest {
  type: 'Export' | 'Erasure';
  contactId: Guid;
  notes?: string | null;
}

export interface DownloadLinkDto {
  url: string;
  expiresAt: IsoDateTime;
}
