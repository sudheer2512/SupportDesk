import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { IonButton, IonIcon, IonSpinner } from '@ionic/angular/standalone';
import { OutboxEntry, OutboxService } from '../core/outbox.service';
import { fileSize } from './format';

/**
 * One unsent message from the outbox with its state and the actions that fit it:
 * queued (waiting for a connection) → Retry now · Discard; failed → Retry · Edit · Discard;
 * conflict (others replied meanwhile) → shows the new messages, then Send anyway · Edit · Discard.
 * `edit` hands the entry back so the composer can restore its text.
 */
@Component({
  selector: 'm-outbox-item',
  imports: [IonButton, IonIcon, IonSpinner],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let e = entry();
    <article class="item" [attr.data-status]="e.status" [attr.aria-label]="'Unsent ' + kindLabel()">
      <header>
        @if (e.status === 'sending') {
          <ion-spinner name="dots" aria-hidden="true" />
        } @else {
          <ion-icon [name]="icon()" aria-hidden="true" />
        }
        <strong>{{ statusLabel() }}</strong>
        <span class="kind">{{ kindLabel() }}</span>
      </header>
      @if (showLabel()) {
        <div class="label">{{ e.label }}</div>
      }
      <p class="body">{{ e.bodyText }}</p>
      @if (e.files.length) {
        <div class="files">
          @for (f of e.files; track $index) {
            <span><ion-icon name="attach-outline" aria-hidden="true" /> {{ f.name }} ({{ size(f.size) }})</span>
          }
        </div>
      }
      @if (e.lastError && e.status !== 'sending') {
        <p class="error">{{ e.lastError }}</p>
      }
      @if (e.status === 'conflict' && e.conflict?.length) {
        <div class="conflict">
          <p><strong>New since you wrote this:</strong></p>
          @for (t of e.conflict; track t.id) {
            <blockquote>
              <cite>{{ t.authorName || 'Someone' }}</cite>
              <span>{{ t.bodyText }}</span>
            </blockquote>
          }
        </div>
      }
      @if (e.status !== 'sending') {
        <div class="actions">
          @if (e.status === 'conflict') {
            <ion-button size="small" (click)="outbox.sendAnyway(e.id)">Send anyway</ion-button>
          } @else {
            <ion-button size="small" fill="outline" (click)="outbox.retry(e.id)">{{ e.status === 'failed' ? 'Retry' : 'Retry now' }}</ion-button>
          }
          @if (e.status !== 'queued' && editable()) {
            <ion-button size="small" fill="clear" (click)="edit.emit(e)">Edit</ion-button>
          }
          <ion-button size="small" fill="clear" color="danger" (click)="outbox.discard(e.id)">Discard</ion-button>
        </div>
      }
    </article>
  `,
  styles: `
    .item {
      margin: 8px 12px;
      padding: 10px 12px;
      border: 1px dashed var(--sd-border-strong);
      border-radius: var(--sd-radius-lg, 12px);
      background: var(--sd-surface);
    }
    [data-status='conflict'],
    [data-status='failed'] {
      border-style: solid;
      border-color: var(--sd-warning-border);
      background: var(--sd-warning-bg);
    }
    header {
      display: flex;
      align-items: center;
      gap: 6px;
      font-size: 0.85rem;
    }
    .kind {
      margin-left: auto;
      color: var(--sd-text-2);
    }
    .label {
      margin-top: 4px;
      font-size: 0.8rem;
      color: var(--sd-text-2);
    }
    .body {
      margin: 6px 0;
      white-space: pre-wrap;
      overflow-wrap: anywhere;
    }
    .files {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      font-size: 0.8rem;
      color: var(--sd-text-2);
    }
    .error {
      margin: 4px 0;
      color: var(--sd-danger);
      font-size: 0.85rem;
    }
    blockquote {
      margin: 6px 0;
      padding: 6px 10px;
      border-left: 3px solid var(--sd-warning);
      background: var(--sd-surface);
      font-size: 0.85rem;
    }
    cite {
      display: block;
      font-style: normal;
      font-weight: 600;
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: 4px;
    }
  `,
})
export class OutboxItem {
  readonly entry = input.required<OutboxEntry>();
  /** Show the ticket label (outbox list on Profile); hidden inside a ticket. */
  readonly showLabel = input(false);
  readonly editable = input(true);
  readonly edit = output<OutboxEntry>();

  protected readonly outbox = inject(OutboxService);

  protected readonly icon = computed(() => {
    switch (this.entry().status) {
      case 'conflict':
        return 'git-compare-outline';
      case 'failed':
        return 'alert-circle-outline';
      default:
        return 'cloud-upload-outline';
    }
  });

  protected readonly statusLabel = computed(() => {
    switch (this.entry().status) {
      case 'sending':
        return 'Sending…';
      case 'conflict':
        return 'Not sent — new messages arrived';
      case 'failed':
        return 'Not sent';
      default:
        return 'Waiting to send';
    }
  });

  protected readonly kindLabel = computed(() => {
    switch (this.entry().kind) {
      case 'agent-note':
        return 'Internal note';
      case 'customer-ticket':
        return 'New ticket';
      default:
        return 'Reply';
    }
  });

  protected size(bytes: number): string {
    return fileSize(bytes);
  }
}
