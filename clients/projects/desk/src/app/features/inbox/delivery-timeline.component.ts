import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { ApiError, ConversationsApi, MessageDetailDto } from '@sd/core';
import { SdBadge, SdDateTimePipe, SdIcon, SdSkeleton } from '@sd/ui';
import { deliveryIcon, deliveryTone } from '../../shared/channel-ui';

/** Delivery attempts and status history of one outbound message (`GET /conversations/messages/{id}`). */
@Component({
  selector: 'desk-delivery-timeline',
  imports: [SdBadge, SdIcon, SdSkeleton, SdDateTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (error(); as e) {
      <p class="sd-small err" role="alert">{{ e }}</p>
    } @else if (!detail()) {
      <div aria-busy="true"><sd-skeleton [lines]="2" /></div>
    } @else if (detail()!.deliveries.length === 0) {
      <p class="sd-small sd-muted">No delivery attempts are recorded for this message.</p>
    } @else {
      @for (d of detail()!.deliveries; track d.id) {
        <div class="delivery">
          <div class="row">
            <sd-badge [tone]="tone(d.status)" [icon]="icon(d.status)" size="sm">{{ d.status }}</sd-badge>
            <span class="sd-small">to {{ d.recipient }}</span>
            <span class="sd-small sd-muted">· {{ d.attempts }} attempt{{ d.attempts === 1 ? '' : 's' }}</span>
            @if (d.providerMessageId) {
              <span class="sd-xs sd-muted sd-mono sd-truncate" [title]="d.providerMessageId">{{ d.providerMessageId }}</span>
            }
          </div>
          @if (d.error) {
            <p class="sd-small err"><sd-icon name="circle-alert" [size]="12" /> {{ d.error }}</p>
          }
          <ol class="history" aria-label="Status history">
            @for (h of d.statusHistory; track $index) {
              <li>
                <span class="dot" [attr.data-tone]="tone(h.status)" aria-hidden="true"></span>
                <span class="status">{{ h.status }}</span>
                <time class="sd-xs sd-muted" [attr.datetime]="h.at">{{ h.at | sdDateTime }}</time>
                @if (h.detail && h.status !== 'Sent') {
                  <span class="sd-xs sd-secondary detail">{{ h.detail }}</span>
                }
              </li>
            }
          </ol>
        </div>
      }
    }
  `,
  styles: `
    :host {
      display: block;
      margin-top: var(--sd-space-2);
      padding: var(--sd-space-3);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface-2);
      border: 1px solid var(--sd-border);
    }
    .delivery + .delivery {
      margin-top: var(--sd-space-3);
      padding-top: var(--sd-space-3);
      border-top: 1px solid var(--sd-divider);
    }
    .row {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: var(--sd-space-2);
    }
    .err {
      color: var(--sd-danger);
      margin: var(--sd-space-1) 0 0;
    }
    .history {
      list-style: none;
      margin: var(--sd-space-2) 0 0;
      padding: 0 0 0 var(--sd-space-1);
    }
    .history li {
      display: flex;
      align-items: baseline;
      flex-wrap: wrap;
      gap: var(--sd-space-2);
      padding: 2px 0;
      font-size: var(--sd-text-sm);
    }
    .dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: var(--sd-text-3);
      flex: none;
      align-self: center;
    }
    .dot[data-tone='success'] {
      background: var(--sd-success);
    }
    .dot[data-tone='danger'] {
      background: var(--sd-danger);
    }
    .dot[data-tone='warning'] {
      background: var(--sd-warning);
    }
    .dot[data-tone='primary'] {
      background: var(--sd-primary);
    }
    .status {
      font-weight: var(--sd-weight-medium);
    }
    .detail {
      flex-basis: 100%;
      padding-left: 16px;
    }
  `,
})
export class DeliveryTimelineComponent {
  private readonly api = inject(ConversationsApi);

  /** Message id; bump `version` to refetch after a realtime delivery update. */
  readonly messageId = input.required<string>();
  readonly version = input(0);

  protected readonly detail = signal<MessageDetailDto | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly tone = deliveryTone;
  protected readonly icon = deliveryIcon;

  constructor() {
    effect((onCleanup) => {
      const id = this.messageId();
      this.version();
      untracked(() => this.error.set(null));
      const sub = this.api.message(id).subscribe({
        next: (d) => this.detail.set(d),
        error: (e: unknown) => this.error.set(ApiError.from(e).userMessage),
      });
      onCleanup(() => sub.unsubscribe());
    });
  }
}
