import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { IonIcon } from '@ionic/angular/standalone';
import { NetworkService } from '../core/network.service';
import { OutboxService } from '../core/outbox.service';
import { SessionCoordinator } from '../core/session-coordinator';
import { savedLabel } from './format';

/**
 * Connectivity strip under a page header: offline / server unreachable / offline session, how many messages wait in
 * the outbox, and (with `staleSince`) that the screen shows a saved copy. Renders nothing when all is well.
 */
@Component({
  selector: 'm-offline-banner',
  imports: [IonIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (text(); as t) {
      <div class="banner" [class.warn]="offline()" role="status" aria-live="polite">
        <ion-icon [name]="offline() ? 'cloud-offline-outline' : 'time-outline'" aria-hidden="true" />
        <span>{{ t }}</span>
      </div>
    }
  `,
  styles: `
    .banner {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 8px 16px;
      font-size: 0.85rem;
      background: var(--sd-info-bg);
      color: var(--sd-text);
      border-bottom: 1px solid var(--sd-info-border);
    }
    .banner.warn {
      background: var(--sd-warning-bg);
      border-bottom-color: var(--sd-warning-border);
    }
    ion-icon {
      font-size: 18px;
      flex: none;
    }
  `,
})
export class OfflineBanner {
  /** When the screen shows cached data: the time it was saved. */
  readonly staleSince = input<number | null>(null);

  private readonly network = inject(NetworkService);
  private readonly session = inject(SessionCoordinator);
  private readonly outbox = inject(OutboxService);

  protected readonly offline = computed(() => !this.network.online() || this.session.offlineSession());

  protected readonly text = computed(() => {
    const parts: string[] = [];
    if (this.session.offlineSession() || !this.network.online()) {
      parts.push(
        this.network.reason() === 'device'
          ? 'You are offline — showing saved data, read-only.'
          : 'Cannot reach SupportDesk — showing saved data, read-only.',
      );
    }
    const pending = this.outbox.pendingCount();
    if (pending > 0) parts.push(`${pending} message${pending === 1 ? '' : 's'} waiting to send.`);
    const stale = this.staleSince();
    if (stale && !parts.length) parts.push(`Showing the copy ${savedLabel(stale)} — refreshing…`);
    else if (stale && this.offline()) parts.push(`Copy ${savedLabel(stale)}.`);
    return parts.join(' ');
  });
}
