import { ChangeDetectionStrategy, Component } from '@angular/core';
import { IonIcon, IonLabel, IonTabBar, IonTabButton, IonTabs } from '@ionic/angular/standalone';

/** Customer tab shell: Tickets, Help, Profile. */
@Component({
  selector: 'm-customer-tabs',
  imports: [IonTabs, IonTabBar, IonTabButton, IonIcon, IonLabel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ion-tabs>
      <ion-tab-bar slot="bottom">
        <ion-tab-button tab="tickets">
          <ion-icon name="ticket-outline" aria-hidden="true" />
          <ion-label>Tickets</ion-label>
        </ion-tab-button>
        <ion-tab-button tab="help">
          <ion-icon name="help-circle-outline" aria-hidden="true" />
          <ion-label>Help</ion-label>
        </ion-tab-button>
        <ion-tab-button tab="profile">
          <ion-icon name="person-circle-outline" aria-hidden="true" />
          <ion-label>Profile</ion-label>
        </ion-tab-button>
      </ion-tab-bar>
    </ion-tabs>
  `,
})
export class CustomerTabsPage {}
