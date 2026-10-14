import { Guid, IsoDateTime } from './common';

/** Mirrors SupportDesk.Contracts.Subscriptions (plans, subscription, usage, invoices, trial sign-up). */

export interface PlanDto {
  id: Guid;
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
  tenantCount: number;
}

export interface PlanFeatureDto {
  key: string;
  label: string;
}

export type UsageMeterState = 'ok' | 'warning' | 'exceeded' | 'unlimited';

export interface UsageMeterDto {
  metric: string;
  label: string;
  used: number;
  limit: number | null;
  /** count | bytes */
  unit: string;
  percent: number | null;
  state: UsageMeterState;
}

export interface BillingContactDto {
  name: string | null;
  email: string | null;
  address: string | null;
  taxId: string | null;
}

export type UpdateBillingContactRequest = BillingContactDto;

export type SubscriptionStatusName = 'Trialing' | 'Active' | 'PastDue' | 'Suspended' | 'Cancelled';
export type TenantStatusName = 'Trial' | 'Active' | 'Suspended' | 'Cancelled' | 'PendingDeletion';

export interface SubscriptionDto {
  id: Guid;
  tenantId: Guid;
  tenantName: string;
  tenantSlug: string;
  tenantStatus: TenantStatusName;
  plan: PlanDto;
  status: SubscriptionStatusName;
  billingCycle: 'Monthly' | 'Annual';
  seats: number;
  startedAt: IsoDateTime;
  trialEndsAt: IsoDateTime | null;
  trialDaysLeft: number | null;
  currentPeriodStart: IsoDateTime;
  currentPeriodEnd: IsoDateTime;
  cancelAtPeriodEnd: boolean;
  pendingPlan: PlanDto | null;
  graceEndsAt: IsoDateTime | null;
  suspendedAt: IsoDateTime | null;
  suspensionReason: string | null;
  paymentProvider: string;
  paymentInstructions: string;
  billingContact: BillingContactDto;
  usage: UsageMeterDto[];
  estimatedNextInvoice: number;
  currency: string;
  openInvoices: number;
  amountDue: number;
  rowVersion: string;
}

export interface PlanConflictDto {
  metric: string;
  label: string;
  used: number;
  limit: number;
}

export interface PlanChangePreviewDto {
  fromPlan: string;
  toPlan: string;
  direction: 'upgrade' | 'downgrade' | 'same';
  immediate: boolean;
  effectiveAt: IsoDateTime;
  proratedAmount: number;
  newPeriodAmount: number;
  currency: string;
  seats: number;
  billingCycle: string;
  conflicts: PlanConflictDto[];
  allowed: boolean;
  message: string;
}

export interface ChangePlanRequest {
  planCode: string;
  billingCycle?: string | null;
  seats?: number | null;
  rowVersion?: string | null;
}

export interface ChangePlanResultDto {
  subscription: SubscriptionDto;
  prorationInvoice: InvoiceSummaryDto | null;
  message: string;
}

export interface UsagePointDto {
  day: IsoDateTime;
  quantity: number;
}

export interface UsageHistoryDto {
  metric: string;
  label: string;
  unit: string;
  points: UsagePointDto[];
}

export interface InvoiceLineDto {
  description: string;
  quantity: number;
  unitPrice: number;
  amount: number;
}

export type InvoiceStatusName = 'Draft' | 'Open' | 'Paid' | 'Void';

export interface InvoiceSummaryDto {
  id: Guid;
  tenantId: Guid;
  tenantName: string;
  number: string;
  kind: 'Subscription' | 'Proration' | 'Manual';
  status: InvoiceStatusName;
  periodStart: IsoDateTime;
  periodEnd: IsoDateTime;
  issuedAt: IsoDateTime;
  dueAt: IsoDateTime;
  paidAt: IsoDateTime | null;
  total: number;
  currency: string;
  overdue: boolean;
}

export interface InvoiceDto {
  summary: InvoiceSummaryDto;
  subtotal: number;
  tax: number;
  lines: InvoiceLineDto[];
  paymentReference: string | null;
  paymentProvider: string | null;
  paymentInstructions: string;
  notes: string | null;
  billTo: BillingContactDto;
}

// ───── Trial sign-up (public) ─────

export interface SignupRequest {
  company: string;
  slug: string;
  adminName: string;
  adminEmail: string;
  password: string;
  planCode?: string | null;
  timeZone?: string | null;
  /** Honeypot: must stay empty. */
  website?: string | null;
}

export interface SignupStartedDto {
  signupId: Guid;
  email: string;
  expiresAt: IsoDateTime;
  /** Only in development/test environments. */
  demoCode: string | null;
}

export interface SignupCompletedDto {
  tenantId: Guid;
  workspace: string;
  adminEmail: string;
  planName: string;
  trialEndsAt: IsoDateTime | null;
}

export interface SlugAvailabilityDto {
  slug: string;
  available: boolean;
  reason: string | null;
  suggestion: string | null;
}
