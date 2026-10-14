import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { IonContent, IonHeader, IonTitle, IonToolbar } from '@ionic/angular/standalone';

/** Honest placeholder for tabs whose screens are still being built (title/text come from route data). */
@Component({
  selector: 'm-placeholder-page',
  imports: [IonHeader, IonToolbar, IonTitle, IonContent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ion-header>
      <ion-toolbar>
        <ion-title>{{ title() }}</ion-title>
      </ion-toolbar>
    </ion-header>
    <ion-content>
      <div class="m-card">
        <h2>This screen is being built</h2>
        <p>{{ text() }}</p>
      </div>
    </ion-content>
  `,
  styles: `
    h2 {
      margin: 0 0 6px;
      font-size: 1.1rem;
    }
    p {
      margin: 0;
      color: var(--sd-text-2);
    }
  `,
})
export class PlaceholderPage {
  readonly title = input('');
  readonly text = input('');
}
