import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ApiError,
  ChangePlanResultDto,
  InvoiceDto,
  InvoiceSummaryDto,
  PlanDto,
  PlanFeatureDto,
  SubscriptionDto,
  SubscriptionsApi,
  UsageHistoryDto,
  saveBlob,
} from '@sd/core';
import { SdBadge, SdButton, SdCard, SdChart, SdDateTimePipe, SdDrawer, SdEmptyState, SdFormField, SdIcon, SdInput, SdPageHeader, SdSkeleton, ToastService } from '@sd/ui';
import type { EChartsCoreOption } from 'echarts/core';
import { InvoiceViewComponent } from './invoice-view.component';
import { PlanPickerComponent } from './plan-picker.component';
import { SAAS_CSS, UsageMetersComponent, bytes, compactNumber, invoiceLabel, invoiceTone, money, tenantTone } from './saas-ui';

/**
 * The tenant administrator's subscription page (admin.billing): plan and trial status, usage against limits with
 * history, plan comparison and change, invoices with PDF download and the billing contact. Works while suspended.
 */
@Component({
  selector: 'desk-subscription-home',
  imports: [
    FormsModule,
    SdPageHeader,
    SdButton,
    SdBadge,
    SdCard,
    SdChart,
    SdDrawer,
    SdEmptyState,
    SdFormField,
    SdIcon,
    SdInput,
    SdSkeleton,
    SdDateTimePipe,
    UsageMetersComponent,
    PlanPickerComponent,
    InvoiceViewComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Subscription" icon="credit-card" subtitle="Plan, usage, limits and invoices." [breadcrumbs]="crumbs">
        <button sdButton sdPageActions variant="ghost" icon="refresh" [loading]="loading()" (click)="load()">Refresh</button>
        <button sdButton sdPageActions variant="primary" icon="layers" [disabled]="!sub()" (click)="openPlans()">Compare &amp; change plan</button>
      </sd-page-header>
      <div class="desk-page__body">
        @if (error(); as e) {
          <sd-empty-state icon="circle-alert" title="The subscription could not be loaded" [message]="e">
            <button sdButton (click)="load()">Try again</button>
          </sd-empty-state>
        } @else if (!sub()) {
          <div class="grid"><sd-skeleton height="160px" /><sd-skeleton height="160px" /><sd-skeleton height="220px" /></div>
        } @else {
          @let s = sub()!;
          @if (s.tenantStatus === 'Suspended') {
            <div class="callout callout--danger" role="alert">
              <sd-icon name="octagon-alert" [size]="18" />
              <div class="grow">
                <strong>Your workspace is suspended</strong> — changes are blocked until the subscription is restored.
                {{ s.suspensionReason }}
                @if (s.openInvoices > 0) {
                  <div>Amount due: <strong>{{ money(s.amountDue, s.currency) }}</strong> ({{ s.openInvoices }} open invoice{{ s.openInvoices === 1 ? '' : 's' }}). {{ s.paymentInstructions }}</div>
                } @else {
                  <div>Choose a paid plan to continue; service resumes as soon as the first invoice is paid.</div>
                }
              </div>
            </div>
          } @else if (s.status === 'PastDue') {
            <div class="callout callout--warning" role="status">
              <sd-icon name="triangle-alert" [size]="18" />
              <div class="grow">
                <strong>{{ s.trialEndsAt ? 'Your trial has ended.' : 'Payment is overdue.' }}</strong>
                The workspace keeps working until {{ s.graceEndsAt | sdDateTime: 'date' }}, then it is suspended.
                {{ s.trialEndsAt ? 'Choose a plan to keep going.' : s.paymentInstructions }}
              </div>
              @if (s.trialEndsAt) {
                <button sdButton size="sm" variant="primary" (click)="openPlans()">Choose a plan</button>
              }
            </div>
          } @else if (s.status === 'Trialing') {
            <div class="callout callout--info" role="status">
              <sd-icon name="hourglass" [size]="18" />
              <div class="grow">
                <strong>{{ s.trialDaysLeft }} day{{ s.trialDaysLeft === 1 ? '' : 's' }} left in your trial</strong> (ends {{ s.trialEndsAt | sdDateTime: 'date' }}).
                Everything in the {{ s.plan.name }} plan is available while you evaluate.
              </div>
              <button sdButton size="sm" variant="primary" (click)="openPlans()">Choose a plan</button>
            </div>
          }
          @if (s.pendingPlan) {
            <div class="callout callout--info" role="status">
              <sd-icon name="calendar" [size]="18" />
              <div class="grow">Your plan changes to <strong>{{ s.pendingPlan.name }}</strong> on {{ s.currentPeriodEnd | sdDateTime: 'date' }}.</div>
              <button sdButton size="sm" [loading]="busy()" (click)="cancelPending()">Keep {{ s.plan.name }}</button>
            </div>
          }

          <div class="grid">
            <sd-card title="Current plan" [subtitle]="s.tenantName + ' · ' + s.tenantSlug">
              <div sdCardActions>
                <sd-badge [tone]="tone(s.status)" [dot]="true">{{ statusLabel(s.status) }}</sd-badge>
              </div>
              <div class="plan">
                <div class="plan-name">{{ s.plan.name }}</div>
                <div class="price">
                  {{ money(s.billingCycle === 'Annual' ? s.plan.annualPricePerAgent : s.plan.monthlyPricePerAgent, s.plan.currency) }}
                  <span class="muted">per agent / {{ s.billingCycle === 'Annual' ? 'year' : 'month' }}</span>
                </div>
              </div>
              <dl class="facts">
                <dt>Seats</dt>
                <dd>{{ s.seats }}</dd>
                <dt>Billing cycle</dt>
                <dd>{{ s.billingCycle }}</dd>
                <dt>{{ s.status === 'Trialing' ? 'Trial period' : 'Current period' }}</dt>
                <dd>{{ s.currentPeriodStart | sdDateTime: 'date' }} – {{ (s.trialEndsAt ?? s.currentPeriodEnd) | sdDateTime: 'date' }}</dd>
                <dt>{{ s.status === 'Trialing' ? 'After the trial' : 'Next invoice (est.)' }}</dt>
                <dd>{{ money(s.estimatedNextInvoice, s.currency) }}</dd>
                <dt>Payment</dt>
                <dd>{{ s.paymentProvider }}</dd>
                <dt>Customer since</dt>
                <dd>{{ s.startedAt | sdDateTime: 'date' }}</dd>
              </dl>
              @if (s.plan.features.length) {
                <ul class="features" aria-label="Included features">
                  @for (f of s.plan.features; track f) {
                    <li><sd-icon name="check" [size]="14" />{{ featureLabel(f) }}</li>
                  }
                </ul>
              }
            </sd-card>

            <sd-card title="Usage vs limits" subtitle="Updated every 15 minutes">
              <desk-usage-meters [meters]="s.usage" />
            </sd-card>
          </div>

          <sd-card title="Usage history" class="block">
            <div sdCardActions class="chart-tools">
              <label class="sd-visually-hidden" for="metric">Metric</label>
              <select sdInput size="sm" id="metric" [ngModel]="metric()" (ngModelChange)="metric.set($event)">
                @for (h of history(); track h.metric) {
                  <option [value]="h.metric">{{ h.label }}</option>
                }
              </select>
              <label class="sd-visually-hidden" for="range">Range</label>
              <select sdInput size="sm" id="range" [ngModel]="days()" (ngModelChange)="days.set(+$event); loadHistory()">
                <option [value]="30">30 days</option>
                <option [value]="90">90 days</option>
                <option [value]="365">12 months</option>
              </select>
            </div>
            @if (historyLoading()) {
              <sd-skeleton height="240px" />
            } @else if (!selectedHistory() || selectedHistory()!.points.length === 0) {
              <sd-empty-state compact icon="chart-column" title="No history yet" message="Usage is recorded daily; check back tomorrow." />
            } @else {
              <sd-chart [options]="chart()" height="240px" [ariaLabel]="chartLabel()" />
            }
          </sd-card>

          <div class="grid">
            <sd-card title="Invoices" class="block">
              @if (invoices().length === 0 && !invoicesLoading()) {
                <sd-empty-state compact icon="receipt" title="No invoices yet" [message]="s.status === 'Trialing' ? 'Your first invoice is issued when the trial converts to a paid plan.' : 'Invoices are issued at the start of each billing period.'" />
              } @else {
                <div class="table-wrap">
                  <table class="grid">
                    <caption class="sd-visually-hidden">Invoices</caption>
                    <thead>
                      <tr>
                        <th scope="col">Number</th>
                        <th scope="col" class="hide-sm">Period</th>
                        <th scope="col" class="hide-sm">Due</th>
                        <th scope="col" class="num">Total</th>
                        <th scope="col">Status</th>
                        <th scope="col"><span class="sd-visually-hidden">Actions</span></th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (i of invoices(); track i.id) {
                        <tr class="clickable" tabindex="0" (click)="openInvoice(i)" (keydown.enter)="openInvoice(i)">
                          <td><strong>{{ i.number }}</strong><div class="muted small">{{ i.kind }}</div></td>
                          <td class="hide-sm">{{ i.periodStart | sdDateTime: 'date' }} – {{ i.periodEnd | sdDateTime: 'date' }}</td>
                          <td class="hide-sm">{{ i.dueAt | sdDateTime: 'date' }}</td>
                          <td class="num">{{ money(i.total, i.currency) }}</td>
                          <td><sd-badge size="sm" [tone]="invoiceTone(i)">{{ invoiceLabel(i) }}</sd-badge></td>
                          <td class="actions">
                            <button sdButton size="sm" variant="ghost" icon="download" iconOnly [attr.aria-label]="'Download ' + i.number + ' as PDF'"
                              (click)="$event.stopPropagation(); download(i)"></button>
                          </td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
                @if (nextCursor()) {
                  <div class="more"><button sdButton size="sm" [loading]="invoicesLoading()" (click)="loadInvoices(true)">Load more</button></div>
                }
              }
            </sd-card>

            <sd-card title="Billing contact" subtitle="Invoices are addressed and sent here">
              <form class="form" (ngSubmit)="saveContact()">
                <sd-form-field label="Name" [error]="contactErrors()['name']">
                  <input sdInput name="cname" autocomplete="name" [(ngModel)]="contact.name" maxlength="200" />
                </sd-form-field>
                <sd-form-field label="E-mail" [error]="contactErrors()['email']">
                  <input sdInput name="cemail" type="email" autocomplete="email" [(ngModel)]="contact.email" />
                </sd-form-field>
                <sd-form-field label="Billing address" optional [error]="contactErrors()['address']">
                  <textarea sdInput name="caddress" rows="3" [(ngModel)]="contact.address" maxlength="1000"></textarea>
                </sd-form-field>
                <sd-form-field label="Tax id (GSTIN / VAT)" optional [error]="contactErrors()['taxId']">
                  <input sdInput name="ctax" [(ngModel)]="contact.taxId" maxlength="64" />
                </sd-form-field>
                <div class="footer"><button sdButton type="submit" variant="primary" [loading]="savingContact()">Save contact</button></div>
              </form>
            </sd-card>
          </div>
        }
      </div>
    </div>

    <sd-drawer [(open)]="plansOpen" title="Compare plans" width="min(980px, 100vw)">
      @if (sub(); as s) {
        @if (plans().length === 0) {
          <sd-skeleton [lines]="6" />
        } @else {
          <desk-plan-picker #picker [subscription]="s" [plans]="plans()" [features]="features()" (changed)="onChanged($event)" />
        }
      }
    </sd-drawer>

    <sd-drawer [(open)]="invoiceOpen" [title]="invoice()?.summary?.number ?? 'Invoice'" width="560px">
      @if (invoice(); as inv) {
        <desk-invoice-view [invoice]="inv" [downloading]="downloading()" (download)="download(inv.summary)" />
      } @else {
        <sd-skeleton [lines]="8" />
      }
    </sd-drawer>
  `,
  styles: `
    ${SAAS_CSS}
    .grid { display: grid; gap: var(--sd-space-4); grid-template-columns: repeat(auto-fit, minmax(min(100%, 380px), 1fr)); margin-bottom: var(--sd-space-4); }
    .block { display: block; margin-bottom: var(--sd-space-4); }
    .plan { display: flex; flex-wrap: wrap; align-items: baseline; justify-content: space-between; gap: 8px; margin-bottom: 12px; }
    .plan-name { font-size: var(--sd-text-xl); font-weight: var(--sd-weight-bold); }
    .price { font-size: var(--sd-text-md); font-weight: var(--sd-weight-semibold); }
    .price .muted { font-weight: var(--sd-weight-regular); font-size: var(--sd-text-sm); }
    .features { list-style: none; margin: 14px 0 0; padding: 0; display: flex; flex-wrap: wrap; gap: 6px 14px; font-size: var(--sd-text-sm); color: var(--sd-text-2); }
    .features li { display: inline-flex; align-items: center; gap: 4px; }
    .features sd-icon { color: var(--sd-success); }
    .chart-tools { display: flex; gap: 8px; }
  `,
})
export class SubscriptionHomePage implements OnInit {
  private readonly api = inject(SubscriptionsApi);
  private readonly toast = inject(ToastService);
  private readonly picker = viewChild<PlanPickerComponent>('picker');

  protected readonly crumbs = [{ label: 'Settings' }, { label: 'Subscription' }];
  protected readonly sub = signal<SubscriptionDto | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly plans = signal<PlanDto[]>([]);
  protected readonly features = signal<PlanFeatureDto[]>([]);
  protected readonly plansOpen = signal(false);
  protected readonly history = signal<UsageHistoryDto[]>([]);
  protected readonly historyLoading = signal(false);
  protected readonly metric = signal('agents');
  protected readonly days = signal(30);
  protected readonly invoices = signal<InvoiceSummaryDto[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly invoicesLoading = signal(false);
  protected readonly invoiceOpen = signal(false);
  protected readonly invoice = signal<InvoiceDto | null>(null);
  protected readonly downloading = signal(false);
  protected readonly savingContact = signal(false);
  protected readonly contactErrors = signal<Record<string, string | null>>({});
  protected contact = { name: '', email: '', address: '', taxId: '' };

  protected readonly money = money;
  protected readonly invoiceTone = invoiceTone;
  protected readonly invoiceLabel = invoiceLabel;
  protected readonly tone = tenantTone;

  protected readonly selectedHistory = computed<UsageHistoryDto | null>(
    () => this.history().find((h) => h.metric === this.metric()) ?? this.history().at(0) ?? null,
  );
  protected readonly chartLabel = computed(() => (this.selectedHistory()?.label ?? 'Usage') + ' per day');
  protected readonly chart = computed<EChartsCoreOption>(() => {
    const h = this.selectedHistory();
    const points = h?.points ?? [];
    const isBytes = h?.unit === 'bytes';
    return {
      tooltip: { trigger: 'axis', valueFormatter: (v: unknown) => (isBytes ? bytes(Number(v)) : compactNumber(Number(v))) },
      grid: { left: 8, right: 16, top: 16, bottom: 8, containLabel: true },
      xAxis: { type: 'category', data: points.map((p) => p.day.slice(0, 10)), axisLabel: { hideOverlap: true } },
      yAxis: { type: 'value', axisLabel: { formatter: (v: number) => (isBytes ? bytes(v) : compactNumber(v)) } },
      series: [{ name: h?.label ?? 'Usage', type: 'line', smooth: true, showSymbol: false, areaStyle: { opacity: 0.12 }, data: points.map((p) => p.quantity) }],
    };
  });

  ngOnInit(): void {
    this.load();
    this.loadHistory();
    this.loadInvoices(false);
    this.api.features().subscribe({ next: (f) => this.features.set(f), error: () => undefined });
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.get().subscribe({
      next: (s) => {
        this.apply(s);
        this.loading.set(false);
      },
      error: (e: ApiError) => {
        this.error.set(e.userMessage);
        this.loading.set(false);
      },
    });
  }

  private apply(s: SubscriptionDto): void {
    this.sub.set(s);
    this.contact = { name: s.billingContact.name ?? '', email: s.billingContact.email ?? '', address: s.billingContact.address ?? '', taxId: s.billingContact.taxId ?? '' };
  }

  protected loadHistory(): void {
    this.historyLoading.set(true);
    this.api.usageHistory(this.days()).subscribe({
      next: (h) => {
        this.history.set(h);
        this.historyLoading.set(false);
      },
      error: () => this.historyLoading.set(false),
    });
  }

  protected loadInvoices(more: boolean): void {
    this.invoicesLoading.set(true);
    this.api.invoices(more ? this.nextCursor() : null).subscribe({
      next: (page) => {
        this.invoices.set(more ? [...this.invoices(), ...page.items] : page.items);
        this.nextCursor.set(page.nextCursor);
        this.invoicesLoading.set(false);
      },
      error: () => this.invoicesLoading.set(false),
    });
  }

  protected openPlans(): void {
    this.plansOpen.set(true);
    const ready = () => queueMicrotask(() => this.picker()?.reset());
    if (this.plans().length) {
      ready();
      return;
    }
    this.api.plans().subscribe({
      next: (p) => {
        this.plans.set(p);
        ready();
      },
      error: (e: ApiError) => this.toast.error('Plans could not be loaded', e.userMessage),
    });
  }

  protected onChanged(r: ChangePlanResultDto): void {
    this.apply(r.subscription);
    this.plansOpen.set(false);
    this.toast.success('Subscription updated', r.message);
    this.loadInvoices(false);
  }

  protected cancelPending(): void {
    this.busy.set(true);
    this.api.cancelPendingChange().subscribe({
      next: (s) => {
        this.apply(s);
        this.busy.set(false);
        this.toast.success('Scheduled change cancelled');
      },
      error: () => this.busy.set(false),
    });
  }

  protected openInvoice(i: InvoiceSummaryDto): void {
    this.invoice.set(null);
    this.invoiceOpen.set(true);
    this.api.invoice(i.id).subscribe({
      next: (inv) => this.invoice.set(inv),
      error: (e: ApiError) => {
        this.invoiceOpen.set(false);
        this.toast.error('Invoice could not be loaded', e.userMessage);
      },
    });
  }

  protected download(i: InvoiceSummaryDto): void {
    this.downloading.set(true);
    this.api.invoicePdf(i.id).subscribe({
      next: (blob) => {
        saveBlob(blob, `${i.number}.pdf`);
        this.downloading.set(false);
      },
      error: () => this.downloading.set(false),
    });
  }

  protected saveContact(): void {
    const email = this.contact.email.trim();
    if (email && !/^\S+@\S+\.\S+$/.test(email)) {
      this.contactErrors.set({ email: 'Enter a valid e-mail address.' });
      return;
    }
    this.contactErrors.set({});
    this.savingContact.set(true);
    this.api.updateBillingContact({ name: this.contact.name, email, address: this.contact.address, taxId: this.contact.taxId }).subscribe({
      next: (s) => {
        this.apply(s);
        this.savingContact.set(false);
        this.toast.success('Billing contact saved');
      },
      error: (e: ApiError) => {
        this.savingContact.set(false);
        this.contactErrors.set({ name: e.fieldError('name'), email: e.fieldError('email'), address: e.fieldError('address'), taxId: e.fieldError('taxId') });
        if (!e.errors || Object.keys(e.errors).length === 0) this.toast.error('Could not save', e.userMessage);
      },
    });
  }

  protected statusLabel(status: string): string {
    return status === 'PastDue' ? 'Past due' : status === 'Trialing' ? 'Trial' : status;
  }

  protected featureLabel(key: string): string {
    return this.features().find((f) => f.key === key)?.label ?? key.replace(/_/g, ' ');
  }
}
