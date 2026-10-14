import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient, ApiRequestOptions } from '../http/api-client';
import { BulkResultDto, Guid, PagedResult } from '../models/common';
import {
  AddWatcherRequest,
  AssignTicketRequest,
  BulkTicketRequest,
  ChangeStatusRequest,
  CloneTicketRequest,
  CloseTicketRequest,
  CreateFollowUpRequest,
  CreateTicketRequest,
  DraftDto,
  DraftKind,
  EscalateTicketRequest,
  FollowUpDto,
  LinkTicketRequest,
  LogTimeRequest,
  MergeTicketsRequest,
  NoteRequest,
  OverrideEntitlementRequest,
  ReopenTicketRequest,
  ReplyRequest,
  ResolveTicketRequest,
  SaveDraftRequest,
  SaveTaskRequest,
  SplitTicketRequest,
  TaskDto,
  ThreadDto,
  TicketContextDto,
  TicketDetailDto,
  TicketListItemDto,
  TicketQuery,
  TimeEntryDto,
  TimelineItemDto,
  TransferTicketRequest,
  UpdateTicketRequest,
  WatcherDto,
} from '../models/tickets';

const enc = encodeURIComponent;

/**
 * Tickets module client (`/api/v1/tickets…`, `/tasks`, `/time-entries`, `/follow-ups`).
 * Errors surface as ApiError: PATCH → 409 `concurrency_conflict` (`err.currentTicket`), reply →
 * 409 `reply_collision` (`err.replyCollision`), workflow → `invalid_transition` / `missing_required_fields`.
 */
@Injectable({ providedIn: 'root' })
export class TicketsApi {
  private readonly api = inject(ApiClient);

  // ───── Queries ─────

  list(query: TicketQuery = {}, options: ApiRequestOptions = {}): Observable<PagedResult<TicketListItemDto>> {
    return this.api.get<PagedResult<TicketListItemDto>>('tickets', { ...options, params: query });
  }

  get(id: Guid): Observable<TicketDetailDto> {
    return this.api.get<TicketDetailDto>(`tickets/${enc(id)}`);
  }

  getByNumber(number: number | string): Observable<TicketDetailDto> {
    return this.api.get<TicketDetailDto>(`tickets/by-number/${enc(String(number))}`);
  }

  timeline(id: Guid): Observable<TimelineItemDto[]> {
    return this.api.get<TimelineItemDto[]>(`tickets/${enc(id)}/timeline`);
  }

  /** Right-hand context panel (customer, contact, SLA, contracts, suggestions…). */
  context(id: Guid): Observable<TicketContextDto> {
    return this.api.get<TicketContextDto>(`tickets/${enc(id)}/context`);
  }

  // ───── Create / update ─────

  create(request: CreateTicketRequest): Observable<TicketDetailDto> {
    return this.api.post<TicketDetailDto>('tickets', request, { silent: true });
  }

  /** Partial update with optimistic concurrency (`rowVersion`). */
  update(id: Guid, request: UpdateTicketRequest): Observable<TicketDetailDto> {
    return this.api.patch<TicketDetailDto>(`tickets/${enc(id)}`, request, { silent: true });
  }

  // ───── Workflow actions ─────

  assign(id: Guid, request: AssignTicketRequest): Observable<TicketDetailDto> {
    return this.action(id, 'assign', request);
  }

  transfer(id: Guid, request: TransferTicketRequest): Observable<TicketDetailDto> {
    return this.action(id, 'transfer', request);
  }

  changeStatus(id: Guid, request: ChangeStatusRequest): Observable<TicketDetailDto> {
    return this.action(id, 'status', request);
  }

  escalate(id: Guid, request: EscalateTicketRequest): Observable<TicketDetailDto> {
    return this.action(id, 'escalate', request);
  }

  resolve(id: Guid, request: ResolveTicketRequest): Observable<TicketDetailDto> {
    return this.action(id, 'resolve', request);
  }

  reopen(id: Guid, request: ReopenTicketRequest): Observable<TicketDetailDto> {
    return this.action(id, 'reopen', request);
  }

  close(id: Guid, request: CloseTicketRequest = {}): Observable<TicketDetailDto> {
    return this.action(id, 'close', request);
  }

  /** Merges the source tickets INTO ticket `id`. */
  merge(id: Guid, request: MergeTicketsRequest): Observable<TicketDetailDto> {
    return this.action(id, 'merge', request);
  }

  /** Moves the given threads into a new ticket; returns the new ticket. */
  split(id: Guid, request: SplitTicketRequest): Observable<TicketDetailDto> {
    return this.action(id, 'split', request);
  }

  clone(id: Guid, request: CloneTicketRequest = {}): Observable<TicketDetailDto> {
    return this.action(id, 'clone', request);
  }

  addLink(id: Guid, request: LinkTicketRequest): Observable<TicketDetailDto> {
    return this.action(id, 'links', request);
  }

  removeLink(id: Guid, linkId: Guid): Observable<void> {
    return this.api.delete<void>(`tickets/${enc(id)}/links/${enc(linkId)}`);
  }

  setTags(id: Guid, tags: string[]): Observable<TicketDetailDto> {
    return this.action(id, 'tags', { tags });
  }

  /** Takes the edit lock (collision avoidance for long edits). */
  lock(id: Guid): Observable<TicketDetailDto> {
    return this.action(id, 'lock', {});
  }

  unlock(id: Guid): Observable<TicketDetailDto> {
    return this.action(id, 'unlock', {});
  }

  overrideEntitlement(id: Guid, request: OverrideEntitlementRequest): Observable<TicketDetailDto> {
    return this.action(id, 'override-entitlement', request);
  }

  bulk(request: BulkTicketRequest): Observable<BulkResultDto> {
    return this.api.post<BulkResultDto>('tickets/bulk', request);
  }

  // ───── Conversation ─────

  /** 409 `reply_collision` → ApiError.replyCollision = {currentSeq, newEntries}; resend with force or new basedOnSeq. */
  reply(id: Guid, request: ReplyRequest): Observable<ThreadDto> {
    return this.api.post<ThreadDto>(`tickets/${enc(id)}/replies`, request, { silent: true });
  }

  addNote(id: Guid, request: NoteRequest): Observable<ThreadDto> {
    return this.api.post<ThreadDto>(`tickets/${enc(id)}/notes`, request, { silent: true });
  }

  /** The current user's draft (null body when none). */
  getDraft(id: Guid, kind: DraftKind): Observable<DraftDto | null> {
    return this.api.get<DraftDto | null>(`tickets/${enc(id)}/draft`, { params: { kind }, silent: true });
  }

  saveDraft(id: Guid, request: SaveDraftRequest): Observable<DraftDto> {
    return this.api.put<DraftDto>(`tickets/${enc(id)}/draft`, request, { params: { kind: request.kind }, silent: true });
  }

  deleteDraft(id: Guid, kind: DraftKind): Observable<void> {
    return this.api.delete<void>(`tickets/${enc(id)}/draft`, { params: { kind }, silent: true });
  }

  // ───── Watchers ─────

  watchers(id: Guid): Observable<WatcherDto[]> {
    return this.api.get<WatcherDto[]>(`tickets/${enc(id)}/watchers`);
  }

  addWatcher(id: Guid, request: AddWatcherRequest): Observable<WatcherDto[]> {
    return this.api.post<WatcherDto[]>(`tickets/${enc(id)}/watchers`, request);
  }

  removeWatcher(id: Guid, userId: Guid): Observable<void> {
    return this.api.delete<void>(`tickets/${enc(id)}/watchers/${enc(userId)}`);
  }

  // ───── Tasks ─────

  tasks(id: Guid): Observable<TaskDto[]> {
    return this.api.get<TaskDto[]>(`tickets/${enc(id)}/tasks`);
  }

  createTask(id: Guid, request: SaveTaskRequest): Observable<TaskDto> {
    return this.api.post<TaskDto>(`tickets/${enc(id)}/tasks`, request);
  }

  updateTask(taskId: Guid, request: SaveTaskRequest): Observable<TaskDto> {
    return this.api.put<TaskDto>(`tasks/${enc(taskId)}`, request);
  }

  deleteTask(taskId: Guid): Observable<void> {
    return this.api.delete<void>(`tasks/${enc(taskId)}`);
  }

  // ───── Time entries ─────

  timeEntries(id: Guid): Observable<TimeEntryDto[]> {
    return this.api.get<TimeEntryDto[]>(`tickets/${enc(id)}/time-entries`);
  }

  logTime(id: Guid, request: LogTimeRequest): Observable<TimeEntryDto> {
    return this.api.post<TimeEntryDto>(`tickets/${enc(id)}/time-entries`, request);
  }

  deleteTimeEntry(entryId: Guid): Observable<void> {
    return this.api.delete<void>(`time-entries/${enc(entryId)}`);
  }

  // ───── Follow-ups ─────

  followUps(id: Guid): Observable<FollowUpDto[]> {
    return this.api.get<FollowUpDto[]>(`tickets/${enc(id)}/follow-ups`);
  }

  createFollowUp(id: Guid, request: CreateFollowUpRequest): Observable<FollowUpDto> {
    return this.api.post<FollowUpDto>(`tickets/${enc(id)}/follow-ups`, request);
  }

  deleteFollowUp(followUpId: Guid): Observable<void> {
    return this.api.delete<void>(`follow-ups/${enc(followUpId)}`);
  }

  private action(id: Guid, action: string, body: unknown): Observable<TicketDetailDto> {
    return this.api.post<TicketDetailDto>(`tickets/${enc(id)}/${action}`, body, { silent: true });
  }
}
