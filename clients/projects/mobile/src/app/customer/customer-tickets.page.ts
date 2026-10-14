import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  IonBadge,
  IonContent,
  IonFab,
  IonFabButton,
  IonHeader,
  IonIcon,
  IonInfiniteScroll,
  IonInfiniteScrollContent,
  IonItem,
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
  PortalRealtimeService,
  PortalTicketListItemDto,
  PortalTicketScope,
  PortalTicketScopeDto,
  PortalTicketsApi,
  formatRelative,
} from '@sd/core';
import { Subscription, debounceTime, firstValueFrom, merge } from 'rxjs';
import { ModeService } from '../core/mobile-session';
import { NetworkService, isUnreachableStatus } from '../core/network.service';
import { OfflineCache } from '../core/offline-cache.service';
import { OutboxService } from '../core/outbox.service';
import { SessionCoordinator } from '../core/session-coordinator';
import { MobileToasts } from '../core/ui-services';
import { OfflineBanner } from '../shared/offline-banner.component';
import { OutboxItem } from '../shared/outbox-item.component';
import { StateView } from '../shared/state-view.component';
import { statusColor } from '../shared/format';

type StatusFilter = 'open' | 'awaiting' | 'closed' | 'all';

const SCOPE_LABELS: Record<PortalTicketScope, string> = { own: 'Mine', location: 'My location', organisation: 'Organisation' };
const PAGE = 20;

/** Customer: my tickets (own / location / organisation scope as allowed), status filter, search, live updates. */
@Component({
  selector: 'm-customer-tickets',
  imports: [
    RouterLink,
    IonHeader,
    IonToolbar,
    IonTitle,
    IonContent,
    IonSearchbar,
    IonSegment,
    IonSegmentButton,
    IonLabel,
    IonList,
    IonItem,
    IonBadge,
    IonIcon,
    IonFab,
    IonFabButton,
    IonRefresher,
    IonRefresherContent,
    IonInfiniteScroll,
    IonInfiniteScrollContent,
    OfflineBanner,
    OutboxItem,
    StateView,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ion-header>
      <ion-toolbar><ion-title>My tickets</ion-title></ion-toolbar>
      @if (scopes().length > 1) {
        <ion-toolbar>
          <ion-segment [value]="scope()" (ionChange)="setScope($any($event).detail.value)" aria-label="Whose tickets">
            @for (s of scopes(); track s) {
              <ion-segment-button [value]="s"><ion-label>{{ scopeLabel(s) }}</ion-label></ion-segment-button>
            }
          </ion-segment>
        </ion-toolbar>
      }
      <ion-toolbar>
        <ion-segment [value]="status()" (ionChange)="setStatus($any($event).detail.value)" aria-label="Status">
          <ion-segment-button value="open"><ion-label>Open</ion-label></ion-segment-button>
          <ion-segment-button value="awaiting"><ion-label>Needs me</ion-label></ion-segment-button>
          <ion-segment-button value="closed"><ion-label>Closed</ion-label></ion-segment-button>
          <ion-segment-button value="all"><ion-label>All</ion-label></ion-segment-button>
        </ion-segment>
      </ion-toolbar>
      <ion-toolbar>
        <ion-searchbar placeholder="Search your tickets" [debounce]="350" (ionInput)="search($any($event).detail.value ?? '')" aria-label="Search tickets" />
      </ion-toolbar>
      <m-offline-banner [staleSince]="staleSince()" />
    </ion-header>

    <ion-content>
      <ion-refresher slot="fixed" (ionRefresh)="refresh($event)"><ion-refresher-content /></ion-refresher>

      @for (e of queuedTickets(); track e.id) {
        <m-outbox-item [entry]="e" [showLabel]="true" [editable]="false" />
      }

      @if (items().length) {
        <ion-list lines="full">
          @for (t of items(); track t.id) {
            <ion-item [routerLink]="['/customer/tickets', t.id]" [button]="true" [detail]="false">
              <ion-label>
                <p class="top">
                  <span>{{ t.displayNumber }}</span>
                  <ion-badge [color]="statusColor(t.statusCategory)">{{ t.status }}</ion-badge>
                  <span class="time">{{ rel(t.lastActivityAt) }}</span>
                </p>
                <h2>{{ t.subject }}</h2>
                <p>
                  @if (!t.isMine && t.contactName) {
                    {{ t.contactName }} ·
                  }
                  {{ t.productName || t.locationName || 'Priority ' + t.priority }}
                </p>
                @if (t.awaitingYourReply) {
                  <ion-badge color="warning">Waiting for your reply</ion-badge>
                }
                @if (t.awaitingConfirmation) {
                  <ion-badge color="success">Please confirm the fix</ion-badge>
                }
              </ion-label>
            </ion-item>
          }
        </ion-list>
        <ion-infinite-scroll [disabled]="!cursor() || offline()" (ionInfinite)="loadMore($event)">
          <ion-infinite-scroll-content loadingText="Loading more…" />
        </ion-infinite-scroll>
      } @else if (loading()) {
        <m-state kind="loading" title="Loading your tickets" />
      } @else if (error()) {
        <m-state [kind]="offline() ? 'offline' : 'error'" [message]="offline() ? 'Your tickets are not saved on this device yet.' : error()!" actionLabel="Try again" (action)="load()" />
      } @else if (loaded()) {
        <m-state kind="empty" [title]="q() ? 'No matches' : status() === 'open' ? 'No open requests' : 'No tickets'"
          [message]="q() ? 'Nothing matches “' + q() + '”.' : 'Need help? Start a new request.'" />
      }

      <ion-fab slot="fixed" vertical="bottom" horizontal="end">
        <ion-fab-button routerLink="/customer/new" aria-label="New request">
          <ion-icon name="add" aria-hidden="true" />
        </ion-fab-button>
      </ion-fab>
    </ion-content>
  `,
  styles: `
    .top {
      display: flex;
      align-items: center;
      gap: 8px;
      font-size: 0.8rem;
    }
    .time {
      margin-left: auto;
    }
    h2 {
      font-weight: 600 !important;
      white-space: normal;
    }
    ion-badge {
      margin-right: 6px;
    }
  `,
})
export class CustomerTicketsPage implements OnInit {
  private readonly api = inject(PortalTicketsApi);
  private readonly mode = inject(ModeService);
  private readonly cache = inject(OfflineCache);
  private readonly network = inject(NetworkService);
  private readonly session = inject(SessionCoordinator);
  private readonly realtime = inject(PortalRealtimeService);
  private readonly outbox = inject(OutboxService);
  private readonly clock = inject(ClockService);
  private readonly toasts = inject(MobileToasts);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly scopes = signal<PortalTicketScope[]>([]);
  protected readonly scope = signal<PortalTicketScope>('own');
  protected readonly status = signal<StatusFilter>('open');
  protected readonly q = signal('');
  protected readonly items = signal<PortalTicketListItemDto[]>([]);
  protected readonly cursor = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly loaded = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly staleSince = signal<number | null>(null);
  protected readonly offline = computed(() => !this.network.online());
  protected readonly queuedTickets = computed(() => this.outbox.entries().filter((e) => e.kind === 'customer-ticket'));
  protected readonly statusColor = statusColor;
  private generation = 0;
  private subscription: Subscription | null = null;

  ngOnInit(): void {
    void this.loadScope();
    void this.load();
    const live = merge(this.realtime.ticketChanged$, this.realtime.ticketThread$, this.realtime.reconnected$)
      .pipe(debounceTime(1500))
      .subscribe(() => void this.load(true));
    const refresh = merge(this.session.refresh$, this.outbox.sent$).subscribe(() => void this.load(true));
    this.destroyRef.onDestroy(() => {
      live.unsubscribe();
      refresh.unsubscribe();
      this.subscription?.unsubscribe();
    });
  }

  protected scopeLabel(s: PortalTicketScope): string {
    return SCOPE_LABELS[s] ?? s;
  }

  protected setScope(value: PortalTicketScope): void {
    this.scope.set(value);
    this.items.set([]);
    void this.load();
  }

  protected setStatus(value: StatusFilter): void {
    this.status.set(value);
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

  private async loadScope(): Promise<void> {
    const key = 'portal:scope';
    const cached = await this.cache.read<PortalTicketScopeDto>(key);
    if (cached) this.applyScope(cached.value);
    try {
      const dto = await firstValueFrom(this.api.scope(this.mode.brand()));
      this.applyScope(dto);
      void this.cache.write(key, 'meta', dto);
    } catch {
      /* keep the cached/default scope */
    }
  }

  private applyScope(dto: PortalTicketScopeDto): void {
    this.scopes.set(dto.available);
    if (!dto.available.includes(this.scope())) this.scope.set(dto.available[0] ?? 'own');
  }

  async load(silent = false): Promise<void> {
    const gen = ++this.generation;
    const key = this.q() ? null : `portal:tickets:${this.scope()}:${this.status()}`;
    this.error.set(null);
    if (!silent) this.loading.set(true);
    if (key && !this.items().length) {
      const cached = await this.cache.read<PortalTicketListItemDto[]>(key);
      if (gen !== this.generation) return;
      if (cached) {
        this.items.set(cached.value);
        this.staleSince.set(cached.savedAt);
      }
    }
    this.subscription?.unsubscribe();
    await new Promise<void>((resolve) => {
      this.subscription = this.api
        .list(this.mode.brand(), { scope: this.scope(), status: this.status(), q: this.q() || null, limit: PAGE })
        .subscribe({
          next: (page) => {
            if (gen !== this.generation) return;
            this.items.set(page.items);
            this.cursor.set(page.nextCursor);
            this.staleSince.set(null);
            if (key) void this.cache.write(key, 'list', page.items);
          },
          error: (e: unknown) => {
            if (gen === this.generation) {
              const err = ApiError.from(e);
              this.loading.set(false);
              this.loaded.set(true);
              if (isUnreachableStatus(err.status)) this.network.markUnreachable();
              if (!this.items().length) this.error.set(err.userMessage);
            }
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
    const target = event.target as HTMLIonInfiniteScrollElement;
    const cursor = this.cursor();
    if (!cursor) {
      await target.complete();
      return;
    }
    const gen = this.generation;
    try {
      const page = await firstValueFrom(
        this.api.list(this.mode.brand(), { scope: this.scope(), status: this.status(), q: this.q() || null, limit: PAGE, cursor }),
      );
      if (gen === this.generation) {
        const seen = new Set(this.items().map((t) => t.id));
        this.items.update((list) => [...list, ...page.items.filter((t) => !seen.has(t.id))]);
        this.cursor.set(page.nextCursor);
      }
    } catch (e) {
      void this.toasts.error(ApiError.from(e).userMessage);
    } finally {
      await target.complete();
    }
  }

  protected rel(value: string): string {
    return formatRelative(value, this.clock.coarseNow());
  }
}
