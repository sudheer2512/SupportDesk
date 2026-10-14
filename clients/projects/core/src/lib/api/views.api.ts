import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { Guid } from '../models/common';
import { SaveViewRequest, SavedViewDto } from '../models/tickets';

/** Saved views (`/views`): personal, team and shared filters with column/sort preferences. */
@Injectable({ providedIn: 'root' })
export class ViewsApi {
  private readonly api = inject(ApiClient);

  /** Views for a scope (e.g. "tickets"); `counts` asks the server for each view's matching-ticket count. */
  list(scope = 'tickets', counts = false): Observable<SavedViewDto[]> {
    return this.api.get<SavedViewDto[]>('views', { params: { scope, counts: counts || null } });
  }

  get(id: Guid): Observable<SavedViewDto> {
    return this.api.get<SavedViewDto>(`views/${encodeURIComponent(id)}`);
  }

  create(request: SaveViewRequest): Observable<SavedViewDto> {
    return this.api.post<SavedViewDto>('views', request, { silent: true });
  }

  update(id: Guid, request: SaveViewRequest): Observable<SavedViewDto> {
    return this.api.put<SavedViewDto>(`views/${encodeURIComponent(id)}`, request, { silent: true });
  }

  delete(id: Guid): Observable<void> {
    return this.api.delete<void>(`views/${encodeURIComponent(id)}`);
  }
}
