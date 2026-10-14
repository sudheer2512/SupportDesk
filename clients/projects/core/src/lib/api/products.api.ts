import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { Guid, PagedResult } from '../models/common';
import {
  AssetDto,
  AssetQuery,
  ContractDetailDto,
  ContractFormMetaDto,
  ContractListItemDto,
  ContractQuery,
  ContractRenewalDto,
  ConsumptionReportDto,
  EligibilityPreviewDto,
  EntitlementDto,
  InstallBaseDto,
  InstallationDetailDto,
  InstallationListItemDto,
  InstallationQuery,
  LicenceDto,
  LicenceQuery,
  ProductDto,
  ProductFamilyDto,
  ProductModuleDto,
  ProductReleaseDto,
  ProductVersionDto,
  SaveAssetRequest,
  SaveContractRequest,
  SaveEntitlementRequest,
  SaveInstallationRequest,
  SaveLicenceRequest,
  SaveProductModuleRequest,
  SaveProductReleaseRequest,
  SaveProductRequest,
  SaveProductVersionRequest,
} from '../models/products';

const enc = encodeURIComponent;

/** Product catalog (`/products…`, `/product-families`): products, modules, versions, releases and install-base statistics. */
@Injectable({ providedIn: 'root' })
export class ProductsApi {
  private readonly api = inject(ApiClient);

  list(includeInactive = true): Observable<ProductDto[]> {
    return this.api.get<ProductDto[]>('products', { params: { includeInactive } });
  }

  get(id: Guid): Observable<ProductDto> {
    return this.api.get<ProductDto>(`products/${enc(id)}`);
  }

  save(id: Guid | null, request: SaveProductRequest): Observable<ProductDto> {
    return id ? this.api.put<ProductDto>(`products/${enc(id)}`, request, { silent: true }) : this.api.post<ProductDto>('products', request, { silent: true });
  }

  delete(id: Guid): Observable<void> {
    return this.api.delete<void>(`products/${enc(id)}`);
  }

  families(): Observable<ProductFamilyDto[]> {
    return this.api.get<ProductFamilyDto[]>('product-families');
  }

  saveFamily(id: Guid | null, name: string, description: string | null): Observable<ProductFamilyDto> {
    const body = { name, description };
    return id
      ? this.api.put<ProductFamilyDto>(`product-families/${enc(id)}`, body, { silent: true })
      : this.api.post<ProductFamilyDto>('product-families', body, { silent: true });
  }

  saveModule(productId: Guid, moduleId: Guid | null, request: SaveProductModuleRequest): Observable<ProductModuleDto> {
    return moduleId
      ? this.api.put<ProductModuleDto>(`products/${enc(productId)}/modules/${enc(moduleId)}`, request, { silent: true })
      : this.api.post<ProductModuleDto>(`products/${enc(productId)}/modules`, request, { silent: true });
  }

  deleteModule(productId: Guid, moduleId: Guid): Observable<void> {
    return this.api.delete<void>(`products/${enc(productId)}/modules/${enc(moduleId)}`);
  }

  saveVersion(productId: Guid, versionId: Guid | null, request: SaveProductVersionRequest): Observable<ProductVersionDto> {
    return versionId
      ? this.api.put<ProductVersionDto>(`products/${enc(productId)}/versions/${enc(versionId)}`, request, { silent: true })
      : this.api.post<ProductVersionDto>(`products/${enc(productId)}/versions`, request, { silent: true });
  }

  deleteVersion(productId: Guid, versionId: Guid): Observable<void> {
    return this.api.delete<void>(`products/${enc(productId)}/versions/${enc(versionId)}`);
  }

  saveRelease(productId: Guid, versionId: Guid, releaseId: Guid | null, request: SaveProductReleaseRequest): Observable<ProductReleaseDto> {
    const base = `products/${enc(productId)}/versions/${enc(versionId)}/releases`;
    return releaseId
      ? this.api.put<ProductReleaseDto>(`${base}/${enc(releaseId)}`, request, { silent: true })
      : this.api.post<ProductReleaseDto>(base, request, { silent: true });
  }

  deleteRelease(productId: Guid, versionId: Guid, releaseId: Guid): Observable<void> {
    return this.api.delete<void>(`products/${enc(productId)}/versions/${enc(versionId)}/releases/${enc(releaseId)}`);
  }

  installBase(productId?: Guid | null): Observable<InstallBaseDto> {
    return this.api.get<InstallBaseDto>('products/install-base', { params: { productId } });
  }
}

/** Installed base (`/installations`, `/licences`, `/assets`). */
@Injectable({ providedIn: 'root' })
export class InstallationsApi {
  private readonly api = inject(ApiClient);

  list(query: InstallationQuery = {}): Observable<PagedResult<InstallationListItemDto>> {
    return this.api.get<PagedResult<InstallationListItemDto>>('installations', { params: query });
  }

  get(id: Guid): Observable<InstallationDetailDto> {
    return this.api.get<InstallationDetailDto>(`installations/${enc(id)}`);
  }

  save(id: Guid | null, request: SaveInstallationRequest): Observable<InstallationDetailDto> {
    return id
      ? this.api.put<InstallationDetailDto>(`installations/${enc(id)}`, request, { silent: true })
      : this.api.post<InstallationDetailDto>('installations', request, { silent: true });
  }

  delete(id: Guid): Observable<void> {
    return this.api.delete<void>(`installations/${enc(id)}`);
  }

  licences(query: LicenceQuery = {}): Observable<LicenceDto[]> {
    return this.api.get<LicenceDto[]>('licences', { params: query });
  }

  saveLicence(id: Guid | null, request: SaveLicenceRequest): Observable<LicenceDto> {
    return id ? this.api.put<LicenceDto>(`licences/${enc(id)}`, request, { silent: true }) : this.api.post<LicenceDto>('licences', request, { silent: true });
  }

  deleteLicence(id: Guid): Observable<void> {
    return this.api.delete<void>(`licences/${enc(id)}`);
  }

  assets(query: AssetQuery = {}): Observable<AssetDto[]> {
    return this.api.get<AssetDto[]>('assets', { params: query });
  }

  saveAsset(id: Guid | null, request: SaveAssetRequest): Observable<AssetDto> {
    return id ? this.api.put<AssetDto>(`assets/${enc(id)}`, request, { silent: true }) : this.api.post<AssetDto>('assets', request, { silent: true });
  }

  deleteAsset(id: Guid): Observable<void> {
    return this.api.delete<void>(`assets/${enc(id)}`);
  }
}

/** Support contracts (`/contracts…`): entitlements, renewals, consumption and eligibility preview. */
@Injectable({ providedIn: 'root' })
export class ContractsApi {
  private readonly api = inject(ApiClient);

  list(query: ContractQuery = {}): Observable<PagedResult<ContractListItemDto>> {
    return this.api.get<PagedResult<ContractListItemDto>>('contracts', { params: query });
  }

  get(id: Guid): Observable<ContractDetailDto> {
    return this.api.get<ContractDetailDto>(`contracts/${enc(id)}`);
  }

  formMeta(): Observable<ContractFormMetaDto> {
    return this.api.get<ContractFormMetaDto>('contracts/form-meta');
  }

  save(id: Guid | null, request: SaveContractRequest): Observable<ContractDetailDto> {
    return id
      ? this.api.put<ContractDetailDto>(`contracts/${enc(id)}`, request, { silent: true })
      : this.api.post<ContractDetailDto>('contracts', request, { silent: true });
  }

  delete(id: Guid): Observable<void> {
    return this.api.delete<void>(`contracts/${enc(id)}`);
  }

  saveEntitlement(contractId: Guid, entitlementId: Guid | null, request: SaveEntitlementRequest): Observable<EntitlementDto> {
    return entitlementId
      ? this.api.put<EntitlementDto>(`contracts/${enc(contractId)}/entitlements/${enc(entitlementId)}`, request, { silent: true })
      : this.api.post<EntitlementDto>(`contracts/${enc(contractId)}/entitlements`, request, { silent: true });
  }

  deleteEntitlement(contractId: Guid, entitlementId: Guid): Observable<void> {
    return this.api.delete<void>(`contracts/${enc(contractId)}/entitlements/${enc(entitlementId)}`);
  }

  renewals(days = 90): Observable<ContractRenewalDto[]> {
    return this.api.get<ContractRenewalDto[]>('contracts/renewals', { params: { days } });
  }

  consumption(id: Guid): Observable<ConsumptionReportDto> {
    return this.api.get<ConsumptionReportDto>(`contracts/${enc(id)}/consumption`);
  }

  /** Support eligibility a new ticket would get (customer + optional product/installation/asset). */
  eligibility(customerId: Guid, productId?: Guid | null, installationId?: Guid | null, assetId?: Guid | null): Observable<EligibilityPreviewDto> {
    return this.api.get<EligibilityPreviewDto>('contracts/eligibility', { params: { customerId, productId, installationId, assetId } });
  }
}
