import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { IsoDateTime } from '../models/common';

/** A satisfaction survey opened from the e-mailed link (GET/POST /public/csat/{token}). `status`: open | answered | expired. */
export interface CsatSurveyDto {
  status: 'open' | 'answered' | 'expired' | string;
  ticketNumber: string;
  subject: string;
  agentName: string | null;
  brandName: string | null;
  primaryColor: string | null;
  rating: number | null;
  comment: string | null;
  expiresAt: IsoDateTime;
}

export interface CsatSubmitRequest {
  rating: number;
  comment?: string | null;
}

/** Anonymous satisfaction survey: the token in the e-mail link is the only credential (hashed server-side, expires). */
@Injectable({ providedIn: 'root' })
export class CsatApi {
  private readonly api = inject(ApiClient);

  survey(token: string): Observable<CsatSurveyDto> {
    return this.api.get<CsatSurveyDto>(`public/csat/${encodeURIComponent(token)}`, { silent: true });
  }

  submit(token: string, request: CsatSubmitRequest): Observable<CsatSurveyDto> {
    return this.api.post<CsatSurveyDto>(`public/csat/${encodeURIComponent(token)}`, request, { silent: true });
  }
}
