import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { Guid, PagedResult } from '../models/common';
import {
  ActiveTrackerDto,
  CollaborationCatalogDto,
  CollaborationChannelDto,
  CollaborationLogDto,
  CollaborationSendResultDto,
  CollaborationShareTargetDto,
  EngineeringAffectedTicketsDto,
  EngineeringAssigneeDto,
  EngineeringBoardDto,
  EngineeringDeliveryDto,
  EngineeringLinkViewDto,
  EngineeringReleaseDto,
  EngineeringSettingsDto,
  EngineeringTrackerDto,
  EngineeringWorkItemDetailDto,
  EngineeringWorkItemDto,
  EngineeringWorkItemQuery,
  EscalateToEngineeringRequest,
  EscalationPrefillDto,
  EscalationResultDto,
  LinkWorkItemRequest,
  MoveEngineeringWorkItemRequest,
  PublishEngineeringReleaseRequest,
  SaveCollaborationChannelRequest,
  SaveEngineeringReleaseRequest,
  SaveEngineeringSettingsRequest,
  SaveEngineeringTrackerRequest,
  SaveEngineeringWorkItemRequest,
  ShareTicketToChannelRequest,
  TicketEngineeringDto,
  TrackerProjectDto,
  TrackerSyncResultDto,
  TrackerTestResultDto,
} from '../models/engineering';
import { IntegrationLogDto } from '../models/erp';

const id = encodeURIComponent;

/**
 * Engineering escalation (`/engineering`): the ticket panel (escalate, link, unlink, refresh, other tickets affected —
 * `engineering.link` / `tickets.view`) and the built-in engineering board (`engineering.view` / `engineering.manage`).
 */
@Injectable({ providedIn: 'root' })
export class EngineeringApi {
  private readonly api = inject(ApiClient);

  activeTrackers(): Observable<ActiveTrackerDto[]> {
    return this.api.get<ActiveTrackerDto[]>('engineering/trackers/active');
  }

  /** Links of a ticket; `refresh` re-reads stale items from their trackers first. */
  ticket(ticketId: Guid, refresh = false): Observable<TicketEngineeringDto> {
    return this.api.get<TicketEngineeringDto>(`engineering/tickets/${id(ticketId)}`, { params: { refresh }, silent: true });
  }

  prefill(ticketId: Guid): Observable<EscalationPrefillDto> {
    return this.api.get<EscalationPrefillDto>(`engineering/tickets/${id(ticketId)}/prefill`, { silent: true });
  }

  escalate(ticketId: Guid, request: EscalateToEngineeringRequest): Observable<EscalationResultDto> {
    return this.api.post<EscalationResultDto>(`engineering/tickets/${id(ticketId)}/escalate`, request, { silent: true });
  }

  link(ticketId: Guid, request: LinkWorkItemRequest): Observable<EscalationResultDto> {
    return this.api.post<EscalationResultDto>(`engineering/tickets/${id(ticketId)}/links`, request, { silent: true });
  }

  unlink(ticketId: Guid, linkId: Guid): Observable<void> {
    return this.api.delete<void>(`engineering/tickets/${id(ticketId)}/links/${id(linkId)}`, { silent: true });
  }

  refreshLink(ticketId: Guid, linkId: Guid): Observable<EngineeringLinkViewDto> {
    return this.api.post<EngineeringLinkViewDto>(`engineering/tickets/${id(ticketId)}/links/${id(linkId)}/refresh`, {}, { silent: true });
  }

  affectedTickets(ticketId: Guid, linkId: Guid): Observable<EngineeringAffectedTicketsDto> {
    return this.api.get<EngineeringAffectedTicketsDto>(`engineering/tickets/${id(ticketId)}/links/${id(linkId)}/tickets`, { silent: true });
  }

  // ───── Board ─────

  board(trackerId?: Guid | null): Observable<EngineeringBoardDto> {
    return this.api.get<EngineeringBoardDto>('engineering/board', { params: { trackerId }, silent: true });
  }

  assignees(): Observable<EngineeringAssigneeDto[]> {
    return this.api.get<EngineeringAssigneeDto[]>('engineering/assignees');
  }

  workItems(query: EngineeringWorkItemQuery = {}): Observable<PagedResult<EngineeringWorkItemDto>> {
    return this.api.get<PagedResult<EngineeringWorkItemDto>>('engineering/work-items', { params: query, silent: true });
  }

  workItem(workItemId: Guid): Observable<EngineeringWorkItemDetailDto> {
    return this.api.get<EngineeringWorkItemDetailDto>(`engineering/work-items/${id(workItemId)}`, { silent: true });
  }

  createWorkItem(request: SaveEngineeringWorkItemRequest): Observable<EngineeringWorkItemDto> {
    return this.api.post<EngineeringWorkItemDto>('engineering/work-items', request, { silent: true });
  }

  updateWorkItem(workItemId: Guid, request: SaveEngineeringWorkItemRequest): Observable<EngineeringWorkItemDto> {
    return this.api.put<EngineeringWorkItemDto>(`engineering/work-items/${id(workItemId)}`, request, { silent: true });
  }

  moveWorkItem(workItemId: Guid, request: MoveEngineeringWorkItemRequest): Observable<EngineeringWorkItemDto> {
    return this.api.post<EngineeringWorkItemDto>(`engineering/work-items/${id(workItemId)}/move`, request, { silent: true });
  }

  deleteWorkItem(workItemId: Guid): Observable<void> {
    return this.api.delete<void>(`engineering/work-items/${id(workItemId)}`, { silent: true });
  }

  releases(trackerId?: Guid | null): Observable<EngineeringReleaseDto[]> {
    return this.api.get<EngineeringReleaseDto[]>('engineering/releases', { params: { trackerId } });
  }

  createRelease(request: SaveEngineeringReleaseRequest): Observable<EngineeringReleaseDto> {
    return this.api.post<EngineeringReleaseDto>('engineering/releases', request, { silent: true });
  }

  updateRelease(releaseId: Guid, request: SaveEngineeringReleaseRequest): Observable<EngineeringReleaseDto> {
    return this.api.put<EngineeringReleaseDto>(`engineering/releases/${id(releaseId)}`, request, { silent: true });
  }

  /** Publishes (and optionally deploys) a release: linked tickets get notes and, when enabled, customer notifications. */
  publishRelease(releaseId: Guid, request: PublishEngineeringReleaseRequest): Observable<EngineeringReleaseDto> {
    return this.api.post<EngineeringReleaseDto>(`engineering/releases/${id(releaseId)}/publish`, request, { silent: true });
  }

  deleteRelease(releaseId: Guid): Observable<void> {
    return this.api.delete<void>(`engineering/releases/${id(releaseId)}`, { silent: true });
  }
}

/**
 * Engineering administration (`/engineering/admin`, `admin.integrations`): tracker connections (Azure DevOps, GitHub,
 * built-in), connection test and project discovery, sync now, webhook secret rotation (secret returned once), delivery
 * and call logs, and tenant-wide auto-notify settings.
 */
@Injectable({ providedIn: 'root' })
export class EngineeringAdminApi {
  private readonly api = inject(ApiClient);

  trackers(): Observable<EngineeringTrackerDto[]> {
    return this.api.get<EngineeringTrackerDto[]>('engineering/admin/trackers', { silent: true });
  }

  tracker(trackerId: Guid): Observable<EngineeringTrackerDto> {
    return this.api.get<EngineeringTrackerDto>(`engineering/admin/trackers/${id(trackerId)}`);
  }

  /** The response carries the inbound webhook secret once. */
  create(request: SaveEngineeringTrackerRequest): Observable<EngineeringTrackerDto> {
    return this.api.post<EngineeringTrackerDto>('engineering/admin/trackers', request, { silent: true });
  }

  update(trackerId: Guid, request: SaveEngineeringTrackerRequest): Observable<EngineeringTrackerDto> {
    return this.api.put<EngineeringTrackerDto>(`engineering/admin/trackers/${id(trackerId)}`, request, { silent: true });
  }

  delete(trackerId: Guid): Observable<void> {
    return this.api.delete<void>(`engineering/admin/trackers/${id(trackerId)}`, { silent: true });
  }

  test(trackerId: Guid): Observable<TrackerTestResultDto> {
    return this.api.post<TrackerTestResultDto>(`engineering/admin/trackers/${id(trackerId)}/test`, {}, { silent: true });
  }

  projects(trackerId: Guid): Observable<TrackerProjectDto[]> {
    return this.api.get<TrackerProjectDto[]>(`engineering/admin/trackers/${id(trackerId)}/projects`, { silent: true });
  }

  sync(trackerId: Guid): Observable<TrackerSyncResultDto> {
    return this.api.post<TrackerSyncResultDto>(`engineering/admin/trackers/${id(trackerId)}/sync`, {}, { silent: true });
  }

  /** New inbound webhook secret (returned once). */
  rotateWebhookSecret(trackerId: Guid): Observable<EngineeringTrackerDto> {
    return this.api.post<EngineeringTrackerDto>(`engineering/admin/trackers/${id(trackerId)}/rotate-webhook-secret`, {});
  }

  deliveries(trackerId: Guid, limit = 50): Observable<EngineeringDeliveryDto[]> {
    return this.api.get<EngineeringDeliveryDto[]>(`engineering/admin/trackers/${id(trackerId)}/deliveries`, { params: { limit }, silent: true });
  }

  logs(trackerId: Guid, limit = 100): Observable<IntegrationLogDto[]> {
    return this.api.get<IntegrationLogDto[]>(`engineering/admin/trackers/${id(trackerId)}/logs`, { params: { limit }, silent: true });
  }

  settings(): Observable<EngineeringSettingsDto> {
    return this.api.get<EngineeringSettingsDto>('engineering/admin/settings', { silent: true });
  }

  saveSettings(request: SaveEngineeringSettingsRequest): Observable<EngineeringSettingsDto> {
    return this.api.put<EngineeringSettingsDto>('engineering/admin/settings', request, { silent: true });
  }
}

/**
 * Microsoft Teams / Slack collaboration (`/collaboration`): incoming-webhook channels with event mappings and send test
 * (`admin.integrations`), and sharing a ticket to a channel (`tickets.note`).
 */
@Injectable({ providedIn: 'root' })
export class CollaborationApi {
  private readonly api = inject(ApiClient);

  catalog(): Observable<CollaborationCatalogDto> {
    return this.api.get<CollaborationCatalogDto>('collaboration/catalog', { silent: true });
  }

  channels(): Observable<CollaborationChannelDto[]> {
    return this.api.get<CollaborationChannelDto[]>('collaboration/channels', { silent: true });
  }

  create(request: SaveCollaborationChannelRequest): Observable<CollaborationChannelDto> {
    return this.api.post<CollaborationChannelDto>('collaboration/channels', request, { silent: true });
  }

  update(channelId: Guid, request: SaveCollaborationChannelRequest): Observable<CollaborationChannelDto> {
    return this.api.put<CollaborationChannelDto>(`collaboration/channels/${id(channelId)}`, request, { silent: true });
  }

  delete(channelId: Guid): Observable<void> {
    return this.api.delete<void>(`collaboration/channels/${id(channelId)}`, { silent: true });
  }

  /** Posts a sample card now and returns the provider's answer. */
  test(channelId: Guid): Observable<CollaborationSendResultDto> {
    return this.api.post<CollaborationSendResultDto>(`collaboration/channels/${id(channelId)}/test`, {}, { silent: true });
  }

  logs(channelId: Guid, limit = 50): Observable<CollaborationLogDto[]> {
    return this.api.get<CollaborationLogDto[]>(`collaboration/channels/${id(channelId)}/logs`, { params: { limit }, silent: true });
  }

  shareTargets(): Observable<CollaborationShareTargetDto[]> {
    return this.api.get<CollaborationShareTargetDto[]>('collaboration/share-targets', { silent: true });
  }

  share(ticketId: Guid, request: ShareTicketToChannelRequest): Observable<CollaborationSendResultDto> {
    return this.api.post<CollaborationSendResultDto>(`collaboration/tickets/${id(ticketId)}/share`, request, { silent: true });
  }
}
