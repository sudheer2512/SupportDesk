import { Guid, IsoDateTime, JsonValue } from './common';

/** Mirrors SupportDesk.Application.Approvals (GET /approvals, POST /approvals/{id}/decide). */

export const APPROVAL_STATUSES = ['Pending', 'Approved', 'Rejected', 'Expired', 'Cancelled'] as const;
export type ApprovalStatus = (typeof APPROVAL_STATUSES)[number] | string;

export interface ApprovalDto {
  id: Guid;
  /** transfer, blueprint, ai_action, custom … */
  type: string;
  entityType: string;
  entityId: Guid;
  ticketId: Guid | null;
  ticketNumber: number | null;
  ticketSubject: string | null;
  requestedById: Guid;
  requestedByName: string;
  status: ApprovalStatus;
  reason: string | null;
  decisionNote: string | null;
  decidedByName: string | null;
  createdAt: IsoDateTime;
  decidedAt: IsoDateTime | null;
  expiresAt: IsoDateTime | null;
  payload: JsonValue;
}

export interface DecideApprovalRequest {
  approve: boolean;
  note?: string | null;
}

export interface CreateApprovalRequest {
  type: string;
  ticketId: Guid;
  reason: string;
  approverUserId?: Guid | null;
  approverRoleId?: Guid | null;
  payload?: JsonValue | null;
}
