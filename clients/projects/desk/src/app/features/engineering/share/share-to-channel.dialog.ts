import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError, CollaborationApi, CollaborationShareTargetDto, Guid } from '@sd/core';
import { SdButton, SdDialog, SdEmptyState, SdFormField, SdInput, SdRadio, SdRadioGroup, SdSkeleton } from '@sd/ui';

export interface ShareToChannelData {
  ticketId: Guid;
  ticketLabel: string;
  comment: string;
}

/** Shares a ticket (summary + deep link) to a Microsoft Teams or Slack channel. Closes with the channel name, or null. */
@Component({
  selector: 'desk-share-to-channel-dialog',
  imports: [FormsModule, SdDialog, SdButton, SdFormField, SdInput, SdRadio, SdRadioGroup, SdEmptyState, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Share to Teams / Slack" [description]="'Posts ' + data.ticketLabel + ' with a link back to the ticket.'">
      @if (loading()) {
        <sd-skeleton [lines]="3" height="20px" />
      } @else if (loadError()) {
        <p class="error" role="alert">{{ loadError() }}</p>
      } @else if (targets().length === 0) {
        <sd-empty-state compact icon="messages" title="No channels" message="An administrator can add Teams or Slack channels under Administration → Teams & Slack." />
      } @else {
        <form id="share-form" class="form" (ngSubmit)="share()">
          <sd-form-field label="Channel" required>
            <sd-radio-group [value]="channelId()" (valueChange)="channelId.set($event)">
              @for (t of targets(); track t.id) {
                <sd-radio [value]="t.id" [description]="t.provider === 'Slack' ? 'Slack' : 'Microsoft Teams'">{{ t.name }}</sd-radio>
              }
            </sd-radio-group>
          </sd-form-field>
          <sd-form-field label="Message" optional hint="Shown above the ticket summary.">
            <textarea sdInput name="comment" rows="3" maxlength="1000" [ngModel]="comment()" (ngModelChange)="comment.set($event)"></textarea>
          </sd-form-field>
          @if (error()) {
            <p class="error" role="alert">{{ error() }}</p>
          }
        </form>
      }
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close(null)">Cancel</button>
        <button sdButton type="submit" form="share-form" variant="primary" icon="share" [loading]="sending()" [disabled]="!channelId()">Share</button>
      </div>
    </sd-dialog>
  `,
  styles: `
    .form {
      display: grid;
      gap: var(--sd-space-3);
    }
    .error {
      color: var(--sd-danger);
    }
  `,
})
export class ShareToChannelDialog implements OnInit {
  protected readonly data = inject<ShareToChannelData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<string | null>>(DialogRef);
  private readonly api = inject(CollaborationApi);

  protected readonly targets = signal<CollaborationShareTargetDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);
  protected readonly channelId = signal<Guid | null>(null);
  protected readonly comment = signal(this.data.comment);
  protected readonly sending = signal(false);
  protected readonly error = signal<string | null>(null);

  ngOnInit(): void {
    this.api.shareTargets().subscribe({
      next: (t) => {
        this.targets.set(t);
        this.channelId.set(t[0]?.id ?? null);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.loadError.set(err instanceof ApiError ? err.userMessage : 'Channels could not be loaded.');
      },
    });
  }

  protected share(): void {
    const channelId = this.channelId();
    if (!channelId) return;
    this.sending.set(true);
    this.error.set(null);
    this.api.share(this.data.ticketId, { channelId, comment: this.comment().trim() || null }).subscribe({
      next: (r) => {
        this.sending.set(false);
        if (r.success) this.ref.close(this.targets().find((t) => t.id === channelId)?.name ?? 'the channel');
        else this.error.set(r.error ?? `The channel answered HTTP ${r.statusCode ?? '—'}.`);
      },
      error: (err: unknown) => {
        this.sending.set(false);
        this.error.set(err instanceof ApiError ? err.userMessage : 'The ticket could not be shared.');
      },
    });
  }
}
