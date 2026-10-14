import { ChangeDetectionStrategy, Component, OnInit, computed, inject } from '@angular/core';
import { Router } from '@angular/router';
import {
  IonBadge,
  IonButton,
  IonButtons,
  IonContent,
  IonHeader,
  IonIcon,
  IonItem,
  IonLabel,
  IonList,
  IonRefresher,
  IonRefresherContent,
  IonTitle,
  IonToolbar,
} from '@ionic/angular/standalone';
import { ApiError, ClockService, NotificationDto, formatRelative, severityRank } from '@sd/core';
import { mobileRouteFor } from '../core/links';
import { SessionCoordinator } from '../core/session-coordinator';
import { MobileToasts } from '../core/ui-services';
import { OfflineBanner } from '../shared/offline-banner.component';
import { StateView } from '../shared/state-view.component';
import { AgentNotificationsStore } from './agent-stores';

/** Agent notifications: newest first (live over SignalR), tap to open the ticket, acknowledge critical alerts. */
@Component({
  selector: 'm-agent-notifications',
  imports: [
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
    IonItem,
    IonLabel,
    IonBadge,
    OfflineBanner,
    StateView,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ion-header>
      <ion-toolbar>
        <ion-title>Notifications</ion-title>
        <ion-buttons slot="end">
          <ion-button [disabled]="readOnly() || !store.unread()" (click)="markAll()">Mark all read</ion-button>
        </ion-buttons>
      </ion-toolbar>
      <m-offline-banner [staleSince]="store.resource.staleSince()" />
    </ion-header>
    <ion-content>
      <ion-refresher slot="fixed" (ionRefresh)="refresh($event)"><ion-refresher-content /></ion-refresher>
      @if (items().length) {
        <ion-list lines="full">
          @for (n of items(); track n.id) {
            <ion-item [button]="true" [detail]="false" [class.unread]="!n.readAt" (click)="open(n)">
              <ion-icon slot="start" [name]="icon(n)" [color]="color(n)" aria-hidden="true" />
              <ion-label class="ion-text-wrap">
                <h3>
                  @if (!n.readAt) {
                    <span class="sr-only">Unread: </span>
                  }
                  {{ n.title }}
                </h3>
                @if (n.body) {
                  <p>{{ n.body }}</p>
                }
                <p class="when">{{ rel(n.createdAt) }}</p>
                @if (n.requiresAck && !n.acknowledgedAt) {
                  <ion-button size="small" color="danger" [disabled]="readOnly()" (click)="ack(n, $event)">Acknowledge</ion-button>
                } @else if (n.acknowledgedAt) {
                  <ion-badge color="success">Acknowledged</ion-badge>
                }
              </ion-label>
            </ion-item>
          }
        </ion-list>
      } @else if (store.resource.loading()) {
        <m-state kind="loading" title="Loading notifications" />
      } @else if (store.resource.error()) {
        <m-state [kind]="readOnly() ? 'offline' : 'error'" [message]="store.resource.error()!.userMessage" actionLabel="Try again" (action)="store.resource.load()" />
      } @else {
        <m-state kind="empty" title="You are all caught up" message="Assignments, mentions and SLA alerts show up here." />
      }
    </ion-content>
  `,
  styles: `
    .unread {
      --background: var(--sd-info-bg);
    }
    .unread h3 {
      font-weight: 700;
    }
    .when {
      font-size: 0.75rem;
    }
    ion-button[size='small'] {
      margin-top: 6px;
    }
    .sr-only {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip: rect(0 0 0 0);
    }
  `,
})
export class AgentNotificationsPage implements OnInit {
  protected readonly store = inject(AgentNotificationsStore);
  private readonly router = inject(Router);
  private readonly session = inject(SessionCoordinator);
  private readonly clock = inject(ClockService);
  private readonly toasts = inject(MobileToasts);

  protected readonly items = this.store.items;
  protected readonly readOnly = this.session.readOnly;
  protected readonly unread = computed(() => this.store.unread());

  ngOnInit(): void {
    void this.store.resource.load();
  }

  protected async refresh(event: Event): Promise<void> {
    await this.store.resource.load();
    await (event.target as HTMLIonRefresherElement).complete();
  }

  protected async open(n: NotificationDto): Promise<void> {
    if (!this.readOnly()) void this.store.markRead(n);
    const route = mobileRouteFor(n.link, 'agent');
    if (route) await this.router.navigateByUrl(route);
    else if (n.link) void this.toasts.show('This item opens in the desktop workbench.');
  }

  protected async ack(n: NotificationDto, event: Event): Promise<void> {
    event.stopPropagation();
    try {
      await this.store.acknowledge(n);
      void this.toasts.success('Acknowledged.');
    } catch (e) {
      void this.toasts.error(ApiError.from(e).userMessage);
    }
  }

  protected async markAll(): Promise<void> {
    try {
      await this.store.markAllRead();
    } catch (e) {
      void this.toasts.error(ApiError.from(e).userMessage);
    }
  }

  protected icon(n: NotificationDto): string {
    if (n.type.startsWith('sla.')) return 'alarm-outline';
    if (n.type.startsWith('approval.')) return 'checkmark-circle-outline';
    if (n.type === 'ticket.mentioned') return 'at-outline';
    if (n.type === 'ticket.assigned') return 'person-add-outline';
    if (n.type === 'ticket.customer_replied') return 'chatbubble-ellipses-outline';
    return 'notifications-outline';
  }

  protected color(n: NotificationDto): string {
    const rank = severityRank(n.severity);
    return rank >= 3 ? 'danger' : rank === 2 ? 'warning' : 'primary';
  }

  protected rel(value: string): string {
    return formatRelative(value, this.clock.coarseNow());
  }
}
