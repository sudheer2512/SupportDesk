import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { GlobalSearchResultDto, SearchEntityType, SearchIndexStatusDto } from '../models/knowledge';

/**
 * Global search for agents (`GET /api/v1/search?q=&types=`): tickets (within the agent's data scope),
 * articles, contacts, customers and community topics, BM25-ranked with highlighted snippets.
 */
@Injectable({ providedIn: 'root' })
export class SearchApi {
  private readonly api = inject(ApiClient);

  search(q: string, types?: SearchEntityType[] | null, limit = 20): Observable<GlobalSearchResultDto> {
    return this.api.get<GlobalSearchResultDto>('search', { params: { q, types: types ?? null, limit }, silent: true });
  }

  status(): Observable<SearchIndexStatusDto> {
    return this.api.get<SearchIndexStatusDto>('search/status');
  }

  /** Rebuilds the tenant's search index in the background (admin). */
  reindex(): Observable<void> {
    return this.api.post<void>('search/reindex', {});
  }
}
