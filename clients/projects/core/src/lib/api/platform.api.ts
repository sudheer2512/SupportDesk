import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';
import { ApiClient, ApiRequestOptions } from '../http/api-client';
import { ApiError } from '../http/api-error';
import { PlatformSession } from '../auth/platform-session';
import { Guid, PagedResult } from '../models/common';
import {
  CancelTenantRequest,
  DedicatedDatabaseResultDto,
  MarkInvoicePaidRequest,
  OperatorChangePlanRequest,
  PlatformAuditEntryDto,
  PlatformChainVerificationDto,
  PlatformOverviewDto,
  PlatformTenantDetailDto,
  PlatformTenantDto,
  RequestSupportAccessRequest,
  SavePlanRequest,
  SupportAccessDto,
  SupportDiagnosticsDto,
} from '../models/platform';
import { InvoiceDto, InvoiceSummaryDto, PlanDto, PlanFeatureDto } from '../models/subscriptions';

const id = encodeURIComponent;

/**
 * Platform operator console API (`/platform/*`). Calls carry the operator's platform token explicitly (never the agent
 * token); a 401 ends the platform session and returns to the platform sign-in page.
 */
@Injectable({ providedIn: 'root' })
export class PlatformApi {
  private readonly api = inject(ApiClient);
  private readonly session = inject(PlatformSession);

  private opts(options: ApiRequestOptions = {}): ApiRequestOptions {
    const token = this.session.accessToken();
    return { ...options, skipAuth: true, headers: token ? { Authorization: `Bearer ${token}` } : {} };
  }

  private guard<T>(source: Observable<T>): Observable<T> {
    return source.pipe(
      catchError((err: unknown) => {
        if (err instanceof ApiError && err.status === 401) this.session.handleExpired();
        return throwError(() => err);
      }),
    );
  }

  overview(): Observable<PlatformOverviewDto> {
    return this.guard(this.api.get<PlatformOverviewDto>('platform/overview', this.opts()));
  }

  tenants(query: { search?: string | null; status?: string | null; cursor?: string | null; limit?: number } = {}): Observable<PagedResult<PlatformTenantDto>> {
    return this.guard(this.api.get<PagedResult<PlatformTenantDto>>('platform/tenants', this.opts({ params: query })));
  }

  tenant(tenantId: Guid): Observable<PlatformTenantDetailDto> {
    return this.guard(this.api.get<PlatformTenantDetailDto>(`platform/tenants/${id(tenantId)}`, this.opts()));
  }

  suspend(tenantId: Guid, reason: string): Observable<PlatformTenantDetailDto> {
    return this.guard(this.api.post<PlatformTenantDetailDto>(`platform/tenants/${id(tenantId)}/suspend`, { reason }, this.opts({ silent: true })));
  }

  reactivate(tenantId: Guid, reason: string | null): Observable<PlatformTenantDetailDto> {
    return this.guard(this.api.post<PlatformTenantDetailDto>(`platform/tenants/${id(tenantId)}/reactivate`, { reason }, this.opts({ silent: true })));
  }

  cancel(tenantId: Guid, request: CancelTenantRequest): Observable<PlatformTenantDetailDto> {
    return this.guard(this.api.post<PlatformTenantDetailDto>(`platform/tenants/${id(tenantId)}/cancel`, request, this.opts({ silent: true })));
  }

  extendTrial(tenantId: Guid, days: number): Observable<PlatformTenantDetailDto> {
    return this.guard(this.api.post<PlatformTenantDetailDto>(`platform/tenants/${id(tenantId)}/extend-trial`, { days }, this.opts({ silent: true })));
  }

  changePlan(tenantId: Guid, request: OperatorChangePlanRequest): Observable<PlatformTenantDetailDto> {
    return this.guard(this.api.post<PlatformTenantDetailDto>(`platform/tenants/${id(tenantId)}/change-plan`, request, this.opts({ silent: true })));
  }

  provisionDedicated(tenantId: Guid, connectionName: string): Observable<DedicatedDatabaseResultDto> {
    return this.guard(
      this.api.post<DedicatedDatabaseResultDto>(`platform/tenants/${id(tenantId)}/dedicated-database`, { connectionName }, this.opts({ silent: true })),
    );
  }

  plans(): Observable<PlanDto[]> {
    return this.guard(this.api.get<PlanDto[]>('platform/plans', this.opts()));
  }

  planFeatures(): Observable<PlanFeatureDto[]> {
    return this.guard(this.api.get<PlanFeatureDto[]>('platform/plans/features', this.opts()));
  }

  createPlan(request: SavePlanRequest): Observable<PlanDto> {
    return this.guard(this.api.post<PlanDto>('platform/plans', request, this.opts({ silent: true })));
  }

  updatePlan(planId: Guid, request: SavePlanRequest): Observable<PlanDto> {
    return this.guard(this.api.put<PlanDto>(`platform/plans/${id(planId)}`, request, this.opts({ silent: true })));
  }

  deletePlan(planId: Guid): Observable<void> {
    return this.guard(this.api.delete<void>(`platform/plans/${id(planId)}`, this.opts({ silent: true })));
  }

  invoices(query: { status?: string | null; tenantId?: Guid | null; cursor?: string | null; limit?: number } = {}): Observable<PagedResult<InvoiceSummaryDto>> {
    return this.guard(this.api.get<PagedResult<InvoiceSummaryDto>>('platform/invoices', this.opts({ params: query })));
  }

  invoice(invoiceId: Guid): Observable<InvoiceDto> {
    return this.guard(this.api.get<InvoiceDto>(`platform/invoices/${id(invoiceId)}`, this.opts()));
  }

  invoicePdf(invoiceId: Guid): Observable<Blob> {
    return this.guard(this.api.blob(`platform/invoices/${id(invoiceId)}/pdf`, this.opts()));
  }

  markPaid(invoiceId: Guid, request: MarkInvoicePaidRequest): Observable<InvoiceDto> {
    return this.guard(this.api.post<InvoiceDto>(`platform/invoices/${id(invoiceId)}/mark-paid`, request, this.opts({ silent: true })));
  }

  voidInvoice(invoiceId: Guid, reason: string): Observable<InvoiceDto> {
    return this.guard(this.api.post<InvoiceDto>(`platform/invoices/${id(invoiceId)}/void`, { reason }, this.opts({ silent: true })));
  }

  audit(query: { search?: string | null; action?: string | null; tenantId?: Guid | null; cursor?: string | null; limit?: number } = {}): Observable<
    PagedResult<PlatformAuditEntryDto>
  > {
    return this.guard(this.api.get<PagedResult<PlatformAuditEntryDto>>('platform/audit', this.opts({ params: query })));
  }

  auditActions(): Observable<string[]> {
    return this.guard(this.api.get<string[]>('platform/audit/actions', this.opts()));
  }

  verifyAudit(): Observable<PlatformChainVerificationDto> {
    return this.guard(this.api.post<PlatformChainVerificationDto>('platform/audit/verify', {}, this.opts()));
  }

  supportAccess(tenantId: Guid | null = null): Observable<SupportAccessDto[]> {
    return this.guard(this.api.get<SupportAccessDto[]>('platform/support-access', this.opts({ params: { tenantId } })));
  }

  requestSupportAccess(tenantId: Guid, request: RequestSupportAccessRequest): Observable<SupportAccessDto> {
    return this.guard(this.api.post<SupportAccessDto>(`platform/support-access/tenants/${id(tenantId)}`, request, this.opts({ silent: true })));
  }

  diagnostics(grantId: Guid): Observable<SupportDiagnosticsDto> {
    return this.guard(this.api.get<SupportDiagnosticsDto>(`platform/support-access/${id(grantId)}/diagnostics`, this.opts({ silent: true })));
  }
}
