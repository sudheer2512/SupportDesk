import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { IonButton, IonIcon, IonSkeletonText } from '@ionic/angular/standalone';

export type StateKind = 'loading' | 'empty' | 'error' | 'offline';

/**
 * Loading / empty / error / offline placeholder used by every screen.
 * `<m-state kind="empty" title="No tickets" message="…" actionLabel="Refresh" (action)="load()" />`
 */
@Component({
  selector: 'm-state',
  imports: [IonButton, IonIcon, IonSkeletonText],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (kind() === 'loading') {
      <div class="skeleton" role="status" aria-live="polite" [attr.aria-label]="title() || 'Loading'">
        @for (row of rows(); track row) {
          <div class="row">
            <ion-skeleton-text [animated]="true" style="width: 70%" />
            <ion-skeleton-text [animated]="true" style="width: 45%" />
          </div>
        }
      </div>
    } @else {
      <div class="state" [attr.role]="kind() === 'error' ? 'alert' : 'status'">
        <ion-icon [name]="icon()" aria-hidden="true" />
        <h2>{{ title() || defaultTitle() }}</h2>
        @if (message()) {
          <p>{{ message() }}</p>
        }
        @if (actionLabel()) {
          <ion-button fill="outline" (click)="action.emit()">{{ actionLabel() }}</ion-button>
        }
      </div>
    }
  `,
  styles: `
    .skeleton {
      padding: 8px 16px;
    }
    .row {
      padding: 12px 0;
      border-bottom: 1px solid var(--sd-border);
    }
    .state {
      display: flex;
      flex-direction: column;
      align-items: center;
      text-align: center;
      gap: 6px;
      padding: 48px 24px;
      color: var(--sd-text-2);
    }
    ion-icon {
      font-size: 44px;
      color: var(--sd-text-3, var(--sd-text-2));
    }
    h2 {
      margin: 6px 0 0;
      font-size: 1.05rem;
      color: var(--sd-text);
    }
    p {
      margin: 0 0 8px;
      max-width: 320px;
    }
  `,
})
export class StateView {
  readonly kind = input.required<StateKind>();
  readonly title = input('');
  readonly message = input('');
  readonly actionLabel = input('');
  readonly lines = input(5);
  readonly action = output<void>();

  protected readonly rows = computed(() => Array.from({ length: this.lines() }, (_, i) => i));
  protected readonly icon = computed(() => {
    switch (this.kind()) {
      case 'error':
        return 'alert-circle-outline';
      case 'offline':
        return 'cloud-offline-outline';
      default:
        return 'file-tray-outline';
    }
  });
  protected readonly defaultTitle = computed(() => {
    switch (this.kind()) {
      case 'error':
        return 'Something went wrong';
      case 'offline':
        return 'You are offline';
      default:
        return 'Nothing here yet';
    }
  });
}
