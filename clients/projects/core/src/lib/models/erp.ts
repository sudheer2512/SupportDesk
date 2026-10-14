/** Wings ERP / Wings Books integration wire types — mirror SupportDesk.Contracts.Erp. */
import { Guid, IsoDate, IsoDateTime, JsonValue } from './common';

export type ErpSystem = 'WingsErp' | 'WingsBooks';
export type ErpMode = 'simulator' | 'http';
export type ErpAuthType = 'OAuth2' | 'ApiKey' | 'None';

export interface ErpBranch {
  id: Guid;
  code: string;
  name: string;
  addressLine1: string | null;
  city: string | null;
  state: string | null;
  postalCode: string | null;
  phone: string | null;
  isHeadOffice: boolean;
}

export interface ErpCustomerMaster {
  id: Guid;
  code: string;
  name: string;
  legalName: string | null;
  gstin: string | null;
  pan: string | null;
  email: string | null;
  phone: string | null;
  website: string | null;
  industry: string | null;
  region: string | null;
  city: string | null;
  state: string | null;
  country: string | null;
  isActive: boolean;
  isBlocked: boolean;
  creditLimit: number | null;
  currency: string;
  branches: ErpBranch[];
  modifiedAt: IsoDateTime;
}

export interface ErpInstallationRecord {
  id: Guid;
  customerId: Guid;
  branchId: Guid | null;
  productCode: string;
  version: string | null;
  build: string | null;
  name: string;
  installationCode: string | null;
  environment: string;
  deploymentMode: string;
  operatingSystem: string | null;
  databaseVersion: string | null;
  userCount: number | null;
  installedOn: IsoDate | null;
  goLiveOn: IsoDate | null;
  status: string;
  modules: string[];
  modifiedAt: IsoDateTime;
}

export interface ErpAmcContractRecord {
  id: Guid;
  customerId: Guid;
  number: string;
  name: string;
  contractType: string;
  startDate: IsoDate;
  endDate: IsoDate;
  value: number;
  currency: string;
  autoRenew: boolean;
  status: string;
  salesOrderNumber: string | null;
  productCodes: string[];
  onsite: boolean;
  modifiedAt: IsoDateTime;
}

export interface ErpTaxBreakup {
  cgst: number;
  sgst: number;
  igst: number;
  cess: number;
}

export interface ErpTransactionLine {
  lineNo: number;
  itemCode: string;
  description: string;
  hsnSac: string | null;
  quantity: number;
  unit: string;
  rate: number;
  discount: number;
  taxableValue: number;
  taxRate: number;
  taxAmount: number;
  amount: number;
}

export interface ErpTransaction {
  system: string;
  recordType: string;
  id: Guid;
  number: string;
  customerId: Guid;
  customerCode: string | null;
  customerName: string | null;
  date: IsoDate;
  dueDate: IsoDate | null;
  status: string;
  taxableAmount: number;
  taxAmount: number;
  totalAmount: number;
  balanceAmount: number | null;
  currency: string;
  placeOfSupply: string | null;
  tax: ErpTaxBreakup | null;
  lines: ErpTransactionLine[];
  reference: string | null;
  url: string | null;
}

export interface ErpLinkedTicketDto {
  ticketId: Guid;
  displayNumber: string;
  subject: string;
  status: string;
  isOrigin: boolean;
  createdAt: IsoDateTime;
}

export interface ErpRecordViewDto {
  record: ErpTransaction;
  customerId: Guid | null;
  customerName: string | null;
  linkedTickets: ErpLinkedTicketDto[];
}

export interface ErpCustomerRecordsDto {
  system: ErpSystem;
  configured: boolean;
  mode: ErpMode | null;
  error: string | null;
  erpCustomerId: Guid | null;
  erpCode: string | null;
  master: ErpCustomerMaster | null;
  outstandingAmount: number;
  currency: string;
  invoices: ErpTransaction[];
  orders: ErpTransaction[];
  receipts: ErpTransaction[];
  amcContracts: ErpAmcContractRecord[];
  installations: ErpInstallationRecord[];
}

export interface ErpIntegrationDto {
  id: Guid | null;
  system: ErpSystem;
  name: string;
  configured: boolean;
  isActive: boolean;
  mode: ErpMode;
  baseUrl: string | null;
  authType: ErpAuthType;
  tokenUrl: string | null;
  clientId: string | null;
  scope: string | null;
  hasClientSecret: boolean;
  hasApiKey: boolean;
  timeoutSeconds: number;
  syncIntervalMinutes: number;
  syncCustomers: boolean;
  syncInstallations: boolean;
  syncLicences: boolean;
  syncContracts: boolean;
  createMissingCustomers: boolean;
  status: string;
  lastSyncAt: IsoDateTime | null;
  lastError: string | null;
  rowVersion: string | null;
}

/** Secrets are write-only: null keeps the stored value, "" clears it. */
export interface SaveErpIntegrationRequest {
  name?: string | null;
  isActive: boolean;
  mode: ErpMode;
  baseUrl?: string | null;
  authType: ErpAuthType;
  tokenUrl?: string | null;
  clientId?: string | null;
  clientSecret?: string | null;
  apiKey?: string | null;
  scope?: string | null;
  timeoutSeconds: number;
  syncIntervalMinutes: number;
  syncCustomers: boolean;
  syncInstallations: boolean;
  syncLicences: boolean;
  syncContracts: boolean;
  createMissingCustomers: boolean;
}

export interface ErpConnectionTestDto {
  success: boolean;
  message: string;
  durationMs: number;
  serverVersion: string | null;
}

export interface ErpSyncEntityResultDto {
  entity: string;
  received: number;
  created: number;
  updated: number;
  unchanged: number;
  skipped: number;
  warnings: string[];
}

export interface ErpSyncResultDto {
  system: ErpSystem;
  trigger: string;
  startedAt: IsoDateTime;
  finishedAt: IsoDateTime;
  success: boolean;
  error: string | null;
  entities: ErpSyncEntityResultDto[];
}

export interface IntegrationLogDto {
  id: Guid;
  system: string;
  direction: string;
  operation: string;
  url: string | null;
  statusCode: number | null;
  success: boolean;
  durationMs: number;
  requestExcerpt: string | null;
  responseExcerpt: string | null;
  correlationId: string | null;
  createdAt: IsoDateTime;
}

export interface CreateErpTicketRequest {
  system: ErpSystem;
  recordType: string;
  recordId?: Guid | null;
  recordNumber?: string | null;
  recordUrl?: string | null;
  context?: Record<string, JsonValue> | null;
  customerErpId?: Guid | null;
  customerCode?: string | null;
  contactEmail?: string | null;
  contactName?: string | null;
  contactPhone?: string | null;
  erpUser?: string | null;
  subject: string;
  description?: string | null;
  descriptionHtml?: string | null;
  priority?: string | null;
  productCode?: string | null;
  clientRequestId?: string | null;
}
