import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import {
  ApiError,
  DedicatedDatabaseResultDto,
  InvoiceSummaryDto,
  PlanDto,
  PlatformApi,
  PlatformTenantDetailDto,
  SupportAccessDto,
  SupportDiagnosticsDto,
  saveBlob,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCard,
  SdDateTimePipe,
  SdDrawer,
  SdDropdownMenu,
  SdEmptyState,
  SdFormField,
  SdIcon,
  SdInput,
  SdMenuItem,
  SdPageHeader,
  SdRelativeTimePipe,
  SdSkeleton,
  ToastService,
} from '@sd/ui';
import { SAAS_CSS, UsageMetersComponent, humanize, invoiceLabel, invoiceTone, money, requestTone, statusLabel, tenantTone } from '../subscription/saas-ui';

type Panel = 'suspend' | 'reactivate' | 'cancel' | 'extend' | 'plan' | 'dedicated' | 'support' | 'paid' | 'diagnostics';

/**
 * Tenant detail for platform operators: status, subscription and usage, invoices (mark paid), support access and recent
 * platform activity; actions suspend / reactivate / cancel (deletion schedule) / extend trial / change plan / move to a
 * dedicated database / request support access. There is no impersonation.
 */
@Component({
  selector: 'desk-platform-tenant-detail-page',
  imports: [
    FormsModule,
    RouterLink,
    SdBadge,
    SdButton,
    SdCard,
    SdDateTimePipe,
    SdDrawer,
    SdDropdownMenu,
    SdEmptyState,
    SdFormField,
    SdIcon,
    SdInput,
    SdPageHeader,
    SdRelativeTimePipe,
    SdSkeleton,
    UsageMetersComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header [title]="d()?.tenant?.name ?? 'Tenant'" icon="building" [subtitle]="d() ? d()!.tenant.slug : null" [breadcrumbs]="crumbs()">
        @if (d()) {
          <sd-dropdown-menu sdPageActions label="Actions" icon="settings" align="end" [items]="menu()" (select)="openPanel($any($event.id))" />
        }
      </sd-page-header>
      <div class="desk-page__body">
        @if (error(); as e) {
          <sd-empty-state icon="circle-alert" title="The tenant could not be loaded" [message]="e">
            <a sdButton routerLink="/platform/tenants">Back to tenants</a>
          </sd-empty-state>
        } @else if (!d()) {
          <sd-skeleton [lines]="10" height="28px" />
        } @else {
          @let t = d()!.tenant;
          @if (t.status === 'Suspended') {
            <div class="callout callout--danger" role="status">
              <sd-icon name="octagon-alert" [size]="16" />
              <div class="grow"><strong>Suspended</strong> {{ d()!.suspendedAt ? '(' + (d()!.suspendedAt | sdDateTime) + ')' : '' }} — {{ d()!.suspensionReason }}</div>
            </div>
          } @else if (t.status === 'Cancelled' || t.status === 'PendingDeletion') {
            <div class="callout callout--warning" role="status">
              <sd-icon name="triangle-alert" [size]="16" />
              <div class="grow"><strong>{{ t.status === 'Cancelled' ? 'Cancelled' : 'Pending deletion' }}</strong> — data is scheduled for deletion on {{ t.deletionScheduledAt | sdDateTime: 'date' }}.</div>
            </div>
          }
          <div class="grid">
            <sd-card title="Workspace">
              <dl class="facts">
                <dt>Status</dt>
                <dd><sd-badge size="sm" [tone]="tone(t.status)" [dot]="true">{{ statusLabel(t.status) }}</sd-badge></dd>
                <dt>Workspace</dt>
                <dd>{{ t.slug }}</dd>
                <dt>Created</dt>
                <dd>{{ t.createdAt | sdDateTime }}</dd>
                <dt>Last activity</dt>
                <dd>{{ t.lastActivityAt ? (t.lastActivityAt | sdRelativeTime) : 'never' }}</dd>
                <dt>Country / time zone</dt>
                <dd>{{ d()!.country || '—' }} · {{ d()!.defaultTimeZone }}</dd>
                <dt>Database</dt>
                <dd>{{ t.databaseMode }}{{ t.dedicatedConnectionName ? ' (' + t.dedicatedConnectionName + ')' : '' }}</dd>
              </dl>
            </sd-card>
            <sd-card title="Subscription">
              @if (d()!.subscription; as s) {
                <dl class="facts">
                  <dt>Plan</dt>
                  <dd>{{ s.plan.name }} · {{ s.seats }} seats · {{ s.billingCycle }}</dd>
                  <dt>Status</dt>
                  <dd><sd-badge size="sm" [tone]="tone(s.status)">{{ statusLabel(s.status) }}</sd-badge></dd>
                  @if (s.trialEndsAt) {
                    <dt>Trial ends</dt>
                    <dd>{{ s.trialEndsAt | sdDateTime: 'date' }} ({{ s.trialDaysLeft }} days left)</dd>
                  }
                  @if (s.graceEndsAt) {
                    <dt>Grace ends</dt>
                    <dd>{{ s.graceEndsAt | sdDateTime: 'date' }}</dd>
                  }
                  <dt>Period</dt>
                  <dd>{{ s.currentPeriodStart | sdDateTime: 'date' }} – {{ s.currentPeriodEnd | sdDateTime: 'date' }}</dd>
                  @if (s.pendingPlan) {
                    <dt>Scheduled</dt>
                    <dd>→ {{ s.pendingPlan.name }} at period end</dd>
                  }
                  <dt>Amount due</dt>
                  <dd>{{ money(s.amountDue, s.currency) }} ({{ s.openInvoices }} open)</dd>
                  <dt>Billing contact</dt>
                  <dd>{{ s.billingContact.name || '—' }} {{ s.billingContact.email ? '· ' + s.billingContact.email : '' }}</dd>
                </dl>
              } @else {
                <sd-empty-state compact icon="credit-card" title="No subscription" />
              }
            </sd-card>
          </div>

          @if (d()!.subscription; as s) {
            <sd-card title="Usage vs limits" class="block"><desk-usage-meters [meters]="s.usage" /></sd-card>
          }

          <h2 class="section-title"><sd-icon name="receipt" [size]="16" />Invoices</h2>
          @if (d()!.recentInvoices.length === 0) {
            <sd-empty-state compact icon="receipt" title="No invoices yet" />
          } @else {
            <div class="table-wrap">
              <table class="data">
                <caption class="sd-visually-hidden">Recent invoices</caption>
                <thead>
                  <tr><th scope="col">Number</th><th scope="col" class="hide-sm">Period</th><th scope="col" class="hide-sm">Due</th><th scope="col" class="num">Total</th><th scope="col">Status</th><th scope="col"><span class="sd-visually-hidden">Actions</span></th></tr>
                </thead>
                <tbody>
                  @for (i of d()!.recentInvoices; track i.id) {
                    <tr>
                      <td><strong>{{ i.number }}</strong><div class="muted small">{{ i.kind }}</div></td>
                      <td class="hide-sm">{{ i.periodStart | sdDateTime: 'date' }} – {{ i.periodEnd | sdDateTime: 'date' }}</td>
                      <td class="hide-sm">{{ i.dueAt | sdDateTime: 'date' }}</td>
                      <td class="num">{{ money(i.total, i.currency) }}</td>
                      <td><sd-badge size="sm" [tone]="invoiceTone(i)">{{ invoiceLabel(i) }}</sd-badge></td>
                      <td class="actions">
                        @if (i.status === 'Open') {
                          <button sdButton size="sm" (click)="markPaid(i)">Mark paid</button>
                        }
                        <button sdButton size="sm" variant="ghost" icon="download" iconOnly [attr.aria-label]="'Download ' + i.number" (click)="pdf(i)"></button>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }

          <h2 class="section-title"><sd-icon name="life-buoy" [size]="16" />Support access</h2>
          @if (d()!.supportAccess.length === 0) {
            <sd-empty-state compact icon="life-buoy" title="No support access requested" message="Request access when a tenant asks for help; their administrator must approve it." />
          } @else {
            <div class="table-wrap">
              <table class="data">
                <caption class="sd-visually-hidden">Support access</caption>
                <thead><tr><th scope="col">Requested</th><th scope="col">Operator</th><th scope="col" class="hide-sm">Reason</th><th scope="col">Status</th><th scope="col"><span class="sd-visually-hidden">Actions</span></th></tr></thead>
                <tbody>
                  @for (g of d()!.supportAccess; track g.id) {
                    <tr>
                      <td>{{ g.requestedAt | sdDateTime }}</td>
                      <td>{{ g.operatorName }}</td>
                      <td class="hide-sm">{{ g.reason }}</td>
                      <td>
                        <sd-badge size="sm" [tone]="rtone(g.status)">{{ g.status }}</sd-badge>
                        @if (g.active && g.expiresAt) {
                          <div class="muted small">until {{ g.expiresAt | sdDateTime }}</div>
                        }
                      </td>
                      <td class="actions">
                        @if (g.active && g.isMine) {
                          <button sdButton size="sm" icon="eye" (click)="diagnostics(g)">Diagnostics</button>
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }

          <h2 class="section-title"><sd-icon name="history" [size]="16" />Recent platform and billing activity</h2>
          @if (d()!.recentActivity.length === 0) {
            <sd-empty-state compact icon="history" title="No activity yet" />
          } @else {
            <ul class="timeline">
              @for (a of d()!.recentActivity; track a.id) {
                <li>
                  <span class="when muted small" [title]="a.occurredAt | sdDateTime">{{ a.occurredAt | sdRelativeTime }}</span>
                  <span class="what small"><strong>{{ a.actorName || a.actorType }}</strong> — {{ a.summary || humanize(a.action) }}</span>
                </li>
              }
            </ul>
          }
        }
      </div>
    </div>

    <sd-drawer [(open)]="panelOpen" [title]="panelTitle()" width="520px">
      @if (d(); as detail) {
        <form class="form" id="panel-form" (ngSubmit)="submit()">
          @switch (panel()) {
            @case ('suspend') {
              <p class="small">Writes are blocked immediately (HTTP 402); users can still sign in, read and pay. The tenant's administrators see the reason.</p>
              <sd-form-field label="Reason" required [error]="fieldError()">
                <textarea sdInput name="reason" rows="3" [(ngModel)]="reason" maxlength="500"></textarea>
              </sd-form-field>
            }
            @case ('reactivate') {
              <p class="small">Restores write access. Unconverted trials return to their trial — extend the trial if it already ended.</p>
              <sd-form-field label="Note" optional><textarea sdInput name="note" rows="2" [(ngModel)]="reason" maxlength="500"></textarea></sd-form-field>
            }
            @case ('cancel') {
              <p class="small">Access stops now. The tenant's data is deleted after the retention period (the tenant becomes "pending deletion").</p>
              <sd-form-field label="Reason" required [error]="fieldError()"><textarea sdInput name="reason" rows="3" [(ngModel)]="reason" maxlength="500"></textarea></sd-form-field>
              <sd-form-field label="Keep data for (days)" hint="0–365 days"><input sdInput type="number" name="ret" min="0" max="365" [(ngModel)]="retentionDays" /></sd-form-field>
            }
            @case ('extend') {
              <sd-form-field label="Extend the trial by (days)" hint="1–90 days; restores a lapsed trial." [error]="fieldError()">
                <input sdInput type="number" name="days" min="1" max="90" [(ngModel)]="days" />
              </sd-form-field>
            }
            @case ('plan') {
              <p class="small">Switches the plan immediately without a pro-rata charge (operator override).</p>
              <sd-form-field label="Plan" [error]="fieldError()">
                <select sdInput name="plan" [(ngModel)]="planCode">
                  @for (p of plans(); track p.id) {
                    <option [value]="p.code">{{ p.name }} ({{ money(p.monthlyPricePerAgent, p.currency) }}/agent/month)</option>
                  }
                </select>
              </sd-form-field>
              <div class="form-grid">
                <sd-form-field label="Seats"><input sdInput type="number" min="1" name="seats" [(ngModel)]="seats" /></sd-form-field>
                <sd-form-field label="Billing cycle">
                  <select sdInput name="cycle" [(ngModel)]="cycle"><option value="Monthly">Monthly</option><option value="Annual">Annual</option></select>
                </sd-form-field>
              </div>
            }
            @case ('dedicated') {
              @if (dedicatedResult(); as r) {
                <div class="callout callout--success" role="status"><sd-icon name="circle-check" [size]="16" /><div class="grow">Moved to <strong>{{ r.database }}</strong>: {{ r.rowsCopied }} rows from {{ r.tablesCopied }} tables.</div></div>
                <ol class="steps">
                  @for (s of r.steps; track $index) {
                    <li>{{ s }}</li>
                  }
                </ol>
              } @else {
                <p class="small">
                  Creates the database named by the connection, applies the schema, copies this tenant's data and switches its routing. The connection
                  name must exist as <code>ConnectionStrings:&lt;name&gt;</code> or be derivable from <code>ConnectionStrings:DedicatedTemplate</code>.
                  Suspend the tenant first to avoid writes during the copy.
                </p>
                <sd-form-field label="Connection name" hint="3–40 lower-case letters, digits or underscores" [error]="fieldError()">
                  <input sdInput name="conn" [(ngModel)]="connectionName" autocapitalize="none" spellcheck="false" />
                </sd-form-field>
              }
            }
            @case ('support') {
              <p class="small">The tenant's administrators must approve the request. Access is read-only diagnostics, time-boxed and audited.</p>
              <sd-form-field label="Reason" required hint="Reference the ticket or case (10–1000 characters)." [error]="fieldError()">
                <textarea sdInput name="reason" rows="3" [(ngModel)]="reason" maxlength="1000"></textarea>
              </sd-form-field>
              <sd-form-field label="Duration">
                <select sdInput name="dur" [(ngModel)]="duration">
                  <option [ngValue]="30">30 minutes</option><option [ngValue]="60">1 hour</option><option [ngValue]="240">4 hours</option><option [ngValue]="1440">24 hours</option>
                </select>
              </sd-form-field>
            }
            @case ('paid') {
              <p class="small">Record an offline payment for <strong>{{ invoiceForPayment()?.number }}</strong> ({{ money(invoiceForPayment()?.total, invoiceForPayment()?.currency) }}). Settling the last overdue invoice restores a billing suspension.</p>
              <sd-form-field label="Payment reference" optional hint="Bank transfer id, cheque number…"><input sdInput name="ref" [(ngModel)]="reference" maxlength="200" /></sd-form-field>
            }
            @case ('diagnostics') {
              @if (diag(); as g) {
                <p class="small">Read-only diagnostics, available until {{ g.accessExpiresAt | sdDateTime }}. This view is audited.</p>
                <desk-usage-meters [meters]="g.usage" />
                <h3 class="section-title">Configuration</h3>
                <dl class="facts">
                  @for (c of counts(); track c.key) {
                    <dt>{{ humanize(c.key) }}</dt>
                    <dd>{{ c.value }}</dd>
                  }
                  <dt>Failed jobs</dt>
                  <dd>{{ g.failedJobs }}</dd>
                  <dt>Dead-letter events</dt>
                  <dd>{{ g.deadLetterEvents }}</dd>
                </dl>
                <h3 class="section-title">Recent audit</h3>
                <ul class="timeline">
                  @for (a of g.recentAudit; track a.id) {
                    <li><span class="when muted small">{{ a.occurredAt | sdRelativeTime }}</span><span class="what small">{{ a.summary || a.action }}</span></li>
                  }
                </ul>
              } @else {
                <sd-skeleton [lines]="6" />
              }
            }
          }
          @if (panelError()) {
            <p class="err" role="alert">{{ panelError() }}</p>
          }
        </form>
      }
      <div sdDrawerFooter class="footer">
        <button sdButton type="button" variant="ghost" (click)="panelOpen.set(false)">{{ panel() === 'diagnostics' || dedicatedResult() ? 'Close' : 'Cancel' }}</button>
        @if (panel() !== 'diagnostics' && !dedicatedResult()) {
          <button sdButton type="submit" form="panel-form" [variant]="panel() === 'suspend' || panel() === 'cancel' ? 'danger' : 'primary'" [loading]="saving()">
            {{ submitLabel() }}
          </button>
        }
      </div>
    </sd-drawer>
  `,
  styles: `
    ${SAAS_CSS}
    .grid { display: grid; gap: var(--sd-space-4); grid-template-columns: repeat(auto-fit, minmax(min(100%, 380px), 1fr)); margin-bottom: var(--sd-space-4); }
    .block { display: block; margin-bottom: var(--sd-space-4); }
    .timeline { list-style: none; margin: 0; padding: 0; display: grid; gap: 6px; }
    .timeline li { display: grid; grid-template-columns: 110px 1fr; gap: 10px; }
    .steps { margin: 0; padding-left: 18px; font-size: var(--sd-text-sm); display: grid; gap: 4px; }
    code { font-family: var(--sd-font-mono); font-size: var(--sd-text-xs); }
  `,
})
export class PlatformTenantDetailPage implements OnInit {
  /** Route param. */
  readonly id = input.required<string>();
  private readonly api = inject(PlatformApi);
  private readonly toast = inject(ToastService);

  protected readonly crumbs = computed(() => [{ label: 'Tenants', link: '/platform/tenants' }, { label: this.d()?.tenant.name ?? 'Tenant' }]);
  protected readonly d = signal<PlatformTenantDetailDto | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly panel = signal<Panel>('suspend');
  protected readonly panelOpen = signal(false);
  protected readonly saving = signal(false);
  protected readonly panelError = signal<string | null>(null);
  protected readonly fieldError = signal<string | null>(null);
  protected readonly plans = signal<PlanDto[]>([]);
  protected readonly dedicatedResult = signal<DedicatedDatabaseResultDto | null>(null);
  protected readonly invoiceForPayment = signal<InvoiceSummaryDto | null>(null);
  protected readonly diag = signal<SupportDiagnosticsDto | null>(null);
  protected reason = '';
  protected reference = '';
  protected retentionDays = 30;
  protected days = 14;
  protected planCode = '';
  protected seats = 1;
  protected cycle = 'Monthly';
  protected connectionName = '';
  protected duration = 60;

  protected readonly money = money;
  protected readonly tone = tenantTone;
  protected readonly statusLabel = statusLabel;
  protected readonly rtone = requestTone;
  protected readonly invoiceTone = invoiceTone;
  protected readonly invoiceLabel = invoiceLabel;
  protected readonly humanize = humanize;

  protected readonly counts = computed(() => Object.entries(this.diag()?.configurationCounts ?? {}).map(([key, value]) => ({ key, value })));

  protected readonly menu = computed<SdMenuItem[]>(() => {
    const t = this.d()?.tenant;
    const sub = this.d()?.subscription;
    const suspended = t?.status === 'Suspended';
    const closed = t?.status === 'Cancelled' || t?.status === 'PendingDeletion';
    return [
      { id: 'reactivate', label: 'Reactivate', icon: 'play', disabled: !(suspended || sub?.status === 'PastDue') || t?.status === 'PendingDeletion' },
      { id: 'suspend', label: 'Suspend…', icon: 'pause', disabled: suspended || closed },
      { id: 'extend', label: 'Extend trial…', icon: 'hourglass', disabled: !sub?.trialEndsAt },
      { id: 'plan', label: 'Change plan…', icon: 'layers', disabled: closed },
      { id: 'support', label: 'Request support access…', icon: 'life-buoy', disabled: closed },
      { id: 'dedicated', label: 'Move to dedicated database…', icon: 'database', disabled: t?.databaseMode === 'Dedicated' || closed },
      { id: 'sep', divider: true },
      { id: 'cancel', label: 'Cancel subscription…', icon: 'circle-x', danger: true, disabled: closed },
    ];
  });

  protected readonly panelTitle = computed(() => {
    switch (this.panel()) {
      case 'suspend':
        return 'Suspend tenant';
      case 'reactivate':
        return 'Reactivate tenant';
      case 'cancel':
        return 'Cancel subscription';
      case 'extend':
        return 'Extend trial';
      case 'plan':
        return 'Change plan';
      case 'dedicated':
        return 'Dedicated database';
      case 'support':
        return 'Request support access';
      case 'paid':
        return 'Mark invoice paid';
      case 'diagnostics':
        return 'Support diagnostics';
    }
  });

  protected readonly submitLabel = computed(() => {
    switch (this.panel()) {
      case 'suspend':
        return 'Suspend';
      case 'reactivate':
        return 'Reactivate';
      case 'cancel':
        return 'Cancel subscription';
      case 'extend':
        return 'Extend trial';
      case 'plan':
        return 'Change plan';
      case 'dedicated':
        return 'Provision and move';
      case 'support':
        return 'Send request';
      case 'paid':
        return 'Mark paid';
      default:
        return 'Save';
    }
  });

  ngOnInit(): void {
    this.load();
    this.api.plans().subscribe({ next: (p) => this.plans.set(p), error: () => undefined });
  }

  protected load(): void {
    this.api.tenant(this.id()).subscribe({
      next: (d) => {
        this.d.set(d);
      },
      error: (e: ApiError) => this.error.set(e.userMessage),
    });
  }

  protected openPanel(id: Panel | 'sep'): void {
    if (id === 'sep') return;
    const d = this.d();
    this.panel.set(id);
    this.reason = '';
    this.reference = '';
    this.panelError.set(null);
    this.fieldError.set(null);
    this.dedicatedResult.set(null);
    if (id === 'plan' && d?.subscription) {
      this.planCode = d.subscription.plan.code;
      this.seats = d.subscription.seats;
      this.cycle = d.subscription.billingCycle;
    }
    if (id === 'dedicated' && d) this.connectionName = 'tenant_' + d.tenant.slug.replace(/[^a-z0-9]/g, '_').slice(0, 30);
    this.panelOpen.set(true);
  }

  protected markPaid(i: InvoiceSummaryDto): void {
    this.invoiceForPayment.set(i);
    this.openPanel('paid');
  }

  protected diagnostics(g: SupportAccessDto): void {
    this.diag.set(null);
    this.openPanel('diagnostics');
    this.api.diagnostics(g.id).subscribe({
      next: (x) => this.diag.set(x),
      error: (e: ApiError) => this.panelError.set(e.userMessage),
    });
  }

  protected pdf(i: InvoiceSummaryDto): void {
    this.api.invoicePdf(i.id).subscribe((blob) => saveBlob(blob, `${i.number}.pdf`));
  }

  protected submit(): void {
    const d = this.d();
    if (!d) return;
    const id = d.tenant.id;
    this.panelError.set(null);
    this.fieldError.set(null);
    this.saving.set(true);
    const done = (message: string) => (detail: PlatformTenantDetailDto) => {
      this.d.set(detail);
      this.saving.set(false);
      this.panelOpen.set(false);
      this.toast.success(message);
    };
    const fail = (e: ApiError) => {
      this.saving.set(false);
      const field = e.fieldError('reason') ?? e.fieldError('days') ?? e.fieldError('seats') ?? e.fieldError('connectionName') ?? e.fieldError('retentionDays');
      if (field) this.fieldError.set(field);
      else this.panelError.set(e.userMessage);
    };
    switch (this.panel()) {
      case 'suspend':
        if (!this.reason.trim()) {
          this.saving.set(false);
          this.fieldError.set('Give a reason.');
          return;
        }
        this.api.suspend(id, this.reason).subscribe({ next: done('Tenant suspended'), error: fail });
        break;
      case 'reactivate':
        this.api.reactivate(id, this.reason || null).subscribe({ next: done('Tenant reactivated'), error: fail });
        break;
      case 'cancel':
        if (!this.reason.trim()) {
          this.saving.set(false);
          this.fieldError.set('Give a reason.');
          return;
        }
        this.api.cancel(id, { reason: this.reason, retentionDays: this.retentionDays }).subscribe({ next: done('Subscription cancelled'), error: fail });
        break;
      case 'extend':
        this.api.extendTrial(id, this.days).subscribe({ next: done('Trial extended'), error: fail });
        break;
      case 'plan':
        this.api.changePlan(id, { planCode: this.planCode, seats: this.seats, billingCycle: this.cycle }).subscribe({ next: done('Plan changed'), error: fail });
        break;
      case 'dedicated':
        this.api.provisionDedicated(id, this.connectionName.trim()).subscribe({
          next: (r) => {
            this.saving.set(false);
            this.dedicatedResult.set(r);
            this.load();
          },
          error: fail,
        });
        break;
      case 'support':
        this.api.requestSupportAccess(id, { reason: this.reason, durationMinutes: this.duration }).subscribe({
          next: () => {
            this.saving.set(false);
            this.panelOpen.set(false);
            this.toast.success('Request sent', 'The tenant administrators must approve it.');
            this.load();
          },
          error: fail,
        });
        break;
      case 'paid': {
        const inv = this.invoiceForPayment();
        if (!inv) return;
        this.api.markPaid(inv.id, { reference: this.reference || null }).subscribe({
          next: () => {
            this.saving.set(false);
            this.panelOpen.set(false);
            this.toast.success(`${inv.number} marked paid`);
            this.load();
          },
          error: fail,
        });
        break;
      }
      default:
        this.saving.set(false);
    }
  }
}
