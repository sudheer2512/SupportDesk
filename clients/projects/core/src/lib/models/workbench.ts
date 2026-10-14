import { Guid, IsoDateTime, JsonValue } from './common';
import { TicketDetailDto, TicketListItemDto } from './tickets';
import { ConditionFieldDto } from './automation';

/** Agent-workbench wire types (queue counts, look-ups, templates, condition catalog). Mirror SupportDesk.Contracts. */

/** Keys of GET /tickets/queue-counts (counts are scoped to what the caller may see). */
export const QUEUE_KEYS = ['mine', 'unassigned', 'open', 'overdue', 'atRisk', 'escalated', 'watching', 'waiting'] as const;
export type QueueKey = (typeof QUEUE_KEYS)[number];
export type QueueCountsDto = Partial<Record<QueueKey, number>> & Record<string, number>;

/** One field of GET /meta/condition-fields (shared with the automation designer). */
export type { ConditionFieldDto } from './automation';

export interface ConditionFieldCatalogDto {
  fields: ConditionFieldDto[];
  operators: string[];
}

/** Reply template / canned response (GET /templates). Placeholders: {{contact.name}}, {{ticket.number}}, … */
export interface ReplyTemplateDto {
  id: Guid;
  name: string;
  category: string | null;
  subject: string | null;
  bodyHtml: string;
  visibility: 'Personal' | 'Team' | 'Department' | 'Global' | string;
  ownerUserId: Guid | null;
  departmentId: Guid | null;
  teamId: Guid | null;
  actions: JsonValue;
  usageCount: number;
}

/** POST /tickets/{id}/render-template/{templateId} */
export interface RenderedTemplateDto {
  bodyHtml: string;
}

/** POST /tickets/{id}/lock */
export interface TicketLockInfoDto {
  userId: Guid;
  userName: string;
  until: IsoDateTime;
}

/** Transfer outcome: done, or a 202 `approval_required` (an approval request was created instead). */
export type TransferResult =
  | { kind: 'transferred'; ticket: TicketDetailDto }
  | { kind: 'approval_required'; approvalId: Guid | null; message: string };

/** GET /search/customers */
export interface CustomerLookupDto {
  id: Guid;
  name: string;
  code: string | null;
  tier: string;
  industry: string | null;
  status: string;
  openTickets: number;
}

/** GET /search/contacts */
export interface ContactLookupDto {
  id: Guid;
  fullName: string;
  email: string | null;
  phone: string | null;
  jobTitle: string | null;
  isVip: boolean;
  customerId: Guid | null;
  customerName: string | null;
  locationId: Guid | null;
  locationName: string | null;
}

/** GET /search/installations */
export interface InstallationLookupDto {
  id: Guid;
  name: string;
  productId: Guid;
  productName: string;
  versionId: Guid | null;
  version: string | null;
  environment: string;
  status: string;
  locationId: Guid | null;
}

/** GET /search?q= */
export interface GlobalSearchResultDto {
  tickets: TicketListItemDto[];
  customers: CustomerLookupDto[];
  contacts: ContactLookupDto[];
}
