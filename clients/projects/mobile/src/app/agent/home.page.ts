import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  ActionSheetController,
  IonBadge,
  IonButton,
  IonButtons,
  IonContent,
  IonHeader,
  IonIcon,
  IonItem,
  IonLabel,
  IonList,
  IonListHeader,
  IonRefresher,
  IonRefresherContent,
  IonTitle,
  IonToolbar,
} from '@ionic/angular/standalone';
import { ApiError, ApprovalDto, ApprovalsApi, AVAILABILITIES, Availability, MeStore, TicketListItemDto, TicketsApi } from '@sd/core';
import { map } from 'rxjs';
import { cachedResource } from '../core/cached-resource';
import { SessionCoordinator } from '../core/session-coordinator';
import { SessionSnapshotStore } from '../core/session-snapshot';
import { MobileToasts } from '../core/ui-services';
import { OfflineBanner } from '../shared/offline-banner.component';
import { SlaChip } from '../shared/sla-chip.component';
import { StateView } from '../shared/state-view.component';
import { priorityColor } from '../shared/format';
import { AvailabilityStore, QueueCountsStore } from './agent-stores';

interface Tile {
  key: string;
  label: string;
  icon: string;
  tone: 'primary' | 'danger' | 'warning' | 'medium';
}

const TILES: Tile[] = [
  { key: 'mine', label: 'Mine', icon: 'person-outline', tone: 'primary' },
  { key: 'unassigned', label: 'Unassigned', icon: 'help-buoy-outline', tone: 'primary' },
  { key: 'overdue', label: 'Overdue', icon: 'alarm-outline', tone: 'danger' },
  { key: 'atRisk', label: 'At risk', icon: 'warning-outline', tone: 'warning' },
  { key: 'escalated', label: 'Escalated', icon: 'trending-up-outline', tone: 'danger' },
  { key: 'waiting', label: 'Waiting', icon: 'hourglass-outline', tone: 'medium' },
  { key: 'watching', label: 'Watching', icon: 'eye-outline', tone: 'medium' },
  { key: 'open', label: 'All open', icon: 'file-tray-full-outline', tone: 'medium' },
];

export const AVAILABILITY_COLORS: Record<Availability, string> = { Online: 'success', Away: 'warning', Busy: 'danger', Offline: 'medium' };

/** Agent home: availability, my queues with live counts, my next SLA deadlines and pending approvals. */
@Component({
  selector: 'm-agent-home',
  imports: [
    RouterLink,
    IonHeader,
    IonToolbar,
    IonTitle,
    IonButtons,
    IonButton,
    IonIcon,
    IonContent,
    IonRefresher,
    IonRefresherContent,
    IonList,
    IonListHeader,
    IonItem,
    IonLabel,
    IonBadge,
    OfflineBanner,
    SlaChip,
    StateView,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ion-header>
      <ion-toolbar>
        <ion-title>Home</ion-title>
        <ion-buttons slot="end">
          @if (live()) {
          <ion-button [disabled]="readOnly()" (click)="chooseAvailability()" [attr.aria-label]="'Availability: ' + availability() + '. Change'">
            <span class="presence" [attr.data-a]="availability()" aria-hidden="true"></span>
            {{ availability() }}
          </ion-button>
          }
        </ion-buttons>
      </ion-toolbar>
      <m-offline-banner [staleSince]="queues.resource.staleSince()" />
    </ion-header>

    <ion-content>
      <ion-refresher slot="fixed" (ionRefresh)="refresh($event)">
        <ion-refresher-content />
      </ion-refresher>

      <section class="hello">
        <h1>Hi {{ firstName() }}</h1>
        <p>{{ tenantName() }}</p>
      </section>

      <h2 class="section">My queues</h2>
      @if (counts(); as c) {
        <div class="tiles">
          @for (tile of tiles; track tile.key) {
            <a class="tile" [attr.data-tone]="tile.tone" [routerLink]="['/agent/tickets']" [queryParams]="{ filter: tile.key }"
              [attr.aria-label]="tile.label + ': ' + (c[tile.key] || 0) + ' tickets'">
              <ion-icon [name]="tile.icon" aria-hidden="true" />
              <span class="count">{{ c[tile.key] || 0 }}</span>
              <span class="label">{{ tile.label }}</span>
            </a>
          }
        </div>
      } @else if (queues.resource.loading()) {
        <m-state kind="loading" [lines]="2" title="Loading queues" />
      } @else if (queues.resource.error()) {
        <m-state [kind]="readOnly() ? 'offline' : 'error'" [message]="queues.resource.error()!.userMessage" actionLabel="Try again" (action)="queues.resource.load()" />
      }

      @if (canDecide() && pendingApprovals() > 0) {
        <ion-list lines="none" class="approvals">
          <ion-item [routerLink]="['/agent/approvals']" [button]="true" detail="true">
            <ion-icon slot="start" name="checkmark-circle-outline" color="warning" aria-hidden="true" />
            <ion-label>Approvals waiting for you</ion-label>
            <ion-badge slot="end" color="warning">{{ pendingApprovals() }}</ion-badge>
          </ion-item>
        </ion-list>
      }

      <ion-list lines="full">
        <ion-list-header><ion-label>My next deadlines</ion-label></ion-list-header>
        @for (t of dueNext(); track t.id) {
          <ion-item [routerLink]="['/agent/tickets', t.id]" [button]="true" detail="false">
            <ion-label>
              <h3>{{ t.displayNumber }} · {{ t.subject }}</h3>
              <p>{{ t.customerName || t.contactName || 'No customer' }}</p>
            </ion-label>
            <div slot="end" class="end">
              <m-sla-chip [compact]="true" [dueAt]="t.agentReplyCount === 0 && t.firstResponseDueAt ? t.firstResponseDueAt : t.dueAt" [state]="t.slaState" />
              <ion-badge [color]="priorityColor(t.priority)">{{ t.priority }}</ion-badge>
            </div>
          </ion-item>
        } @empty {
          @if (deadlines.loading()) {
            <m-state kind="loading" [lines]="3" />
          } @else if (deadlines.error() && !deadlines.value()) {
            <m-state [kind]="readOnly() ? 'offline' : 'error'" [message]="deadlines.error()!.userMessage" />
          } @else {
            <m-state kind="empty" title="No deadlines" message="None of your open tickets has a running SLA." />
          }
        }
      </ion-list>
    </ion-content>
  `,
  styles: `
    .presence {
      width: 10px;
      height: 10px;
      border-radius: 50%;
      margin-right: 6px;
      background: var(--ion-color-medium);
    }
    .presence[data-a='Online'] {
      background: var(--ion-color-success);
    }
    .presence[data-a='Away'] {
      background: var(--ion-color-warning);
    }
    .presence[data-a='Busy'] {
      background: var(--ion-color-danger);
    }
    .hello {
      padding: 16px 16px 0;
    }
    .hello h1 {
      margin: 0;
      font-size: 1.4rem;
    }
    .hello p {
      margin: 2px 0 0;
      color: var(--sd-text-2);
    }
    .section {
      margin: 16px 16px 8px;
      font-size: 0.8rem;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      color: var(--sd-text-2);
    }
    .tiles {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));
      gap: 10px;
      padding: 0 16px 8px;
    }
    .tile {
      display: grid;
      grid-template-columns: auto 1fr;
      grid-template-rows: auto auto;
      column-gap: 10px;
      align-items: center;
      min-height: 64px;
      padding: 12px;
      border-radius: var(--sd-radius-lg, 12px);
      border: 1px solid var(--sd-border);
      background: var(--sd-surface);
      color: var(--sd-text);
      text-decoration: none;
    }
    .tile:focus-visible {
      outline: 2px solid var(--ion-color-primary);
      outline-offset: 2px;
    }
    .tile ion-icon {
      grid-row: span 2;
      font-size: 24px;
      color: var(--ion-color-primary);
    }
    .tile[data-tone='danger'] ion-icon {
      color: var(--ion-color-danger);
    }
    .tile[data-tone='warning'] ion-icon {
      color: var(--ion-color-warning);
    }
    .tile[data-tone='medium'] ion-icon {
      color: var(--sd-text-2);
    }
    .count {
      font-size: 1.4rem;
      font-weight: 700;
      line-height: 1.1;
    }
    .label {
      font-size: 0.8rem;
      color: var(--sd-text-2);
    }
    .approvals {
      margin: 8px 16px 0;
      border-radius: var(--sd-radius-lg, 12px);
      overflow: hidden;
      border: 1px solid var(--sd-warning-border);
    }
    .end {
      display: flex;
      flex-direction: column;
      align-items: flex-end;
      gap: 4px;
    }
    h3 {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
  `,
})
export class AgentHomePage implements OnInit {
  protected readonly queues = inject(QueueCountsStore);
  private readonly availabilityStore = inject(AvailabilityStore);
  private readonly me = inject(MeStore);
  private readonly snapshot = inject(SessionSnapshotStore);
  private readonly session = inject(SessionCoordinator);
  private readonly tickets = inject(TicketsApi);
  private readonly approvals = inject(ApprovalsApi);
  private readonly sheets = inject(ActionSheetController);
  private readonly toasts = inject(MobileToasts);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly tiles = TILES;
  protected readonly priorityColor = priorityColor;
  protected readonly readOnly = this.session.readOnly;
  /** Availability lives on the server: hidden in an offline session (the saved copy would be stale). */
  protected readonly live = this.session.live;
  protected readonly counts = this.queues.counts;
  protected readonly availability = this.availabilityStore.current;
  protected readonly canDecide = computed(() => this.me.can('approvals.decide'));

  protected readonly deadlines = cachedResource<TicketListItemDto[]>({
    key: () => 'home:deadlines',
    kind: 'list',
    fetch: () =>
      this.tickets.list({ mine: true, statusCategory: 'open', sort: 'due', dir: 'asc', limit: 6 }, { silent: true }).pipe(map((p) => p.items)),
  });
  protected readonly dueNext = computed(() => (this.deadlines.value() ?? []).filter((t) => t.dueAt || t.firstResponseDueAt));

  private readonly pending = cachedResource<ApprovalDto[]>({
    key: () => 'home:approvals',
    kind: 'meta',
    fetch: () => this.approvals.list('Pending'),
  });
  protected readonly pendingApprovals = computed(() => (this.pending.value() ?? []).filter((a) => a.status === 'Pending').length);

  protected readonly firstName = computed(() => {
    const s = this.snapshot.current();
    const name = this.me.user()?.displayName ?? (s?.kind === 'agent' ? s.displayName : '');
    return name.split(' ')[0] || 'there';
  });
  protected readonly tenantName = computed(() => {
    const s = this.snapshot.current();
    return this.me.user()?.tenantName ?? (s?.kind === 'agent' ? s.tenantName : '');
  });

  ngOnInit(): void {
    void this.loadAll();
    const sub = this.session.refresh$.subscribe(() => void this.loadAll());
    this.destroyRef.onDestroy(() => sub.unsubscribe());
  }

  protected async refresh(event: Event): Promise<void> {
    await this.loadAll();
    await (event.target as HTMLIonRefresherElement).complete();
  }

  protected async chooseAvailability(): Promise<void> {
    const sheet = await this.sheets.create({
      header: 'Your availability',
      subHeader: 'Routing only assigns new tickets while you are Online.',
      buttons: [
        ...AVAILABILITIES.map((a) => ({
          text: a === this.availability() ? `${a} (current)` : a,
          handler: () => void this.setAvailability(a),
        })),
        { text: 'Cancel', role: 'cancel' },
      ],
    });
    await sheet.present();
  }

  private async setAvailability(value: Availability): Promise<void> {
    try {
      await this.availabilityStore.set(value);
      void this.toasts.success(`You are ${value}.`);
    } catch (e) {
      void this.toasts.error(ApiError.from(e).userMessage);
    }
  }

  private async loadAll(): Promise<void> {
    await Promise.all([this.queues.resource.load(), this.deadlines.load(), this.canDecide() ? this.pending.load() : Promise.resolve()]);
  }
}
