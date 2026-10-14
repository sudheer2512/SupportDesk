import { ChangeDetectionStrategy, Component, OnInit, computed, inject } from '@angular/core';
import { IonBadge, IonIcon, IonLabel, IonTabBar, IonTabButton, IonTabs } from '@ionic/angular/standalone';
import { AgentNotificationsStore, QueueCountsStore } from '../agent/agent-stores';
import { SessionCoordinator } from '../core/session-coordinator';

/** Agent tab shell: Home, Tickets, Alerts, Approvals, Profile — with live badges. */
@Component({
  selector: 'm-agent-tabs',
  imports: [IonTabs, IonTabBar, IonTabButton, IonIcon, IonLabel, IonBadge],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ion-tabs>
      <ion-tab-bar slot="bottom">
        <ion-tab-button tab="home">
          <ion-icon name="home-outline" aria-hidden="true" />
          <ion-label>Home</ion-label>
        </ion-tab-button>
        <ion-tab-button tab="tickets" [attr.aria-label]="mine() ? 'Tickets, ' + mine() + ' assigned to you' : 'Tickets'">
          <ion-icon name="file-tray-full-outline" aria-hidden="true" />
          <ion-label>Tickets</ion-label>
          @if (mine()) {
            <ion-badge color="primary" aria-hidden="true">{{ mine() }}</ion-badge>
          }
        </ion-tab-button>
        <ion-tab-button tab="notifications" [attr.aria-label]="unread() ? 'Alerts, ' + unread() + ' unread' : 'Alerts'">
          <ion-icon name="notifications-outline" aria-hidden="true" />
          <ion-label>Alerts</ion-label>
          @if (unread()) {
            <ion-badge color="danger" aria-hidden="true">{{ unread() > 99 ? '99+' : unread() }}</ion-badge>
          }
        </ion-tab-button>
        <ion-tab-button tab="approvals">
          <ion-icon name="checkmark-circle-outline" aria-hidden="true" />
          <ion-label>Approvals</ion-label>
        </ion-tab-button>
        <ion-tab-button tab="profile">
          <ion-icon name="person-circle-outline" aria-hidden="true" />
          <ion-label>Me</ion-label>
        </ion-tab-button>
      </ion-tab-bar>
    </ion-tabs>
  `,
})
export class AgentTabsPage implements OnInit {
  private readonly notifications = inject(AgentNotificationsStore);
  private readonly queues = inject(QueueCountsStore);
  private readonly session = inject(SessionCoordinator);

  protected readonly unread = this.notifications.unread;
  protected readonly mine = computed(() => this.queues.count('mine') ?? 0);

  ngOnInit(): void {
    void this.notifications.resource.load();
    if (this.session.live() || this.session.offlineSession()) void this.queues.resource.load();
  }
}
