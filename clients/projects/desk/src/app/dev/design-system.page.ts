import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { FilterFieldDef, FilterGroup, ThemeService, emptyFilterGroup } from '@sd/core';
import {
  CommandPaletteService,
  SdAvatar,
  SdBadge,
  SdButton,
  SdCalendar,
  SdCalendarEvent,
  SdCard,
  SdCellDef,
  SdChart,
  SdCheckbox,
  SdChipsInput,
  SdColumn,
  SdCombobox,
  SdDataTable,
  SdDateInput,
  SdDialogService,
  SdDrawer,
  SdDropdownMenu,
  SdDurationPipe,
  SdEmptyState,
  SdFileSizePipe,
  SdFilterBuilder,
  SdFormField,
  SdInput,
  SdJsonViewer,
  SdKpiTile,
  SdLogo,
  SdMentionItem,
  SdMenuItem,
  SdOption,
  SdPageHeader,
  SdPriorityBadge,
  SdQrCode,
  SdRadio,
  SdRadioGroup,
  SdRelativeTimePipe,
  SdRichTextEditor,
  SdSkeleton,
  SdSlaCountdown,
  SdSort,
  SdSplitLayout,
  SdStatusPill,
  SdTab,
  SdTabs,
  SdTimeline,
  SdTimelineItem,
  SdToggle,
  SdTooltip,
  ToastService,
} from '@sd/ui';
import type { EChartsCoreOption } from 'echarts/core';

interface DemoRow {
  id: string;
  number: string;
  subject: string;
  priority: string;
  status: string;
  color: string;
  assignee: string;
  dueAt: string;
  slaState: string;
}

const PRIORITIES = ['Low', 'Medium', 'High', 'Critical'];
const STATUSES: [string, string][] = [
  ['Open', '#3b82f6'],
  ['In progress', '#8b5cf6'],
  ['Waiting on customer', '#f59e0b'],
  ['Resolved', '#10b981'],
];
const PEOPLE = ['Asha Rao', 'Marcus Lee', 'Priya Nair', 'Tom Becker', 'Lina Haddad', 'Diego Alvarez'];

function demoRows(start: number, count: number): DemoRow[] {
  const now = Date.now();
  return Array.from({ length: count }, (_, k) => {
    const i = start + k;
    const [status, color] = STATUSES[i % STATUSES.length];
    return {
      id: `row-${i}`,
      number: `SD-${1000 + i}`,
      subject: ['Cannot export invoices to PDF', 'Login loop after password reset', 'Webhook retries failing', 'Request: bulk user import'][i % 4],
      priority: PRIORITIES[(i * 7) % 4],
      status,
      color,
      assignee: PEOPLE[i % PEOPLE.length],
      dueAt: new Date(now + ((i % 9) - 3) * 37 * 60_000).toISOString(),
      slaState: ['OnTrack', 'AtRisk', 'Breached', 'Paused', 'Met'][i % 5],
    };
  });
}

/**
 * Developer reference for @sd/ui (route /design-system, dev builds only). Uses locally generated sample
 * values purely to exercise the components — feature screens always talk to the real API.
 */
@Component({
  selector: 'desk-design-system-page',
  imports: [
    FormsModule,
    SdPageHeader,
    SdLogo,
    SdButton,
    SdBadge,
    SdPriorityBadge,
    SdStatusPill,
    SdAvatar,
    SdCard,
    SdTabs,
    SdTab,
    SdDropdownMenu,
    SdTooltip,
    SdDrawer,
    SdFormField,
    SdInput,
    SdCombobox,
    SdDateInput,
    SdCheckbox,
    SdToggle,
    SdRadioGroup,
    SdRadio,
    SdChipsInput,
    SdDataTable,
    SdCellDef,
    SdFilterBuilder,
    SdTimeline,
    SdRichTextEditor,
    SdChart,
    SdKpiTile,
    SdCalendar,
    SdEmptyState,
    SdSkeleton,
    SdSplitLayout,
    SdSlaCountdown,
    SdJsonViewer,
    SdQrCode,
    SdRelativeTimePipe,
    SdDurationPipe,
    SdFileSizePipe,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <sd-page-header title="Design system" subtitle="@sd/ui components — developer reference (dev builds only)" icon="layers">
        <button sdPageActions sdButton icon="sun-moon" (click)="theme.toggle()">Toggle theme</button>
        <button sdPageActions sdButton icon="command" (click)="palette.open()">Command palette</button>
      </sd-page-header>

      <div class="grid">
        <sd-card title="Brand & buttons">
          <div class="row"><sd-logo wordmark /></div>
          <div class="row">
            <button sdButton variant="primary" icon="plus">Primary</button>
            <button sdButton>Secondary</button>
            <button sdButton variant="subtle">Subtle</button>
            <button sdButton variant="ghost">Ghost</button>
            <button sdButton variant="danger" icon="trash">Danger</button>
            <button sdButton variant="link">Link</button>
          </div>
          <div class="row">
            <button sdButton size="sm">Small</button>
            <button sdButton size="lg" variant="primary" [loading]="true">Saving</button>
            <button sdButton icon="settings" iconOnly aria-label="Settings" sdTooltip="Icon-only button with tooltip"></button>
            <button sdButton [disabled]="true">Disabled</button>
            <sd-dropdown-menu label="Actions" icon="ellipsis" [items]="menuItems" (select)="toast.info('Menu: ' + $event.label)" />
          </div>
        </sd-card>

        <sd-card title="Badges, avatars & SLA">
          <div class="row">
            <sd-badge>Neutral</sd-badge>
            <sd-badge tone="primary">Primary</sd-badge>
            <sd-badge tone="success" dot>Resolved</sd-badge>
            <sd-badge tone="warning" icon="clock">Waiting</sd-badge>
            <sd-badge tone="danger" variant="solid">Escalated</sd-badge>
            <sd-badge tone="info" variant="outline">Portal</sd-badge>
          </div>
          <div class="row">
            @for (p of priorities; track p) {
              <sd-priority-badge [priority]="p" />
            }
            <sd-status-pill label="In progress" color="#8b5cf6" />
          </div>
          <div class="row">
            <sd-avatar name="Asha Rao" presence="Online" />
            <sd-avatar name="Marcus Lee" presence="Away" size="lg" />
            <sd-avatar name="Priya Nair" presence="Busy" size="sm" />
            <sd-avatar name="Tom Becker" presence="Offline" size="xl" />
          </div>
          <div class="row">
            <sd-sla-countdown [dueAt]="soon" state="OnTrack" label="Response" />
            <sd-sla-countdown [dueAt]="verySoon" state="OnTrack" />
            <sd-sla-countdown [dueAt]="past" state="Breached" />
            <sd-sla-countdown [dueAt]="null" state="Paused" />
            <sd-sla-countdown [dueAt]="null" state="Met" />
          </div>
          <p class="sd-small sd-muted">{{ past | sdRelativeTime }} · {{ 95 | sdDuration }} · {{ 1536000 | sdFileSize }}</p>
        </sd-card>

        <sd-card title="KPI tiles">
          <div class="kpis">
            <sd-kpi-tile label="Open tickets" [value]="412" [delta]="-6.2" deltaLabel="vs last week" [positiveIsGood]="false" icon="ticket" [sparkline]="spark" interactive (activate)="toast.info('Drill into open tickets')" />
            <sd-kpi-tile label="First response" value="38" unit="min" [delta]="12" deltaLabel="slower" [positiveIsGood]="false" icon="timer" [sparkline]="spark2" />
            <sd-kpi-tile label="CSAT" value="4.6" unit="/5" [delta]="3.1" icon="star" />
            <sd-kpi-tile label="Loading" [loading]="true" />
          </div>
        </sd-card>

        <sd-card title="Feedback & overlays">
          <div class="row">
            <button sdButton (click)="toast.success('Saved', 'Ticket SD-1042 updated')">Success toast</button>
            <button sdButton (click)="toast.error('Could not send', 'The mail server rejected the message')">Error toast</button>
            <button sdButton (click)="confirmDemo()">Confirm dialog</button>
            <button sdButton (click)="drawerOpen.set(true)">Drawer</button>
          </div>
          <sd-drawer [(open)]="drawerOpen" title="Filters">
            <p>Side sheet content. Escape or the backdrop closes it.</p>
            <sd-form-field label="Search"><input sdInput /></sd-form-field>
          </sd-drawer>
          <sd-tabs [(selected)]="tab" variant="pill" ariaLabel="Demo tabs">
            <sd-tab id="one" label="Overview" [badge]="3"><p>Tab one content.</p></sd-tab>
            <sd-tab id="two" label="Activity"><sd-skeleton [lines]="3" /></sd-tab>
            <sd-tab id="three" label="Disabled" [disabled]="true"><p>Never shown.</p></sd-tab>
          </sd-tabs>
        </sd-card>

        <sd-card title="Form controls" class="wide">
          <div class="form">
            <sd-form-field label="Subject" hint="Shown to the customer" required>
              <input sdInput [(ngModel)]="subject" />
            </sd-form-field>
            <sd-form-field label="Minutes" error="Must be at least 1">
              <input sdInput type="number" [(ngModel)]="minutes" />
            </sd-form-field>
            <sd-form-field label="Native select">
              <select sdInput [(ngModel)]="nativeSelect">
                @for (p of priorities; track p) {
                  <option [value]="p">{{ p }}</option>
                }
              </select>
            </sd-form-field>
            <sd-form-field label="Assignee (combobox)">
              <sd-combobox [options]="agentOptions" [(value)]="assignee" placeholder="Unassigned" />
            </sd-form-field>
            <sd-form-field label="Teams (multi-select)">
              <sd-combobox multiple [options]="teamOptions" [(value)]="teams" />
            </sd-form-field>
            <sd-form-field label="Tags (chips)">
              <sd-chips-input [(value)]="tags" [suggestions]="tagSuggestions" />
            </sd-form-field>
            <sd-form-field label="Due date"><sd-date-input [(value)]="dueDate" /></sd-form-field>
            <sd-form-field label="Remind at"><sd-date-input type="datetime" [(value)]="remindAt" /></sd-form-field>
            <sd-form-field label="Notes" class="span2"><textarea sdInput [(ngModel)]="notes"></textarea></sd-form-field>
            <div class="checks">
              <sd-checkbox [(value)]="check">Notify customer</sd-checkbox>
              <sd-toggle [(value)]="toggle">Auto-assign</sd-toggle>
              <sd-radio-group [(value)]="radio" direction="row" ariaLabel="Channel">
                <sd-radio value="email">E-mail</sd-radio>
                <sd-radio value="phone">Phone</sd-radio>
                <sd-radio value="chat" description="Live chat only">Chat</sd-radio>
              </sd-radio-group>
            </div>
          </div>
          <p class="sd-small sd-muted">assignee={{ assignee() }} · teams={{ teams() }} · tags={{ tags() }} · due={{ dueDate() }} · remind={{ remindAt() }}</p>
        </sd-card>

        <sd-card title="Data table (virtual scroll, selection, column chooser, load more)" class="wide" padding="none">
          <div class="table-host">
            <sd-data-table
              [columns]="columns"
              [rows]="rows()"
              storageKey="dev.demo"
              selectable
              [(selection)]="selection"
              [(sort)]="sort"
              [hasMore]="rows().length < 2000"
              [loadingMore]="loadingMore()"
              (loadMore)="loadMore()"
              (rowClick)="toast.info('Open ' + $event.number)"
              ariaLabel="Demo tickets"
            >
              <ng-template sdCell="priority" [sdCellRows]="rows()" let-row><sd-priority-badge [priority]="row.priority" /></ng-template>
              <ng-template sdCell="status" [sdCellRows]="rows()" let-row><sd-status-pill [label]="row.status" [color]="row.color" /></ng-template>
              <ng-template sdCell="assignee" [sdCellRows]="rows()" let-row>
                <sd-avatar [name]="row.assignee" size="xs" /><span class="sd-truncate">{{ row.assignee }}</span>
              </ng-template>
              <ng-template sdCell="due" [sdCellRows]="rows()" let-row><sd-sla-countdown [dueAt]="row.dueAt" [state]="row.slaState" compact /></ng-template>
            </sd-data-table>
          </div>
          <p class="sd-small sd-muted pad">{{ selection().length }} selected · sort {{ sort()?.key ?? 'none' }} {{ sort()?.dir ?? '' }}</p>
        </sd-card>

        <sd-card title="Filter builder" class="wide">
          <sd-filter-builder [fields]="filterFields" [(value)]="filter" />
          <sd-json-viewer [value]="filter()" [expandDepth]="3" class="json" />
        </sd-card>

        <sd-card title="Timeline">
          <sd-timeline [items]="timeline" />
        </sd-card>

        <sd-card title="Rich text editor">
          <sd-rich-text-editor [(value)]="html" [mentionSource]="mentionSource" placeholder="Write a reply… type @ to mention" (submitted)="toast.info('Mod+Enter submit')" />
          <sd-json-viewer [value]="{ html: html() }" class="json" />
        </sd-card>

        <sd-card title="Chart (click a bar)">
          <sd-chart [options]="chart()" height="260px" ariaLabel="Tickets created per weekday" (chartClick)="toast.info('Drill: ' + $event.name)" />
        </sd-card>

        <sd-card title="Calendar" class="wide">
          <sd-calendar [events]="events" (eventClick)="toast.info($event.title)" />
        </sd-card>

        <sd-card title="Split layout" class="wide" padding="none">
          <div class="split-host">
            <sd-split-layout storageKey="dev.demo">
              <div sdSplitLeft class="pane">List panel</div>
              <div sdSplitMain class="pane main">Conversation panel</div>
              <div sdSplitRight class="pane">Context panel</div>
            </sd-split-layout>
          </div>
        </sd-card>

        <sd-card title="Empty state, skeleton & QR">
          <sd-empty-state icon="inbox" title="No tickets match" message="Try clearing a filter." compact>
            <button sdButton size="sm">Clear filters</button>
          </sd-empty-state>
          <sd-skeleton [lines]="3" />
          <div class="row qr"><sd-qr-code value="otpauth://totp/SupportDesk:asha@example.com?secret=JBSWY3DPEHPK3PXP&issuer=SupportDesk" [size]="120" /></div>
        </sd-card>
      </div>
    </div>
  `,
  styles: `
    :host {
      display: block;
      height: 100dvh;
      overflow: auto;
      background: var(--sd-bg);
    }
    .grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 520px), 1fr));
      gap: 16px;
      padding: 0 24px 48px;
    }
    .wide {
      grid-column: 1 / -1;
    }
    .row {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      margin-bottom: 12px;
    }
    .kpis {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
      gap: 12px;
    }
    .form {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 260px), 1fr));
      gap: 14px;
      margin-bottom: 12px;
    }
    .span2 {
      grid-column: span 2;
    }
    .checks {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 18px;
      grid-column: 1 / -1;
    }
    .table-host {
      height: 420px;
      padding: 12px;
    }
    .pad {
      padding: 0 16px 12px;
    }
    .json {
      margin-top: 12px;
      max-height: 260px;
    }
    .split-host {
      height: 260px;
    }
    .pane {
      padding: 48px 16px 16px;
      color: var(--sd-text-2);
    }
    .main {
      background: var(--sd-bg);
    }
    .qr {
      margin-top: 12px;
    }
  `,
})
export class DesignSystemPage {
  protected readonly theme = inject(ThemeService);
  protected readonly palette = inject(CommandPaletteService);
  protected readonly toast = inject(ToastService);
  private readonly dialogs = inject(SdDialogService);

  protected readonly priorities = PRIORITIES;
  protected readonly soon = new Date(Date.now() + 65 * 60_000).toISOString();
  protected readonly verySoon = new Date(Date.now() + 12 * 60_000).toISOString();
  protected readonly past = new Date(Date.now() - 12 * 60_000).toISOString();
  protected readonly spark = [42, 44, 41, 47, 52, 49, 55, 51, 48, 46, 44, 41];
  protected readonly spark2 = [30, 32, 35, 31, 36, 38, 37, 40, 38, 41, 39, 38];
  protected readonly menuItems: SdMenuItem[] = [
    { id: 'h', heading: true, label: 'Ticket' },
    { id: 'assign', label: 'Assign to me', icon: 'user-check', hint: 'A' },
    { id: 'merge', label: 'Merge…', icon: 'layers' },
    { id: 'd', divider: true },
    { id: 'delete', label: 'Delete', icon: 'trash', danger: true },
  ];

  protected readonly drawerOpen = signal(false);
  protected readonly tab = signal<string | null>('one');
  protected subject = 'Printer offline after update';
  protected minutes = 0;
  protected nativeSelect = 'High';
  protected notes = '';
  protected readonly assignee = signal<string | string[] | null>(null);
  protected readonly teams = signal<string | string[] | null>(['l2']);
  protected readonly tags = signal<string[]>(['billing']);
  protected readonly dueDate = signal<string | null>(null);
  protected readonly remindAt = signal<string | null>(null);
  protected readonly check = signal(true);
  protected readonly toggle = signal(false);
  protected readonly radio = signal<string | null>('email');
  protected readonly agentOptions: SdOption[] = PEOPLE.map((p, i) => ({ value: `a${i}`, label: p, avatar: p, description: `${3 + i} open tickets` }));
  protected readonly teamOptions: SdOption[] = [
    { value: 'l1', label: 'Level 1', group: 'Support' },
    { value: 'l2', label: 'Level 2', group: 'Support' },
    { value: 'billing', label: 'Billing', group: 'Finance' },
    { value: 'onboarding', label: 'Onboarding', group: 'Success' },
  ];
  protected readonly tagSuggestions = ['billing', 'bug', 'feature-request', 'outage', 'vip', 'refund'];

  protected readonly columns: SdColumn<DemoRow>[] = [
    { key: 'number', header: 'Ticket', width: 100, sortable: true, locked: true },
    { key: 'subject', header: 'Subject', minWidth: 240, flex: 3, sortable: true, locked: true },
    { key: 'priority', header: 'Priority', width: 120, sortable: true },
    { key: 'status', header: 'Status', width: 190 },
    { key: 'assignee', header: 'Assignee', minWidth: 160 },
    { key: 'due', header: 'Due', width: 170, sortable: true, sortKey: 'due' },
    { key: 'slaState', header: 'SLA state', width: 110, hiddenByDefault: true },
  ];
  protected readonly rows = signal<DemoRow[]>(demoRows(0, 200));
  protected readonly loadingMore = signal(false);
  protected readonly selection = signal<string[]>([]);
  protected readonly sort = signal<SdSort | null>(null);

  protected readonly filterFields: FilterFieldDef[] = [
    { key: 'subject', label: 'Subject', type: 'text', group: 'Ticket' },
    {
      key: 'priority',
      label: 'Priority',
      type: 'enum',
      group: 'Ticket',
      trackChanges: true,
      options: PRIORITIES.map((p) => ({ value: p, label: p })),
    },
    { key: 'createdAt', label: 'Created', type: 'datetime', group: 'Ticket' },
    { key: 'replyCount', label: 'Agent replies', type: 'number', group: 'Ticket' },
    { key: 'isEscalated', label: 'Escalated', type: 'boolean', group: 'Ticket' },
    { key: 'tags', label: 'Tags', type: 'tags', group: 'Ticket' },
    { key: 'customer.tier', label: 'Customer tier', type: 'enum', group: 'Customer', options: ['Gold', 'Silver', 'Bronze'].map((t) => ({ value: t, label: t })) },
  ];
  protected readonly filter = signal<FilterGroup>({
    ...emptyFilterGroup(),
    conditions: [{ field: 'priority', op: 'in', value: ['High', 'Critical'] }],
    groups: [{ logic: 'or', conditions: [{ field: 'isEscalated', op: 'eq', value: true }], groups: [] }],
  });

  protected readonly timeline: SdTimelineItem[] = [
    { id: '1', kind: 'customer', at: new Date(Date.now() - 5 * 3600_000).toISOString(), author: 'Jordan Ellis', icon: 'mail', title: 'via e-mail', bodyHtml: '<p>Hi, our printer went offline right after the update. Can you help?</p>', meta: ['Email'] },
    { id: '2', kind: 'event', at: new Date(Date.now() - 4.8 * 3600_000).toISOString(), author: 'Routing', title: 'assigned the ticket to Level 1', icon: 'user-check' },
    { id: '3', kind: 'internal', at: new Date(Date.now() - 4 * 3600_000).toISOString(), author: 'Asha Rao', bodyHtml: '<p>Known issue with driver 4.2 — <span class="mention">@Marcus Lee</span> can you confirm?</p>' },
    { id: '4', kind: 'public', at: new Date(Date.now() - 3 * 3600_000).toISOString(), author: 'Asha Rao', title: 'replied', bodyHtml: '<p>Thanks Jordan — please roll back to driver 4.1 while we fix this.</p><blockquote>Hi, our printer went offline…</blockquote>', collapsed: true },
    { id: '5', kind: 'event', at: new Date(Date.now() - 60 * 60_000).toISOString(), author: 'Asha Rao', title: 'changed priority Medium → High', icon: 'flag' },
  ];

  protected html = signal('<p>Hello <strong>world</strong></p>');
  protected readonly mentionSource = (query: string): Promise<SdMentionItem[]> =>
    Promise.resolve(
      PEOPLE.filter((p) => p.toLowerCase().includes(query.toLowerCase())).map((p, i) => ({ id: `u${i}`, label: p, description: 'Agent' })),
    );

  protected readonly chart = computed<EChartsCoreOption>(() => ({
    tooltip: { trigger: 'axis' },
    grid: { left: 36, right: 12, top: 24, bottom: 28 },
    xAxis: { type: 'category', data: ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'] },
    yAxis: { type: 'value' },
    legend: { top: 0, right: 0 },
    series: [
      { name: 'Created', type: 'bar', data: [120, 132, 101, 134, 90, 30, 22] },
      { name: 'Resolved', type: 'line', data: [98, 120, 115, 128, 101, 41, 25] },
    ],
  }));

  protected readonly events: SdCalendarEvent[] = (() => {
    const base = new Date();
    base.setHours(0, 0, 0, 0);
    const at = (d: number, h: number) => new Date(base.getTime() + d * 86400_000 + h * 3600_000).toISOString();
    return [
      { id: 'e1', title: 'Early shift — Level 1', start: at(0, 7), end: at(0, 15) },
      { id: 'e2', title: 'Late shift — Level 2', start: at(0, 14), end: at(0, 22), color: 'var(--sd-chart-2)' },
      { id: 'e3', title: 'Customer QBR', start: at(1, 10), end: at(1, 11), color: 'var(--sd-chart-3)' },
      { id: 'e4', title: 'Public holiday', start: at(3, 0), allDay: true, color: 'var(--sd-chart-4)' },
      { id: 'e5', title: 'On-call', start: at(-2, 18), end: at(-2, 23), color: 'var(--sd-chart-5)' },
    ];
  })();

  protected loadMore(): void {
    this.loadingMore.set(true);
    setTimeout(() => {
      this.rows.update((r) => [...r, ...demoRows(r.length, 200)]);
      this.loadingMore.set(false);
    }, 400);
  }

  protected async confirmDemo(): Promise<void> {
    const ok = await this.dialogs.confirm({ title: 'Delete ticket SD-1042?', message: 'This cannot be undone.', danger: true, confirmLabel: 'Delete', typeToConfirm: 'SD-1042' });
    this.toast.info(ok ? 'Confirmed' : 'Cancelled');
  }
}
