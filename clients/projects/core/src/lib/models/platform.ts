import { Guid, IsoDateTime } from './common';
import { InvoiceSummaryDto, SubscriptionDto, UsageMeterDto } from './subscriptions';

/** Mirrors SupportDesk.Contracts.Platform (SaaS operator console). */

export interface PlatformOverviewDto {
  tenants: number;
  trials: number;
  active: number;
  suspended: number;
  cancelled: number;
  trialsEndingSoon: number;
  monthlyRecurringRevenue: number;
  openReceivables: number;
  overdueInvoices: number;
  pendingSupportRequests: number;
  currency: string;
}

export interface PlatformTenantDto {
  id: Guid;
  slug: string;
  name: string;
  status: string;
  planCode: string;
  planName: string;
  subscriptionStatus: string;
  createdAt: IsoDateTime;
  lastActivityAt: IsoDateTime | null;
  trialEndsAt: IsoDateTime | null;
  agents: number;
  storageBytes: number;
  databaseMode: 'Shared' | 'Dedicated';
  dedicatedConnectionName: string | null;
  deletionScheduledAt: IsoDateTime | null;
}

export interface PlatformAuditEntryDto {
  id: Guid;
  tenantId: Guid | null;
  tenantName: string | null;
  sequence: number;
  occurredAt: IsoDateTime;
  actorType: string;
  actorName: string | null;
  action: string;
  entityType: string | null;
  entityId: Guid | null;
  summary: string | null;
  ipAddress: string | null;
}

export interface SupportAccessDto {
  id: Guid;
  tenantId: Guid;
  tenantName: string;
  operatorName: string;
  reason: string;
  durationMinutes: number;
  status: 'Pending' | 'Approved' | 'Rejected' | 'Revoked' | 'Expired';
  requestedAt: IsoDateTime;
  decidedByName: string | null;
  decidedAt: IsoDateTime | null;
  decisionNote: string | null;
  expiresAt: IsoDateTime | null;
  active: boolean;
  /** The signed-in operator's own grant (only its operator can open diagnostics). */
  isMine: boolean;
}

export interface PlatformTenantDetailDto {
  tenant: PlatformTenantDto;
  subscription: SubscriptionDto | null;
  recentInvoices: InvoiceSummaryDto[];
  supportAccess: SupportAccessDto[];
  recentActivity: PlatformAuditEntryDto[];
  suspensionReason: string | null;
  suspendedAt: IsoDateTime | null;
  country: string | null;
  defaultTimeZone: string;
}

export interface TenantActionRequest {
  reason: string | null;
}

export interface CancelTenantRequest {
  reason: string | null;
  retentionDays: number;
}

export interface ExtendTrialRequest {
  days: number;
}

export interface OperatorChangePlanRequest {
  planCode: string;
  seats?: number | null;
  billingCycle?: string | null;
}

export interface ProvisionDedicatedRequest {
  connectionName: string;
}

export interface DedicatedDatabaseResultDto {
  connectionName: string;
  database: string;
  tablesCopied: number;
  rowsCopied: number;
  steps: string[];
}

export interface SavePlanRequest {
  code: string;
  name: string;
  description: string | null;
  monthlyPricePerAgent: number;
  annualPricePerAgent: number;
  currency: string;
  isPublic: boolean;
  trialDays: number;
  maxAgents: number | null;
  maxLightAgents: number | null;
  maxPortalUsers: number | null;
  storageMb: number | null;
  maxChannels: number | null;
  aiTokensMonthly: number | null;
  maxAttachmentMb: number | null;
  apiCallsMonthly: number | null;
  maxBrands: number | null;
  features: string[];
  sortOrder: number;
}

export interface MarkInvoicePaidRequest {
  reference: string | null;
  paidAt?: IsoDateTime | null;
}

export interface VoidInvoiceRequest {
  reason: string | null;
}

export interface PlatformChainVerificationDto {
  valid: boolean;
  checked: number;
  brokenAtSequence: number | null;
  message: string | null;
}

export interface RequestSupportAccessRequest {
  reason: string;
  durationMinutes: number;
}

export interface DecideSupportAccessRequest {
  approve: boolean;
  note: string | null;
}

export interface SupportDiagnosticsDto {
  tenantId: Guid;
  tenantName: string;
  accessExpiresAt: IsoDateTime;
  usage: UsageMeterDto[];
  configurationCounts: Record<string, number>;
  recentAudit: PlatformAuditEntryDto[];
  failedJobs: number;
  deadLetterEvents: number;
}

/** Response of POST /api/v1/platform/auth/login. */
export interface PlatformLoginResponse {
  status: 'ok' | 'mfa_required' | 'invalid' | string;
  accessToken: string | null;
  expiresAt: IsoDateTime | null;
  displayName: string | null;
  message?: string | null;
}
