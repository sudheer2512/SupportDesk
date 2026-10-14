import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { Guid, PagedResult } from '../models/common';
import {
  AgentChatMessageRequest,
  ConversationDetailDto,
  ConversationListItemDto,
  ConversationMessageDto,
  ConversationQuery,
  MessageDetailDto,
} from '../models/communication';

/** Omnichannel inbox (`/conversations`): unified conversations, messages, delivery status and agent live-chat actions. */
@Injectable({ providedIn: 'root' })
export class ConversationsApi {
  private readonly api = inject(ApiClient);

  list(query: ConversationQuery = {}): Observable<PagedResult<ConversationListItemDto>> {
    return this.api.get<PagedResult<ConversationListItemDto>>('conversations', { params: query });
  }

  get(id: Guid): Observable<ConversationDetailDto> {
    return this.api.get<ConversationDetailDto>(`conversations/${encodeURIComponent(id)}`);
  }

  messages(id: Guid, cursor: string | null = null, limit = 100): Observable<PagedResult<ConversationMessageDto>> {
    return this.api.get<PagedResult<ConversationMessageDto>>(`conversations/${encodeURIComponent(id)}/messages`, { params: { cursor, limit } });
  }

  /** Accepts a waiting live chat (assigns the chat and its ticket to the caller). */
  accept(id: Guid): Observable<ConversationDetailDto> {
    return this.api.post<ConversationDetailDto>(`conversations/${encodeURIComponent(id)}/accept`, {});
  }

  /** Sends a live-chat message to the visitor (recorded as a public reply on the chat's ticket). */
  send(id: Guid, request: AgentChatMessageRequest): Observable<ConversationMessageDto> {
    return this.api.post<ConversationMessageDto>(`conversations/${encodeURIComponent(id)}/messages`, request);
  }

  typing(id: Guid, isTyping: boolean): Observable<void> {
    return this.api.post<void>(`conversations/${encodeURIComponent(id)}/typing`, {}, { params: { isTyping }, silent: true });
  }

  /** Ends a live chat; the transcript is added to the ticket timeline. */
  close(id: Guid, reason?: string | null): Observable<ConversationDetailDto> {
    return this.api.post<ConversationDetailDto>(`conversations/${encodeURIComponent(id)}/close`, { reason: reason ?? null });
  }

  /** One message with its delivery attempts and status history. */
  message(messageId: Guid): Observable<MessageDetailDto> {
    return this.api.get<MessageDetailDto>(`conversations/messages/${encodeURIComponent(messageId)}`);
  }

  /** Original RFC 822 source of an inbound e-mail. */
  raw(messageId: Guid): Observable<Blob> {
    return this.api.blob(`conversations/messages/${encodeURIComponent(messageId)}/raw`);
  }
}
