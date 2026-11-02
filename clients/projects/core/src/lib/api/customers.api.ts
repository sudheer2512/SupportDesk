import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { Guid, PagedResult } from '../models/common';
import {
  ContactConsentDto,
  ContactDetailDto,
  ContactIdentityDto,
  ContactListItemDto,
  ContactMergeLogDto,
  ContactMergeResultDto,
  ContactQuery,
  Customer360Dto,
  CustomerDepartmentDto,
  CustomerDetailDto,
  CustomerFacetsDto,
  CustomerGroupDto,
  CustomerListItemDto,
  CustomerMergeLogDto,
  CustomerMergeResultDto,
  CustomerQuery,
  DuplicateContactCandidateDto,
  DuplicatePairDto,
  LocationDto,
  MergeContactsRequest,
  MergeCustomersRequest,
  PortalAccessScope,
  PortalInviteResultDto,
  SaveContactRequest,
  SaveCustomerGroupRequest,
  SaveCustomerRequest,
  SaveLocationRequest,
  SaveServiceActivityRequest,
  ServiceActivityDto,
  ServiceActivityQuery,
} from '../models/customers';
import { ErpCustomerRecordsDto, ErpSystem } from '../models/erp';

const enc = encodeURIComponent;

/**
 * Customers module client (`/customers…`, `/customer-groups`): keyset-paged search, detail, Customer 360,
 * hierarchy, locations, customer departments and the customer's ERP records. Validation errors come back as
 * 400 `validation_failed` (field map), stale edits as 409 `concurrency_conflict`.
 */
@Injectable({ providedIn: 'root' })
export class CustomersApi {
  private readonly api = inject(ApiClient);

  list(query: CustomerQuery = {}, options: { silent?: boolean } = {}): Observable<PagedResult<CustomerListItemDto>> {
    return this.api.get<PagedResult<CustomerListItemDto>>('customers', { params: query, silent: options.silent });
  }

  facets(): Observable<CustomerFacetsDto> {
    return this.api.get<CustomerFacetsDto>('customers/facets');
  }

  get(id: Guid): Observable<CustomerDetailDto> {
    return this.api.get<CustomerDetailDto>(`customers/${enc(id)}`);
  }

  get360(id: Guid): Observable<Customer360Dto> {
    return this.api.get<Customer360Dto>(`customers/${enc(id)}/360`);
  }

  erpRecords(id: Guid, system: ErpSystem = 'WingsErp'): Observable<ErpCustomerRecordsDto> {
    return this.api.get<ErpCustomerRecordsDto>(`customers/${enc(id)}/erp`, { params: { system } });
  }

  create(request: SaveCustomerRequest): Observable<CustomerDetailDto> {
    return this.api.post<CustomerDetailDto>('customers', request, { silent: true });
  }

  update(id: Guid, request: SaveCustomerRequest): Observable<CustomerDetailDto> {
    return this.api.put<CustomerDetailDto>(`customers/${enc(id)}`, request, { silent: true });
  }

  delete(id: Guid): Observable<void> {
    return this.api.delete<void>(`customers/${enc(id)}`);
  }

  restore(id: Guid): Observable<CustomerDetailDto> {
    return this.api.post<CustomerDetailDto>(`customers/${enc(id)}/restore`);
  }

  contactDuplicates(id: Guid): Observable<DuplicatePairDto[]> {
    return this.api.get<DuplicatePairDto[]>(`customers/${enc(id)}/contact-duplicates`);
  }

  /** Organisation merge (customers.merge): moves everything of `mergeId` to `keepId`; idempotent; audited. */
  merge(request: MergeCustomersRequest): Observable<CustomerMergeResultDto> {
    return this.api.post<CustomerMergeResultDto>('customers/merge', request, { silent: true });
  }

  /** Merges involving the customer: as survivor, or — for a merged customer — the merge that folded it away. */
  mergeHistory(id: Guid, options: { silent?: boolean } = {}): Observable<CustomerMergeLogDto[]> {
    return this.api.get<CustomerMergeLogDto[]>(`customers/${enc(id)}/merge-history`, options);
  }

  // ───── Locations & departments ─────

  locations(id: Guid): Observable<LocationDto[]> {
    return this.api.get<LocationDto[]>(`customers/${enc(id)}/locations`);
  }

  saveLocation(customerId: Guid, locationId: Guid | null, request: SaveLocationRequest): Observable<LocationDto> {
    return locationId
      ? this.api.put<LocationDto>(`customers/${enc(customerId)}/locations/${enc(locationId)}`, request, { silent: true })
      : this.api.post<LocationDto>(`customers/${enc(customerId)}/locations`, request, { silent: true });
  }

  deleteLocation(customerId: Guid, locationId: Guid): Observable<void> {
    return this.api.delete<void>(`customers/${enc(customerId)}/locations/${enc(locationId)}`);
  }

  departments(id: Guid): Observable<CustomerDepartmentDto[]> {
    return this.api.get<CustomerDepartmentDto[]>(`customers/${enc(id)}/departments`);
  }

  saveDepartment(customerId: Guid, departmentId: Guid | null, name: string): Observable<CustomerDepartmentDto> {
    return departmentId
      ? this.api.put<CustomerDepartmentDto>(`customers/${enc(customerId)}/departments/${enc(departmentId)}`, { name }, { silent: true })
      : this.api.post<CustomerDepartmentDto>(`customers/${enc(customerId)}/departments`, { name }, { silent: true });
  }

  deleteDepartment(customerId: Guid, departmentId: Guid): Observable<void> {
    return this.api.delete<void>(`customers/${enc(customerId)}/departments/${enc(departmentId)}`);
  }

  // ───── Groups ─────

  groups(): Observable<CustomerGroupDto[]> {
    return this.api.get<CustomerGroupDto[]>('customer-groups');
  }

  saveGroup(id: Guid | null, request: SaveCustomerGroupRequest): Observable<CustomerGroupDto> {
    return id
      ? this.api.put<CustomerGroupDto>(`customer-groups/${enc(id)}`, request, { silent: true })
      : this.api.post<CustomerGroupDto>('customer-groups', request, { silent: true });
  }

  deleteGroup(id: Guid): Observable<void> {
    return this.api.delete<void>(`customer-groups/${enc(id)}`);
  }
}

/** Contacts client (`/contacts…`): search, detail, identities, consent, portal access, duplicates and merge. */
@Injectable({ providedIn: 'root' })
export class ContactsApi {
  private readonly api = inject(ApiClient);

  list(query: ContactQuery = {}): Observable<PagedResult<ContactListItemDto>> {
    return this.api.get<PagedResult<ContactListItemDto>>('contacts', { params: query });
  }

  get(id: Guid): Observable<ContactDetailDto> {
    return this.api.get<ContactDetailDto>(`contacts/${enc(id)}`);
  }

  create(request: SaveContactRequest): Observable<ContactDetailDto> {
    return this.api.post<ContactDetailDto>('contacts', request, { silent: true });
  }

  update(id: Guid, request: SaveContactRequest): Observable<ContactDetailDto> {
    return this.api.put<ContactDetailDto>(`contacts/${enc(id)}`, request, { silent: true });
  }

  delete(id: Guid): Observable<void> {
    return this.api.delete<void>(`contacts/${enc(id)}`);
  }

  addIdentity(id: Guid, channel: string, identifier: string, verified = false): Observable<ContactIdentityDto> {
    return this.api.post<ContactIdentityDto>(`contacts/${enc(id)}/identities`, { channel, identifier, verified }, { silent: true });
  }

  removeIdentity(id: Guid, identityId: Guid): Observable<void> {
    return this.api.delete<void>(`contacts/${enc(id)}/identities/${enc(identityId)}`);
  }

  setConsent(id: Guid, channel: string, purpose: string, status: string, source?: string | null): Observable<ContactConsentDto> {
    return this.api.put<ContactConsentDto>(`contacts/${enc(id)}/consents`, { channel, purpose, status, source: source ?? null });
  }

  setPortalAccess(id: Guid, access: PortalAccessScope): Observable<ContactDetailDto> {
    return this.api.put<ContactDetailDto>(`contacts/${enc(id)}/portal-access`, { access }, { silent: true });
  }

  sendPortalInvite(id: Guid, mode: 'invite' | 'reset', brandId?: Guid | null): Observable<PortalInviteResultDto> {
    return this.api.post<PortalInviteResultDto>(`contacts/${enc(id)}/portal-invite`, { mode, brandId: brandId ?? null }, { silent: true });
  }

  duplicatesOf(id: Guid, minScore?: number): Observable<DuplicateContactCandidateDto[]> {
    return this.api.get<DuplicateContactCandidateDto[]>(`contacts/${enc(id)}/duplicates`, { params: { minScore } });
  }

  duplicates(customerId?: Guid | null, minScore?: number, limit = 50): Observable<DuplicatePairDto[]> {
    return this.api.get<DuplicatePairDto[]>('contacts/duplicates', { params: { customerId, minScore, limit } });
  }

  merge(request: MergeContactsRequest): Observable<ContactMergeResultDto> {
    return this.api.post<ContactMergeResultDto>('contacts/merge', request, { silent: true });
  }

  mergeHistory(id: Guid): Observable<ContactMergeLogDto[]> {
    return this.api.get<ContactMergeLogDto[]>(`contacts/${enc(id)}/merge-history`);
  }
}

/** Service visits and scheduled activities (`/service-activities`), queried by date range for calendars. */
@Injectable({ providedIn: 'root' })
export class ServiceActivitiesApi {
  private readonly api = inject(ApiClient);

  list(query: ServiceActivityQuery = {}): Observable<ServiceActivityDto[]> {
    return this.api.get<ServiceActivityDto[]>('service-activities', { params: query });
  }

  save(id: Guid | null, request: SaveServiceActivityRequest): Observable<ServiceActivityDto> {
    return id
      ? this.api.put<ServiceActivityDto>(`service-activities/${enc(id)}`, request, { silent: true })
      : this.api.post<ServiceActivityDto>('service-activities', request, { silent: true });
  }

  delete(id: Guid): Observable<void> {
    return this.api.delete<void>(`service-activities/${enc(id)}`);
  }
}
