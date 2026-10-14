import { HttpEventType } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, filter, map } from 'rxjs';
import { toApiPath } from '../api/knowledge.api';
import { ApiClient } from '../http/api-client';
import { Guid, PagedResult, UploadedFileDto } from '../models/common';
import { UploadEvent } from '../models/files';
import {
  ChangePortalPasswordRequest,
  PortalAccountOverviewDto,
  PortalCreateTicketRequest,
  PortalMessageDto,
  PortalPreferencesDto,
  PortalProfileDto,
  PortalReplyRequest,
  PortalTicketCreatedDto,
  PortalTicketDetailDto,
  PortalTicketFormDto,
  PortalTicketListItemDto,
  PortalTicketQuery,
  PortalTicketScopeDto,
  PortalTicketStatsDto,
  UpdatePortalPreferencesRequest,
  UpdatePortalProfileRequest,
} from '../models/portal-content';

const enc = encodeURIComponent;

/**
 * Signed-in customer endpoints (`/api/v1/portal/{brand}/tickets…`, `/files`, `/account…`). Ticket access
 * (own / location / organisation) is enforced server-side; out-of-scope tickets return 404.
 */
@Injectable({ providedIn: 'root' })
export class PortalTicketsApi {
  private readonly api = inject(ApiClient);

  scope(brand: string): Observable<PortalTicketScopeDto> {
    return this.api.get<PortalTicketScopeDto>(`${base(brand)}/tickets/scope`);
  }

  stats(brand: string): Observable<PortalTicketStatsDto> {
    return this.api.get<PortalTicketStatsDto>(`${base(brand)}/tickets/stats`);
  }

  list(brand: string, query: PortalTicketQuery = {}): Observable<PagedResult<PortalTicketListItemDto>> {
    return this.api.get<PagedResult<PortalTicketListItemDto>>(`${base(brand)}/tickets`, { params: query });
  }

  form(brand: string): Observable<PortalTicketFormDto> {
    return this.api.get<PortalTicketFormDto>(`${base(brand)}/tickets/form`);
  }

  create(brand: string, body: PortalCreateTicketRequest): Observable<PortalTicketCreatedDto> {
    return this.api.post<PortalTicketCreatedDto>(`${base(brand)}/tickets`, body, { silent: true });
  }

  get(brand: string, id: Guid): Observable<PortalTicketDetailDto> {
    return this.api.get<PortalTicketDetailDto>(`${base(brand)}/tickets/${enc(id)}`, { silent: true });
  }

  messages(brand: string, id: Guid): Observable<PortalMessageDto[]> {
    return this.api.get<PortalMessageDto[]>(`${base(brand)}/tickets/${enc(id)}/messages`);
  }

  reply(brand: string, id: Guid, body: PortalReplyRequest): Observable<PortalMessageDto> {
    return this.api.post<PortalMessageDto>(`${base(brand)}/tickets/${enc(id)}/replies`, body, { silent: true });
  }

  reopen(brand: string, id: Guid, reason: string): Observable<PortalTicketDetailDto> {
    return this.api.post<PortalTicketDetailDto>(`${base(brand)}/tickets/${enc(id)}/reopen`, { reason }, { silent: true });
  }

  /** resolved = true closes the ticket; false reopens it (the comment becomes a message). */
  confirm(brand: string, id: Guid, resolved: boolean, comment?: string | null): Observable<PortalTicketDetailDto> {
    return this.api.post<PortalTicketDetailDto>(`${base(brand)}/tickets/${enc(id)}/confirm`, { resolved, comment: comment ?? null }, { silent: true });
  }

  rate(brand: string, id: Guid, rating: number, comment?: string | null): Observable<PortalTicketDetailDto> {
    return this.api.post<PortalTicketDetailDto>(`${base(brand)}/tickets/${enc(id)}/satisfaction`, { rating, comment: comment ?? null }, { silent: true });
  }

  /** Downloads an attachment URL returned by the API (`/api/v1/portal/...`). */
  download(url: string): Observable<Blob> {
    return this.api.blob(toApiPath(url));
  }

  /** Uploads an attachment as the signed-in contact; emits progress then `done` with the file id. */
  upload(brand: string, file: File): Observable<UploadEvent> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.api.upload<UploadedFileDto>(`${base(brand)}/files`, form, { silent: true }).pipe(
      map((event): UploadEvent | null => {
        if (event.type === HttpEventType.UploadProgress) {
          const total = event.total ?? null;
          return { kind: 'progress', loaded: event.loaded, total, percent: total ? Math.round((event.loaded / total) * 100) : null };
        }
        if (event.type === HttpEventType.Response && event.body) return { kind: 'done', file: event.body };
        return null;
      }),
      filter((e): e is UploadEvent => e !== null),
    );
  }

  // ───── Account ─────

  profile(brand: string): Observable<PortalProfileDto> {
    return this.api.get<PortalProfileDto>(`${base(brand)}/account/profile`);
  }

  updateProfile(brand: string, body: UpdatePortalProfileRequest): Observable<PortalProfileDto> {
    return this.api.put<PortalProfileDto>(`${base(brand)}/account/profile`, body, { silent: true });
  }

  preferences(brand: string): Observable<PortalPreferencesDto> {
    return this.api.get<PortalPreferencesDto>(`${base(brand)}/account/preferences`);
  }

  updatePreferences(brand: string, body: UpdatePortalPreferencesRequest): Observable<PortalPreferencesDto> {
    return this.api.put<PortalPreferencesDto>(`${base(brand)}/account/preferences`, body, { silent: true });
  }

  changePassword(brand: string, body: ChangePortalPasswordRequest): Observable<void> {
    return this.api.post<void>(`${base(brand)}/account/password`, body, { silent: true });
  }

  /** Contracts, licences, installations and scheduled service (contacts with location/organisation access). */
  overview(brand: string): Observable<PortalAccountOverviewDto> {
    return this.api.get<PortalAccountOverviewDto>(`${base(brand)}/account/overview`, { silent: true });
  }
}

function base(brand: string): string {
  return `portal/${enc(brand.toLowerCase())}`;
}
