import { ChangeDetectionStrategy, Component } from '@angular/core';
import { IonIcon, IonLabel, IonTabBar, IonTabButton, IonTabs } from '@ionic/angular/standalone';

/** Agent tab shell: Queue, Notifications, Profile. */
@Component({
  selector: 'm-agent-tabs',
  imports: [IonTabs, IonTabBar, IonTabButton, IonIcon, IonLabel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ion-tabs>
      <ion-tab-bar slot="bottom">
        <ion-tab-button tab="queue">
          <ion-icon name="list-outline" aria-hidden="true" />
          <ion-label>Queue</ion-label>
        </ion-tab-button>
        <ion-tab-button tab="notifications">
          <ion-icon name="notifications-outline" aria-hidden="true" />
          <ion-label>Notifications</ion-label>
        </ion-tab-button>
        <ion-tab-button tab="profile">
          <ion-icon name="person-circle-outline" aria-hidden="true" />
          <ion-label>Profile</ion-label>
        </ion-tab-button>
      </ion-tab-bar>
    </ion-tabs>
  `,
})
export class AgentTabsPage {}
