import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiError, Customer360Dto, CustomerDetailDto, CustomersApi, MeStore } from '@sd/core';
import {
  SdBadge,
  SdBreadcrumb,
  SdButton,
  SdCard,
  SdChart,
  SdDialogService,
  SdEmptyState,
  SdIcon,
  SdKpiTile,
  SdPageHeader,
  SdPriorityBadge,
  SdRelativeTimePipe,
  SdDateTimePipe,
  SdSkeleton,
  SdTab,
  SdTabs,
  ToastService,
} from '@sd/ui';
import type { EChartsCoreOption } from 'echarts/core';
import { ActivitiesCalendarComponent } from './activities-calendar.component';
import { C360ContactsComponent } from './c360-contacts.component';
import { C360ContractsComponent } from './c360-contracts.component';
import { C360ErpComponent } from './c360-erp.component';
import { C360LocationsComponent } from './c360-locations.component';
import { C360TicketsComponent } from './c360-tickets.component';
import { CustomerFormData, CustomerFormDialog } from './customer-form.dialog';
import { DateOnlyPipe, HumanizePipe, statusTone, tierTone } from './shared/customer-ui';
import { SECTION_STYLES, TABLE_STYLES } from './shared/styles';

/** /customers/:id — Customer 360: KPIs and trends, tickets, contacts, locations, products, contracts, activities and ERP records. */
@Component({
  selector: 'desk-customer-360-page',
  imports: [
    RouterLink,
    SdPageHeader,
    SdButton,
    SdBadge,
    SdCard,
    SdChart,
    SdKpiTile,
    SdTabs,
    SdTab,
    SdIcon,
    SdSkeleton,
    SdEmptyState,
    SdPriorityBadge,
    SdRelativeTimePipe,
    SdDateTimePipe,
    DateOnlyPipe,
    HumanizePipe,
    C360TicketsComponent,
    C360ContactsComponent,
    C360LocationsComponent,
    C360ContractsComponent,
    C360ErpComponent,
    ActivitiesCalendarComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      @if (notFound()) {
        <sd-empty-state icon="building" title="Customer not found" message="It may have been deleted, or it is not one of your accounts." />
      } @else if (!data()) {
        <div class="desk-page__body loading"><sd-skeleton [lines]="10" /></div>
      } @else {
        @let d = data()!;
        @let c = d.customer;
        <sd-page-header [title]="c.name" [subtitle]="subtitle()" icon="building" [breadcrumbs]="crumbs()">
          @if (c.permissions.edit) {
            <ng-container sdPageActions>
              <button sdButton icon="pencil" (click)="edit()">Edit</button>
              <button sdButton icon="plus" (click)="addSubsidiary()">Subsidiary</button>
            </ng-container>
          }
          @if (c.permissions.delete) {
            <button sdButton sdPageActions variant="ghost" icon="trash" iconOnly aria-label="Delete customer" (click)="remove()"></button>
          }
        </sd-page-header>
        <div class="desk-page__body">
          <div class="badges">
            <sd-badge [tone]="tierTone(c.tier)" icon="award">{{ c.tier }}</sd-badge>
            <sd-badge [tone]="statusTone(c.status)" dot>{{ c.status | deskHumanize }}</sd-badge>
            @if (c.groupName) {
              <sd-badge variant="outline">{{ c.groupName }}</sd-badge>
            }
            @if (c.erpCode) {
              <sd-badge tone="info" variant="outline" icon="plug">ERP {{ c.erpCode }}</sd-badge>
            }
            @if (c.accountManagerName) {
              <span class="sd-small sd-muted"><sd-icon name="user-check" [size]="13" /> {{ c.accountManagerName }}</span>
            }
          </div>

          <sd-tabs [selected]="tab()" (selectedChange)="tab.set($event ?? 'overview')" ariaLabel="Customer 360 sections">
            <sd-tab id="overview" label="Overview" icon="dashboard">
              <div class="kpis">
                <sd-kpi-tile label="Open tickets" [value]="d.kpis.openTickets" icon="ticket" [deltaLabel]="d.kpis.overdueTickets + ' overdue'" />
                <sd-kpi-tile label="Escalated" [value]="d.kpis.escalatedTickets" icon="triangle-alert" />
                <sd-kpi-tile label="SLA breaches (90 d)" [value]="d.kpis.slaBreachesLast90Days" icon="timer" [deltaLabel]="d.kpis.slaBreachesOpen + ' on open tickets'" />
                <sd-kpi-tile label="CSAT (90 d)" [value]="d.kpis.csatAverage90Days === null ? '—' : d.kpis.csatAverage90Days.toFixed(2)" unit="/5" icon="thumbs-up" [deltaLabel]="d.kpis.csatResponses90Days + ' responses'" />
                <sd-kpi-tile label="Avg. resolution" [value]="d.kpis.avgResolutionHours90Days ?? '—'" unit="h" icon="clock" [deltaLabel]="d.kpis.resolvedLast90Days + ' resolved (90 d)'" />
                <sd-kpi-tile label="Tickets (90 d)" [value]="d.kpis.ticketsLast90Days" icon="activity" [sparkline]="sparkline()" />
                <sd-kpi-tile label="Active contracts" [value]="d.kpis.activeContracts" icon="file-text" [deltaLabel]="d.kpis.contractsExpiringSoon ? d.kpis.contractsExpiringSoon + ' expiring ≤ 60 d' : null" />
                <sd-kpi-tile label="Installations" [value]="d.kpis.installations" icon="server" [deltaLabel]="d.kpis.contacts + ' contacts'" />
              </div>

              <div class="charts">
                <sd-card title="Satisfaction trend" subtitle="Average CSAT and responses per month">
                  <sd-chart [options]="csatChart()" height="240px" ariaLabel="CSAT average and number of responses per month for the last 12 months" />
                </sd-card>
                <sd-card title="Ticket volume" subtitle="Created vs resolved per month">
                  <sd-chart [options]="volumeChart()" height="240px" ariaLabel="Tickets created and resolved per month for the last 12 months" />
                </sd-card>
                <sd-card title="Open tickets by priority">
                  <sd-chart [options]="priorityChart()" height="240px" ariaLabel="Open tickets by priority" />
                </sd-card>
              </div>

              <div class="cols">
                <div>
                  <div class="section-head"><h2>Outstanding complaints &amp; escalations</h2></div>
                  @if (d.outstandingComplaints.length === 0) {
                    <p class="empty-note">Nothing outstanding — no escalated, breached, critical or unhappy tickets.</p>
                  } @else {
                    <div class="dt-wrap">
                      <table class="dt">
                        <tbody>
                          @for (t of d.outstandingComplaints; track t.id) {
                            <tr>
                              <td class="nowrap"><a [routerLink]="['/tickets', t.id]">{{ t.displayNumber }}</a></td>
                              <td>
                                <a [routerLink]="['/tickets', t.id]">{{ t.subject }}</a>
                                <div class="reasons">
                                  @for (r of t.reasons; track r) {
                                    <sd-badge tone="danger" variant="outline" size="sm">{{ r }}</sd-badge>
                                  }
                                </div>
                              </td>
                              <td><sd-priority-badge [priority]="t.priority" /></td>
                              <td class="sd-small">{{ t.assigneeName ?? 'Unassigned' }}</td>
                            </tr>
                          }
                        </tbody>
                      </table>
                    </div>
                  }

                  <div class="section-head"><h2>Recent conversations</h2></div>
                  @if (d.recentConversations.length === 0) {
                    <p class="empty-note">No conversations you can see yet.</p>
                  } @else {
                    <ul class="convo">
                      @for (m of d.recentConversations; track m.threadId) {
                        <li [class.out]="m.direction === 'Outbound'">
                          <div class="convo-head">
                            <sd-icon [name]="m.direction === 'Outbound' ? 'send' : 'message-circle'" [size]="14" />
                            <strong>{{ m.authorName ?? (m.direction === 'Outbound' ? 'Agent' : 'Customer') }}</strong>
                            <span class="sd-xs sd-muted">{{ m.channel }} · {{ m.createdAt | sdRelativeTime }}</span>
                            <span class="sd-spacer"></span>
                            <a class="sd-xs" [routerLink]="['/tickets', m.ticketId]">{{ m.displayNumber }}</a>
                          </div>
                          <p class="sd-small">{{ m.excerpt }}</p>
                        </li>
                      }
                    </ul>
                  }
                </div>
                <div>
                  <sd-card title="Profile">
                    <dl class="meta">
                      <dt>Account manager</dt>
                      <dd>{{ c.accountManagerName ?? 'Unassigned' }}</dd>
                      <dt>Industry</dt>
                      <dd>{{ c.industry ?? '—' }}@if (c.category) { · {{ c.category }} }</dd>
                      <dt>Region</dt>
                      <dd>{{ c.serviceRegion ?? '—' }}</dd>
                      <dt>Contact</dt>
                      <dd>{{ c.email ?? '—' }}<br />{{ c.phone ?? '' }}</dd>
                      @if (c.website) {
                        <dt>Website</dt>
                        <dd><a [href]="c.website" target="_blank" rel="noopener noreferrer">{{ c.website }}</a></dd>
                      }
                      <dt>Domains</dt>
                      <dd>{{ c.domains.join(', ') || '—' }}</dd>
                      <dt>Prefers</dt>
                      <dd>{{ prefs() }}</dd>
                      <dt>Language</dt>
                      <dd>{{ c.preferredLanguage }} · {{ c.timeZone ?? '—' }}</dd>
                      @for (f of customFieldEntries(); track f.key) {
                        <dt>{{ f.key }}</dt>
                        <dd>{{ f.value }}</dd>
                      }
                      @if (c.erpSyncedAt) {
                        <dt>ERP sync</dt>
                        <dd>{{ c.erpSyncedAt | sdRelativeTime }}</dd>
                      }
                    </dl>
                  </sd-card>
                  <sd-card title="Hierarchy" class="mt">
                    @if (c.ancestors.length) {
                      <p class="sd-small">
                        Part of
                        @for (a of reversedAncestors(); track a.id; let last = $last) {
                          <a [routerLink]="['/customers', a.id]">{{ a.name }}</a>@if (!last) { › }
                        }
                      </p>
                    }
                    @if (c.children.length) {
                      <ul class="children">
                        @for (ch of c.children; track ch.id) {
                          <li>
                            <a [routerLink]="['/customers', ch.id]">{{ ch.name }}</a>
                            <span class="sd-xs sd-muted">{{ ch.tier }} · {{ ch.openTickets }} open</span>
                          </li>
                        }
                      </ul>
                    } @else if (!c.ancestors.length) {
                      <p class="empty-note">No parent or subsidiaries.</p>
                    }
                    @if (c.rollup.descendants > 0) {
                      <p class="sd-xs sd-muted rollup">
                        Including subsidiaries: {{ c.rollup.openTickets }} open tickets · {{ c.rollup.contacts }} contacts · {{ c.rollup.locations }} locations ·
                        {{ c.rollup.installations }} installations · {{ c.rollup.activeContracts }} active contracts
                      </p>
                    }
                  </sd-card>
                  <sd-card title="Upcoming activities" class="mt">
                    @if (d.upcomingActivities.length === 0) {
                      <p class="empty-note">Nothing scheduled.</p>
                    } @else {
                      <ul class="acts">
                        @for (a of d.upcomingActivities.slice(0, 6); track a.id) {
                          <li>
                            <span class="sd-small">{{ a.title }}</span>
                            <span class="sd-xs sd-muted">{{ a.scheduledStart | sdDateTime: 'datetime' : 'short' }} · {{ a.type | deskHumanize }} · {{ a.status | deskHumanize }}</span>
                          </li>
                        }
                      </ul>
                    }
                  </sd-card>
                </div>
              </div>
            </sd-tab>

            <sd-tab id="tickets" label="Tickets" icon="ticket" [badge]="d.kpis.openTickets || null">
              <desk-c360-tickets [customerId]="c.id" />
            </sd-tab>

            <sd-tab id="contacts" label="Contacts" icon="users" [badge]="d.kpis.contacts">
              <desk-c360-contacts [customerId]="c.id" [customerName]="c.name" (changed)="reload()" />
            </sd-tab>

            <sd-tab id="locations" label="Locations" icon="map-pin" [badge]="d.locations.length">
              <desk-c360-locations [customerId]="c.id" />
            </sd-tab>

            @if (c.permissions.viewProducts) {
              <sd-tab id="products" label="Installations & assets" icon="server" [badge]="d.installations.length">
                <div class="section-head first"><h2>Installations</h2></div>
                @if (d.installations.length === 0) {
                  <p class="empty-note">No installations recorded.</p>
                } @else {
                  <div class="dt-wrap">
                    <table class="dt">
                      <thead>
                        <tr>
                          <th scope="col">Installation</th>
                          <th scope="col">Product / version</th>
                          <th scope="col">Environment</th>
                          <th scope="col">Deployment</th>
                          <th scope="col">Location</th>
                          <th scope="col" class="num">Users</th>
                          <th scope="col" class="num">Open</th>
                        </tr>
                      </thead>
                      <tbody>
                        @for (i of d.installations; track i.id) {
                          <tr>
                            <td>
                              <a [routerLink]="['/products/installations']" [queryParams]="{ id: i.id }">{{ i.name }}</a>
                              <div class="sd-xs sd-muted">{{ i.installationCode }}</div>
                            </td>
                            <td>
                              {{ i.productName }} {{ i.release ?? i.version }}
                              @if (i.versionEndOfSupport) {
                                <sd-badge tone="danger" size="sm">End of support</sd-badge>
                              }
                            </td>
                            <td>{{ i.environment }}</td>
                            <td>{{ i.deploymentMode | deskHumanize }}</td>
                            <td>{{ i.locationName ?? '—' }}</td>
                            <td class="num">{{ i.userCount ?? '—' }}</td>
                            <td class="num">{{ i.openTickets }}</td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  </div>
                }
                <div class="section-head"><h2>Licences</h2></div>
                @if (d.licences.length === 0) {
                  <p class="empty-note">No licences recorded.</p>
                } @else {
                  <div class="dt-wrap">
                    <table class="dt">
                      <thead>
                        <tr>
                          <th scope="col">Key</th>
                          <th scope="col">Product</th>
                          <th scope="col">Edition</th>
                          <th scope="col" class="num">Seats</th>
                          <th scope="col">Expires</th>
                          <th scope="col">Status</th>
                        </tr>
                      </thead>
                      <tbody>
                        @for (l of d.licences; track l.id) {
                          <tr>
                            <td class="sd-mono">{{ l.keyMasked }}</td>
                            <td>{{ l.productName }} <span class="sd-xs sd-muted">{{ l.installationName }}</span></td>
                            <td>{{ l.edition ?? '—' }}</td>
                            <td class="num">{{ l.seats }}</td>
                            <td class="nowrap">{{ l.expiresOn | deskDate }}</td>
                            <td><sd-badge [tone]="statusTone(l.status)" size="sm" dot>{{ l.status }}</sd-badge></td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  </div>
                }
                <div class="section-head"><h2>Assets</h2></div>
                @if (d.assets.length === 0) {
                  <p class="empty-note">No assets recorded.</p>
                } @else {
                  <div class="dt-wrap">
                    <table class="dt">
                      <thead>
                        <tr>
                          <th scope="col">Asset</th>
                          <th scope="col">Serial</th>
                          <th scope="col">Installation</th>
                          <th scope="col">Warranty</th>
                          <th scope="col">Status</th>
                        </tr>
                      </thead>
                      <tbody>
                        @for (a of d.assets; track a.id) {
                          <tr>
                            <td>{{ a.name }}</td>
                            <td class="sd-mono">{{ a.serialNumber ?? '—' }}</td>
                            <td>{{ a.installationName ?? '—' }}</td>
                            <td>
                              <sd-badge [tone]="a.underWarranty ? 'success' : 'neutral'" size="sm">{{ a.underWarranty ? 'Until ' + (a.warrantyEndsOn | deskDate) : 'Expired' }}</sd-badge>
                            </td>
                            <td>{{ a.status | deskHumanize }}</td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  </div>
                }
              </sd-tab>
            }

            @if (c.permissions.viewContracts) {
              <sd-tab id="contracts" label="Contracts" icon="file-text" [badge]="d.kpis.activeContracts">
                <desk-c360-contracts
                  [customerId]="c.id"
                  [customerName]="c.name"
                  [contracts]="d.contracts"
                  [entitlements]="d.entitlements"
                  [installations]="d.installations"
                  (changed)="reload()"
                />
              </sd-tab>
            }

            <sd-tab id="activities" label="Activities" icon="calendar" [badge]="d.kpis.upcomingActivities || null">
              <desk-activities-calendar [customerId]="c.id" [customerName]="c.name" />
            </sd-tab>

            <sd-tab id="erp" label="ERP records" icon="plug">
              <desk-c360-erp [customerId]="c.id" />
            </sd-tab>
          </sd-tabs>
        </div>
      }
    </div>
  `,
  styles: [
    TABLE_STYLES,
    SECTION_STYLES,
    `
      .loading {
        padding-top: var(--sd-space-6);
      }
      .badges {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sd-space-2);
        margin-bottom: var(--sd-space-4);
      }
      sd-tabs {
        display: block;
      }
      .charts {
        display: grid;
        gap: var(--sd-space-4);
        grid-template-columns: repeat(auto-fill, minmax(min(100%, 360px), 1fr));
      }
      .cols {
        display: grid;
        gap: var(--sd-space-5);
        grid-template-columns: minmax(0, 2fr) minmax(0, 1fr);
      }
      @media (max-width: 1100px) {
        .cols {
          grid-template-columns: 1fr;
        }
      }
      .mt {
        margin-top: var(--sd-space-4);
      }
      .first {
        margin-top: 0;
      }
      .reasons {
        display: flex;
        flex-wrap: wrap;
        gap: 4px;
        margin-top: 4px;
      }
      .convo {
        display: grid;
        gap: var(--sd-space-2);
        list-style: none;
        padding: 0;
      }
      .convo li {
        border: 1px solid var(--sd-border);
        border-left: 3px solid var(--sd-accent);
        border-radius: var(--sd-radius-md);
        padding: var(--sd-space-2) var(--sd-space-3);
        background: var(--sd-surface);
      }
      .convo li.out {
        border-left-color: var(--sd-primary);
      }
      .convo-head {
        display: flex;
        align-items: center;
        gap: 6px;
        margin-bottom: 4px;
      }
      .convo a,
      .meta a,
      .children a,
      .sd-small a {
        color: var(--sd-primary);
        text-decoration: none;
      }
      .children,
      .acts {
        display: grid;
        gap: 6px;
        list-style: none;
        padding: 0;
      }
      .children li,
      .acts li {
        display: grid;
      }
      .rollup {
        margin-top: var(--sd-space-3);
      }
    `,
  ],
})
export class Customer360Page {
  readonly id = input.required<string>();
  protected readonly me = inject(MeStore);
  private readonly api = inject(CustomersApi);
  private readonly dialog = inject(SdDialogService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  protected readonly tierTone = tierTone;
  protected readonly statusTone = statusTone;
  protected readonly data = signal<Customer360Dto | null>(null);
  protected readonly notFound = signal(false);
  protected readonly tab = signal('overview');

  protected readonly subtitle = computed(() => {
    const c = this.data()?.customer;
    if (!c) return null;
    return [c.code, c.industry, c.serviceRegion ? `${c.serviceRegion} region` : null].filter(Boolean).join(' · ') || null;
  });
  protected readonly crumbs = computed<SdBreadcrumb[]>(() => {
    const c = this.data()?.customer;
    const list: SdBreadcrumb[] = [{ label: 'Customers', link: '/customers' }];
    for (const a of [...(c?.ancestors ?? [])].reverse()) list.push({ label: a.name, link: ['/customers', a.id] });
    list.push({ label: c?.name ?? '…' });
    return list;
  });
  protected readonly reversedAncestors = computed(() => [...(this.data()?.customer.ancestors ?? [])].reverse());
  protected readonly prefs = computed(() => {
    const p = this.data()?.customer.communicationPreferences;
    if (!p) return '—';
    const on = [p.email && 'E-mail', p.phone && 'Phone', p.sms && 'SMS', p.whatsApp && 'WhatsApp'].filter(Boolean).join(', ');
    return p.preferredChannel ? `${p.preferredChannel} (also ${on || 'none'})` : on || '—';
  });
  protected readonly customFieldEntries = computed(() =>
    Object.entries(this.data()?.customer.customFields ?? {}).map(([key, value]) => ({ key: key.replace(/_/g, ' '), value: Array.isArray(value) ? value.join(', ') : String(value) })),
  );
  protected readonly sparkline = computed(() => (this.data()?.ticketTrend ?? []).map((p) => p.created));

  protected readonly csatChart = computed<EChartsCoreOption>(() => {
    const t = this.data()?.csatTrend ?? [];
    return {
      tooltip: { trigger: 'axis' },
      legend: { top: 0, right: 0 },
      grid: { left: 36, right: 36, top: 30, bottom: 24 },
      xAxis: { type: 'category', data: t.map((p) => monthLabel(p.month)) },
      yAxis: [
        { type: 'value', min: 1, max: 5, name: 'CSAT' },
        { type: 'value', name: 'Responses', splitLine: { show: false }, minInterval: 1 },
      ],
      series: [
        { name: 'Responses', type: 'bar', yAxisIndex: 1, data: t.map((p) => p.responses), barMaxWidth: 18, itemStyle: { opacity: 0.45 } },
        { name: 'Average CSAT', type: 'line', smooth: true, connectNulls: true, data: t.map((p) => p.average), symbolSize: 6 },
      ],
    };
  });

  protected readonly volumeChart = computed<EChartsCoreOption>(() => {
    const t = this.data()?.ticketTrend ?? [];
    return {
      tooltip: { trigger: 'axis' },
      legend: { top: 0, right: 0 },
      grid: { left: 32, right: 12, top: 30, bottom: 24 },
      xAxis: { type: 'category', data: t.map((p) => monthLabel(p.month)) },
      yAxis: { type: 'value', minInterval: 1 },
      series: [
        { name: 'Created', type: 'bar', data: t.map((p) => p.created), barMaxWidth: 16 },
        { name: 'Resolved', type: 'line', smooth: true, data: t.map((p) => p.resolved) },
      ],
    };
  });

  protected readonly priorityChart = computed<EChartsCoreOption>(() => {
    const p = this.data()?.openByPriority ?? [];
    const colors: Record<string, string> = {
      Critical: 'var(--sd-priority-critical)',
      High: 'var(--sd-priority-high)',
      Medium: 'var(--sd-priority-medium)',
      Low: 'var(--sd-priority-low)',
    };
    const css = getComputedStyle(document.documentElement);
    const resolve = (v: string) => css.getPropertyValue(v.slice(4, -1)).trim() || undefined;
    return {
      tooltip: { trigger: 'item' },
      grid: { left: 70, right: 16, top: 8, bottom: 24 },
      xAxis: { type: 'value', minInterval: 1 },
      yAxis: { type: 'category', data: p.map((x) => x.label).reverse() },
      series: [
        {
          name: 'Open tickets',
          type: 'bar',
          barMaxWidth: 18,
          data: p
            .map((x) => ({ value: x.count, itemStyle: { color: resolve(colors[x.key] ?? 'var(--sd-chart-1)') } }))
            .reverse(),
        },
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
    this.api.get360(id).subscribe({
      next: (d) => this.data.set(d),
      error: (err: unknown) => {
        if (ApiError.from(err).status === 404) this.notFound.set(true);
      },
    });
  }

  protected reload(): void {
    this.load(this.id());
  }

  protected edit(): void {
    const c = this.data()?.customer;
    if (!c) return;
    this.dialog
      .open<CustomerDetailDto, CustomerFormData>(CustomerFormDialog, { data: { customer: c }, size: 'lg', disableClose: true })
      .closed.subscribe((saved) => {
        if (!saved) return;
        this.toast.success('Customer updated');
        this.reload();
      });
  }

  protected addSubsidiary(): void {
    const c = this.data()?.customer;
    if (!c) return;
    this.dialog
      .open<CustomerDetailDto, CustomerFormData>(CustomerFormDialog, { data: { parent: { id: c.id, name: c.name } }, size: 'lg', disableClose: true })
      .closed.subscribe((saved) => {
        if (!saved) return;
        this.toast.success('Subsidiary created', saved.name);
        this.reload();
      });
  }

  protected async remove(): Promise<void> {
    const c = this.data()?.customer;
    if (!c) return;
    const ok = await this.dialog.confirm({
      title: `Delete ${c.name}?`,
      message: 'The customer is hidden from lists; its tickets, contacts and history are kept and it can be restored.',
      danger: true,
      confirmLabel: 'Delete customer',
      typeToConfirm: c.code ?? undefined,
    });
    if (!ok) return;
    this.api.delete(c.id).subscribe(() => {
      this.toast.success('Customer deleted', c.name);
      void this.router.navigate(['/customers']);
    });
  }
}

function monthLabel(month: string): string {
  const [y, m] = month.split('-').map(Number);
  return new Date(Date.UTC(y, m - 1, 1)).toLocaleDateString('en-GB', { month: 'short', year: '2-digit', timeZone: 'UTC' });
}
