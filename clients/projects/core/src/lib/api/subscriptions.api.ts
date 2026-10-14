import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { Guid, PagedResult } from '../models/common';
import {
  ChangePlanRequest,
  ChangePlanResultDto,
  InvoiceDto,
  InvoiceSummaryDto,
  PlanChangePreviewDto,
  PlanDto,
  PlanFeatureDto,
  SignupCompletedDto,
  SignupRequest,
  SignupStartedDto,
  SlugAvailabilityDto,
  SubscriptionDto,
  UpdateBillingContactRequest,
  UsageHistoryDto,
} from '../models/subscriptions';

const id = encodeURIComponent;

/**
 * The tenant's subscription (`/subscription`, `admin.billing`): plan, trial, usage meters and history, plan change
 * (upgrade now with pro-rata, downgrade at period end), invoices + PDF and billing contact. Stays usable while the
 * workspace is suspended so the plan can be changed and invoices settled.
 */
@Injectable({ providedIn: 'root' })
export class SubscriptionsApi {
  private readonly api = inject(ApiClient);

  get(): Observable<SubscriptionDto> {
    return this.api.get<SubscriptionDto>('subscription');
  }

  plans(): Observable<PlanDto[]> {
    return this.api.get<PlanDto[]>('subscription/plans');
  }

  features(): Observable<PlanFeatureDto[]> {
    return this.api.get<PlanFeatureDto[]>('subscription/features');
  }

  usageHistory(days = 30): Observable<UsageHistoryDto[]> {
    return this.api.get<UsageHistoryDto[]>('subscription/usage-history', { params: { days } });
  }

  previewChange(request: ChangePlanRequest): Observable<PlanChangePreviewDto> {
    return this.api.post<PlanChangePreviewDto>('subscription/plan-change/preview', request, { silent: true });
  }

  changePlan(request: ChangePlanRequest): Observable<ChangePlanResultDto> {
    return this.api.post<ChangePlanResultDto>('subscription/plan-change', request, { silent: true });
  }

  cancelPendingChange(): Observable<SubscriptionDto> {
    return this.api.delete<SubscriptionDto>('subscription/plan-change');
  }

  updateBillingContact(request: UpdateBillingContactRequest): Observable<SubscriptionDto> {
    return this.api.put<SubscriptionDto>('subscription/billing-contact', request, { silent: true });
  }

  invoices(cursor: string | null = null, limit = 25): Observable<PagedResult<InvoiceSummaryDto>> {
    return this.api.get<PagedResult<InvoiceSummaryDto>>('subscription/invoices', { params: { cursor, limit } });
  }

  invoice(invoiceId: Guid): Observable<InvoiceDto> {
    return this.api.get<InvoiceDto>(`subscription/invoices/${id(invoiceId)}`);
  }

  invoicePdf(invoiceId: Guid): Observable<Blob> {
    return this.api.blob(`subscription/invoices/${id(invoiceId)}/pdf`);
  }
}

/** Public trial sign-up (`/signup`): no authentication; rate limited server-side. */
@Injectable({ providedIn: 'root' })
export class SignupApi {
  private readonly api = inject(ApiClient);

  slugAvailability(slug: string): Observable<SlugAvailabilityDto> {
    return this.api.get<SlugAvailabilityDto>('signup/slug-availability', { params: { slug }, skipAuth: true, silent: true });
  }

  plans(): Observable<PlanDto[]> {
    return this.api.get<PlanDto[]>('signup/plans', { skipAuth: true });
  }

  start(request: SignupRequest): Observable<SignupStartedDto> {
    return this.api.post<SignupStartedDto>('signup', request, { skipAuth: true, silent: true });
  }

  verify(signupId: Guid, code: string): Observable<SignupCompletedDto> {
    return this.api.post<SignupCompletedDto>(`signup/${id(signupId)}/verify`, { code }, { skipAuth: true, silent: true });
  }
}
