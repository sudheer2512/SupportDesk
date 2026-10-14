/**
 * Products & Assets and Contracts wire types — mirror SupportDesk.Contracts.Products.
 * DateOnly values are "YYYY-MM-DD" strings (IsoDate); money is a number in the contract/document currency.
 */
import { Guid, IdName, IsoDate, IsoDateTime, JsonValue } from './common';

export const VERSION_STATUSES = ['Planned', 'Released', 'Supported', 'EndOfLife'] as const;
export type VersionStatus = (typeof VERSION_STATUSES)[number];

export const RELEASE_TYPES = ['Major', 'Minor', 'Patch', 'Hotfix'] as const;
export type ReleaseType = (typeof RELEASE_TYPES)[number];

export const INSTALLATION_ENVIRONMENTS = ['Production', 'Staging', 'Test', 'Training'] as const;
export type InstallationEnvironment = (typeof INSTALLATION_ENVIRONMENTS)[number];

export const DEPLOYMENT_MODES = ['OnPremise', 'Cloud', 'Hybrid', 'Hosted'] as const;
export type DeploymentMode = (typeof DEPLOYMENT_MODES)[number];

export const INSTALLATION_STATUSES = ['Active', 'Suspended', 'Decommissioned'] as const;
export type InstallationStatus = (typeof INSTALLATION_STATUSES)[number];

export const LICENCE_STATUSES = ['Active', 'Expired', 'Suspended', 'Revoked'] as const;
export const ASSET_STATUSES = ['InService', 'InRepair', 'Retired'] as const;

export const CONTRACT_TYPES = ['Warranty', 'Amc', 'AnnualSupport', 'PrepaidHours', 'TimeAndMaterial', 'Subscription'] as const;
export type ContractType = (typeof CONTRACT_TYPES)[number];

export const CONTRACT_STATUSES = ['Draft', 'Active', 'Expired', 'Suspended', 'Cancelled'] as const;
export type ContractStatus = (typeof CONTRACT_STATUSES)[number];

export const ENTITLEMENT_TYPES = ['Support', 'Warranty', 'Hours', 'Incidents', 'Onsite'] as const;
export type EntitlementType = (typeof ENTITLEMENT_TYPES)[number];

// ───── Catalog ─────

export interface ProductFamilyDto {
  id: Guid;
  name: string;
  description: string | null;
  productCount: number;
}

export interface ProductModuleDto {
  id: Guid;
  productId: Guid;
  name: string;
  code: string | null;
  isActive: boolean;
}

export interface ProductReleaseDto {
  id: Guid;
  productId: Guid;
  versionId: Guid;
  name: string;
  buildNumber: string | null;
  type: ReleaseType;
  releaseDate: IsoDate | null;
  releaseNotes: string | null;
  engineeringReleaseRef: string | null;
  installationCount: number;
}

export interface ProductVersionDto {
  id: Guid;
  productId: Guid;
  version: string;
  releaseDate: IsoDate | null;
  endOfSupportDate: IsoDate | null;
  status: VersionStatus;
  notes: string | null;
  isEndOfSupport: boolean;
  installationCount: number;
  releases: ProductReleaseDto[];
}

export interface ProductDto {
  id: Guid;
  familyId: Guid | null;
  familyName: string | null;
  name: string;
  code: string;
  description: string | null;
  isActive: boolean;
  defaultDepartmentId: Guid | null;
  defaultDepartmentName: string | null;
  defaultTeamId: Guid | null;
  defaultTeamName: string | null;
  erpProductId: Guid | null;
  modules: ProductModuleDto[];
  versions: ProductVersionDto[];
  installationCount: number;
  customerCount: number;
  openTickets: number;
}

export interface SaveProductRequest {
  familyId?: Guid | null;
  name: string;
  code: string;
  description?: string | null;
  isActive: boolean;
  defaultDepartmentId?: Guid | null;
  defaultTeamId?: Guid | null;
  erpProductId?: Guid | null;
}

export interface SaveProductModuleRequest {
  name: string;
  code?: string | null;
  isActive: boolean;
}

export interface SaveProductVersionRequest {
  version: string;
  releaseDate?: IsoDate | null;
  endOfSupportDate?: IsoDate | null;
  status: VersionStatus;
  notes?: string | null;
}

export interface SaveProductReleaseRequest {
  name: string;
  buildNumber?: string | null;
  type: ReleaseType;
  releaseDate?: IsoDate | null;
  releaseNotes?: string | null;
  engineeringReleaseRef?: string | null;
}

// ───── Installations ─────

export interface InstallationQuery {
  q?: string | null;
  customerId?: Guid | null;
  productId?: Guid | null;
  versionId?: Guid | null;
  environment?: string | null;
  deploymentMode?: string | null;
  status?: string | null;
  endOfSupport?: boolean | null;
  sort?: 'name' | 'updated' | null;
  dir?: 'asc' | 'desc' | null;
  cursor?: string | null;
  limit?: number | null;
  includeTotal?: boolean | null;
}

export interface InstallationListItemDto {
  id: Guid;
  name: string;
  installationCode: string | null;
  customerId: Guid;
  customerName: string;
  locationId: Guid | null;
  locationName: string | null;
  productId: Guid;
  productName: string;
  versionId: Guid | null;
  version: string | null;
  releaseId: Guid | null;
  release: string | null;
  environment: InstallationEnvironment;
  deploymentMode: DeploymentMode;
  status: InstallationStatus;
  userCount: number | null;
  goLiveOn: IsoDate | null;
  versionEndOfSupport: boolean;
  openTickets: number;
  updatedAt: IsoDateTime;
}

export interface InstallationIntegrationDto {
  system: string;
  direction: string | null;
  notes: string | null;
}

export interface InstallationDetailDto {
  summary: InstallationListItemDto;
  operatingSystem: string | null;
  databaseVersion: string | null;
  serverConfiguration: string | null;
  integrations: InstallationIntegrationDto[];
  enabledModules: ProductModuleDto[];
  installedOn: IsoDate | null;
  notes: string | null;
  erpInstallationId: Guid | null;
  licences: LicenceDto[];
  assets: AssetDto[];
  ticketsLast90Days: number;
  rowVersion: string;
}

export interface SaveInstallationRequest {
  customerId: Guid;
  locationId?: Guid | null;
  productId: Guid;
  versionId?: Guid | null;
  releaseId?: Guid | null;
  name: string;
  installationCode?: string | null;
  environment: InstallationEnvironment;
  deploymentMode: DeploymentMode;
  operatingSystem?: string | null;
  databaseVersion?: string | null;
  serverConfiguration?: string | null;
  integrations?: InstallationIntegrationDto[] | null;
  enabledModuleIds?: Guid[] | null;
  userCount?: number | null;
  installedOn?: IsoDate | null;
  goLiveOn?: IsoDate | null;
  status?: InstallationStatus | null;
  notes?: string | null;
  rowVersion?: string | null;
}

export interface LicenceDto {
  id: Guid;
  customerId: Guid;
  customerName: string;
  installationId: Guid;
  installationName: string;
  productId: Guid;
  productName: string;
  keyMasked: string;
  edition: string | null;
  seats: number;
  issuedOn: IsoDate;
  expiresOn: IsoDate | null;
  status: string;
  daysToExpiry: number | null;
  erpLicenceId: Guid | null;
}

export interface SaveLicenceRequest {
  installationId: Guid;
  licenceKey: string;
  edition?: string | null;
  seats: number;
  issuedOn: IsoDate;
  expiresOn?: IsoDate | null;
  status?: string | null;
}

export interface LicenceQuery {
  customerId?: Guid | null;
  productId?: Guid | null;
  status?: string | null;
  expiringWithinDays?: number | null;
  limit?: number | null;
}

export interface AssetDto {
  id: Guid;
  customerId: Guid;
  customerName: string;
  locationId: Guid | null;
  locationName: string | null;
  productId: Guid | null;
  productName: string | null;
  installationId: Guid | null;
  installationName: string | null;
  name: string;
  serialNumber: string | null;
  assetTag: string | null;
  purchaseDate: IsoDate | null;
  warrantyEndsOn: IsoDate | null;
  underWarranty: boolean;
  status: string;
  notes: string | null;
}

export interface SaveAssetRequest {
  customerId: Guid;
  locationId?: Guid | null;
  productId?: Guid | null;
  installationId?: Guid | null;
  name: string;
  serialNumber?: string | null;
  assetTag?: string | null;
  purchaseDate?: IsoDate | null;
  warrantyEndsOn?: IsoDate | null;
  status?: string | null;
  notes?: string | null;
}

export interface AssetQuery {
  q?: string | null;
  customerId?: Guid | null;
  productId?: Guid | null;
  status?: string | null;
  warrantyExpired?: boolean | null;
  limit?: number | null;
}

export interface InstallBaseVersionDto {
  versionId: Guid | null;
  version: string;
  status: string;
  endOfSupportDate: IsoDate | null;
  isEndOfSupport: boolean;
  installations: number;
  customers: number;
  users: number;
  production: number;
  nonProduction: number;
  openTickets: number;
  ticketsLast90Days: number;
}

export interface InstallBaseProductDto {
  productId: Guid;
  productName: string;
  code: string;
  installations: number;
  customers: number;
  versions: InstallBaseVersionDto[];
}

export interface InstallBaseDto {
  products: InstallBaseProductDto[];
  totalInstallations: number;
  onEndOfSupport: number;
}

// ───── Contracts & entitlements ─────

export interface ContractQuery {
  q?: string | null;
  customerId?: Guid | null;
  type?: string | null;
  status?: string | null;
  expiringWithinDays?: number | null;
  sort?: 'endDate' | 'number' | 'updated' | null;
  dir?: 'asc' | 'desc' | null;
  cursor?: string | null;
  limit?: number | null;
  includeTotal?: boolean | null;
}

export interface ContractListItemDto {
  id: Guid;
  number: string;
  name: string;
  customerId: Guid;
  customerName: string;
  type: ContractType;
  status: ContractStatus;
  startDate: IsoDate;
  endDate: IsoDate;
  daysToExpiry: number;
  isBillable: boolean;
  contractValue: number | null;
  currency: string;
  prepaidHours: number | null;
  consumedHours: number;
  autoRenew: boolean;
  renewalDate: IsoDate | null;
  slaPolicyId: Guid | null;
  slaPolicyName: string | null;
  entitlementCount: number;
  erpReference: string | null;
  accountManagerUserId: Guid | null;
  accountManagerName: string | null;
}

export interface EntitlementDto {
  id: Guid;
  contractId: Guid;
  contractNumber: string | null;
  customerId: Guid;
  name: string;
  type: EntitlementType;
  productId: Guid | null;
  productName: string | null;
  installationId: Guid | null;
  installationName: string | null;
  assetId: Guid | null;
  assetName: string | null;
  incidentsAllowed: number | null;
  incidentsUsed: number;
  hoursAllowed: number | null;
  hoursUsed: number;
  startDate: IsoDate;
  endDate: IsoDate;
  isActive: boolean;
  usagePercent: number | null;
  exhausted: boolean;
}

export interface SaveEntitlementRequest {
  name: string;
  type: EntitlementType;
  productId?: Guid | null;
  installationId?: Guid | null;
  assetId?: Guid | null;
  incidentsAllowed?: number | null;
  hoursAllowed?: number | null;
  startDate?: IsoDate | null;
  endDate?: IsoDate | null;
  isActive: boolean;
}

export interface ContractConsumptionSummaryDto {
  prepaidHours: number | null;
  consumedHours: number;
  remainingHours: number | null;
  billableHours: number;
  nonBillableHours: number;
  billableAmount: number;
  percentUsed: number | null;
  ticketsCovered: number;
}

export interface ContractDetailDto {
  summary: ContractListItemDto;
  calendarId: Guid | null;
  calendarName: string | null;
  hourlyRate: number | null;
  coverage: Record<string, JsonValue>;
  notes: string | null;
  erpContractId: Guid | null;
  entitlements: EntitlementDto[];
  consumption: ContractConsumptionSummaryDto;
  createdAt: IsoDateTime;
  updatedAt: IsoDateTime;
  rowVersion: string;
  permissions: { edit: boolean; viewCosts: boolean };
}

export interface SaveContractRequest {
  customerId: Guid;
  number?: string | null;
  name: string;
  type: ContractType;
  status?: ContractStatus | null;
  startDate: IsoDate;
  endDate: IsoDate;
  slaPolicyId?: Guid | null;
  calendarId?: Guid | null;
  prepaidHours?: number | null;
  isBillable: boolean;
  hourlyRate?: number | null;
  contractValue?: number | null;
  currency?: string | null;
  autoRenew: boolean;
  renewalDate?: IsoDate | null;
  coverage?: Record<string, JsonValue> | null;
  erpReference?: string | null;
  notes?: string | null;
  entitlements?: SaveEntitlementRequest[] | null;
  rowVersion?: string | null;
}

export interface ConsumptionMonthDto {
  month: string;
  hours: number;
  billableHours: number;
  billableAmount: number;
}

export interface ConsumptionAgentDto {
  userId: Guid;
  name: string;
  hours: number;
  billableHours: number;
}

export interface ConsumptionEntryDto {
  id: Guid;
  ticketId: Guid;
  ticketNumber: string;
  ticketSubject: string;
  userName: string;
  startedAt: IsoDateTime;
  minutes: number;
  isBillable: boolean;
  activityType: string | null;
  billableAmount: number;
}

export interface ConsumptionReportDto {
  contractId: Guid;
  contractNumber: string;
  summary: ContractConsumptionSummaryDto;
  byMonth: ConsumptionMonthDto[];
  byAgent: ConsumptionAgentDto[];
  entries: ConsumptionEntryDto[];
}

export interface ContractRenewalDto {
  contract: ContractListItemDto;
  daysToExpiry: number;
  urgency: 'Expired' | 'Critical' | 'Soon' | 'Upcoming';
  openTickets: number;
}

export interface EligibilityPreviewDto {
  status: string;
  note: string;
  contractId: Guid | null;
  contractNumber: string | null;
  contractType: string | null;
  entitlementId: Guid | null;
  entitlementName: string | null;
  allowance: string | null;
  coveredUntil: IsoDate | null;
  slaPolicyId: Guid | null;
  slaPolicyName: string | null;
  isBillable: boolean;
  activeContracts: ContractListItemDto[];
}

export interface ContractFormMetaDto {
  types: ContractType[];
  statuses: ContractStatus[];
  entitlementTypes: EntitlementType[];
  slaPolicies: IdName[];
  calendars: IdName[];
}
