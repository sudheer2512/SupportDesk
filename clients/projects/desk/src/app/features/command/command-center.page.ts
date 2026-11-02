import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import {
  ApiError,
  ApprovalDto,
  ApprovalsApi,
  DesktopNotifier,
  MeStore,
  NotificationDto,
  NotificationStore,
  NotificationsApi,
  QueueCountsDto,
  QueueKey,
  RealtimeService,
  TicketListItemDto,
  TicketsApi,
  WorkbenchApi,
} from '@sd/core';
import {
  SdAvatar,
  SdButton,
  SdCard,
  SdChart,
  SdEmptyState,
  SdIcon,
  SdKpiTile,
  SdPageHeader,
  SdRelativeTimePipe,
  SdSkeleton,
  SdSlaCountdown,
  SdStatusPill,
} from '@sd/ui';
import type { EChartsCoreOption } from 'echarts/core';
import { debounceTime, merge } from 'rxjs';
import { ApprovalItemComponent } from './approval-item.component';
import { SlaRiskBoardComponent } from './sla-risk-board.component';
import { TicketWorkbenchStore } from '../tickets/workspace/workbench.store';
import { TeamWorkloadComponent } from './team-workload.component';
import '../tickets/shared/workbench-icons';

const REPLY_TYPES = new Set(['ticket.customer_replied', 'ticket.mentioned']);

/**
 * /command — the agent's (and supervisor's) live overview: my open / overdue / due-today counts, queue health,
 * the SLA risk board, team workload, recent customer replies and @mentions, pending approvals and escalations.
 * Everything is real data, refreshed from realtime events.
 */
@Component({
  selector: 'desk-command-center',
  imports: [
    RouterLink,
    SdAvatar,
    SdButton,
    SdCard,
    SdChart,
    SdEmptyState,
    SdIcon,
    SdKpiTile,
    SdPageHeader,
    SdRelativeTimePipe,
    SdSkeleton,
    SdSlaCountdown,
    SdStatusPill,
    ApprovalItemComponent,
    SlaRiskBoardComponent,
    TeamWorkloadComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Command Center" [subtitle]="subtitle()" icon="radar">
        @if (notifier.permission() === 'default') {
          <button sdButton sdPageActions size="sm" variant="subtle" icon="bell" type="button" (click)="notifier.requestPermission()">Enable desktop alerts</button>
        }
        <a sdButton sdPageActions size="sm" routerLink="/tickets" icon="ticket">Open workspace</a>
      </sd-page-header>
      <div class="desk-page__body">
        <section class="kpis" aria-label="Key numbers">
          <sd-kpi-tile label="My open tickets" icon="user-check" [value]="counts()['mine'] ?? null" [loading]="!countsLoaded()" interactive (activate)="openQueue('mine')" />
          <sd-kpi-tile label="My overdue" icon="alarm-clock" [value]="myOverdue()" [loading]="myLoading()" [positiveIsGood]="false" interactive (activate)="openQueue('mine')" />
          <sd-kpi-tile label="My due today" icon="calendar" [value]="myDueToday()" [loading]="myLoading()" interactive (activate)="openQueue('mine')" />
          <sd-kpi-tile [label]="supervisor() ? 'Unassigned (my departments)' : 'Unassigned'" icon="user-minus" [value]="counts()['unassigned'] ?? null" [loading]="!countsLoaded()" interactive (activate)="openQueue('unassigned')" />
          <sd-kpi-tile label="SLA at risk" icon="triangle-alert" [value]="counts()['atRisk'] ?? null" [loading]="!countsLoaded()" interactive (activate)="openQueue('atRisk')" />
          <sd-kpi-tile label="Overdue (all visible)" icon="timer" [value]="counts()['overdue'] ?? null" [loading]="!countsLoaded()" interactive (activate)="openQueue('overdue')" />
          <sd-kpi-tile label="Escalated" icon="arrow-up-right" [value]="counts()['escalated'] ?? null" [loading]="!countsLoaded()" interactive (activate)="openQueue('escalated')" />
          <sd-kpi-tile label="Pending approvals" icon="shield-check" [value]="approvalsLoaded() ? approvals().length : null" [loading]="!approvalsLoaded()" interactive (activate)="router.navigate(['/approvals'])" />
        </section>

        <div class="grid">
          <sd-card class="span-2" title="SLA risk board" subtitle="Open tickets at risk or breached — soonest due first">
            <desk-sla-risk-board />
          </sd-card>

          <sd-card title="My open tickets by status">
            @if (myLoading() && myOpen().length === 0) {
              <sd-skeleton height="200px" />
            } @else if (myError()) {
              <p class="error" role="alert">{{ myError() }} <button sdButton size="sm" variant="link" type="button" (click)="loadMine()">Retry</button></p>
            } @else if (myOpen().length === 0) {
              <sd-empty-state icon="circle-check" title="Your queue is clear" message="Nothing is assigned to you right now." compact />
            } @else {
              <sd-chart [options]="statusChart()" height="220px" [ariaLabel]="statusChartLabel()" />
              <ul class="next">
                @for (t of nextDue(); track t.id) {
                  <li>
                    <a [routerLink]="['/tickets', t.id]" class="next-link">
                      <span class="num">{{ t.displayNumber }}</span>
                      <span class="sd-truncate grow">{{ t.subject }}</span>
                      @if (t.dueAt) {
                        <sd-sla-countdown [dueAt]="t.dueAt" [state]="t.slaState" compact />
                      }
                    </a>
                  </li>
                }
              </ul>
            }
          </sd-card>

          <sd-card [title]="supervisor() ? 'Team workload' : 'My team'" subtitle="Open tickets against capacity">
            <desk-team-workload />
          </sd-card>

          <sd-card title="Customer replies & mentions">
            <a sdCardActions sdButton size="sm" variant="link" routerLink="/notifications">All notifications</a>
            @if (activityLoading() && activity().length === 0) {
              <sd-skeleton [lines]="5" />
            } @else if (activityError()) {
              <p class="error" role="alert">{{ activityError() }} <button sdButton size="sm" variant="link" type="button" (click)="loadActivity()">Retry</button></p>
            } @else if (activity().length === 0) {
              <sd-empty-state icon="messages" title="No new replies or mentions" compact />
            } @else {
              <ul class="feed">
                @for (n of activity(); track n.id) {
                  <li [class.is-unread]="!n.readAt">
                    <button type="button" class="feed-item" (click)="openNotification(n)">
                      <sd-icon [name]="n.type === 'ticket.mentioned' ? 'at-sign' : 'reply'" [size]="15" />
                      <span class="grow">
                        <span class="feed-title">{{ n.title }}</span>
                        @if (n.body) {
                          <span class="sd-small sd-muted feed-body">{{ n.body }}</span>
                        }
                      </span>
                      <span class="sd-xs sd-muted">{{ n.createdAt | sdRelativeTime }}</span>
                    </button>
                  </li>
                }
              </ul>
            }
          </sd-card>

          <sd-card title="Pending approvals">
            <a sdCardActions sdButton size="sm" variant="link" routerLink="/approvals">View all</a>
            @if (!approvalsLoaded()) {
              <sd-skeleton [lines]="4" />
            } @else if (approvalsError()) {
              <p class="error" role="alert">{{ approvalsError() }} <button sdButton size="sm" variant="link" type="button" (click)="loadApprovals()">Retry</button></p>
            } @else if (approvals().length === 0) {
              <sd-empty-state icon="shield-check" title="Nothing waiting for approval" compact />
            } @else {
              <div class="approvals">
                @for (a of approvals().slice(0, 4); track a.id) {
                  <desk-approval-item [approval]="a" (decided)="loadApprovals()" />
                }
              </div>
            }
          </sd-card>

          <sd-card title="Escalations" subtitle="Open escalated tickets">
            @if (escLoading() && escalations().length === 0) {
              <sd-skeleton [lines]="4" />
            } @else if (escError()) {
              <p class="error" role="alert">{{ escError() }} <button sdButton size="sm" variant="link" type="button" (click)="loadEscalations()">Retry</button></p>
            } @else if (escalations().length === 0) {
              <sd-empty-state icon="shield-check" title="No open escalations" compact />
            } @else {
              <ul class="next">
                @for (t of escalations(); track t.id) {
                  <li>
                    <a [routerLink]="['/tickets', t.id]" class="next-link">
                      <span class="num">{{ t.displayNumber }}</span>
                      <span class="sd-truncate grow">{{ t.subject }}</span>
                      <sd-status-pill [label]="t.status" [color]="t.statusColor" />
                      @if (t.assigneeName) {
                        <sd-avatar [name]="t.assigneeName" size="xs" />
                      }
                    </a>
                  </li>
                }
              </ul>
            }
          </sd-card>
        </div>
      </div>
    </div>
  `,
  styles: `
    .kpis {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 170px), 1fr));
      gap: var(--sd-space-3);
      margin-bottom: var(--sd-space-4);
    }
    .grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 380px), 1fr));
      gap: var(--sd-space-4);
      align-items: start;
    }
    .grid > *,
    .kpis > * {
      min-width: 0;
    }
    .span-2 {
      grid-column: span 2;
    }
    @media (max-width: 900px) {
      .span-2 {
        grid-column: auto;
      }
    }
    .error {
      color: var(--sd-danger);
    }
    .next,
    .feed {
      display: grid;
      /* One column no wider than the card: long subjects truncate instead of pushing rows out of the card. */
      grid-template-columns: minmax(0, 1fr);
      gap: 2px;
      margin: 8px 0 0;
      padding: 0;
      list-style: none;
    }
    .next-link,
    .feed-item {
      display: flex;
      align-items: center;
      gap: 8px;
      width: 100%;
      min-height: 34px;
      padding: 4px 6px;
      border: 0;
      border-radius: var(--sd-radius-sm);
      background: transparent;
      color: var(--sd-text);
      font: inherit;
      font-size: var(--sd-text-sm);
      text-align: left;
      text-decoration: none;
      cursor: pointer;
    }
    .next-link:hover,
    .feed-item:hover {
      background: var(--sd-surface-2);
    }
    .feed-item {
      align-items: flex-start;
    }
    .feed li.is-unread .feed-title {
      font-weight: var(--sd-weight-semibold);
    }
    .feed-body {
      display: -webkit-box;
      -webkit-line-clamp: 2;
      -webkit-box-orient: vertical;
      overflow: hidden;
    }
    .grow {
      display: grid;
      flex: 1;
      min-width: 0;
    }
    .num {
      flex: none;
      color: var(--sd-text-2);
      font-variant-numeric: tabular-nums;
    }
    .approvals {
      display: grid;
      gap: 8px;
    }
  `,
})
export class CommandCenterPage {
  protected readonly router = inject(Router);
  protected readonly notifier = inject(DesktopNotifier);
  private readonly me = inject(MeStore);
  private readonly tickets = inject(TicketsApi);
  private readonly workbench = inject(WorkbenchApi);
  private readonly approvalsApi = inject(ApprovalsApi);
  private readonly notificationsApi = inject(NotificationsApi);
  private readonly notificationStore = inject(NotificationStore);
  private readonly realtime = inject(RealtimeService);
  private readonly workbenchStore = inject(TicketWorkbenchStore);

  protected readonly supervisor = computed(() => this.me.can('supervisor.monitor'));
  protected readonly subtitle = computed(() => {
    const name = this.me.displayName().split(' ')[0];
    const hour = new Date().getHours();
    const greeting = hour < 12 ? 'Good morning' : hour < 18 ? 'Good afternoon' : 'Good evening';
    return `${greeting}${name ? ', ' + name : ''}. ${this.supervisor() ? 'Numbers cover your departments.' : 'Your queue, SLA risk and team at a glance.'}`;
  });

  protected readonly counts = signal<QueueCountsDto>({});
  protected readonly countsLoaded = signal(false);
  protected readonly myOpen = signal<TicketListItemDto[]>([]);
  protected readonly myLoading = signal(true);
  protected readonly myError = signal<string | null>(null);
  protected readonly approvals = signal<ApprovalDto[]>([]);
  protected readonly approvalsLoaded = signal(false);
  protected readonly approvalsError = signal<string | null>(null);
  protected readonly activity = signal<NotificationDto[]>([]);
  protected readonly activityLoading = signal(false);
  protected readonly activityError = signal<string | null>(null);
  protected readonly escalations = signal<TicketListItemDto[]>([]);
  protected readonly escLoading = signal(false);
  protected readonly escError = signal<string | null>(null);

  protected readonly myOverdue = computed(() => {
    if (this.myLoading() && this.myOpen().length === 0) return null;
    const now = Date.now();
    return this.myOpen().filter((t) => t.dueAt && new Date(t.dueAt).getTime() < now).length;
  });
  protected readonly myDueToday = computed(() => {
    if (this.myLoading() && this.myOpen().length === 0) return null;
    const now = new Date();
    const end = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1).getTime();
    return this.myOpen().filter((t) => t.dueAt && new Date(t.dueAt).getTime() >= now.getTime() && new Date(t.dueAt).getTime() < end).length;
  });
  protected readonly nextDue = computed(() => this.myOpen().filter((t) => !!t.dueAt).slice(0, 5));
  private readonly byStatus = computed(() => {
    const map = new Map<string, { count: number; color: string }>();
    for (const t of this.myOpen()) {
      const entry = map.get(t.status) ?? { count: 0, color: t.statusColor };
      entry.count++;
      map.set(t.status, entry);
    }
    return [...map.entries()].map(([name, v]) => ({ name, value: v.count, color: v.color }));
  });
  protected readonly statusChartLabel = computed(
    () => `My open tickets by status: ${this.byStatus().map((s) => `${s.name} ${s.value}`).join(', ')}`,
  );
  protected readonly statusChart = computed<EChartsCoreOption>(() => ({
    tooltip: { trigger: 'item' },
    legend: { bottom: 0, type: 'scroll' },
    series: [
      {
        type: 'pie',
        radius: ['48%', '72%'],
        center: ['50%', '44%'],
        avoidLabelOverlap: true,
        label: { show: false },
        data: this.byStatus().map((s) => ({ name: s.name, value: s.value, itemStyle: { color: s.color } })),
      },
    ],
  }));

  constructor() {
    this.loadCounts();
    this.loadMine();
    this.loadApprovals();
    this.loadActivity();
    this.loadEscalations();

    const changes = merge(this.realtime.ticketChanged$, this.realtime.ticketThread$)
      .pipe(debounceTime(2000))
      .subscribe(() => {
        this.loadCounts();
        this.loadMine(true);
        this.loadEscalations(true);
      });
    const notes = this.realtime.notification$.subscribe((n) => {
      if (REPLY_TYPES.has(n.type)) this.activity.update((list) => [{ readAt: null, acknowledgedAt: null, ...n }, ...list.filter((x) => x.id !== n.id)].slice(0, 12));
      if (n.type.startsWith('approval.')) this.loadApprovals();
    });
    const sla = this.realtime.slaAlert$.pipe(debounceTime(1000)).subscribe(() => this.loadCounts());
    const reconnect = this.realtime.reconnected$.subscribe(() => {
      this.loadCounts();
      this.loadMine(true);
      this.loadApprovals();
      this.loadActivity();
      this.loadEscalations(true);
    });
    inject(DestroyRef).onDestroy(() => {
      changes.unsubscribe();
      notes.unsubscribe();
      sla.unsubscribe();
      reconnect.unsubscribe();
    });
  }

  protected openQueue(key: QueueKey): void {
    this.workbenchStore.select({ kind: 'queue', key });
    void this.router.navigate(['/tickets']);
  }

  protected openNotification(n: NotificationDto): void {
    if (!n.readAt) {
      this.notificationStore.markRead(n.id);
      this.activity.update((list) => list.map((x) => (x.id === n.id ? { ...x, readAt: new Date().toISOString() } : x)));
    }
    if (n.link?.startsWith('/')) void this.router.navigateByUrl(n.link);
  }

  loadCounts(): void {
    this.workbench.queueCounts().subscribe({
      next: (c) => {
        this.counts.set(c);
        this.countsLoaded.set(true);
      },
      error: () => this.countsLoaded.set(true),
    });
  }

  loadMine(quiet = false): void {
    if (!quiet) this.myLoading.set(true);
    this.tickets.list({ mine: true, statusCategory: 'open', sort: 'due', dir: 'asc', limit: 200 }, { silent: true }).subscribe({
      next: (page) => {
        this.myOpen.set(page.items);
        this.myLoading.set(false);
        this.myError.set(null);
      },
      error: (err: unknown) => {
        this.myLoading.set(false);
        this.myError.set(ApiError.from(err).userMessage);
      },
    });
  }

  loadApprovals(): void {
    this.approvalsApi.list('Pending').subscribe({
      next: (list) => {
        this.approvals.set(list);
        this.approvalsLoaded.set(true);
        this.approvalsError.set(null);
      },
      error: (err: unknown) => {
        this.approvalsLoaded.set(true);
        this.approvalsError.set(ApiError.from(err).userMessage);
      },
    });
  }

  loadActivity(): void {
    this.activityLoading.set(true);
    this.notificationsApi.list(100).subscribe({
      next: (list) => {
        this.activity.set(list.filter((n) => REPLY_TYPES.has(n.type)).slice(0, 12));
        this.activityLoading.set(false);
        this.activityError.set(null);
      },
      error: (err: unknown) => {
        this.activityLoading.set(false);
        this.activityError.set(ApiError.from(err).userMessage);
      },
    });
  }

  loadEscalations(quiet = false): void {
    if (!quiet) this.escLoading.set(true);
    this.tickets.list({ escalated: true, statusCategory: 'open', sort: 'priority', limit: 10 }, { silent: true }).subscribe({
      next: (page) => {
        this.escalations.set(page.items);
        this.escLoading.set(false);
        this.escError.set(null);
      },
      error: (err: unknown) => {
        this.escLoading.set(false);
        this.escError.set(ApiError.from(err).userMessage);
      },
    });
  }
}
