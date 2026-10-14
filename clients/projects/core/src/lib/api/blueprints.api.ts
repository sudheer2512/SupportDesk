import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import {
  BlueprintDefinition,
  BlueprintDetailDto,
  BlueprintSummaryDto,
  ExecuteTransitionRequest,
  RuleVersionDto,
  SaveBlueprintRequest,
  TicketBlueprintDto,
  TransitionResultDto,
  ValidationResultDto,
} from '../models/automation';
import { Guid } from '../models/common';

const id = encodeURIComponent;

/**
 * Blueprints (state-transition workflows): administration under `/automation/blueprints` (`admin.automation`)
 * and the per-ticket view/transitions under `/tickets/{id}/blueprint`.
 */
@Injectable({ providedIn: 'root' })
export class BlueprintsApi {
  private readonly api = inject(ApiClient);

  list(): Observable<BlueprintSummaryDto[]> {
    return this.api.get<BlueprintSummaryDto[]>('automation/blueprints');
  }

  get(blueprintId: Guid): Observable<BlueprintDetailDto> {
    return this.api.get<BlueprintDetailDto>(`automation/blueprints/${id(blueprintId)}`);
  }

  version(blueprintId: Guid, versionId: Guid): Observable<RuleVersionDto> {
    return this.api.get<RuleVersionDto>(`automation/blueprints/${id(blueprintId)}/versions/${id(versionId)}`);
  }

  create(request: SaveBlueprintRequest): Observable<BlueprintDetailDto> {
    return this.api.post<BlueprintDetailDto>('automation/blueprints', request, { silent: true });
  }

  update(blueprintId: Guid, request: SaveBlueprintRequest): Observable<BlueprintDetailDto> {
    return this.api.put<BlueprintDetailDto>(`automation/blueprints/${id(blueprintId)}`, request, { silent: true });
  }

  delete(blueprintId: Guid): Observable<void> {
    return this.api.delete<void>(`automation/blueprints/${id(blueprintId)}`, { silent: true });
  }

  publish(blueprintId: Guid, versionId: Guid, changeNote?: string | null): Observable<BlueprintDetailDto> {
    return this.api.post<BlueprintDetailDto>(`automation/blueprints/${id(blueprintId)}/versions/${id(versionId)}/publish`, { changeNote: changeNote ?? null }, { silent: true });
  }

  setActive(blueprintId: Guid, active: boolean): Observable<BlueprintDetailDto> {
    return this.api.post<BlueprintDetailDto>(`automation/blueprints/${id(blueprintId)}/${active ? 'activate' : 'deactivate'}`, {}, { silent: true });
  }

  validate(definition: BlueprintDefinition): Observable<ValidationResultDto> {
    return this.api.post<ValidationResultDto>('automation/blueprints/validate', { definition }, { silent: true });
  }

  // ───── Ticket ─────

  /** The ticket's blueprint position and the transitions the caller may run; null when it follows none (204). */
  forTicket(ticketId: Guid): Observable<TicketBlueprintDto | null> {
    return this.api.get<TicketBlueprintDto | null>(`tickets/${id(ticketId)}/blueprint`);
  }

  /** Runs a transition: `status` "completed", or "pending_approval" (HTTP 202) with the approval id. */
  transition(ticketId: Guid, key: string, request: ExecuteTransitionRequest): Observable<TransitionResultDto> {
    return this.api.post<TransitionResultDto>(`tickets/${id(ticketId)}/blueprint/transitions/${id(key)}`, request, { silent: true });
  }

  /** Takes the ticket off its blueprint (`admin.automation`). */
  removeFromTicket(ticketId: Guid): Observable<void> {
    return this.api.delete<void>(`tickets/${id(ticketId)}/blueprint`);
  }
}
