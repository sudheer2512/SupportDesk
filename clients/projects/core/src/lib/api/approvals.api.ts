import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { Guid } from '../models/common';
import { ApprovalDto, CreateApprovalRequest, DecideApprovalRequest } from '../models/approvals';

/**
 * Approval requests (`/approvals`): department transfers, blueprint transitions, AI actions, custom requests.
 * Deciding needs `approvals.decide` (or being the named approver); requesters cannot decide their own.
 */
@Injectable({ providedIn: 'root' })
export class ApprovalsApi {
  private readonly api = inject(ApiClient);

  /** `status` e.g. "Pending"; `mine` = requests I raised. Without approvals.decide only my own / addressed to me. */
  list(status: string | null = null, mine = false): Observable<ApprovalDto[]> {
    return this.api.get<ApprovalDto[]>('approvals', { params: { status, mine: mine || null } });
  }

  create(request: CreateApprovalRequest): Observable<{ id: Guid }> {
    return this.api.post<{ id: Guid }>('approvals', request, { silent: true });
  }

  decide(id: Guid, request: DecideApprovalRequest): Observable<ApprovalDto> {
    return this.api.post<ApprovalDto>(`approvals/${encodeURIComponent(id)}/decide`, request, { silent: true });
  }
}
