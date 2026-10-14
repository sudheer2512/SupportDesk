import { AttachmentDto, Guid, IsoDateTime, JsonValue } from './common';

/** Mirrors SupportDesk.Contracts.Communication — channel accounts, omnichannel inbox, live chat and simulator. */

export const CHANNEL_TYPES = ['Email', 'WhatsApp', 'Sms', 'Chat', 'Voice', 'Facebook', 'Instagram'] as const;
export type ChannelType = (typeof CHANNEL_TYPES)[number];

export type ChannelProvider =
  | 'FileDrop'
  | 'Imap'
  | 'Smtp'
  | 'Microsoft365'
  | 'Gmail'
  | 'MetaCloud'
  | 'MetaGraph'
  | 'Twilio'
  | 'GenericSms'
  | 'GenericVoice'
  | 'Widget'
  | 'Simulator';

export type ChannelAccountStatus = 'Connected' | 'Error' | 'Disabled' | 'PendingVerification';

/** A configured channel endpoint. Secrets are write-only: only the names of the secrets that are set come back. */
export interface ChannelAccountDto {
  id: Guid;
  type: ChannelType;
  provider: ChannelProvider | string;
  name: string;
  address: string;
  displayName: string | null;
  brandId: Guid | null;
  departmentId: Guid | null;
  settings: { [key: string]: JsonValue };
  secretKeys: string[];
  isActive: boolean;
  status: ChannelAccountStatus;
  lastError: string | null;
  lastPolledAt: IsoDateTime | null;
  signatureHtml: string | null;
  autoAcknowledge: boolean;
  acceptUnknownSenders: boolean;
  /** Relative webhook URL to configure at the provider (null when the provider does not use webhooks). */
  webhookPath: string | null;
  createdAt: IsoDateTime;
  updatedAt: IsoDateTime;
  rowVersion: string;
}

export interface CreateChannelAccountRequest {
  type: ChannelType;
  provider: ChannelProvider | string;
  name: string;
  address?: string | null;
  displayName?: string | null;
  brandId?: Guid | null;
  departmentId?: Guid | null;
  settings?: { [key: string]: JsonValue } | null;
  /** Provider secrets (passwords, tokens). Stored encrypted; never returned. */
  secrets?: Record<string, string> | null;
  signatureHtml?: string | null;
  autoAcknowledge?: boolean;
  acceptUnknownSenders?: boolean;
  isActive?: boolean;
}

/** Partial update. Secrets: a value replaces, an empty string removes, absent keys are kept. Settings keys set to null are removed. */
export interface UpdateChannelAccountRequest {
  rowVersion: string;
  name?: string | null;
  address?: string | null;
  displayName?: string | null;
  brandId?: Guid | null;
  departmentId?: Guid | null;
  settings?: { [key: string]: JsonValue } | null;
  secrets?: Record<string, string> | null;
  signatureHtml?: string | null;
  autoAcknowledge?: boolean | null;
  acceptUnknownSenders?: boolean | null;
  isActive?: boolean | null;
  resetCursor?: boolean;
}

export interface ChannelTestResultDto {
  success: boolean;
  message: string;
  details: Record<string, string> | null;
  status: ChannelAccountStatus;
  testedAt: IsoDateTime;
}

export interface ChannelProviderDto {
  provider: ChannelProvider | string;
  channelTypes: ChannelType[];
  description: string;
  settingKeys: string[];
  secretKeys: string[];
  polling: boolean;
  webhooks: boolean;
}

export interface PollResultDto {
  fetched: number;
  processed: number;
  duplicates: number;
  failed: number;
  error: string | null;
}

// ───────────── Simulator ─────────────

export interface SimulatedAttachment {
  fileName: string;
  contentType: string;
  base64: string;
  contentId?: string | null;
  inline?: boolean;
}

export interface SimulateEmailRequest {
  from: string;
  fromName?: string | null;
  to?: string[] | null;
  cc?: string[] | null;
  subject: string;
  text?: string | null;
  html?: string | null;
  messageId?: string | null;
  inReplyTo?: string | null;
  references?: string[] | null;
  headers?: Record<string, string> | null;
  attachments?: SimulatedAttachment[] | null;
}

export interface SimulateMessageRequest {
  /** Phone number (WhatsApp/SMS) or social user id (Messenger/Instagram PSID). */
  from: string;
  fromName?: string | null;
  text?: string | null;
  externalId?: string | null;
  media?: SimulatedAttachment | null;
}

export type CallEventType = 'started' | 'answered' | 'ended' | 'missed' | 'voicemail';

export interface SimulateCallRequest {
  event: CallEventType;
  callId?: string | null;
  from: string;
  to?: string | null;
  direction?: 'inbound' | 'outbound' | null;
  durationSeconds?: number | null;
  recordingUrl?: string | null;
  agentEmail?: string | null;
  disposition?: string | null;
  notes?: string | null;
}

export interface SimulateStatusRequest {
  providerMessageId: string;
  status: 'Sent' | 'Delivered' | 'Read' | 'Failed';
  error?: string | null;
}

/** created, threaded, duplicate, ignored, rejected, processing, status, call */
export interface InboundResultDto {
  outcome: string;
  messageId: Guid | null;
  conversationId: Guid | null;
  ticketId: Guid | null;
  ticketNumber: number | null;
  detail: string | null;
}

export type MessageStatus = 'Received' | 'Processed' | 'Duplicate' | 'Rejected' | 'Queued' | 'Sending' | 'Sent' | 'Delivered' | 'Read' | 'Failed';

export interface DeliveryHistoryEntry {
  status: string;
  at: IsoDateTime;
  detail: string | null;
}

export interface MessageDeliveryDto {
  id: Guid;
  recipient: string;
  status: MessageStatus;
  attempts: number;
  nextAttemptAt: IsoDateTime;
  lastAttemptAt: IsoDateTime | null;
  providerMessageId: string | null;
  error: string | null;
  statusHistory: DeliveryHistoryEntry[];
  updatedAt: IsoDateTime;
}

export interface OutboundMessageDto {
  id: Guid;
  channel: string;
  to: string | null;
  subject: string | null;
  bodyText: string | null;
  status: MessageStatus;
  ticketId: Guid | null;
  threadId: Guid | null;
  internetMessageId: string | null;
  occurredAt: IsoDateTime;
  deliveries: MessageDeliveryDto[];
}

// ───────────── Omnichannel inbox ─────────────

export type ConversationStatus = 'Open' | 'BotActive' | 'WaitingForAgent' | 'AgentActive' | 'Closed';

/** Inbox query. `status: 'active'` = everything except Closed. */
export interface ConversationQuery {
  channel?: string | null;
  status?: ConversationStatus | 'active' | null;
  assigneeId?: Guid | null;
  mine?: boolean;
  unassigned?: boolean;
  channelAccountId?: Guid | null;
  contactId?: Guid | null;
  ticketId?: Guid | null;
  q?: string | null;
  cursor?: string | null;
  limit?: number;
}

export interface ConversationListItemDto {
  id: Guid;
  /** Ticket channel names: Email, WhatsApp, Sms, Chat, Phone, Facebook, Instagram, … */
  channel: string;
  status: ConversationStatus;
  channelAccountId: Guid;
  channelAccountName: string | null;
  subject: string | null;
  contactId: Guid | null;
  contactName: string | null;
  visitorName: string | null;
  ticketId: Guid | null;
  ticketNumber: number | null;
  ticketStatusCategory: string | null;
  assignedUserId: Guid | null;
  assignedUserName: string | null;
  lastMessagePreview: string | null;
  lastMessageDirection: 'Inbound' | 'Outbound' | 'Internal' | null;
  startedAt: IsoDateTime;
  lastMessageAt: IsoDateTime;
  serviceWindowEndsAt: IsoDateTime | null;
  identityVerified: boolean;
}

export type MessageAuthor = 'customer' | 'agent' | 'bot' | 'system';

export interface ConversationMessageDto {
  id: Guid;
  direction: 'Inbound' | 'Outbound' | 'Internal';
  channel: string;
  /** text, image, document, audio, video, location, template, interactive, call */
  contentType: string;
  author: MessageAuthor;
  authorName: string | null;
  fromAddress: string | null;
  to: string[];
  subject: string | null;
  bodyText: string | null;
  bodyHtml: string | null;
  status: MessageStatus;
  isAutoReply: boolean;
  ticketId: Guid | null;
  threadId: Guid | null;
  internetMessageId: string | null;
  metadata: { [key: string]: JsonValue } | null;
  occurredAt: IsoDateTime;
  attachments: AttachmentDto[] | null;
}

export interface CallRecordDto {
  id: Guid;
  externalCallId: string;
  direction: 'Inbound' | 'Outbound';
  status: 'Ringing' | 'InProgress' | 'Completed' | 'Missed' | 'Voicemail' | 'Failed';
  fromNumber: string;
  toNumber: string;
  startedAt: IsoDateTime;
  answeredAt: IsoDateTime | null;
  endedAt: IsoDateTime | null;
  durationSeconds: number;
  recordingUrl: string | null;
  agentUserId: Guid | null;
  ticketId: Guid | null;
  disposition: string | null;
}

export interface ConversationDetailDto {
  summary: ConversationListItemDto;
  externalKey: string;
  visitorEmail: string | null;
  rating: number | null;
  metadata: { [key: string]: JsonValue } | null;
  messages: ConversationMessageDto[];
  calls: CallRecordDto[];
  /** WhatsApp only: true while the 24-hour customer service window is open. */
  serviceWindowOpen: boolean | null;
}

export interface MessageDetailDto {
  message: ConversationMessageDto;
  deliveries: MessageDeliveryDto[];
  rawFileId: Guid | null;
}

export interface AgentChatMessageRequest {
  text: string;
  clientRequestId?: string | null;
}

/** POST /conversations/{id}/transfer — to an agent and/or a department (department needs tickets.transfer). */
export interface TransferChatRequest {
  agentId?: Guid | null;
  departmentId?: Guid | null;
  note?: string | null;
}

/** Result of a chat transfer: moved, or the ticket's department transfer is waiting for approval (HTTP 202). */
export type ChatTransferResult =
  | { kind: 'transferred'; detail: ConversationDetailDto }
  | { kind: 'approval_required'; approvalId: Guid | null; message: string };

/** GET /conversations/chat-capacity — active live chats against the routing profile's MaxConcurrentChats. */
export interface ChatCapacityDto {
  active: number;
  limit: number;
  atLimit: boolean;
}

// ───────────── Live chat (widget + /hubs/chat) ─────────────

export interface ChatWidgetConfigDto {
  widgetKey: string;
  title: string;
  brandSlug: string | null;
  brandName: string | null;
  primaryColor: string;
  accentColor: string;
  greeting: string;
  botEnabled: boolean;
  requireEmail: boolean;
  identityVerificationEnabled: boolean;
  agentsOnline: boolean;
  offlineMessage: string | null;
}

export interface ChatCitationDto {
  index: number;
  title: string;
  url: string | null;
}

/** `chat.message` on /hubs/chat. `author`: visitor, agent, bot, system. */
export interface ChatMessageDto {
  id: Guid;
  author: 'visitor' | 'agent' | 'bot' | 'system' | string;
  authorName: string | null;
  text: string;
  at: IsoDateTime;
  citations?: ChatCitationDto[] | null;
}

/** `chat.status` on /hubs/chat. */
export interface ChatStatusEvent {
  conversationId: Guid;
  status: ConversationStatus;
  agentName: string | null;
  identityVerified: boolean;
  ticketId: Guid | null;
}

/** `chat.typing` on /hubs/chat. */
export interface ChatTypingEvent {
  conversationId: Guid;
  from: 'visitor' | 'agent';
  name: string;
  isTyping: boolean;
}

/** `conversation.updated` on /hubs/desk (tenant group). `kind`: status, message, delivery. */
export interface ConversationChangedEvent {
  conversationId: Guid;
  channel: string;
  status: ConversationStatus;
  ticketId: Guid | null;
  assignedUserId: Guid | null;
  lastMessageAt: IsoDateTime;
  kind?: 'status' | 'message' | 'delivery';
  messageId?: Guid | null;
}
