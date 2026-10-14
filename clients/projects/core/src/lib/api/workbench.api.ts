import { Injectable, inject } from '@angular/core';
import { Observable, map, shareReplay } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { Guid, ProblemDetails } from '../models/common';
import { TicketDetailDto, TransferTicketRequest } from '../models/tickets';
import {
  ConditionFieldCatalogDto,
  ContactLookupDto,
  CustomerLookupDto,
  QuickSearchResultDto,
  InstallationLookupDto,
  QueueCountsDto,
  RenderedTemplateDto,
  ReplyTemplateDto,
  TransferResult,
} from '../models/workbench';

const enc = encodeURIComponent;

/**
 * Agent-workbench extras next to TicketsApi: live queue counts, the condition-field catalog for views,
 * reply templates (with server-side placeholder rendering), approval-aware transfer, delete/restore and the
 * look-ups used by the ticket form and global search (`/search…`).
 */
@Injectable({ providedIn: 'root' })
export class WorkbenchApi {
  private readonly api = inject(ApiClient);
  private catalog$: Observable<ConditionFieldCatalogDto> | null = null;

  /** GET /tickets/queue-counts — mine, unassigned, open, overdue, atRisk, escalated, watching, waiting. */
  queueCounts(): Observable<QueueCountsDto> {
    return this.api.get<QueueCountsDto>('tickets/queue-counts', { silent: true });
  }

  /** GET /meta/condition-fields (cached for the session). */
  conditionFields(): Observable<ConditionFieldCatalogDto> {
    this.catalog$ ??= this.api.get<ConditionFieldCatalogDto>('meta/condition-fields').pipe(shareReplay({ bufferSize: 1, refCount: false }));
    return this.catalog$;
  }

  /** Reply templates visible to the caller (personal, team, department, global). */
  templates(): Observable<ReplyTemplateDto[]> {
    return this.api.get<ReplyTemplateDto[]>('templates');
  }

  /** Renders a template's placeholders against a ticket. */
  renderTemplate(ticketId: Guid, templateId: Guid): Observable<RenderedTemplateDto> {
    return this.api.post<RenderedTemplateDto>(`tickets/${enc(ticketId)}/render-template/${enc(templateId)}`, {}, { silent: true });
  }

  /**
   * POST /tickets/{id}/transfer. A department that requires approval answers 202 with problem details
   * (`code: approval_required`, `approvalId`) instead of the ticket — surfaced here as a typed result.
   */
  transfer(id: Guid, request: TransferTicketRequest): Observable<TransferResult> {
    return this.api.post<TicketDetailDto | ProblemDetails>(`tickets/${enc(id)}/transfer`, request, { silent: true }).pipe(
      map((body): TransferResult => {
        const problem = body as ProblemDetails;
        if (problem && problem.code === 'approval_required') {
          return {
            kind: 'approval_required',
            approvalId: (problem['approvalId'] as string | undefined) ?? null,
            message: problem.title ?? 'An approval request was sent.',
          };
        }
        return { kind: 'transferred', ticket: body as TicketDetailDto };
      }),
    );
  }

  /** Soft-deletes a ticket (or marks it as spam). */
  deleteTicket(id: Guid, spam = false): Observable<void> {
    return this.api.delete<void>(`tickets/${enc(id)}`, { params: { spam }, silent: true });
  }

  restoreTicket(id: Guid): Observable<void> {
    return this.api.post<void>(`tickets/${enc(id)}/restore`, {}, { silent: true });
  }

  // ───── Look-ups ─────

  search(q: string, limit = 6): Observable<QuickSearchResultDto> {
    return this.api.get<QuickSearchResultDto>('search/quick', { params: { q, limit }, silent: true });
  }

  customers(q: string, limit = 20): Observable<CustomerLookupDto[]> {
    return this.api.get<CustomerLookupDto[]>('search/customers', { params: { q, limit }, silent: true });
  }

  contacts(q: string, customerId: Guid | null = null, limit = 20): Observable<ContactLookupDto[]> {
    return this.api.get<ContactLookupDto[]>('search/contacts', { params: { q, customerId, limit }, silent: true });
  }

  installations(customerId: Guid, productId: Guid | null = null): Observable<InstallationLookupDto[]> {
    return this.api.get<InstallationLookupDto[]>('search/installations', { params: { customerId, productId }, silent: true });
  }
}
