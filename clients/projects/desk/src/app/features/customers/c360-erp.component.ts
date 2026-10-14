import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { RouterLink } from '@angular/router';
import { CustomersApi, ErpApi, ErpCustomerRecordsDto, ErpRecordViewDto, ErpTransaction, Guid, MeStore } from '@sd/core';
import { SdBadge, SdButton, SdCard, SdDrawer, SdEmptyState, SdKpiTile, SdSkeleton, SdTab, SdTabs } from '@sd/ui';
import { DateOnlyPipe, HumanizePipe, MoneyPipe, formatMoney, statusTone } from './shared/customer-ui';
import { SECTION_STYLES, TABLE_STYLES } from './shared/styles';

/** The customer as seen in Wings ERP: master data, outstanding balance, invoices, orders, receipts, AMC contracts and installations. */
@Component({
  selector: 'desk-c360-erp',
  imports: [NgTemplateOutlet, RouterLink, SdCard, SdBadge, SdButton, SdKpiTile, SdSkeleton, SdEmptyState, SdTabs, SdTab, SdDrawer, DateOnlyPipe, MoneyPipe, HumanizePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading()) {
      <sd-skeleton [lines]="8" />
    } @else if (data(); as d) {
      @if (!d.configured) {
        <sd-empty-state icon="plug" title="Wings ERP is not connected" message="An administrator can connect it under Products & Assets → ERP integration." compact />
        @if (me.can('admin.integrations')) {
          <p class="center"><a sdButton size="sm" routerLink="/products/erp">Configure ERP integration</a></p>
        }
      } @else if (d.error && !d.master) {
        <sd-empty-state icon="circle-alert" title="ERP records unavailable" [message]="d.error" compact />
      } @else {
        <div class="kpis">
          <sd-kpi-tile label="Outstanding" [value]="money(d.outstandingAmount, d.currency)" icon="wallet" />
          <sd-kpi-tile label="Invoices (latest)" [value]="d.invoices.length" icon="receipt" />
          <sd-kpi-tile label="Open orders" [value]="openOrders(d)" icon="package" />
          <sd-kpi-tile label="AMC contracts" [value]="d.amcContracts.length" icon="file-text" />
        </div>
        @if (d.master; as m) {
          <sd-card [title]="m.legalName ?? m.name" [subtitle]="'ERP customer ' + m.code + (d.mode === 'simulator' ? ' · simulator' : '')">
            <dl class="meta">
              <dt>GSTIN</dt>
              <dd class="sd-mono">{{ m.gstin ?? '—' }}</dd>
              <dt>PAN</dt>
              <dd class="sd-mono">{{ m.pan ?? '—' }}</dd>
              <dt>Credit limit</dt>
              <dd>{{ m.creditLimit | deskMoney: m.currency }}</dd>
              <dt>Status</dt>
              <dd>
                <sd-badge [tone]="m.isBlocked ? 'danger' : m.isActive ? 'success' : 'neutral'" size="sm">{{ m.isBlocked ? 'Blocked' : m.isActive ? 'Active' : 'Inactive' }}</sd-badge>
              </dd>
              <dt>Branches</dt>
              <dd>{{ branchNames(d) }}</dd>
            </dl>
          </sd-card>
        }
        <sd-tabs [selected]="tab()" (selectedChange)="tab.set($event ?? 'invoices')" ariaLabel="ERP records" variant="pill" class="tabs">
          <sd-tab id="invoices" label="Invoices" [badge]="d.invoices.length">
            <ng-container [ngTemplateOutlet]="docs" [ngTemplateOutletContext]="{ $implicit: d.invoices }" />
          </sd-tab>
          <sd-tab id="orders" label="Orders & deliveries" [badge]="d.orders.length">
            <ng-container [ngTemplateOutlet]="docs" [ngTemplateOutletContext]="{ $implicit: d.orders }" />
          </sd-tab>
          <sd-tab id="receipts" label="Receipts" [badge]="d.receipts.length">
            <ng-container [ngTemplateOutlet]="docs" [ngTemplateOutletContext]="{ $implicit: d.receipts }" />
          </sd-tab>
          <sd-tab id="amc" label="AMC contracts" [badge]="d.amcContracts.length">
            <div class="dt-wrap">
              <table class="dt">
                <thead>
                  <tr>
                    <th scope="col">Number</th>
                    <th scope="col">Name</th>
                    <th scope="col">Period</th>
                    <th scope="col">Status</th>
                    <th scope="col">Products</th>
                    <th scope="col" class="num">Value</th>
                  </tr>
                </thead>
                <tbody>
                  @for (a of d.amcContracts; track a.id) {
                    <tr>
                      <td class="nowrap">{{ a.number }}</td>
                      <td>{{ a.name }}</td>
                      <td class="nowrap sd-small">{{ a.startDate | deskDate }} – {{ a.endDate | deskDate }}</td>
                      <td><sd-badge [tone]="statusTone(a.status)" size="sm" dot>{{ a.status }}</sd-badge></td>
                      <td>{{ a.productCodes.join(', ') }}</td>
                      <td class="num">{{ a.value | deskMoney: a.currency }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          </sd-tab>
          <sd-tab id="installations" label="Installations" [badge]="d.installations.length">
            <div class="dt-wrap">
              <table class="dt">
                <thead>
                  <tr>
                    <th scope="col">Installation</th>
                    <th scope="col">Product</th>
                    <th scope="col">Environment</th>
                    <th scope="col">Deployment</th>
                    <th scope="col" class="num">Users</th>
                  </tr>
                </thead>
                <tbody>
                  @for (i of d.installations; track i.id) {
                    <tr>
                      <td>{{ i.name }} <span class="sd-xs sd-muted">{{ i.installationCode }}</span></td>
                      <td>{{ i.productCode }} {{ i.build ?? i.version }}</td>
                      <td>{{ i.environment }}</td>
                      <td>{{ i.deploymentMode | deskHumanize }}</td>
                      <td class="num">{{ i.userCount ?? '—' }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          </sd-tab>
        </sd-tabs>
      }
    }

    <ng-template #docs let-rows>
      @if (rows.length === 0) {
        <p class="empty-note">No documents.</p>
      } @else {
        <div class="dt-wrap">
          <table class="dt">
            <thead>
              <tr>
                <th scope="col">Number</th>
                <th scope="col">Type</th>
                <th scope="col">Date</th>
                <th scope="col">Status</th>
                <th scope="col" class="num">Total</th>
                <th scope="col" class="num">Balance</th>
              </tr>
            </thead>
            <tbody>
              @for (t of asDocs(rows); track t.id) {
                <tr class="clickable" tabindex="0" (click)="openRecord(t)" (keydown.enter)="openRecord(t)">
                  <td class="nowrap sd-mono">{{ t.number }}</td>
                  <td>{{ t.recordType | deskHumanize }}</td>
                  <td class="nowrap">{{ t.date | deskDate }}</td>
                  <td><sd-badge [tone]="statusTone(t.status)" size="sm" dot>{{ t.status }}</sd-badge></td>
                  <td class="num">{{ t.totalAmount | deskMoney: t.currency : 2 }}</td>
                  <td class="num">{{ t.balanceAmount === null ? '—' : (t.balanceAmount | deskMoney: t.currency : 2) }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </ng-template>

    <sd-drawer [(open)]="drawerOpen" [title]="record()?.record?.number ?? 'ERP record'" width="560px">
      @if (record(); as r) {
        <div class="rec">
          <dl class="meta">
            <dt>Type</dt>
            <dd>{{ r.record.recordType | deskHumanize }} · {{ r.record.system }}</dd>
            <dt>Date</dt>
            <dd>{{ r.record.date | deskDate }}@if (r.record.dueDate) { · due {{ r.record.dueDate | deskDate }} }</dd>
            <dt>Status</dt>
            <dd><sd-badge [tone]="statusTone(r.record.status)" size="sm">{{ r.record.status }}</sd-badge></dd>
            <dt>Place of supply</dt>
            <dd>{{ r.record.placeOfSupply ?? '—' }}</dd>
            @if (r.record.reference) {
              <dt>Reference</dt>
              <dd>{{ r.record.reference }}</dd>
            }
          </dl>
          <div class="dt-wrap lines">
            <table class="dt">
              <thead>
                <tr>
                  <th scope="col">Item</th>
                  <th scope="col" class="num">Qty</th>
                  <th scope="col" class="num">Rate</th>
                  <th scope="col" class="num">Taxable</th>
                  <th scope="col" class="num">GST</th>
                </tr>
              </thead>
              <tbody>
                @for (l of r.record.lines; track l.lineNo) {
                  <tr>
                    <td>{{ l.description }}<div class="sd-xs sd-muted">{{ l.itemCode }}@if (l.hsnSac) { · SAC {{ l.hsnSac }} }</div></td>
                    <td class="num">{{ l.quantity }} {{ l.unit }}</td>
                    <td class="num">{{ l.rate | deskMoney: r.record.currency : 2 }}</td>
                    <td class="num">{{ l.taxableValue | deskMoney: r.record.currency : 2 }}</td>
                    <td class="num">{{ l.taxAmount | deskMoney: r.record.currency : 2 }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          <dl class="meta totals">
            <dt>Taxable value</dt>
            <dd class="sd-num">{{ r.record.taxableAmount | deskMoney: r.record.currency : 2 }}</dd>
            @if (r.record.tax; as tax) {
              @if (tax.igst > 0) {
                <dt>IGST</dt>
                <dd class="sd-num">{{ tax.igst | deskMoney: r.record.currency : 2 }}</dd>
              } @else {
                <dt>CGST</dt>
                <dd class="sd-num">{{ tax.cgst | deskMoney: r.record.currency : 2 }}</dd>
                <dt>SGST</dt>
                <dd class="sd-num">{{ tax.sgst | deskMoney: r.record.currency : 2 }}</dd>
              }
            }
            <dt><strong>Total</strong></dt>
            <dd class="sd-num"><strong>{{ r.record.totalAmount | deskMoney: r.record.currency : 2 }}</strong></dd>
            @if (r.record.balanceAmount !== null) {
              <dt>Balance due</dt>
              <dd class="sd-num">{{ r.record.balanceAmount | deskMoney: r.record.currency : 2 }}</dd>
            }
          </dl>
          @if (r.linkedTickets.length) {
            <h3 class="sub">Tickets about this record</h3>
            <ul class="links">
              @for (t of r.linkedTickets; track t.ticketId) {
                <li>
                  <a [routerLink]="['/tickets', t.ticketId]">{{ t.displayNumber }} {{ t.subject }}</a>
                  <span class="sd-xs sd-muted">{{ t.status }}@if (t.isOrigin) { · raised from this record }</span>
                </li>
              }
            </ul>
          }
        </div>
      }
    </sd-drawer>
  `,
  styles: [
    TABLE_STYLES,
    SECTION_STYLES,
    `
      .center {
        text-align: center;
      }
      .tabs {
        display: block;
        margin-top: var(--sd-space-4);
      }
      .rec {
        display: grid;
        gap: var(--sd-space-4);
      }
      .totals {
        justify-content: end;
      }
      .sub {
        font-size: var(--sd-text-md);
        font-weight: var(--sd-weight-semibold);
      }
      .links {
        display: grid;
        gap: 6px;
        list-style: none;
        padding: 0;
      }
      .links li {
        display: grid;
        gap: 2px;
      }
      .links a {
        color: var(--sd-primary);
      }
    `,
  ],
})
export class C360ErpComponent {
  protected readonly me = inject(MeStore);
  private readonly customers = inject(CustomersApi);
  private readonly erp = inject(ErpApi);
  readonly customerId = input.required<Guid>();

  protected readonly statusTone = statusTone;
  protected readonly data = signal<ErpCustomerRecordsDto | null>(null);
  protected readonly loading = signal(true);
  protected readonly tab = signal('invoices');
  protected readonly drawerOpen = signal(false);
  protected readonly record = signal<ErpRecordViewDto | null>(null);

  constructor() {
    effect(() => {
      const id = this.customerId();
      untracked(() => {
        this.loading.set(true);
        this.customers.erpRecords(id).subscribe({
          next: (d) => {
            this.data.set(d);
            this.loading.set(false);
          },
          error: () => this.loading.set(false),
        });
      });
    });
  }

  protected money(value: number, currency: string): string {
    return formatMoney(value, currency);
  }

  protected openOrders(d: ErpCustomerRecordsDto): number {
    return d.orders.filter((o) => o.recordType === 'SalesOrder' && o.status === 'Open').length;
  }

  protected branchNames(d: ErpCustomerRecordsDto): string {
    return d.master?.branches.map((b) => b.name).join(', ') || '—';
  }

  protected asDocs(rows: unknown): ErpTransaction[] {
    return rows as ErpTransaction[];
  }

  protected openRecord(t: ErpTransaction): void {
    this.record.set({ record: t, customerId: this.customerId(), customerName: null, linkedTickets: [] });
    this.drawerOpen.set(true);
    // Fetch the record through the record endpoint to show the tickets that reference it.
    this.erp.record(t.recordType, t.id, (t.system as 'WingsErp' | 'WingsBooks') ?? 'WingsErp').subscribe((r) => this.record.set(r));
  }
}
