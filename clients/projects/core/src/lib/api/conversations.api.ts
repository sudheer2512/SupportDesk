import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { Guid, PagedResult, ProblemDetails } from '../models/common';
import {
  AgentChatMessageRequest,
  ChatCapacityDto,
  ChatTransferResult,
  ConversationDetailDto,
  ConversationListItemDto,
  ConversationMessageDto,
  ConversationQuery,
  MessageDetailDto,
  TransferChatRequest,
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

  /**
   * Hands a live chat to another agent and/or department; the chat's ticket follows. 409 `chat_limit_reached` when the
   * agent is full; a department whose transfers need approval answers 202 (problem details) — a typed result here.
   */
  transfer(id: Guid, request: TransferChatRequest): Observable<ChatTransferResult> {
    return this.api.post<ConversationDetailDto | ProblemDetails>(`conversations/${encodeURIComponent(id)}/transfer`, request, { silent: true }).pipe(
      map((body): ChatTransferResult => {
        const problem = body as ProblemDetails;
        if (problem && problem.code === 'approval_required') {
          return {
            kind: 'approval_required',
            approvalId: (problem['approvalId'] as string | undefined) ?? null,
            message: problem.title ?? 'An approval request was sent.',
          };
        }
        return { kind: 'transferred', detail: body as ConversationDetailDto };
      }),
    );
  }

  /** The caller's live-chat capacity (active chats vs. MaxConcurrentChats). */
  chatCapacity(): Observable<ChatCapacityDto> {
    return this.api.get<ChatCapacityDto>('conversations/chat-capacity', { silent: true });
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
