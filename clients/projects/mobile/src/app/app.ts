import { ChangeDetectionStrategy, Component } from '@angular/core';
import { IonApp, IonRouterOutlet } from '@ionic/angular/standalone';
import { addIcons } from 'ionicons';
import {
  helpCircleOutline,
  listOutline,
  logOutOutline,
  notificationsOutline,
  personCircleOutline,
  ticketOutline,
} from 'ionicons/icons';

@Component({
  selector: 'm-root',
  imports: [IonApp, IonRouterOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<ion-app><ion-router-outlet /></ion-app>`,
})
export class App {
  constructor() {
    addIcons({ listOutline, notificationsOutline, personCircleOutline, ticketOutline, helpCircleOutline, logOutOutline });
  }
}
