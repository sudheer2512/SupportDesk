import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  ActionSheetController,
  AlertController,
  IonBadge,
  IonButton,
  IonButtons,
  IonContent,
  IonHeader,
  IonIcon,
  IonInfiniteScroll,
  IonInfiniteScrollContent,
  IonItem,
  IonItemOption,
  IonItemOptions,
  IonItemSliding,
  IonLabel,
  IonList,
  IonRefresher,
  IonRefresherContent,
  IonSearchbar,
  IonSegment,
  IonSegmentButton,
  IonTitle,
  IonToolbar,
} from '@ionic/angular/standalone';
import {
  ApiError,
  ClockService,
  MeStore,
  RealtimeService,
  TicketListItemDto,
  TicketQuery,
  TicketsApi,
  formatRelative,
} from '@sd/core';
import { Subscription, debounceTime, firstValueFrom } from 'rxjs';
import { NetworkService, isUnreachable } from '../core/network.service';
import { OfflineCache } from '../core/offline-cache.service';
import { SessionCoordinator } from '../core/session-coordinator';
import { SessionSnapshotStore } from '../core/session-snapshot';
import { MobileToasts } from '../core/ui-services';
import { OfflineBanner } from '../shared/offline-banner.component';
import { SlaChip } from '../shared/sla-chip.component';
import { StateView } from '../shared/state-view.component';
import { priorityColor, statusColor } from '../shared/format';

export type TicketFilter = 'mine' | 'unassigned' | 'overdue' | 'atRisk' | 'escalated' | 'waiting' | 'watching' | 'open';

interface FilterDef {
  key: TicketFilter;
  label: string;
  query: TicketQuery;
  empty: string;
}

export const TICKET_FILTERS: FilterDef[] = [
  { key: 'mine', label: 'Mine', query: { mine: true, statusCategory: 'open', sort: 'lastActivity', dir: 'desc' }, empty: 'Nothing assigned to you is open.' },
  { key: 'unassigned', label: 'Unassigned', query: { unassigned: true, statusCategory: 'open', sort: 'created', dir: 'desc' }, empty: 'Every open ticket has an owner.' },
  { key: 'overdue', label: 'Overdue', query: { overdue: true, statusCategory: 'open', sort: 'due', dir: 'asc' }, empty: 'No overdue tickets — nice.' },
  { key: 'atRisk', label: 'At risk', query: { slaState: 'AtRisk', statusCategory: 'open', sort: 'due', dir: 'asc' }, empty: 'No ticket is close to breaching its SLA.' },
  { key: 'escalated', label: 'Escalated', query: { escalated: true, statusCategory: 'open', sort: 'due', dir: 'asc' }, empty: 'No escalated tickets.' },
  { key: 'waiting', label: 'Waiting', query: { statusCategory: 'Waiting', sort: 'lastActivity', dir: 'desc' }, empty: 'No ticket is waiting on someone else.' },
  { key: 'watching', label: 'Watching', query: { watching: true, statusCategory: 'open', sort: 'lastActivity', dir: 'desc' }, empty: 'You are not watching any open ticket.' },
  { key: 'open', label: 'All open', query: { statusCategory: 'open', sort: 'lastActivity', dir: 'desc' }, empty: 'There are no open tickets you can see.' },
];

const PAGE = 25;

/** Agent ticket list: queue filters, search, pull-to-refresh, keyset infinite scroll, swipe actions, live updates. */
@Component({
  selector: 'm-agent-tickets',
  imports: [
    RouterLink,
    IonHeader,
    IonToolbar,
    IonTitle,
    IonButtons,
    IonButton,
    IonIcon,
    IonContent,
    IonSearchbar,
    IonSegment,
    IonSegmentButton,
    IonLabel,
    IonList,
    IonItem,
    IonItemSliding,
    IonItemOptions,
    IonItemOption,
    IonBadge,
    IonRefresher,
    IonRefresherContent,
    IonInfiniteScroll,
    IonInfiniteScrollContent,
    OfflineBanner,
    SlaChip,
    StateView,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ion-header>
      <ion-toolbar>
        <ion-title>Tickets</ion-title>
        <ion-buttons slot="end">
          <ion-button aria-label="Refresh" [disabled]="loading()" (click)="load()">
            <ion-icon slot="icon-only" name="refresh-outline" aria-hidden="true" />
          </ion-button>
        </ion-buttons>
      </ion-toolbar>
      <ion-toolbar>
        <ion-searchbar
          placeholder="Search number, subject, customer"
          [debounce]="350"
          [value]="q()"
          (ionInput)="search($any($event).detail.value ?? '')"
          aria-label="Search tickets"
        />
      </ion-toolbar>
      <ion-toolbar class="filters">
        <ion-segment [scrollable]="true" [value]="current()" (ionChange)="setFilter($any($event).detail.value)" aria-label="Queue">
          @for (f of filters; track f.key) {
            <ion-segment-button [value]="f.key"><ion-label>{{ f.label }}</ion-label></ion-segment-button>
          }
        </ion-segment>
      </ion-toolbar>
      <m-offline-banner [staleSince]="staleSince()" />
    </ion-header>

    <ion-content>
      <ion-refresher slot="fixed" (ionRefresh)="refresh($event)">
        <ion-refresher-content />
      </ion-refresher>

      @if (updates()) {
        <div class="updates">
          <ion-button size="small" shape="round" (click)="load()">
            <ion-icon slot="start" name="arrow-up-outline" aria-hidden="true" />New activity — refresh
          </ion-button>
        </div>
      }

      @if (items().length) {
        <ion-list lines="full">
          @for (t of items(); track t.id) {
            <ion-item-sliding #sliding [disabled]="readOnly()">
              <ion-item [routerLink]="['/agent/tickets', t.id]" [detail]="false" [button]="true">
                <div class="row">
                  <div class="top">
                    <span class="num">{{ t.displayNumber }}</span>
                    <ion-badge [color]="statusColor(t.statusCategory)">{{ t.status }}</ion-badge>
                    @if (t.hasUnreadCustomerReply) {
                      <span class="dot" aria-label="Customer replied"></span>
                    }
                    <span class="time">{{ rel(t.lastActivityAt) }}</span>
                  </div>
                  <div class="subject">{{ t.subject }}</div>
                  <div class="meta">{{ t.customerName || t.contactName || 'No customer' }} · {{ t.assigneeName || 'Unassigned' }}</div>
                  <div class="chips">
                    <ion-badge class="prio" [color]="priorityColor(t.priority)">{{ t.priority }}</ion-badge>
                    @if (t.isEscalated) {
                      <ion-badge color="danger">Escalated</ion-badge>
                    }
                    <m-sla-chip [compact]="true" [label]="slaLabel(t)" [dueAt]="slaDue(t)" [state]="t.slaState" />
                  </div>
                </div>
                <ion-button slot="end" fill="clear" color="medium" [attr.aria-label]="'Actions for ' + t.displayNumber"
                  [disabled]="readOnly()" (click)="actions(t, $event)">
                  <ion-icon slot="icon-only" name="ellipsis-vertical" aria-hidden="true" />
                </ion-button>
              </ion-item>
              @if (canAssign() && t.assigneeId !== myId()) {
                <ion-item-options side="start">
                  <ion-item-option color="primary" (click)="assignToMe(t); sliding.close()">
                    <ion-icon slot="top" name="person-add-outline" aria-hidden="true" />Assign to me
                  </ion-item-option>
                </ion-item-options>
              }
              @if (canClose()) {
                <ion-item-options side="end">
                  <ion-item-option color="danger" (click)="close(t); sliding.close()">
                    <ion-icon slot="top" name="checkmark-done-outline" aria-hidden="true" />Close
                  </ion-item-option>
                </ion-item-options>
              }
            </ion-item-sliding>
          }
        </ion-list>
        <ion-infinite-scroll [disabled]="!cursor() || readOnly()" (ionInfinite)="loadMore($event)">
          <ion-infinite-scroll-content loadingText="Loading more tickets…" />
        </ion-infinite-scroll>
      } @else if (loading()) {
        <m-state kind="loading" title="Loading tickets" />
      } @else if (error()) {
        <m-state [kind]="offline() ? 'offline' : 'error'" [message]="offline() ? 'This list has not been saved on this device yet.' : error()!" actionLabel="Try again" (action)="load()" />
      } @else if (loaded()) {
        <m-state kind="empty" [title]="q() ? 'No matches' : 'All clear'" [message]="q() ? 'No ticket matches “' + q() + '”.' : filterDef().empty" />
      }
    </ion-content>
  `,
  styles: `
    .filters {
      --min-height: 44px;
    }
    .row {
      display: flex;
      flex-direction: column;
      gap: 3px;
      padding: 10px 0;
      min-width: 0;
      flex: 1;
    }
    .top {
      display: flex;
      align-items: center;
      gap: 8px;
      font-size: 0.8rem;
    }
    .num {
      font-weight: 600;
      color: var(--sd-text-2);
    }
    .time {
      margin-left: auto;
      color: var(--sd-text-2);
    }
    .dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: var(--ion-color-primary);
    }
    .subject {
      font-weight: 600;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .meta {
      font-size: 0.85rem;
      color: var(--sd-text-2);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .chips {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      align-items: center;
    }
    ion-badge {
      font-weight: 600;
    }
    .updates {
      position: sticky;
      top: 8px;
      z-index: 2;
      display: flex;
      justify-content: center;
    }
  `,
})
export class AgentTicketsPage implements OnInit {
  /** Initial queue from the query string (`?filter=overdue`), e.g. when coming from Home. */
  readonly filter = input<string | undefined>(undefined);

  private readonly api = inject(TicketsApi);
  private readonly me = inject(MeStore);
  private readonly snapshot = inject(SessionSnapshotStore);
  private readonly cache = inject(OfflineCache);
  private readonly network = inject(NetworkService);
  private readonly session = inject(SessionCoordinator);
  private readonly realtime = inject(RealtimeService);
  private readonly clock = inject(ClockService);
  private readonly toasts = inject(MobileToasts);
  private readonly alerts = inject(AlertController);
  private readonly sheets = inject(ActionSheetController);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly filters = TICKET_FILTERS;
  protected readonly current = signal<TicketFilter>('mine');
  protected readonly q = signal('');
  protected readonly items = signal<TicketListItemDto[]>([]);
  protected readonly cursor = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly loaded = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly staleSince = signal<number | null>(null);
  protected readonly updates = signal(false);
  private pages = 0;
  private generation = 0;
  private subscription: Subscription | null = null;

  protected readonly filterDef = computed(() => TICKET_FILTERS.find((f) => f.key === this.current()) ?? TICKET_FILTERS[0]);
  protected readonly readOnly = this.session.readOnly;
  protected readonly offline = computed(() => !this.network.online());
  protected readonly myId = computed(() => this.me.user()?.id ?? this.snapshot.userId());
  /** Self-assignment needs tickets.edit (or tickets.assign); the team is left unchanged. */
  protected readonly canAssign = computed(() => this.me.can('tickets.assign') || this.me.can('tickets.edit'));
  protected readonly canClose = computed(() => this.me.can('tickets.resolve'));
  protected readonly priorityColor = priorityColor;
  protected readonly statusColor = statusColor;

  private initialized = false;

  constructor() {
    // Home tiles navigate here with ?filter=… while this tab page stays alive: follow the query string.
    effect(() => {
      const wanted = TICKET_FILTERS.find((f) => f.key === this.filter());
      untracked(() => {
        if (this.initialized && wanted && wanted.key !== this.current()) this.setFilter(wanted.key);
      });
    });
  }

  ngOnInit(): void {
    const initial = TICKET_FILTERS.find((f) => f.key === this.filter());
    if (initial) this.current.set(initial.key);
    this.initialized = true;
    void this.load();

    const live = this.realtime.ticketChanged$.pipe(debounceTime(2000)).subscribe(() => {
      // Refresh in place while the user is on the first page; otherwise offer it (keeps the scroll position).
      if (this.pages <= 1 && !this.q()) void this.load(true);
      else this.updates.set(true);
    });
    const refresh = this.session.refresh$.subscribe(() => void this.load(true));
    this.destroyRef.onDestroy(() => {
      live.unsubscribe();
      refresh.unsubscribe();
      this.subscription?.unsubscribe();
    });
  }

  protected setFilter(value: TicketFilter): void {
    if (value === this.current()) return;
    this.current.set(value);
    this.items.set([]);
    void this.load();
  }

  protected search(value: string): void {
    this.q.set(value.trim());
    this.items.set([]);
    void this.load();
  }

  protected async refresh(event: Event): Promise<void> {
    await this.load();
    await (event.target as HTMLIonRefresherElement).complete();
  }

  /** First page (network first; the cached first page is shown while loading or when offline). */
  async load(silent = false): Promise<void> {
    const gen = ++this.generation;
    const key = this.q() ? null : `tickets:${this.current()}`;
    this.updates.set(false);
    this.error.set(null);
    if (!silent) this.loading.set(true);
    if (key && !this.items().length) {
      const cached = await this.cache.read<TicketListItemDto[]>(key);
      if (gen !== this.generation) return;
      if (cached) {
        this.items.set(cached.value);
        this.staleSince.set(cached.savedAt);
      }
    }
    this.subscription?.unsubscribe();
    await new Promise<void>((resolve) => {
      this.subscription = this.api.list({ ...this.filterDef().query, q: this.q() || null, limit: PAGE }, { silent: true }).subscribe({
        next: (page) => {
          if (gen !== this.generation) return;
          this.items.set(page.items);
          this.cursor.set(page.nextCursor);
          this.staleSince.set(null);
          this.pages = 1;
          if (key) void this.cache.write(key, 'list', page.items);
        },
        error: (e: unknown) => {
          if (gen === this.generation) this.fail(e);
          resolve();
        },
        complete: () => {
          if (gen === this.generation) {
            this.loading.set(false);
            this.loaded.set(true);
          }
          resolve();
        },
      });
    });
  }

  protected async loadMore(event: Event): Promise<void> {
    const cursor = this.cursor();
    const target = event.target as HTMLIonInfiniteScrollElement;
    if (!cursor) {
      await target.complete();
      return;
    }
    const gen = this.generation;
    try {
      const page = await firstValueFrom(this.api.list({ ...this.filterDef().query, q: this.q() || null, limit: PAGE, cursor }, { silent: true }));
      if (gen === this.generation) {
        const seen = new Set(this.items().map((t) => t.id));
        this.items.update((list) => [...list, ...page.items.filter((t) => !seen.has(t.id))]);
        this.cursor.set(page.nextCursor);
        this.pages++;
      }
    } catch (e) {
      const err = ApiError.from(e);
      void this.toasts.error(isUnreachable(err) ? 'You are offline — more tickets load when you reconnect.' : err.userMessage);
    } finally {
      await target.complete();
    }
  }

  protected async assignToMe(t: TicketListItemDto): Promise<void> {
    const me = this.myId();
    if (!me) return;
    try {
      const updated = await firstValueFrom(this.api.assign(t.id, { assigneeId: me, teamId: null }));
      this.replace({ ...t, assigneeId: updated.assigneeId, assigneeName: updated.assigneeName, status: updated.status, statusCategory: updated.statusCategory });
      void this.toasts.success(`${t.displayNumber} is now yours.`);
      if (this.current() === 'unassigned') this.items.update((list) => list.filter((x) => x.id !== t.id));
    } catch (e) {
      void this.toasts.error(ApiError.from(e).userMessage);
    }
  }

  protected async close(t: TicketListItemDto): Promise<void> {
    const alert = await this.alerts.create({
      header: `Close ${t.displayNumber}?`,
      message: 'The customer is not asked to confirm. You can reopen it later.',
      buttons: [
        { text: 'Cancel', role: 'cancel' },
        { text: 'Close ticket', role: 'destructive' },
      ],
    });
    await alert.present();
    const { role } = await alert.onDidDismiss();
    if (role !== 'destructive') return;
    try {
      await firstValueFrom(this.api.close(t.id));
      this.items.update((list) => list.filter((x) => x.id !== t.id));
      void this.toasts.success(`${t.displayNumber} closed.`);
    } catch (e) {
      void this.toasts.error(ApiError.from(e).userMessage);
    }
  }

  /** Keyboard / screen-reader path to the swipe actions. */
  protected async actions(t: TicketListItemDto, event: Event): Promise<void> {
    event.stopPropagation();
    event.preventDefault();
    const buttons = [];
    if (this.canAssign() && t.assigneeId !== this.myId()) buttons.push({ text: 'Assign to me', handler: () => void this.assignToMe(t) });
    if (this.canClose()) buttons.push({ text: 'Close ticket', role: 'destructive', handler: () => void this.close(t) });
    buttons.push({ text: 'Cancel', role: 'cancel' });
    const sheet = await this.sheets.create({ header: `${t.displayNumber} · ${t.subject}`, buttons });
    await sheet.present();
  }

  protected rel(value: string): string {
    return formatRelative(value, this.clock.coarseNow());
  }

  protected slaDue(t: TicketListItemDto): string | null {
    return t.agentReplyCount === 0 && t.firstResponseDueAt ? t.firstResponseDueAt : t.dueAt;
  }

  protected slaLabel(t: TicketListItemDto): string {
    return t.agentReplyCount === 0 && t.firstResponseDueAt ? 'Response' : 'Resolve';
  }

  private replace(t: TicketListItemDto): void {
    this.items.update((list) => list.map((x) => (x.id === t.id ? t : x)));
  }

  private fail(e: unknown): void {
    const err = ApiError.from(e);
    this.loading.set(false);
    this.loaded.set(true);
    if (isUnreachable(err)) this.network.markUnreachable();
    // Keep showing whatever is on screen (cached or previous) when the network fails.
    if (!this.items().length) this.error.set(err.userMessage);
    else if (!isUnreachable(err)) void this.toasts.error(err.userMessage);
  }
}
