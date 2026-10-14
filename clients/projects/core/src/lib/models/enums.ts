/**
 * String enums used on the wire (see the header of SupportDesk.Contracts.Tickets.TicketDtos).
 * Each union has a matching readonly array for building pickers and filters.
 */

export const CHANNELS = [
  'Email',
  'Portal',
  'WebForm',
  'Chat',
  'WhatsApp',
  'Phone',
  'Sms',
  'Facebook',
  'Instagram',
  'MobileApp',
  'Api',
  'WalkIn',
  'Erp',
  'Internal',
] as const;
export type Channel = (typeof CHANNELS)[number];

export const PRIORITIES = ['Low', 'Medium', 'High', 'Critical'] as const;
export type Priority = (typeof PRIORITIES)[number];

export const SEVERITIES = ['Cosmetic', 'Minor', 'Major', 'Critical'] as const;
export type Severity = (typeof SEVERITIES)[number];

export const STATUS_CATEGORIES = ['Open', 'Assigned', 'InProgress', 'Waiting', 'Resolved', 'Closed'] as const;
export type StatusCategory = (typeof STATUS_CATEGORIES)[number];

export const SLA_STATES = ['None', 'OnTrack', 'AtRisk', 'Breached', 'Paused', 'Met'] as const;
export type SlaState = (typeof SLA_STATES)[number];

export const ENTITLEMENT_STATUSES = ['Unknown', 'Entitled', 'NotEntitled', 'Overridden', 'Expired'] as const;
export type EntitlementStatus = (typeof ENTITLEMENT_STATUSES)[number];

export const SENTIMENTS = ['Unknown', 'Positive', 'Neutral', 'Negative', 'Frustrated'] as const;
export type Sentiment = (typeof SENTIMENTS)[number];

export const AVAILABILITIES = ['Online', 'Away', 'Busy', 'Offline'] as const;
export type Availability = (typeof AVAILABILITIES)[number];

/** Ticket list sort keys accepted by GET /tickets?sort=. */
export const TICKET_SORTS = ['lastActivity', 'created', 'due', 'priority', 'number'] as const;
export type TicketSort = (typeof TICKET_SORTS)[number];

export type SortDirection = 'asc' | 'desc';

/** Bulk ticket actions (SupportDesk.Contracts.Tickets.BulkActions). */
export const BULK_ACTIONS = [
  'assign',
  'status',
  'priority',
  'tags_add',
  'tags_remove',
  'transfer',
  'merge',
  'delete',
  'spam',
  'close',
  'watch',
] as const;
export type BulkAction = (typeof BULK_ACTIONS)[number];
