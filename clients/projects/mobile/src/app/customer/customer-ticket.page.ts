import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  AlertController,
  IonBackButton,
  IonBadge,
  IonButton,
  IonButtons,
  IonContent,
  IonFooter,
  IonHeader,
  IonIcon,
  IonItem,
  IonLabel,
  IonList,
  IonNote,
  IonRefresher,
  IonRefresherContent,
  IonSpinner,
  IonTextarea,
  IonTitle,
  IonToolbar,
} from '@ionic/angular/standalone';
import {
  ApiError,
  ClockService,
  PortalAttachmentDto,
  PortalMessageDto,
  PortalRealtimeService,
  PortalTicketDetailDto,
  PortalTicketsApi,
  formatDateTime,
  formatRelative,
  saveBlob,
} from '@sd/core';
import { debounceTime, firstValueFrom, merge } from 'rxjs';
import { cachedResource } from '../core/cached-resource';
import { DraftStore } from '../core/drafts.service';
import { ModeService } from '../core/mobile-session';
import { NetworkService } from '../core/network.service';
import { OutboxEntry, OutboxService } from '../core/outbox.service';
import { SessionCoordinator } from '../core/session-coordinator';
import { MobileToasts } from '../core/ui-services';
import { AttachmentPicker } from '../shared/attachment-picker.component';
import { OfflineBanner } from '../shared/offline-banner.component';
import { OutboxItem } from '../shared/outbox-item.component';
import { StateView } from '../shared/state-view.component';
import { fileSize, statusColor, textToHtml } from '../shared/format';

/**
 * Customer ticket: status, details, the public conversation with attachments, reply composer (offline-queued),
 * resolution confirmation, reopen and the satisfaction (CSAT) rating on resolved tickets. Live over /hubs/portal.
 */
@Component({
  selector: 'm-customer-ticket',
  imports: [
    FormsModule,
    IonHeader,
    IonToolbar,
    IonTitle,
    IonButtons,
    IonBackButton,
    IonButton,
    IonIcon,
    IonContent,
    IonFooter,
    IonRefresher,
    IonRefresherContent,
    IonBadge,
    IonList,
    IonItem,
    IonLabel,
    IonNote,
    IonTextarea,
    IonSpinner,
    AttachmentPicker,
    OfflineBanner,
    OutboxItem,
    StateView,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ion-header>
      <ion-toolbar>
        <ion-buttons slot="start"><ion-back-button defaultHref="/customer/tickets" text="" aria-label="Back to my tickets" /></ion-buttons>
        <ion-title>{{ t()?.displayNumber ?? 'Request' }}</ion-title>
      </ion-toolbar>
      <m-offline-banner [staleSince]="detail.staleSince()" />
    </ion-header>

    <ion-content>
      <ion-refresher slot="fixed" (ionRefresh)="refresh($event)"><ion-refresher-content /></ion-refresher>
      @if (t(); as t) {
        <section class="head">
          <h1>{{ t.subject }}</h1>
          <p class="badges">
            <ion-badge [color]="statusColor(t.statusCategory)">{{ t.status }}</ion-badge>
            <span>Priority {{ t.priority }}</span>
            <span>· opened {{ rel(t.createdAt) }}</span>
          </p>
          @if (!t.isMine && t.contactName) {
            <p class="muted">Raised by {{ t.contactName }}</p>
          }
        </section>

        @if (t.awaitingConfirmation && t.actions.confirm) {
          <section class="callout" aria-labelledby="confirm-title">
            <h2 id="confirm-title">Did this solve your problem?</h2>
            <p>Our team marked this request as resolved.</p>
            <div class="row">
              <ion-button [disabled]="readOnly() || busy()" (click)="confirm(true)">Yes, close it</ion-button>
              <ion-button fill="outline" [disabled]="readOnly() || busy()" (click)="confirm(false)">No, still a problem</ion-button>
            </div>
          </section>
        }

        @if (t.actions.rate && t.csatScore === null) {
          <section class="callout" aria-labelledby="rate-title">
            <h2 id="rate-title">How did we do?</h2>
            <div class="stars" role="radiogroup" aria-label="Rating from 1 to 5">
              @for (n of [1, 2, 3, 4, 5]; track n) {
                <button type="button" role="radio" [attr.aria-checked]="rating() === n" [attr.aria-label]="n + ' star' + (n === 1 ? '' : 's')"
                  [class.on]="rating() >= n" (click)="rating.set(n)">
                  <ion-icon [name]="rating() >= n ? 'star' : 'star-outline'" aria-hidden="true" />
                </button>
              }
            </div>
            @if (rating()) {
              <ion-textarea [(ngModel)]="ratingComment" [autoGrow]="true" placeholder="Anything we could do better? (optional)" aria-label="Rating comment" />
              <ion-button [disabled]="readOnly() || busy()" (click)="rate()">Send rating</ion-button>
            }
          </section>
        } @else if (t.csatScore !== null) {
          <p class="rated"><ion-icon name="star" aria-hidden="true" /> You rated this {{ t.csatScore }}/5{{ t.csatComment ? ' — “' + t.csatComment + '”' : '' }}</p>
        }

        @if (t.actions.reopen) {
          <div class="reopen">
            <ion-button fill="clear" size="small" [disabled]="readOnly() || busy()" (click)="reopen()">
              <ion-icon slot="start" name="refresh-circle-outline" aria-hidden="true" />Reopen request
            </ion-button>
            @if (t.reopenUntil) {
              <ion-note>until {{ dateTime(t.reopenUntil) }}</ion-note>
            }
          </div>
        }

        <h2 class="section">Conversation</h2>
        @if (t.descriptionHtml && !messages.value()?.length) {
          <article class="msg mine"><div class="body" [innerHTML]="t.descriptionHtml"></div></article>
        }
        @if (messages.value(); as list) {
          @for (m of list; track m.id) {
            <article class="msg" [class.mine]="m.isMine" [class.agent]="m.kind === 'agent'">
              <header>
                <strong>{{ m.isMine ? 'You' : m.authorName || (m.kind === 'agent' ? 'Support' : 'Customer') }}</strong>
                <time [attr.datetime]="m.createdAt">{{ rel(m.createdAt) }}</time>
              </header>
              <div class="body" [innerHTML]="m.bodyHtml"></div>
              @for (a of m.attachments; track a.id) {
                <ion-button fill="clear" size="small" [disabled]="readOnly()" (click)="download(a)">
                  <ion-icon slot="start" name="attach-outline" aria-hidden="true" />{{ a.fileName }} ({{ size(a.sizeBytes) }})
                </ion-button>
              }
            </article>
          }
        } @else if (messages.loading()) {
          <m-state kind="loading" [lines]="3" />
        } @else if (messages.error()) {
          <m-state [kind]="readOnly() ? 'offline' : 'error'" [message]="messages.error()!.userMessage" actionLabel="Try again" (action)="messages.load()" />
        }
        @for (e of pending(); track e.id) {
          <m-outbox-item [entry]="e" (edit)="editEntry($event)" />
        }

        @if (t.fields.length || t.productName || t.attachments.length) {
          <ion-list lines="full">
            <h2 class="section">Details</h2>
            @if (t.productName) {
              <ion-item><ion-label>Product</ion-label><ion-note slot="end">{{ t.productName }}</ion-note></ion-item>
            }
            @if (t.categoryName) {
              <ion-item><ion-label>Category</ion-label><ion-note slot="end">{{ t.categoryName }}</ion-note></ion-item>
            }
            @for (f of t.fields; track f.key) {
              <ion-item><ion-label>{{ f.label }}</ion-label><ion-note slot="end">{{ f.value || '—' }}</ion-note></ion-item>
            }
            @for (a of t.attachments; track a.id) {
              <ion-item [button]="true" [disabled]="readOnly()" (click)="download(a)" detail="false">
                <ion-icon slot="start" name="attach-outline" aria-hidden="true" /><ion-label>{{ a.fileName }}</ion-label>
                <ion-note slot="end">{{ size(a.sizeBytes) }}</ion-note>
              </ion-item>
            }
          </ion-list>
        }
      } @else if (detail.loading()) {
        <m-state kind="loading" title="Loading your request" />
      } @else if (detail.error(); as err) {
        <m-state [kind]="readOnly() ? 'offline' : 'error'" [title]="err.status === 404 ? 'Request not found' : ''"
          [message]="err.status === 404 ? 'It may belong to someone else in your organisation.' : readOnly() ? 'This request is not saved on this device yet.' : err.userMessage"
          actionLabel="Try again" (action)="loadAll()" />
      }
    </ion-content>

    @if (t()?.actions?.reply) {
      <ion-footer>
        <div class="composer">
          <ion-textarea [autoGrow]="true" [rows]="1" placeholder="Write a reply…" aria-label="Reply to support" [ngModel]="text()" (ngModelChange)="onText($event)" />
          <m-attachment-picker [(files)]="files" [compact]="true">
            <span class="hint">{{ online() ? '' : 'Offline · sends later' }}</span>
            <ion-button size="small" [disabled]="sending() || (!text().trim() && !files().length)" (click)="send()">
              @if (sending()) {
                <ion-spinner name="crescent" aria-label="Sending" />
              } @else {
                <ion-icon slot="end" [name]="online() ? 'send' : 'cloud-upload-outline'" aria-hidden="true" />
                {{ online() ? 'Send' : 'Queue' }}
              }
            </ion-button>
          </m-attachment-picker>
        </div>
      </ion-footer>
    }
  `,
  styles: `
    .head {
      padding: 12px 16px 0;
    }
    h1 {
      margin: 0 0 6px;
      font-size: 1.15rem;
    }
    .badges {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      align-items: center;
      margin: 0;
      font-size: 0.85rem;
      color: var(--sd-text-2);
    }
    .muted,
    .rated {
      margin: 6px 16px;
      font-size: 0.85rem;
      color: var(--sd-text-2);
    }
    .callout {
      margin: 12px;
      padding: 12px 14px;
      border-radius: var(--sd-radius-lg, 12px);
      border: 1px solid var(--sd-info-border);
      background: var(--sd-info-bg);
    }
    .callout h2 {
      margin: 0 0 4px;
      font-size: 1rem;
    }
    .callout p {
      margin: 0 0 8px;
    }
    .row {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
    }
    .stars {
      display: flex;
      gap: 4px;
    }
    .stars button {
      min-width: 44px;
      min-height: 44px;
      border: none;
      background: transparent;
      font-size: 28px;
      color: var(--sd-text-3);
      cursor: pointer;
    }
    .stars button.on {
      color: var(--sd-warning);
    }
    .reopen {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 0 8px;
    }
    .section {
      margin: 16px 16px 4px;
      font-size: 0.8rem;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      color: var(--sd-text-2);
    }
    .msg {
      margin: 8px 12px;
      padding: 10px 12px;
      max-width: 88%;
      border-radius: 14px;
      border: 1px solid var(--sd-border);
      background: var(--sd-surface);
    }
    .msg.mine {
      margin-left: auto;
      background: var(--sd-info-bg);
      border-color: var(--sd-info-border);
    }
    .msg header {
      display: flex;
      justify-content: space-between;
      gap: 8px;
      font-size: 0.8rem;
    }
    .msg time {
      color: var(--sd-text-2);
    }
    .body {
      margin-top: 4px;
      overflow-wrap: anywhere;
      line-height: 1.45;
    }
    .body :is(p, ul, ol) {
      margin: 0 0 6px;
    }
    .composer {
      padding: 6px 12px calc(6px + var(--ion-safe-area-bottom, 0px));
      background: var(--sd-surface);
      border-top: 1px solid var(--sd-border);
    }
    .composer ion-textarea {
      --padding-start: 10px;
      min-height: 44px;
      border: 1px solid var(--sd-border);
      border-radius: 10px;
      max-height: 35vh;
      overflow: auto;
    }
    .hint {
      flex: 1;
      font-size: 0.72rem;
      color: var(--sd-text-2);
      text-align: right;
    }
  `,
})
export class CustomerTicketPage implements OnInit {
  readonly id = input.required<string>();

  private readonly api = inject(PortalTicketsApi);
  private readonly mode = inject(ModeService);
  private readonly realtime = inject(PortalRealtimeService);
  private readonly outbox = inject(OutboxService);
  private readonly drafts = inject(DraftStore);
  private readonly network = inject(NetworkService);
  private readonly session = inject(SessionCoordinator);
  private readonly clock = inject(ClockService);
  private readonly alerts = inject(AlertController);
  private readonly toasts = inject(MobileToasts);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly detail = cachedResource<PortalTicketDetailDto>({
    key: () => `portal:ticket:${this.id()}`,
    kind: 'ticket',
    fetch: () => this.api.get(this.mode.brand(), this.id()),
  });
  protected readonly messages = cachedResource<PortalMessageDto[]>({
    key: () => `portal:messages:${this.id()}`,
    kind: 'ticket',
    fetch: () => this.api.messages(this.mode.brand(), this.id()),
  });

  protected readonly t = this.detail.value;
  protected readonly readOnly = this.session.readOnly;
  protected readonly online = this.network.online;
  protected readonly statusColor = statusColor;
  protected readonly pending = computed(() => this.outbox.entries().filter((e) => e.kind === 'customer-reply' && e.ticketId === this.id()));
  protected readonly text = signal('');
  protected readonly files = signal<File[]>([]);
  protected readonly sending = signal(false);
  protected readonly busy = signal(false);
  protected readonly rating = signal(0);
  protected ratingComment = '';
  private draftLoaded: string | null = null;

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => {
        if (this.draftLoaded === id) return;
        this.draftLoaded = id;
        void this.drafts.get<string>(`portal:reply:${id}`).then((v) => {
          if (this.draftLoaded === id && v && !this.text()) this.text.set(v);
        });
      });
    });
  }

  ngOnInit(): void {
    void this.loadAll();
    const live = merge(this.realtime.ticketEvents(this.id()), this.realtime.reconnected$, this.session.refresh$)
      .pipe(debounceTime(300))
      .subscribe(() => void this.loadAll());
    const sent = this.outbox.sent$.subscribe((s) => {
      if (s.ticketId === this.id()) void this.loadAll();
    });
    this.destroyRef.onDestroy(() => {
      live.unsubscribe();
      sent.unsubscribe();
    });
  }

  async loadAll(): Promise<void> {
    await Promise.all([this.detail.load(), this.messages.load()]);
  }

  protected async refresh(event: Event): Promise<void> {
    await this.loadAll();
    await (event.target as HTMLIonRefresherElement).complete();
  }

  protected onText(value: string | null | undefined): void {
    this.text.set(value ?? '');
    this.drafts.save(`portal:reply:${this.id()}`, this.text());
  }

  protected async send(): Promise<void> {
    const t = this.t();
    const text = this.text().trim();
    if (!t || (!text && !this.files().length)) return;
    this.sending.set(true);
    try {
      const entry = await this.outbox.enqueue({
        kind: 'customer-reply',
        ticketId: t.id,
        brand: this.mode.brand(),
        label: `${t.displayNumber} · ${t.subject}`,
        bodyHtml: textToHtml(text),
        bodyText: text,
        files: this.files(),
      });
      this.text.set('');
      this.files.set([]);
      await this.drafts.remove(`portal:reply:${t.id}`);
      const outcome = await this.outbox.settle(entry.id);
      if (!outcome) void this.toasts.success('Reply sent.');
      else if (outcome.status === 'failed') void this.toasts.error(outcome.lastError ?? 'Your reply was not accepted.');
      else void this.toasts.show('Saved on this device — it will be sent when you are back online.');
    } catch (e) {
      void this.toasts.error(ApiError.from(e).userMessage);
    } finally {
      this.sending.set(false);
    }
  }

  protected editEntry(entry: OutboxEntry): void {
    void this.outbox.takeBack(entry.id).then((e) => {
      if (!e) return;
      this.onText(e.bodyText);
      this.files.set(e.files.map((f) => (f.blob instanceof File ? f.blob : new File([f.blob], f.name, { type: f.type }))));
    });
  }

  protected async confirm(resolved: boolean): Promise<void> {
    let comment: string | null = null;
    if (!resolved) {
      comment = await this.prompt('What is still not working?', 'Tell us what you see now', 'Reopen');
      if (comment === null) return;
    }
    await this.act(() => this.api.confirm(this.mode.brand(), this.id(), resolved, comment), resolved ? 'Thanks — the request is closed.' : 'Reopened — we will get back to you.');
  }

  protected async reopen(): Promise<void> {
    const reason = await this.prompt('Reopen this request?', 'Why are you reopening it?', 'Reopen');
    if (reason === null) return;
    await this.act(() => this.api.reopen(this.mode.brand(), this.id(), reason || 'Reopened from the mobile app'), 'Reopened — we will get back to you.');
  }

  protected async rate(): Promise<void> {
    const score = this.rating();
    if (!score) return;
    await this.act(() => this.api.rate(this.mode.brand(), this.id(), score, this.ratingComment.trim() || null), 'Thank you for your feedback!');
    this.rating.set(0);
    this.ratingComment = '';
  }

  protected async download(a: PortalAttachmentDto): Promise<void> {
    try {
      saveBlob(await firstValueFrom(this.api.download(a.url)), a.fileName);
    } catch (e) {
      void this.toasts.error(ApiError.from(e).userMessage);
    }
  }

  private async act(run: () => ReturnType<PortalTicketsApi['get']>, success: string): Promise<void> {
    this.busy.set(true);
    try {
      this.detail.set(await firstValueFrom(run()));
      void this.messages.load();
      void this.toasts.success(success);
    } catch (e) {
      void this.toasts.error(ApiError.from(e).userMessage);
    } finally {
      this.busy.set(false);
    }
  }

  private async prompt(header: string, placeholder: string, ok: string): Promise<string | null> {
    const alert = await this.alerts.create({
      header,
      inputs: [{ name: 'text', type: 'textarea', placeholder, attributes: { 'aria-label': placeholder } }],
      buttons: [
        { text: 'Cancel', role: 'cancel' },
        { text: ok, role: 'confirm' },
      ],
    });
    await alert.present();
    const { role, data } = await alert.onDidDismiss<{ values: { text?: string } }>();
    return role === 'confirm' ? (data?.values?.text ?? '').trim() : null;
  }

  protected rel(value: string): string {
    return formatRelative(value, this.clock.coarseNow());
  }

  protected dateTime(value: string): string {
    return formatDateTime(value);
  }

  protected size(bytes: number): string {
    return fileSize(bytes);
  }
}
