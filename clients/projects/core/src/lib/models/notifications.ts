import { Guid, IsoDateTime } from './common';

/** In-app notification (GET /notifications, realtime `notification`). */
export type NotificationSeverity = 'info' | 'success' | 'warning' | 'error' | 'critical' | string;

export interface NotificationDto {
  id: Guid;
  type: string;
  title: string;
  body: string | null;
  link: string | null;
  severity: NotificationSeverity;
  requiresAck: boolean;
  readAt: IsoDateTime | null;
  acknowledgedAt: IsoDateTime | null;
  createdAt: IsoDateTime;
  /** What the notification is about (e.g. "Ticket", "ApprovalRequest") — sent by the API, optional for realtime payloads. */
  entityType?: string | null;
  entityId?: Guid | null;
}

/** Ordering used to decide toasts / desktop notifications (case-insensitive; unknown → info). */
export function severityRank(severity: string | null | undefined): number {
  switch ((severity ?? '').toLowerCase()) {
    case 'critical':
      return 4;
    case 'error':
    case 'danger':
    case 'breach':
      return 3;
    case 'warning':
    case 'warn':
      return 2;
    case 'success':
      return 1;
    default:
      return 0;
  }
}
