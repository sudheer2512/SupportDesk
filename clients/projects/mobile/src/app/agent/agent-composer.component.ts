import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActionSheetController, IonButton, IonIcon, IonSegment, IonSegmentButton, IonLabel, IonSpinner, IonTextarea } from '@ionic/angular/standalone';
import { ApiError, ReplyTemplateDto, TicketDetailDto, WorkbenchApi } from '@sd/core';
import { firstValueFrom } from 'rxjs';
import { DraftStore } from '../core/drafts.service';
import { NetworkService } from '../core/network.service';
import { OfflineCache } from '../core/offline-cache.service';
import { OutboxEntry, OutboxService } from '../core/outbox.service';
import { MobileToasts } from '../core/ui-services';
import { AttachmentPicker } from '../shared/attachment-picker.component';
import { htmlToText, textToHtml } from '../shared/format';

export type ComposerMode = 'reply' | 'note';

/**
 * Reply / internal-note composer for the agent ticket screen. Text is kept as a local draft while typing; sending
 * goes through the outbox (idempotency key, offline queue, reply-collision handling). Templates are rendered by the
 * server for this ticket (offline: inserted unrendered). Attachments come from the camera or files.
 */
@Component({
  selector: 'm-agent-composer',
  imports: [FormsModule, IonSegment, IonSegmentButton, IonLabel, IonTextarea, IonButton, IonIcon, IonSpinner, AttachmentPicker],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="composer" [class.note]="mode() === 'note'">
      @if (ticket().can.reply && ticket().can.note) {
        <ion-segment [value]="mode()" (ionChange)="setMode($any($event).detail.value)" aria-label="Message type">
          <ion-segment-button value="reply"><ion-label>Reply</ion-label></ion-segment-button>
          <ion-segment-button value="note"><ion-label>Internal note</ion-label></ion-segment-button>
        </ion-segment>
      }
      <ion-textarea
        [autoGrow]="true"
        [rows]="1"
        [maxlength]="20000"
        [placeholder]="mode() === 'reply' ? 'Reply to ' + (ticket().contactName || 'the customer') + '…' : 'Internal note (agents only)…'"
        [attr.aria-label]="mode() === 'reply' ? 'Reply to the customer' : 'Internal note'"
        [ngModel]="text()"
        (ngModelChange)="onText($event)"
        (ionFocus)="activity.emit(mode() === 'reply' ? 'replying' : 'noting')"
        (ionBlur)="activity.emit('viewing')"
      />
      <m-attachment-picker [(files)]="files" [compact]="true">
        <ion-button fill="clear" size="small" aria-label="Insert a template" (click)="pickTemplate()" [disabled]="busy()">
          <ion-icon slot="icon-only" name="document-text-outline" aria-hidden="true" />
        </ion-button>
        <span class="hint">{{ hint() }}</span>
        <ion-button size="small" [disabled]="busy() || !canSend()" (click)="send()">
          @if (busy()) {
            <ion-spinner name="crescent" aria-label="Sending" />
          } @else {
            <ion-icon slot="end" [name]="online() ? 'send' : 'cloud-upload-outline'" aria-hidden="true" />
            {{ online() ? (mode() === 'reply' ? 'Send' : 'Add note') : 'Queue' }}
          }
        </ion-button>
      </m-attachment-picker>
    </div>
  `,
  styles: `
    .composer {
      padding: 6px 12px calc(6px + var(--ion-safe-area-bottom, 0px));
      background: var(--sd-surface);
      border-top: 1px solid var(--sd-border);
    }
    .composer.note {
      background: var(--sd-note-bg);
      border-top-color: var(--sd-note-border);
    }
    ion-segment {
      margin-bottom: 4px;
      min-height: 36px;
    }
    ion-segment-button {
      min-height: 36px;
    }
    ion-textarea {
      --padding-start: 10px;
      --padding-end: 10px;
      --padding-top: 8px;
      --padding-bottom: 8px;
      min-height: 44px;
      max-height: 35vh;
      overflow: auto;
      border: 1px solid var(--sd-border);
      border-radius: 10px;
      background: var(--sd-bg);
    }
    .hint {
      flex: 1;
      font-size: 0.72rem;
      line-height: 1.2;
      color: var(--sd-text-2);
      text-align: right;
    }
  `,
})
export class AgentComposer {
  readonly ticket = input.required<TicketDetailDto>();
  /** Last conversation entry the agent has seen (reply-collision detection). */
  readonly seenSeq = input(0);
  readonly sent = output<OutboxEntry | null>();
  readonly activity = output<'replying' | 'noting' | 'viewing'>();

  private readonly outbox = inject(OutboxService);
  private readonly drafts = inject(DraftStore);
  private readonly network = inject(NetworkService);
  private readonly workbench = inject(WorkbenchApi);
  private readonly cache = inject(OfflineCache);
  private readonly sheets = inject(ActionSheetController);
  private readonly toasts = inject(MobileToasts);

  protected readonly mode = signal<ComposerMode>('reply');
  protected readonly text = signal('');
  protected readonly files = signal<File[]>([]);
  protected readonly busy = signal(false);
  protected readonly online = this.network.online;
  protected readonly canSend = computed(() => this.text().trim().length > 0 || this.files().length > 0);
  protected readonly hint = computed(() => (this.online() ? '' : 'Offline · sends later'));
  private loadedFor: string | null = null;

  constructor() {
    // Restore the draft whenever the ticket or the mode changes; light agents can only write notes.
    effect(() => {
      const t = this.ticket();
      const mode = this.mode();
      untracked(() => {
        if (!t.can.reply && mode === 'reply') {
          this.mode.set('note');
          return;
        }
        const key = this.draftKey(t.id, mode);
        if (this.loadedFor === key) return;
        this.loadedFor = key;
        this.text.set('');
        void this.drafts.get<string>(key).then((text) => {
          // Never overwrite what the user started typing while the draft was loading.
          if (this.loadedFor === key && text && !this.text()) this.text.set(text);
        });
      });
    });
  }

  /** Puts an unsent outbox entry back into the composer (outbox "Edit"). */
  restore(entry: OutboxEntry): void {
    this.mode.set(entry.kind === 'agent-note' ? 'note' : 'reply');
    this.loadedFor = this.draftKey(this.ticket().id, this.mode());
    this.text.set(entry.bodyText);
    this.files.set(entry.files.map((f) => (f.blob instanceof File ? f.blob : new File([f.blob], f.name, { type: f.type }))));
    this.drafts.save(this.loadedFor, entry.bodyText);
  }

  protected setMode(mode: ComposerMode): void {
    this.mode.set(mode === 'note' ? 'note' : 'reply');
  }

  protected onText(value: string | null | undefined): void {
    this.text.set(value ?? '');
    this.drafts.save(this.draftKey(this.ticket().id, this.mode()), this.text());
  }

  protected async pickTemplate(): Promise<void> {
    let templates: ReplyTemplateDto[] = [];
    try {
      templates = await firstValueFrom(this.workbench.templates());
      void this.cache.write('templates', 'meta', templates);
    } catch {
      templates = (await this.cache.read<ReplyTemplateDto[]>('templates'))?.value ?? [];
    }
    if (!templates.length) {
      void this.toasts.show(this.online() ? 'No reply templates are available to you.' : 'Templates are not saved on this device yet.');
      return;
    }
    const sheet = await this.sheets.create({
      header: 'Insert a template',
      buttons: [
        ...templates.slice(0, 25).map((t) => ({ text: t.category ? `${t.name} · ${t.category}` : t.name, handler: () => void this.insertTemplate(t) })),
        { text: 'Cancel', role: 'cancel' },
      ],
    });
    await sheet.present();
  }

  private async insertTemplate(t: ReplyTemplateDto): Promise<void> {
    let html = t.bodyHtml;
    if (this.online()) {
      try {
        html = (await firstValueFrom(this.workbench.renderTemplate(this.ticket().id, t.id))).bodyHtml;
      } catch (e) {
        void this.toasts.error(ApiError.from(e).userMessage);
      }
    } else {
      void this.toasts.show('Offline: placeholders like {{contact.name}} are not filled in.', 'warning');
    }
    const insert = htmlToText(html);
    const current = this.text().trimEnd();
    this.onText(current ? `${current}\n\n${insert}` : insert);
  }

  protected async send(): Promise<void> {
    const t = this.ticket();
    const text = this.text().trim();
    if (!text && !this.files().length) return;
    const mode = t.can.reply ? this.mode() : 'note';
    this.busy.set(true);
    try {
      const entry = await this.outbox.enqueue({
        kind: mode === 'reply' ? 'agent-reply' : 'agent-note',
        ticketId: t.id,
        label: `${t.displayNumber} · ${t.subject}`,
        bodyHtml: textToHtml(text),
        bodyText: text,
        basedOnSeq: mode === 'reply' ? Math.max(t.lastThreadSeq, this.seenSeq()) : null,
        files: this.files(),
      });
      this.text.set('');
      this.files.set([]);
      await this.drafts.remove(this.draftKey(t.id, mode));
      const outcome = await this.outbox.settle(entry.id);
      if (!outcome) void this.toasts.success(mode === 'reply' ? 'Reply sent.' : 'Note added.');
      else if (outcome.status === 'conflict') void this.toasts.show('New messages arrived while you were writing — review them below.', 'warning', 5000);
      else if (outcome.status === 'failed') void this.toasts.error(outcome.lastError ?? 'The message was not accepted.');
      else void this.toasts.show('Saved on this device — it will be sent when you are back online.', 'info');
      this.sent.emit(outcome);
    } catch (e) {
      void this.toasts.error(ApiError.from(e).userMessage);
    } finally {
      this.busy.set(false);
    }
  }

  private draftKey(ticketId: string, mode: ComposerMode): string {
    return `ticket:${ticketId}:${mode}`;
  }
}
