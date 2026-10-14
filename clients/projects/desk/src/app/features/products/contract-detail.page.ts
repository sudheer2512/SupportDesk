import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import {
  ApiError,
  AssetDto,
  ConsumptionReportDto,
  ContractDetailDto,
  ContractsApi,
  ENTITLEMENT_TYPES,
  EntitlementDto,
  EntitlementType,
  Guid,
  InstallationListItemDto,
  InstallationsApi,
  ProductDto,
  ProductsApi,
  saveErrorMessage,
  validationMessages,
} from '@sd/core';
import {
  SdBadge,
  SdBreadcrumb,
  SdButton,
  SdCard,
  SdChart,
  SdCheckbox,
  SdDateInput,
  SdDateTimePipe,
  SdDialog,
  SdDialogService,
  SdEmptyState,
  SdFormField,
  SdInput,
  SdKpiTile,
  SdPageHeader,
  SdSkeleton,
  ToastService,
} from '@sd/ui';
import type { EChartsCoreOption } from 'echarts/core';
import { DateOnlyPipe, HumanizePipe, formatMoney, humanize, statusTone, urgencyTone } from '../customers/shared/customer-ui';
import { SECTION_STYLES, TABLE_STYLES } from '../customers/shared/styles';
import { ContractFormData, ContractFormDialog } from './contract-form.dialog';

export interface EntitlementFormData {
  contract: ContractDetailDto;
  entitlement?: EntitlementDto | null;
}

/** Create / edit an entitlement of a contract (product / installation / asset coverage, incident or hour allowance). */
@Component({
  selector: 'desk-entitlement-form-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdDateInput, SdCheckbox],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="e ? 'Edit entitlement' : 'New entitlement'" [description]="'Contract ' + data.contract.summary.number">
      <form id="ent-form" class="form" (ngSubmit)="save()" novalidate>
        <div class="two">
          <sd-form-field label="Name" required [error]="errors()['name']">
            <input sdInput name="name" [(ngModel)]="name" required maxlength="250" />
          </sd-form-field>
          <sd-form-field label="Type">
            <select sdInput name="type" [(ngModel)]="type">
              @for (t of types; track t) {
                <option [value]="t">{{ humanize(t) }}</option>
              }
            </select>
          </sd-form-field>
        </div>
        <sd-form-field label="Product" optional [error]="errors()['productId']">
          <select sdInput name="product" [(ngModel)]="productId">
            <option [ngValue]="null">Any product</option>
            @for (p of products(); track p.id) {
              <option [ngValue]="p.id">{{ p.name }}</option>
            }
          </select>
        </sd-form-field>
        <div class="two">
          <sd-form-field label="Installation" optional [error]="errors()['installationId']">
            <select sdInput name="inst" [(ngModel)]="installationId">
              <option [ngValue]="null">Any installation</option>
              @for (i of installations(); track i.id) {
                <option [ngValue]="i.id">{{ i.name }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Asset" optional [error]="errors()['assetId']">
            <select sdInput name="asset" [(ngModel)]="assetId">
              <option [ngValue]="null">Any asset</option>
              @for (a of assets(); track a.id) {
                <option [ngValue]="a.id">{{ a.name }} {{ a.serialNumber ?? '' }}</option>
              }
            </select>
          </sd-form-field>
        </div>
        <div class="two">
          <sd-form-field label="Incidents allowed" [optional]="type !== 'Incidents'" [error]="errors()['incidentsAllowed']" hint="Empty = unlimited">
            <input sdInput type="number" min="0" name="inc" [(ngModel)]="incidents" />
          </sd-form-field>
          <sd-form-field label="Hours allowed" [optional]="type !== 'Hours'" [error]="errors()['hoursAllowed']" hint="Empty = unlimited">
            <input sdInput type="number" min="0" step="0.5" name="hrs" [(ngModel)]="hours" />
          </sd-form-field>
        </div>
        <div class="two">
          <sd-form-field label="From" optional><sd-date-input [(value)]="start" /></sd-form-field>
          <sd-form-field label="Until" optional [error]="errors()['endDate']"><sd-date-input [(value)]="end" /></sd-form-field>
        </div>
        <sd-checkbox [(value)]="active">Active</sd-checkbox>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="ent-form" [loading]="saving()">Save</button>
      </div>
    </sd-dialog>
  `,
  styles: `
    .form { display: grid; gap: var(--sd-space-3); }
    .two { display: grid; gap: var(--sd-space-3); grid-template-columns: repeat(2, minmax(0, 1fr)); }
    @media (max-width: 640px) { .two { grid-template-columns: 1fr; } }
    .error { color: var(--sd-danger); }
  `,
})
export class EntitlementFormDialog {
  protected readonly data = inject<EntitlementFormData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<EntitlementDto>>(DialogRef);
  private readonly api = inject(ContractsApi);
  private readonly installationsApi = inject(InstallationsApi);
  private readonly productsApi = inject(ProductsApi);
  protected readonly humanize = humanize;
  protected readonly types = ENTITLEMENT_TYPES;
  protected readonly e = this.data.entitlement ?? null;
  protected name = this.e?.name ?? '';
  protected type: EntitlementType = this.e?.type ?? 'Support';
  protected productId: Guid | null = this.e?.productId ?? null;
  protected installationId: Guid | null = this.e?.installationId ?? null;
  protected assetId: Guid | null = this.e?.assetId ?? null;
  protected incidents: number | null = this.e?.incidentsAllowed ?? null;
  protected hours: number | null = this.e?.hoursAllowed ?? null;
  protected readonly start = signal<string | null>(this.e?.startDate ?? this.data.contract.summary.startDate);
  protected readonly end = signal<string | null>(this.e?.endDate ?? this.data.contract.summary.endDate);
  protected readonly active = signal(this.e?.isActive ?? true);
  protected readonly products = signal<ProductDto[]>([]);
  protected readonly installations = signal<InstallationListItemDto[]>([]);
  protected readonly assets = signal<AssetDto[]>([]);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  constructor() {
    const customerId = this.data.contract.summary.customerId;
    this.productsApi.list(false).subscribe((p) => this.products.set(p));
    this.installationsApi.list({ customerId, limit: 200 }).subscribe((p) => this.installations.set(p.items));
    this.installationsApi.assets({ customerId, limit: 200 }).subscribe((a) => this.assets.set(a));
  }

  private num(v: number | null): number | null {
    return v === null || (v as unknown) === '' ? null : Number(v);
  }

  protected save(): void {
    this.saving.set(true);
    this.api
      .saveEntitlement(this.data.contract.summary.id, this.e?.id ?? null, {
        name: this.name.trim(),
        type: this.type,
        productId: this.productId,
        installationId: this.installationId,
        assetId: this.assetId,
        incidentsAllowed: this.num(this.incidents),
        hoursAllowed: this.num(this.hours),
        startDate: this.start(),
        endDate: this.end(),
        isActive: this.active(),
      })
      .subscribe({
        next: (saved) => this.ref.close(saved),
        error: (err: unknown) => {
          this.saving.set(false);
          this.errors.set(validationMessages(err));
          this.formError.set(saveErrorMessage(err));
        },
      });
  }
}

/** /products/contracts/:id — contract detail: terms, entitlements with usage, consumption from logged time. */
@Component({
  selector: 'desk-contract-detail-page',
  imports: [
    RouterLink,
    SdPageHeader,
    SdButton,
    SdBadge,
    SdCard,
    SdChart,
    SdKpiTile,
    SdSkeleton,
    SdEmptyState,
    SdDateTimePipe,
    DateOnlyPipe,
    HumanizePipe,

  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      @if (notFound()) {
        <sd-empty-state icon="file-text" title="Contract not found" />
      } @else if (!contract()) {
        <div class="desk-page__body pad"><sd-skeleton [lines]="8" /></div>
      } @else {
        @let c = contract()!;
        @let s = c.summary;
        <sd-page-header [title]="s.number + ' — ' + s.name" [subtitle]="s.customerName" icon="file-text" [breadcrumbs]="crumbs()">
          @if (c.permissions.edit) {
            <ng-container sdPageActions>
              <button sdButton icon="pencil" (click)="edit()">Edit</button>
              <button sdButton variant="ghost" icon="trash" iconOnly aria-label="Cancel or delete contract" (click)="remove()"></button>
            </ng-container>
          }
        </sd-page-header>
        <div class="desk-page__body">
          <div class="badges">
            <sd-badge [tone]="statusTone(s.status)" dot>{{ s.status }}</sd-badge>
            <sd-badge variant="outline">{{ s.type | deskHumanize }}</sd-badge>
            @if (s.status === 'Active') {
              <sd-badge [tone]="urgencyTone(s.daysToExpiry)">{{ s.daysToExpiry }} days left</sd-badge>
            }
            @if (s.autoRenew) {
              <sd-badge tone="info" icon="refresh">Auto-renews</sd-badge>
            }
            @if (s.isBillable) {
              <sd-badge tone="warning">Billable</sd-badge>
            }
            @if (c.erpContractId) {
              <sd-badge tone="info" variant="outline" icon="plug">From ERP</sd-badge>
            }
          </div>
          <div class="kpis">
            <sd-kpi-tile label="Contract value" [value]="money(s.contractValue, s.currency)" icon="wallet" />
            <sd-kpi-tile label="Hours consumed" [value]="c.consumption.consumedHours" unit="h" icon="clock" [deltaLabel]="s.prepaidHours ? 'of ' + s.prepaidHours + ' prepaid' : null" />
            <sd-kpi-tile label="Billable hours" [value]="c.consumption.billableHours" unit="h" icon="receipt" [deltaLabel]="c.permissions.viewCosts ? money(c.consumption.billableAmount, s.currency) : null" />
            <sd-kpi-tile label="Tickets covered" [value]="c.consumption.ticketsCovered" icon="ticket" />
          </div>
          <div class="cols">
            <sd-card title="Terms">
              <dl class="meta">
                <dt>Customer</dt>
                <dd><a [routerLink]="['/customers', s.customerId]">{{ s.customerName }}</a></dd>
                <dt>Period</dt>
                <dd>{{ s.startDate | deskDate }} – {{ s.endDate | deskDate }}</dd>
                <dt>Renewal</dt>
                <dd>{{ s.renewalDate | deskDate }}</dd>
                <dt>SLA policy</dt>
                <dd>{{ s.slaPolicyName ?? 'Default selection' }}</dd>
                <dt>Support hours</dt>
                <dd>{{ c.calendarName ?? 'Policy / department hours' }}</dd>
                <dt>Hourly rate</dt>
                <dd>{{ c.hourlyRate === null ? '—' : money(c.hourlyRate, s.currency) }}</dd>
                <dt>Coverage</dt>
                <dd>{{ coverage() }}</dd>
                <dt>ERP reference</dt>
                <dd>{{ s.erpReference ?? '—' }}</dd>
                <dt>Account manager</dt>
                <dd>{{ s.accountManagerName ?? '—' }}</dd>
                @if (c.notes) {
                  <dt>Notes</dt>
                  <dd>{{ c.notes }}</dd>
                }
              </dl>
            </sd-card>
            <sd-card title="Consumption by month" subtitle="Hours logged on covered tickets">
              @if (report(); as r) {
                @if (r.byMonth.length === 0) {
                  <p class="empty-note">No time logged against this contract yet.</p>
                } @else {
                  <sd-chart [options]="consumptionChart()" height="220px" ariaLabel="Hours logged per month, billable and non-billable" />
                }
              } @else {
                <sd-skeleton [lines]="4" />
              }
            </sd-card>
          </div>

          <div class="section-head">
            <h2>Entitlements</h2>
            <span class="sd-spacer"></span>
            @if (c.permissions.edit) {
              <button sdButton size="sm" icon="plus" (click)="editEntitlement(null)">Add entitlement</button>
            }
          </div>
          @if (c.entitlements.length === 0) {
            <p class="empty-note">No entitlements — this contract covers the customer generally.</p>
          } @else {
            <div class="dt-wrap">
              <table class="dt">
                <thead>
                  <tr>
                    <th scope="col">Entitlement</th>
                    <th scope="col">Covers</th>
                    <th scope="col">Valid</th>
                    <th scope="col">Usage</th>
                    <th scope="col"><span class="sd-visually-hidden">Actions</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (e of c.entitlements; track e.id) {
                    <tr [class.inactive]="!e.isActive">
                      <td>{{ e.name }}<div class="sd-xs sd-muted">{{ e.type | deskHumanize }}@if (!e.isActive) { · inactive }</div></td>
                      <td class="sd-small">{{ covers(e) }}</td>
                      <td class="nowrap sd-small">{{ e.startDate | deskDate }} – {{ e.endDate | deskDate }}</td>
                      <td>
                        @if (e.usagePercent !== null) {
                          <div class="usage">
                            <div class="bar" [class.is-warn]="e.usagePercent > 75" [class.is-danger]="e.exhausted"><span [style.width.%]="min(100, e.usagePercent)"></span></div>
                            <span class="sd-xs sd-muted">
                              @if (e.hoursAllowed !== null) {
                                {{ e.hoursUsed }} / {{ e.hoursAllowed }} h
                              } @else {
                                {{ e.incidentsUsed }} / {{ e.incidentsAllowed }} incidents
                              }
                            </span>
                          </div>
                        } @else {
                          <span class="sd-small sd-muted">Unlimited</span>
                        }
                      </td>
                      <td class="row-actions">
                        @if (c.permissions.edit) {
                          <button sdButton size="sm" variant="ghost" icon="pencil" iconOnly [attr.aria-label]="'Edit ' + e.name" (click)="editEntitlement(e)"></button>
                          <button sdButton size="sm" variant="ghost" icon="trash" iconOnly [attr.aria-label]="'Remove ' + e.name" (click)="removeEntitlement(e)"></button>
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }

          @if (report(); as r) {
            @if (r.byAgent.length) {
              <div class="section-head"><h2>Time by agent</h2></div>
              <div class="dt-wrap">
                <table class="dt">
                  <thead>
                    <tr>
                      <th scope="col">Agent</th>
                      <th scope="col" class="num">Hours</th>
                      <th scope="col" class="num">Billable</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (a of r.byAgent; track a.userId) {
                      <tr>
                        <td>{{ a.name }}</td>
                        <td class="num">{{ a.hours }}</td>
                        <td class="num">{{ a.billableHours }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
            @if (r.entries.length) {
              <div class="section-head"><h2>Time entries</h2></div>
              <div class="dt-wrap">
                <table class="dt">
                  <thead>
                    <tr>
                      <th scope="col">When</th>
                      <th scope="col">Ticket</th>
                      <th scope="col">Agent</th>
                      <th scope="col">Activity</th>
                      <th scope="col" class="num">Minutes</th>
                      <th scope="col">Billable</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (t of r.entries.slice(0, 100); track t.id) {
                      <tr>
                        <td class="nowrap sd-small">{{ t.startedAt | sdDateTime: 'datetime' : 'short' }}</td>
                        <td><a [routerLink]="['/tickets', t.ticketId]">{{ t.ticketNumber }}</a>&ngsp;<span class="sd-xs sd-muted">{{ t.ticketSubject }}</span></td>
                        <td>{{ t.userName }}</td>
                        <td>{{ t.activityType ?? '—' }}</td>
                        <td class="num">{{ t.minutes }}</td>
                        <td>{{ t.isBillable ? 'Yes' : 'No' }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          }
        </div>
      }
    </div>
  `,
  styles: [
    TABLE_STYLES,
    SECTION_STYLES,
    `
      .pad { padding-top: var(--sd-space-6); }
      .badges { display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: var(--sd-space-4); }
      .cols { display: grid; gap: var(--sd-space-4); grid-template-columns: repeat(auto-fit, minmax(min(100%, 400px), 1fr)); }
      .usage { display: grid; gap: 2px; min-width: 140px; }
      tr.inactive td { opacity: 0.6; }
      .meta a { color: var(--sd-primary); }
    `,
  ],
})
export class ContractDetailPage {
  readonly id = input.required<string>();
  private readonly api = inject(ContractsApi);
  private readonly dialog = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  protected readonly statusTone = statusTone;
  protected readonly urgencyTone = urgencyTone;
  protected readonly min = Math.min;
  protected readonly contract = signal<ContractDetailDto | null>(null);
  protected readonly report = signal<ConsumptionReportDto | null>(null);
  protected readonly notFound = signal(false);

  protected readonly crumbs = computed<SdBreadcrumb[]>(() => [
    { label: 'Contracts', link: '/products/contracts' },
    { label: this.contract()?.summary.number ?? '…' },
  ]);
  protected readonly coverage = computed(() => {
    const cov = this.contract()?.coverage ?? {};
    const channels = Array.isArray(cov['channels']) ? (cov['channels'] as string[]).join(', ') : 'All channels';
    const parts = [channels || 'All channels'];
    if (cov['onsite'] === true) parts.push('on-site visits');
    if (typeof cov['maxSeverity'] === 'string') parts.push(`up to ${cov['maxSeverity']} severity`);
    if (cov['hardware'] === true) parts.push('hardware');
    return parts.join(' · ');
  });
  protected readonly consumptionChart = computed<EChartsCoreOption>(() => {
    const months = this.report()?.byMonth ?? [];
    return {
      tooltip: { trigger: 'axis' },
      legend: { top: 0, right: 0 },
      grid: { left: 36, right: 12, top: 30, bottom: 24 },
      xAxis: { type: 'category', data: months.map((m) => m.month) },
      yAxis: { type: 'value', name: 'h' },
      series: [
        { name: 'Billable', type: 'bar', stack: 'h', data: months.map((m) => m.billableHours), barMaxWidth: 24 },
        { name: 'Non-billable', type: 'bar', stack: 'h', data: months.map((m) => Math.round((m.hours - m.billableHours) * 100) / 100), barMaxWidth: 24 },
      ],
    };
  });

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => this.load(id));
    });
  }

  private load(id: string): void {
    this.notFound.set(false);
    this.api.get(id).subscribe({
      next: (c) => this.contract.set(c),
      error: (err: unknown) => {
        if (ApiError.from(err).status === 404) this.notFound.set(true);
      },
    });
    this.api.consumption(id).subscribe({ next: (r) => this.report.set(r), error: () => this.report.set(null) });
  }

  protected money(value: number | null, currency: string): string {
    return formatMoney(value, currency);
  }

  protected covers(e: EntitlementDto): string {
    return [e.productName, e.installationName, e.assetName].filter(Boolean).join(' · ') || 'Customer-wide';
  }

  protected edit(): void {
    const c = this.contract();
    if (!c) return;
    this.dialog
      .open<ContractDetailDto, ContractFormData>(ContractFormDialog, { data: { contract: c }, size: 'lg', disableClose: true })
      .closed.subscribe((saved) => {
        if (!saved) return;
        this.contract.set(saved);
        this.toast.success('Contract saved', 'Open tickets of the customer are being re-evaluated.');
      });
  }

  protected async remove(): Promise<void> {
    const c = this.contract();
    if (!c) return;
    const ok = await this.dialog.confirm({
      title: `Remove ${c.summary.number}?`,
      message: 'Contracts referenced by tickets or time entries are cancelled (kept for history); unused ones are deleted.',
      danger: true,
      confirmLabel: 'Remove',
      typeToConfirm: c.summary.number,
    });
    if (!ok) return;
    this.api.delete(c.summary.id).subscribe(() => {
      this.toast.success('Contract removed');
      void this.router.navigate(['/products/contracts']);
    });
  }

  protected editEntitlement(entitlement: EntitlementDto | null): void {
    const c = this.contract();
    if (!c) return;
    this.dialog
      .open<EntitlementDto, EntitlementFormData>(EntitlementFormDialog, { data: { contract: c, entitlement }, size: 'md' })
      .closed.subscribe((saved) => saved && this.load(c.summary.id));
  }

  protected async removeEntitlement(e: EntitlementDto): Promise<void> {
    const c = this.contract();
    if (!c) return;
    if (!(await this.dialog.confirm({ title: `Remove ${e.name}?`, message: 'Entitlements already used by tickets are deactivated instead.', danger: true, confirmLabel: 'Remove' }))) return;
    this.api.deleteEntitlement(c.summary.id, e.id).subscribe(() => this.load(c.summary.id));
  }
}
