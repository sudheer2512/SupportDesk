import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { Guid, PagedResult } from '../models/common';
import {
  AuditChainVerificationDto,
  AuditEntryDetailDto,
  AuditEntryDto,
  AuditFacetsDto,
  AuditQuery,
  CreateDataRequestRequest,
  DataRequestDto,
  DownloadLinkDto,
  LegalHoldDto,
  RetentionDataTypeDto,
  RetentionDryRunDto,
  RetentionPolicyDto,
  RetentionRunResultDto,
  SaveLegalHoldRequest,
  SaveRetentionPolicyRequest,
} from '../models/compliance';
import { DecideSupportAccessRequest, SupportAccessDto } from '../models/platform';

const id = encodeURIComponent;

/** Tenant audit viewer (`/admin/audit`, `admin.audit`): search, detail with diff, CSV export, hash-chain verification. */
@Injectable({ providedIn: 'root' })
export class AuditApi {
  private readonly api = inject(ApiClient);

  search(query: AuditQuery = {}): Observable<PagedResult<AuditEntryDto>> {
    return this.api.get<PagedResult<AuditEntryDto>>('admin/audit', { params: query });
  }

  facets(): Observable<AuditFacetsDto> {
    return this.api.get<AuditFacetsDto>('admin/audit/facets');
  }

  get(entryId: Guid): Observable<AuditEntryDetailDto> {
    return this.api.get<AuditEntryDetailDto>(`admin/audit/${id(entryId)}`);
  }

  /** CSV of the filtered entries (requires the audit_export plan feature). */
  exportCsv(query: AuditQuery = {}): Observable<Blob> {
    const { cursor: _cursor, limit: _limit, ...filters } = query;
    return this.api.blob('admin/audit/export', { params: filters, silent: true });
  }

  verify(): Observable<AuditChainVerificationDto> {
    return this.api.post<AuditChainVerificationDto>('admin/audit/verify', {});
  }
}

/** Compliance (`/admin/compliance`, `admin.compliance`): retention policies, legal holds and data-subject requests. */
@Injectable({ providedIn: 'root' })
export class ComplianceApi {
  private readonly api = inject(ApiClient);

  dataTypes(): Observable<RetentionDataTypeDto[]> {
    return this.api.get<RetentionDataTypeDto[]>('admin/compliance/retention/data-types');
  }

  policies(): Observable<RetentionPolicyDto[]> {
    return this.api.get<RetentionPolicyDto[]>('admin/compliance/retention');
  }

  createPolicy(request: SaveRetentionPolicyRequest): Observable<RetentionPolicyDto> {
    return this.api.post<RetentionPolicyDto>('admin/compliance/retention', request, { silent: true });
  }

  updatePolicy(policyId: Guid, request: SaveRetentionPolicyRequest): Observable<RetentionPolicyDto> {
    return this.api.put<RetentionPolicyDto>(`admin/compliance/retention/${id(policyId)}`, request, { silent: true });
  }

  deletePolicy(policyId: Guid): Observable<void> {
    return this.api.delete<void>(`admin/compliance/retention/${id(policyId)}`);
  }

  dryRunProposed(request: SaveRetentionPolicyRequest): Observable<RetentionDryRunDto> {
    return this.api.post<RetentionDryRunDto>('admin/compliance/retention/dry-run', request, { silent: true });
  }

  dryRun(policyId: Guid): Observable<RetentionDryRunDto> {
    return this.api.post<RetentionDryRunDto>(`admin/compliance/retention/${id(policyId)}/dry-run`, {});
  }

  run(policyId: Guid): Observable<RetentionRunResultDto> {
    return this.api.post<RetentionRunResultDto>(`admin/compliance/retention/${id(policyId)}/run`, {});
  }

  holds(includeReleased = false): Observable<LegalHoldDto[]> {
    return this.api.get<LegalHoldDto[]>('admin/compliance/legal-holds', { params: { includeReleased } });
  }

  createHold(request: SaveLegalHoldRequest): Observable<LegalHoldDto> {
    return this.api.post<LegalHoldDto>('admin/compliance/legal-holds', request, { silent: true });
  }

  releaseHold(holdId: Guid): Observable<LegalHoldDto> {
    return this.api.post<LegalHoldDto>(`admin/compliance/legal-holds/${id(holdId)}/release`, {});
  }

  requests(status: string | null = null): Observable<DataRequestDto[]> {
    return this.api.get<DataRequestDto[]>('admin/compliance/requests', { params: { status } });
  }

  request(requestId: Guid): Observable<DataRequestDto> {
    return this.api.get<DataRequestDto>(`admin/compliance/requests/${id(requestId)}`);
  }

  createRequest(request: CreateDataRequestRequest): Observable<DataRequestDto> {
    return this.api.post<DataRequestDto>('admin/compliance/requests', request, { silent: true });
  }

  approve(requestId: Guid): Observable<DataRequestDto> {
    return this.api.post<DataRequestDto>(`admin/compliance/requests/${id(requestId)}/approve`, {}, { silent: true });
  }

  reject(requestId: Guid, reason: string): Observable<DataRequestDto> {
    return this.api.post<DataRequestDto>(`admin/compliance/requests/${id(requestId)}/reject`, { reason }, { silent: true });
  }

  /** A one-time, expiring link (relative URL under the API base) — open it to download the zip. */
  downloadLink(requestId: Guid): Observable<DownloadLinkDto> {
    return this.api.post<DownloadLinkDto>(`admin/compliance/requests/${id(requestId)}/download-link`, {});
  }
}

/** Platform support-access requests, tenant side (approve / reject / revoke). No impersonation exists. */
@Injectable({ providedIn: 'root' })
export class SupportAccessApi {
  private readonly api = inject(ApiClient);

  list(): Observable<SupportAccessDto[]> {
    return this.api.get<SupportAccessDto[]>('admin/support-access');
  }

  decide(grantId: Guid, request: DecideSupportAccessRequest): Observable<SupportAccessDto> {
    return this.api.post<SupportAccessDto>(`admin/support-access/${id(grantId)}/decision`, request);
  }

  revoke(grantId: Guid): Observable<SupportAccessDto> {
    return this.api.post<SupportAccessDto>(`admin/support-access/${id(grantId)}/revoke`, {});
  }
}

