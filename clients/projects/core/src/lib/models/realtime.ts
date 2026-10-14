import { FieldChangeDto, Guid, IsoDateTime, JsonValue } from './common';
import { Availability } from './enums';
import { NotificationDto } from './notifications';

/** Server → client events on the `/hubs/desk` SignalR hub (method names in comments). */

/** `ticket.changed` */
export interface TicketChangedEvent {
  ticketId: Guid;
  number: number;
  changes: FieldChangeDto[];
  actorName: string | null;
}

/** `ticket.thread` */
export interface TicketThreadEvent {
  ticketId: Guid;
  threadId: Guid;
  seq: number;
  kind: string;
  visibility: string;
}

export type PresenceActivity = 'viewing' | 'replying' | 'noting';

export interface PresenceViewer {
  userId: Guid;
  name: string;
  activity: PresenceActivity;
}

/** `ticket.presence` */
export interface TicketPresenceEvent {
  ticketId: Guid;
  viewers: PresenceViewer[];
}

/** `notification` */
export type NotificationEvent = Omit<NotificationDto, 'readAt' | 'acknowledgedAt'> &
  Partial<Pick<NotificationDto, 'readAt' | 'acknowledgedAt'>>;

/** `agent.availability` */
export interface AgentAvailabilityEvent {
  userId: Guid;
  availability: Availability;
}

/** `chat.message` — shape owned by the live-chat module; kept open here. */
export type ChatMessageEvent = { [key: string]: JsonValue } & { conversationId?: Guid };

/** `conversation.updated` — shape owned by the live-chat module; kept open here. */
export type ConversationUpdatedEvent = { [key: string]: JsonValue } & { conversationId?: Guid };

/** `sla.alert` */
export interface SlaAlertEvent {
  ticketId: Guid;
  number: number;
  milestone: string;
  kind: 'warning' | 'breach';
  dueAt: IsoDateTime;
}

export type RealtimeConnectionState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';
