import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import {
  CONTRACT_STATUSES,
  CONTRACT_TYPES,
  ContractDetailDto,
  ContractListItemDto,
  ContractRenewalDto,
  ContractsApi,
  MeStore,
  cursorList,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCellDef,
  SdColumn,
  SdCombobox,
  SdDataTable,
  SdDialogService,
  SdEmptyState,
  SdIcon,
  SdInput,
  SdKpiTile,
  SdOption,
  SdPageHeader,
  SdSkeleton,
  ToastService,
} from '@sd/ui';
import { DateOnlyPipe, HumanizePipe, MoneyPipe, formatMoney, humanize, statusTone, urgencyTone } from '../customers/shared/customer-ui';
import { ModuleTabsComponent, PRODUCT_TABS } from '../customers/shared/module-tabs.component';
import { SECTION_STYLES, TABLE_STYLES } from '../customers/shared/styles';
import { ContractFormData, ContractFormDialog } from './contract-form.dialog';

/** /products/contracts — support contracts with type/status/expiry filters. */
@Component({
  selector: 'desk-contracts-page',
  imports: [
    FormsModule,
    SdPageHeader,
    SdButton,
    SdDataTable,
    SdCellDef,
    SdBadge,
    SdCombobox,
    SdIcon,
    SdInput,
    DateOnlyPipe,
    HumanizePipe,
    MoneyPipe,
    ModuleTabsComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Contracts" subtitle="Warranties, AMCs, support subscriptions and prepaid hours that decide support eligibility." icon="file-text">
        @if (canManage) {
          <button sdButton sdPageActions variant="primary" icon="plus" (click)="create()">New contract</button>
        }
      </sd-page-header>
      <desk-module-tabs [tabs]="tabs" ariaLabel="Products & Assets sections" />
      <div class="desk-page__body">
        <div class="toolbar">
          <label class="search">
            <sd-icon name="search" [size]="16" />
            <input sdInput type="search" placeholder="Search number, name, customer or ERP reference" aria-label="Search contracts" [ngModel]="q()" (ngModelChange)="onSearch($event)" />
          </label>
          <sd-combobox class="f" multiple size="sm" [options]="typeOptions" [(value)]="types" placeholder="Type" ariaLabel="Contract type" />
          <sd-combobox class="f" multiple size="sm" [options]="statusOptions" [(value)]="statuses" placeholder="Status" ariaLabel="Contract status" />
          <select sdInput size="sm" class="f" aria-label="Expiry" [ngModel]="expiring()" (ngModelChange)="expiring.set($event)">
            <option [ngValue]="null">Any expiry</option>
            <option [ngValue]="30">Expiring in 30 days</option>
            <option [ngValue]="90">Expiring in 90 days</option>
          </select>
        </div>
        <div class="table-host">
          <sd-data-table
            [columns]="columns"
            [rows]="list.items()"
            storageKey="contracts.list"
            [loading]="list.loading()"
            [loadingMore]="list.loadingMore()"
            [hasMore]="list.hasMore()"
            [total]="list.total()"
            [error]="list.error()?.userMessage ?? null"
            emptyTitle="No contracts match"
            emptyIcon="file-text"
            ariaLabel="Contracts"
            (loadMore)="list.loadMore()"
            (rowClick)="open($event)"
          >
            <ng-template sdCell="number" [sdCellRows]="list.items()" let-row>
              <span class="sd-truncate"><strong>{{ row.number }}</strong>&ngsp;&ngsp;<span class="sd-xs sd-muted">{{ row.name }}</span></span>
            </ng-template>
            <ng-template sdCell="type" [sdCellRows]="list.items()" let-row>{{ row.type | deskHumanize }}</ng-template>
            <ng-template sdCell="status" [sdCellRows]="list.items()" let-row>
              <sd-badge [tone]="statusTone(row.status)" size="sm" dot>{{ row.status }}</sd-badge>
            </ng-template>
            <ng-template sdCell="endDate" [sdCellRows]="list.items()" let-row>
              {{ row.endDate | deskDate }}
              @if (row.status === 'Active' && row.daysToExpiry <= 60) {
                <sd-badge [tone]="urgencyTone(row.daysToExpiry)" size="sm">{{ row.daysToExpiry }} d</sd-badge>
              }
            </ng-template>
            <ng-template sdCell="contractValue" [sdCellRows]="list.items()" let-row>{{ row.contractValue | deskMoney: row.currency }}</ng-template>
            <ng-template sdCell="hours" [sdCellRows]="list.items()" let-row>
              @if (row.prepaidHours) {
                {{ row.consumedHours }} / {{ row.prepaidHours }} h
              } @else {
                <span class="sd-muted">—</span>
              }
            </ng-template>
          </sd-data-table>
        </div>
      </div>
    </div>
  `,
  styles: `
    .toolbar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--sd-space-2);
      margin-bottom: var(--sd-space-3);
    }
    .search {
      position: relative;
      flex: 1 1 260px;
      max-width: 380px;
    }
    .search sd-icon {
      position: absolute;
      left: 10px;
      top: 50%;
      transform: translateY(-50%);
      color: var(--sd-text-3);
    }
    .search input {
      padding-left: 32px;
    }
    .f {
      width: 170px;
    }
    .table-host {
      height: max(420px, calc(100dvh - 330px));
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      overflow: hidden;
      background: var(--sd-surface);
    }
  `,
})
export class ContractsPage {
  private readonly api = inject(ContractsApi);
  private readonly router = inject(Router);
  private readonly dialog = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  protected readonly canManage = inject(MeStore).can('contracts.manage');
  protected readonly tabs = PRODUCT_TABS;
  protected readonly statusTone = statusTone;
  protected readonly urgencyTone = urgencyTone;
  protected readonly typeOptions: SdOption[] = CONTRACT_TYPES.map((t) => ({ value: t, label: humanize(t) }));
  protected readonly statusOptions: SdOption[] = CONTRACT_STATUSES.map((t) => ({ value: t, label: t }));
  protected readonly q = signal('');
  protected readonly types = signal<string[]>([]);
  protected readonly statuses = signal<string[]>([]);
  protected readonly expiring = signal<number | null>(null);
  private timer: ReturnType<typeof setTimeout> | null = null;

  protected readonly columns: SdColumn<ContractListItemDto>[] = [
    { key: 'number', header: 'Contract', flex: 2, minWidth: 220, locked: true },
    { key: 'customerName', header: 'Customer', flex: 2, minWidth: 160 },
    { key: 'type', header: 'Type', width: 140 },
    { key: 'status', header: 'Status', width: 110 },
    { key: 'endDate', header: 'Ends', width: 160 },
    { key: 'contractValue', header: 'Value', width: 130, align: 'end' },
    { key: 'hours', header: 'Prepaid hours', width: 130 },
    { key: 'slaPolicyName', header: 'SLA policy', flex: 1, minWidth: 120, hiddenByDefault: true },
    { key: 'accountManagerName', header: 'Account manager', flex: 1, minWidth: 130, hiddenByDefault: true },
    { key: 'erpReference', header: 'ERP reference', width: 140, hiddenByDefault: true },
  ];

  private readonly query = computed(() => ({
    q: this.q() || null,
    type: this.types().join(',') || null,
    status: this.statuses().join(',') || null,
    expiringWithinDays: this.expiring(),
  }));
  // Latest-ending first, so current contracts lead and history trails.
  protected readonly list = cursorList<ContractListItemDto>((cursor) =>
    this.api.list({ ...this.query(), sort: 'endDate', dir: 'desc', cursor, limit: 50, includeTotal: !cursor }),
  );

  constructor() {
    effect(() => {
      this.query();
      untracked(() => this.list.load());
    });
  }

  protected onSearch(value: string): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => this.q.set(value), 250);
  }

  protected open(row: ContractListItemDto): void {
    void this.router.navigate(['/products/contracts', row.id]);
  }

  protected create(): void {
    this.dialog
      .open<ContractDetailDto, ContractFormData>(ContractFormDialog, { data: {}, size: 'lg', disableClose: true })
      .closed.subscribe((saved) => {
        if (!saved) return;
        this.toast.success('Contract created', saved.summary.number);
        void this.router.navigate(['/products/contracts', saved.summary.id]);
      });
  }
}

/** /products/renewals — contracts expiring soon (and recently expired), grouped by urgency. */
@Component({
  selector: 'desk-renewals-page',
  imports: [FormsModule, RouterLink, SdPageHeader, SdBadge, SdInput, SdKpiTile, SdSkeleton, SdEmptyState, DateOnlyPipe, HumanizePipe, MoneyPipe, ModuleTabsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Renewals" subtitle="Contracts ending soon — account managers are notified 30 and 7 days ahead." icon="history" />
      <desk-module-tabs [tabs]="tabs" ariaLabel="Products & Assets sections" />
      <div class="desk-page__body">
        <div class="toolbar">
          <label class="sd-small" for="renew-days">Horizon</label>
          <select sdInput size="sm" id="renew-days" class="f" [ngModel]="days()" (ngModelChange)="days.set(+$event); load()">
            <option [ngValue]="30">30 days</option>
            <option [ngValue]="90">90 days</option>
            <option [ngValue]="180">6 months</option>
            <option [ngValue]="365">12 months</option>
          </select>
        </div>
        @if (rows() === null) {
          <sd-skeleton [lines]="6" />
        } @else {
          <div class="kpis">
            <sd-kpi-tile label="Expired (last 60 days)" [value]="count('Expired')" icon="circle-x" />
            <sd-kpi-tile label="Critical (≤ 7 days)" [value]="count('Critical')" icon="triangle-alert" />
            <sd-kpi-tile label="Soon (≤ 30 days)" [value]="count('Soon')" icon="clock" />
            <sd-kpi-tile label="Value up for renewal" [value]="renewalValue()" icon="wallet" />
          </div>
          @if (rows()!.length === 0) {
            <sd-empty-state icon="check-check" title="Nothing to renew in this period" />
          } @else {
            <div class="dt-wrap">
              <table class="dt">
                <thead>
                  <tr>
                    <th scope="col">Urgency</th>
                    <th scope="col">Contract</th>
                    <th scope="col">Customer</th>
                    <th scope="col">Type</th>
                    <th scope="col">Ends</th>
                    <th scope="col" class="num">Value</th>
                    <th scope="col">Account manager</th>
                    <th scope="col" class="num">Open tickets</th>
                  </tr>
                </thead>
                <tbody>
                  @for (r of rows(); track r.contract.id) {
                    <tr>
                      <td>
                        <sd-badge [tone]="r.urgency === 'Expired' || r.urgency === 'Critical' ? 'danger' : r.urgency === 'Soon' ? 'warning' : 'info'" size="sm">
                          {{ r.urgency }} · {{ r.daysToExpiry < 0 ? -r.daysToExpiry + ' d ago' : r.daysToExpiry + ' d' }}
                        </sd-badge>
                      </td>
                      <td>
                        <a [routerLink]="['/products/contracts', r.contract.id]">{{ r.contract.number }}</a>
                        @if (r.contract.autoRenew) {
                          <sd-badge tone="info" variant="outline" size="sm">Auto-renew</sd-badge>
                        }
                      </td>
                      <td><a [routerLink]="['/customers', r.contract.customerId]">{{ r.contract.customerName }}</a></td>
                      <td>{{ r.contract.type | deskHumanize }}</td>
                      <td class="nowrap">{{ r.contract.endDate | deskDate }}</td>
                      <td class="num">{{ r.contract.contractValue | deskMoney: r.contract.currency }}</td>
                      <td>{{ r.contract.accountManagerName ?? '—' }}</td>
                      <td class="num">{{ r.openTickets }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        }
      </div>
    </div>
  `,
  styles: [TABLE_STYLES, SECTION_STYLES, `.toolbar { display: flex; align-items: center; gap: var(--sd-space-2); margin-bottom: var(--sd-space-3); } .f { width: 160px; } sd-badge { margin-left: 4px; }`],
})
export class RenewalsPage {
  private readonly api = inject(ContractsApi);
  protected readonly tabs = PRODUCT_TABS;
  protected readonly days = signal(90);
  protected readonly rows = signal<ContractRenewalDto[] | null>(null);
  protected readonly renewalValue = computed(() => {
    const active = (this.rows() ?? []).filter((r) => r.daysToExpiry >= 0);
    return formatMoney(active.reduce((sum, r) => sum + (r.contract.contractValue ?? 0), 0), active[0]?.contract.currency ?? 'INR');
  });

  constructor() {
    this.load();
  }

  protected load(): void {
    this.rows.set(null);
    this.api.renewals(this.days()).subscribe((r) => this.rows.set(r));
  }

  protected count(urgency: string): number {
    return (this.rows() ?? []).filter((r) => r.urgency === urgency).length;
  }
}
