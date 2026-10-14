import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import {
  ApiError,
  CallRecordDto,
  ChatHubService,
  ConversationDetailDto,
  ConversationMessageDto,
  ConversationsApi,
  MeStore,
  RealtimeService,
  ReplyTemplateDto,
  WorkbenchApi,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdDateTimePipe,
  SdDialogService,
  SdDropdownMenu,
  SdDurationPipe,
  SdEmptyState,
  SdIcon,
  SdMenuItem,
  SdRelativeTimePipe,
  SdSkeleton,
  SdTooltip,
  ToastService,
} from '@sd/ui';
import { Subscription, debounceTime, filter, firstValueFrom } from 'rxjs';
import {
  channelIcon,
  channelLabel,
  conversationStatusLabel,
  conversationStatusTone,
  deliveryIcon,
  deliveryTone,
  metaString,
  saveBlob,
} from '../../shared/channel-ui';
import { AttachmentChipComponent } from './attachment-chip.component';
import { ChatTransferData, ChatTransferDialog } from './chat-transfer.dialog';
import { DeliveryTimelineComponent } from './delivery-timeline.component';

/** Rendered template HTML → chat text: paragraphs and line breaks become new lines, list items get a bullet. */
function templateText(html: string): string {
  const doc = new DOMParser().parseFromString(
    html
      .replace(/<br\s*\/?>/gi, '\n')
      .replace(/<li[^>]*>/gi, '• ')
      .replace(/<\/(p|div|li|h[1-6]|tr)>/gi, '\n'),
    'text/html',
  );
  return (doc.body.textContent ?? '')
    .replace(/ /g, ' ')
    .replace(/[ \t]+\n/g, '\n')
    .replace(/\n{3,}/g, '\n\n')
    .trim();
}

/** Strips inline images that point at authenticated attachment URLs (they are listed as attachment thumbnails instead). */
function displayHtml(html: string | null): string {
  return (html ?? '').replace(/<img[^>]+src="\/api\/v1\/attachments\/[^"]+"[^>]*>/gi, '');
}

/**
 * Conversation detail with channel-specific rendering: e-mail cards (headers, attachments, quoted history, original
 * .eml), messaging bubbles (WhatsApp media/templates and the 24-hour window), call records (duration, recording) and
 * live chat (accept, real-time messages, typing indicators, close → transcript on the ticket). Every outbound message
 * shows its delivery status with an expandable delivery timeline. Live updates come from `/hubs/desk`
 * (`conversation.updated`) and, for chats, `/hubs/chat`.
 */
@Component({
  selector: 'desk-conversation-view',
  imports: [
    FormsModule,
    RouterLink,
    SdBadge,
    SdButton,
    SdIcon,
    SdSkeleton,
    SdEmptyState,
    SdTooltip,
    SdDateTimePipe,
    SdRelativeTimePipe,
    SdDurationPipe,
    SdDropdownMenu,
    AttachmentChipComponent,
    DeliveryTimelineComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (error(); as e) {
      <sd-empty-state icon="circle-alert" title="This conversation could not be loaded" [message]="e">
        <button sdButton type="button" icon="refresh" (click)="reload()">Try again</button>
      </sd-empty-state>
    } @else if (!detail()) {
      <div class="loading" aria-busy="true"><sd-skeleton [lines]="6" /></div>
    } @else {
      @let d = detail()!;
      @let s = d.summary;
      <header class="head">
        <span class="channel" [attr.data-channel]="s.channel" aria-hidden="true"><sd-icon [name]="channelIcon(s.channel)" [size]="18" /></span>
        <div class="titles">
          <h2 class="sd-truncate">{{ title() }}</h2>
          <p class="sd-small sd-muted sd-truncate">
            {{ channelLabel(s.channel) }}@if (s.channelAccountName) { · {{ s.channelAccountName }} } · started {{ s.startedAt | sdRelativeTime }}
            @if (s.assignedUserName) { · {{ s.assignedUserName }} }
          </p>
        </div>
        <sd-badge [tone]="statusTone(s.status)" dot>{{ statusLabel(s.status) }}</sd-badge>
        <div class="actions">
          @if (s.ticketId) {
            <a sdButton size="sm" variant="secondary" icon="ticket" [routerLink]="['/tickets', s.ticketId]">Ticket #{{ s.ticketNumber }}</a>
          }
          @if (isChat() && canAccept()) {
            <button sdButton size="sm" variant="primary" icon="user-check" [loading]="busy()" (click)="accept()">Accept chat</button>
          }
          @if (canTransfer()) {
            <button sdButton size="sm" variant="secondary" icon="share" [disabled]="busy()" (click)="transfer()">Transfer</button>
          }
          @if (isChat() && s.status !== 'Closed') {
            <button sdButton size="sm" variant="ghost" icon="x" [loading]="busy()" (click)="closeChat()">End chat</button>
          }
          <button sdButton size="sm" variant="ghost" icon="refresh" iconOnly aria-label="Refresh conversation" sdTooltip="Refresh" (click)="reload()"></button>
        </div>
      </header>

      @if (s.channel === 'WhatsApp') {
        <div class="banner" [class.warn]="!d.serviceWindowOpen" role="status">
          <sd-icon name="clock" [size]="14" />
          @if (d.serviceWindowOpen) {
            24-hour service window open until {{ s.serviceWindowEndsAt | sdDateTime }} ({{ s.serviceWindowEndsAt | sdRelativeTime }}) — free-form replies are allowed.
          } @else {
            The 24-hour service window is closed — replies are sent as a pre-approved template (or wait for the customer to write).
          }
        </div>
      }
      @if (isChat()) {
        <div class="banner subtle" role="status">
          <sd-icon [name]="s.identityVerified ? 'shield-check' : 'shield-alert'" [size]="14" />
          @if (s.identityVerified) {
            Visitor verified by e-mail code{{ d.visitorEmail ? ' (' + d.visitorEmail + ')' : '' }}.
          } @else {
            Visitor not verified{{ d.visitorEmail ? ' — claims ' + d.visitorEmail : '' }}.
          }
          @if (pageUrl()) {
            <span class="sd-muted sd-truncate">· on {{ pageUrl() }}</span>
          }
        </div>
      }

      <div class="scroll" #scroll tabindex="0" role="log" [attr.aria-label]="'Messages of ' + title()">
        @for (c of d.calls; track c.id) {
          <article class="call" [attr.data-status]="c.status">
            <sd-icon [name]="callIcon(c)" [size]="20" />
            <div class="call-body">
              <strong>{{ callTitle(c) }}</strong>
              <span class="sd-small sd-muted">{{ c.fromNumber }} → {{ c.toNumber }} · {{ c.startedAt | sdDateTime }}</span>
              <span class="sd-small">
                @if (c.durationSeconds > 0) { Duration {{ c.durationSeconds | sdDuration: 'seconds' }} }
                @if (c.disposition) { · {{ c.disposition }} }
              </span>
              @if (c.recordingUrl) {
                <a class="sd-small" [href]="c.recordingUrl" target="_blank" rel="noopener noreferrer"><sd-icon name="play" [size]="12" /> Play recording</a>
              }
            </div>
            @if (c.ticketId) {
              <a sdButton size="sm" variant="ghost" icon="ticket" [routerLink]="['/tickets', c.ticketId]">Ticket</a>
            }
          </article>
        }
        @for (m of d.messages; track m.id) {
          @if (m.author === 'system' && !isEmail()) {
            <p class="system sd-small">{{ m.bodyText }} <time class="sd-xs sd-muted">{{ m.occurredAt | sdDateTime }}</time></p>
          } @else if (isEmail()) {
            <article class="mail" [class.out]="m.direction === 'Outbound'">
              <header>
                <div class="from">
                  <strong>{{ m.authorName || m.fromAddress || (m.direction === 'Outbound' ? 'Support' : 'Customer') }}</strong>
                  @if (m.fromAddress && m.authorName) {
                    <span class="sd-small sd-muted">&lt;{{ m.fromAddress }}&gt;</span>
                  }
                  @if (m.isAutoReply) {
                    <sd-badge size="sm" tone="info">Auto-reply</sd-badge>
                  }
                  @if (kindOf(m) === 'ack') {
                    <sd-badge size="sm">Acknowledgement</sd-badge>
                  }
                  @if (kindOf(m) === 'csat') {
                    <sd-badge size="sm" tone="accent">CSAT survey</sd-badge>
                  }
                </div>
                <time class="sd-xs sd-muted" [attr.datetime]="m.occurredAt">{{ m.occurredAt | sdDateTime }}</time>
              </header>
              <dl class="hdrs sd-small">
                @if (m.to.length) {
                  <dt>To</dt>
                  <dd>{{ m.to.join(', ') }}</dd>
                }
                @if (m.subject) {
                  <dt>Subject</dt>
                  <dd>{{ m.subject }}</dd>
                }
              </dl>
              <div class="sd-prose body" [innerHTML]="html(m)"></div>
              @if (quoted(m); as q) {
                <details class="quoted">
                  <summary class="sd-small">Quoted history</summary>
                  <pre class="sd-small">{{ q }}</pre>
                </details>
              }
              @if (m.attachments?.length) {
                <div class="atts">
                  @for (a of m.attachments!; track a.id) {
                    <desk-attachment-chip [attachment]="a" />
                  }
                </div>
              }
              <footer>
                @if (m.direction === 'Outbound') {
                  <button type="button" class="status" (click)="toggle(m.id)" [attr.aria-expanded]="expanded().has(m.id)">
                    <sd-badge size="sm" [tone]="deliveryTone(m.status)" [icon]="deliveryIcon(m.status)">{{ m.status }}</sd-badge>
                    <span class="sd-xs sd-muted">Delivery details</span>
                  </button>
                }
                @if (metaString(m.metadata, 'rawFileId')) {
                  <button sdButton size="sm" variant="ghost" icon="download" type="button" (click)="downloadRaw(m)">Original (.eml)</button>
                }
                @if (m.internetMessageId) {
                  <span class="sd-xs sd-muted sd-mono sd-truncate" [title]="m.internetMessageId">{{ m.internetMessageId }}</span>
                }
              </footer>
              @if (expanded().has(m.id)) {
                <desk-delivery-timeline [messageId]="m.id" [version]="deliveryVersion()" />
              }
            </article>
          } @else {
            <div class="bubble-row" [class.out]="m.direction === 'Outbound'">
              <div class="bubble" [attr.data-author]="m.author">
                <span class="who sd-xs">{{ whoOf(m) }}</span>
                @if (m.contentType === 'template' || metaString(m.metadata, 'templateName')) {
                  <sd-badge size="sm" tone="accent" icon="file-text">Template {{ metaString(m.metadata, 'templateName') ?? '' }}</sd-badge>
                }
                <p class="text">{{ m.bodyText }}</p>
                @if (m.attachments?.length) {
                  <div class="atts">
                    @for (a of m.attachments!; track a.id) {
                      <desk-attachment-chip [attachment]="a" />
                    }
                  </div>
                }
                <span class="meta sd-xs">
                  <time [attr.datetime]="m.occurredAt">{{ m.occurredAt | sdDateTime }}</time>
                  @if (m.direction === 'Outbound' && m.author !== 'bot') {
                    <button type="button" class="tick" (click)="toggle(m.id)" [attr.aria-expanded]="expanded().has(m.id)" [attr.aria-label]="'Delivery: ' + m.status">
                      <sd-icon [name]="deliveryIcon(m.status)" [size]="13" [attr.data-tone]="deliveryTone(m.status)" />
                      {{ m.status }}
                    </button>
                  }
                </span>
              </div>
            </div>
            @if (expanded().has(m.id)) {
              <desk-delivery-timeline class="bubble-timeline" [class.out]="m.direction === 'Outbound'" [messageId]="m.id" [version]="deliveryVersion()" />
            }
          }
        } @empty {
          @if (d.calls.length === 0) {
            <sd-empty-state compact icon="messages" title="No messages yet" />
          }
        }
      </div>

      @if (isChat() && s.status !== 'Closed') {
        <div class="typing sd-small sd-muted" aria-live="polite">{{ visitorTyping() ? (s.visitorName || s.contactName || 'Visitor') + ' is typing…' : '' }}</div>
        <form class="composer" (ngSubmit)="send()">
          <label class="sd-visually-hidden" for="chat-reply-{{ s.id }}">Message to the visitor</label>
          <textarea
            id="chat-reply-{{ s.id }}"
            class="sd-input"
            name="reply"
            rows="2"
            [placeholder]="s.status === 'AgentActive' ? 'Type a message — Enter to send, Shift+Enter for a new line' : 'Accept the chat to reply'"
            [ngModel]="draft()"
            (ngModelChange)="onDraft($event)"
            (keydown.enter)="onEnter($event)"
            [disabled]="s.status !== 'AgentActive' && s.status !== 'WaitingForAgent'"
            maxlength="4000"
          ></textarea>
          <div class="composer-actions">
            <sd-dropdown-menu
              size="sm"
              variant="ghost"
              icon="file-text"
              label="Templates"
              ariaLabel="Insert a reply template"
              align="end"
              [items]="templateItems()"
              [disabled]="!s.ticketId || (s.status !== 'AgentActive' && s.status !== 'WaitingForAgent')"
            />
            <button sdButton variant="primary" type="submit" icon="send" [loading]="sending()" [disabled]="!draft().trim()">Send</button>
          </div>
        </form>
      } @else if (!isChat() && s.ticketId && s.channel !== 'Phone') {
        <p class="reply-hint sd-small sd-muted">
          <sd-icon name="info" [size]="13" /> Replies go out from the ticket and are delivered on this {{ channelLabel(s.channel) }} channel.
          <a [routerLink]="['/tickets', s.ticketId]">Reply on ticket #{{ s.ticketNumber }}</a>
        </p>
      }
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 0;
      background: var(--sd-surface);
    }
    .loading {
      padding: var(--sd-space-5);
    }
    .head {
      display: flex;
      align-items: center;
      gap: var(--sd-space-3);
      padding: var(--sd-space-3) var(--sd-space-4);
      border-bottom: 1px solid var(--sd-border);
      flex-wrap: wrap;
    }
    .channel {
      display: grid;
      place-items: center;
      width: 36px;
      height: 36px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
      flex: none;
    }
    .channel[data-channel='WhatsApp'] {
      background: var(--sd-success-bg);
      color: var(--sd-success);
    }
    .channel[data-channel='Phone'] {
      background: var(--sd-info-bg);
      color: var(--sd-info);
    }
    .channel[data-channel='Chat'] {
      background: var(--sd-accent-soft);
      color: var(--sd-accent);
    }
    .titles {
      flex: 1;
      min-width: 160px;
    }
    h2 {
      margin: 0;
      font-size: var(--sd-text-lg);
      font-weight: var(--sd-weight-semibold);
    }
    .titles p {
      margin: 2px 0 0;
    }
    .actions {
      display: flex;
      gap: var(--sd-space-2);
      flex-wrap: wrap;
    }
    .banner {
      display: flex;
      align-items: center;
      gap: var(--sd-space-2);
      padding: 6px var(--sd-space-4);
      font-size: var(--sd-text-sm);
      background: var(--sd-success-bg);
      color: var(--sd-text);
      border-bottom: 1px solid var(--sd-border);
    }
    .banner.warn {
      background: var(--sd-warning-bg);
    }
    .banner.subtle {
      background: var(--sd-surface-2);
    }
    .scroll {
      flex: 1;
      min-height: 0;
      overflow-y: auto;
      padding: var(--sd-space-4);
      display: flex;
      flex-direction: column;
      gap: var(--sd-space-3);
      background: var(--sd-surface-sunken);
    }
    .scroll:focus-visible {
      outline: none;
      box-shadow: inset var(--sd-focus-ring);
    }
    .system {
      align-self: center;
      margin: 0;
      color: var(--sd-text-3);
      text-align: center;
    }
    .mail {
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
      padding: var(--sd-space-3) var(--sd-space-4);
      box-shadow: var(--sd-shadow-xs);
    }
    .mail.out {
      border-left: 3px solid var(--sd-primary);
    }
    .mail header {
      display: flex;
      align-items: baseline;
      justify-content: space-between;
      gap: var(--sd-space-2);
      flex-wrap: wrap;
    }
    .from {
      display: flex;
      align-items: center;
      gap: var(--sd-space-2);
      flex-wrap: wrap;
    }
    .hdrs {
      display: grid;
      grid-template-columns: auto 1fr;
      gap: 0 var(--sd-space-2);
      margin: var(--sd-space-1) 0 var(--sd-space-2);
      color: var(--sd-text-2);
    }
    .hdrs dt {
      color: var(--sd-text-3);
    }
    .hdrs dd {
      margin: 0;
      overflow-wrap: anywhere;
    }
    .body {
      overflow-wrap: anywhere;
    }
    .quoted pre {
      white-space: pre-wrap;
      margin: var(--sd-space-1) 0 0;
      color: var(--sd-text-3);
      font-family: inherit;
    }
    .atts {
      display: flex;
      flex-wrap: wrap;
      gap: var(--sd-space-2);
      margin-top: var(--sd-space-2);
    }
    .mail footer {
      display: flex;
      align-items: center;
      gap: var(--sd-space-2);
      margin-top: var(--sd-space-2);
      flex-wrap: wrap;
    }
    .status,
    .tick {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      padding: 2px 4px;
      border: 0;
      border-radius: var(--sd-radius-sm);
      background: transparent;
      color: inherit;
      font: inherit;
      cursor: pointer;
    }
    .status:focus-visible,
    .tick:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .bubble-row {
      display: flex;
    }
    .bubble-row.out {
      justify-content: flex-end;
    }
    .bubble {
      max-width: min(560px, 82%);
      padding: 8px 12px;
      border-radius: 14px;
      background: var(--sd-surface);
      border: 1px solid var(--sd-border);
      border-bottom-left-radius: 4px;
    }
    .bubble-row.out .bubble {
      background: var(--sd-primary-soft);
      border-color: var(--sd-primary-border);
      border-bottom-left-radius: 14px;
      border-bottom-right-radius: 4px;
    }
    .bubble[data-author='bot'] {
      background: var(--sd-info-bg);
      border-color: var(--sd-info-border);
    }
    .who {
      display: block;
      color: var(--sd-text-3);
      margin-bottom: 2px;
    }
    .text {
      margin: 0;
      white-space: pre-wrap;
      overflow-wrap: anywhere;
    }
    .meta {
      display: flex;
      justify-content: flex-end;
      align-items: center;
      gap: var(--sd-space-2);
      margin-top: 4px;
      color: var(--sd-text-3);
    }
    sd-icon[data-tone='success'] {
      color: var(--sd-success);
    }
    sd-icon[data-tone='danger'] {
      color: var(--sd-danger);
    }
    sd-icon[data-tone='primary'] {
      color: var(--sd-primary);
    }
    .bubble-timeline {
      max-width: 560px;
    }
    .bubble-timeline.out {
      align-self: flex-end;
    }
    .call {
      display: flex;
      align-items: flex-start;
      gap: var(--sd-space-3);
      padding: var(--sd-space-3);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    .call[data-status='Missed'],
    .call[data-status='Failed'] {
      border-left: 3px solid var(--sd-danger);
    }
    .call[data-status='Voicemail'] {
      border-left: 3px solid var(--sd-warning);
    }
    .call[data-status='Completed'] {
      border-left: 3px solid var(--sd-success);
    }
    .call-body {
      flex: 1;
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    .typing {
      min-height: 22px;
      padding: 2px var(--sd-space-4) 0;
    }
    .composer {
      display: flex;
      gap: var(--sd-space-2);
      align-items: flex-end;
      padding: 0 var(--sd-space-4) var(--sd-space-3);
    }
    .composer textarea {
      flex: 1;
      resize: vertical;
      min-height: 44px;
      max-height: 160px;
    }
    .composer-actions {
      display: flex;
      flex-direction: column;
      align-items: stretch;
      gap: 4px;
    }
    @media (max-width: 480px) {
      .composer {
        flex-wrap: wrap;
      }
      .composer textarea {
        flex-basis: 100%;
      }
      .composer-actions {
        flex-direction: row;
        justify-content: flex-end;
        width: 100%;
      }
    }
    .reply-hint {
      display: flex;
      align-items: center;
      gap: 6px;
      flex-wrap: wrap;
      margin: 0;
      padding: var(--sd-space-2) var(--sd-space-4);
      border-top: 1px solid var(--sd-border);
    }
  `,
})
export class ConversationViewComponent {
  private readonly api = inject(ConversationsApi);
  private readonly realtime = inject(RealtimeService);
  private readonly chatHub = inject(ChatHubService);
  private readonly toast = inject(ToastService);
  private readonly me = inject(MeStore);
  private readonly destroyRef = inject(DestroyRef);
  private readonly workbench = inject(WorkbenchApi);
  private readonly dialogs = inject(SdDialogService);
  private readonly templates = signal<ReplyTemplateDto[] | null>(null);
  private readonly templatesError = signal<string | null>(null);

  readonly conversationId = input.required<string>();
  /** Emitted after every (re)load so lists can patch their row. */
  readonly changed = output<ConversationDetailDto>();

  private readonly scroll = viewChild<ElementRef<HTMLElement>>('scroll');
  protected readonly detail = signal<ConversationDetailDto | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly sending = signal(false);
  protected readonly draft = signal('');
  protected readonly visitorTyping = signal(false);
  protected readonly expanded = signal<ReadonlySet<string>>(new Set());
  protected readonly deliveryVersion = signal(0);

  protected readonly channelIcon = channelIcon;
  protected readonly channelLabel = channelLabel;
  protected readonly statusLabel = conversationStatusLabel;
  protected readonly statusTone = conversationStatusTone;
  protected readonly deliveryTone = deliveryTone;
  protected readonly deliveryIcon = deliveryIcon;
  protected readonly metaString = metaString;

  protected readonly isChat = computed(() => this.detail()?.summary.channel === 'Chat');
  protected readonly isEmail = computed(() => this.detail()?.summary.channel === 'Email');
  protected readonly title = computed(() => {
    const s = this.detail()?.summary;
    return s?.subject || s?.contactName || s?.visitorName || 'Conversation';
  });
  protected readonly canAccept = computed(() => {
    const s = this.detail()?.summary;
    if (!s || !this.me.can('tickets.reply')) return false;
    return s.status === 'WaitingForAgent' || s.status === 'BotActive' || (s.status === 'AgentActive' && !s.assignedUserId);
  });
  protected readonly pageUrl = computed(() => metaString(this.detail()?.metadata, 'pageUrl'));
  /** The agent handling the chat (or anyone while it waits) may hand it on; supervisors always. */
  protected readonly canTransfer = computed(() => {
    const s = this.detail()?.summary;
    if (!s || s.channel !== 'Chat' || s.status === 'Closed' || !this.me.can('tickets.reply')) return false;
    return !s.assignedUserId || s.assignedUserId === this.me.user()?.id || this.me.can('supervisor.monitor');
  });
  /** Reply templates (canned responses) rendered against the chat's ticket and inserted as plain text. */
  protected readonly templateItems = computed<SdMenuItem[]>(() => {
    const list = this.templates();
    if (this.templatesError()) return [{ id: 'err', label: this.templatesError()!, disabled: true }];
    if (list === null) return [{ id: 'loading', label: 'Loading templates…', disabled: true }];
    if (list.length === 0) return [{ id: 'none', label: 'No templates available', disabled: true }];
    const items: SdMenuItem[] = [];
    let category: string | null | undefined;
    for (const t of [...list].sort((a, b) => (a.category ?? '').localeCompare(b.category ?? '') || a.name.localeCompare(b.name))) {
      if (t.category !== category) {
        category = t.category;
        if (category) items.push({ id: `h-${category}`, label: category, heading: true });
      }
      items.push({ id: t.id, label: t.name, icon: 'file-text', run: () => void this.insertTemplate(t) });
    }
    return items;
  });

  private typingTimer: ReturnType<typeof setTimeout> | null = null;
  private lastTypingSent = 0;
  private chatSubs: Subscription | null = null;
  private joinedChat: string | null = null;

  constructor() {
    effect(() => {
      const id = this.conversationId();
      untracked(() => {
        this.detail.set(null);
        this.error.set(null);
        this.expanded.set(new Set());
        this.draft.set('');
        this.visitorTyping.set(false);
        this.leaveChat();
        this.reload();
      });
      void id;
    });

    // Desk hub: any change to this conversation (new message, status, delivery receipt) → refresh.
    const sub = this.realtime.conversationUpdated$
      .pipe(
        filter((e) => e.conversationId === this.conversationId()),
        debounceTime(250),
      )
      .subscribe((e) => {
        if (e['kind'] === 'delivery') this.deliveryVersion.update((v) => v + 1);
        this.reload(true);
      });
    const reconnect = this.realtime.reconnected$.subscribe(() => this.reload(true));
    this.destroyRef.onDestroy(() => {
      sub.unsubscribe();
      reconnect.unsubscribe();
      this.leaveChat();
      if (this.typingTimer) clearTimeout(this.typingTimer);
    });

    // Keep the newest message in view.
    effect(() => {
      const count = this.detail()?.messages.length ?? 0;
      const el = this.scroll()?.nativeElement;
      if (el && count >= 0) setTimeout(() => (el.scrollTop = el.scrollHeight));
    });
  }

  reload(quiet = false): void {
    const id = this.conversationId();
    this.api.get(id).subscribe({
      next: (d) => {
        if (id !== this.conversationId()) return;
        this.detail.set(d);
        this.changed.emit(d);
        if (d.summary.channel === 'Chat' && d.summary.status !== 'Closed') {
          this.joinChat(id);
          // The chat composer offers reply templates (rendered against the chat's ticket).
          if (d.summary.ticketId) this.loadTemplates();
        }
      },
      error: (e: unknown) => {
        if (!quiet) this.error.set(ApiError.from(e).userMessage);
      },
    });
  }

  protected toggle(messageId: string): void {
    this.expanded.update((set) => {
      const next = new Set(set);
      if (next.has(messageId)) next.delete(messageId);
      else next.add(messageId);
      return next;
    });
  }

  protected html(m: ConversationMessageDto): string {
    return displayHtml(m.bodyHtml ?? (m.bodyText ? m.bodyText.replace(/[&<>]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' })[c]!) : ''));
  }

  protected quoted(m: ConversationMessageDto): string | null {
    return metaString(m.metadata, 'quoted');
  }

  protected kindOf(m: ConversationMessageDto): string | null {
    return metaString(m.metadata, 'kind');
  }

  protected whoOf(m: ConversationMessageDto): string {
    const s = this.detail()?.summary;
    if (m.author === 'customer') return m.authorName || s?.contactName || s?.visitorName || m.fromAddress || 'Customer';
    if (m.author === 'bot') return 'Assistant';
    return m.authorName || 'Agent';
  }

  protected callIcon(c: CallRecordDto): string {
    if (c.status === 'Missed') return 'phone-missed';
    if (c.status === 'Voicemail') return 'voicemail';
    return c.direction === 'Inbound' ? 'phone-incoming' : 'phone-outgoing';
  }

  protected callTitle(c: CallRecordDto): string {
    switch (c.status) {
      case 'Missed':
        return 'Missed call';
      case 'Voicemail':
        return 'Voicemail';
      case 'Completed':
        return `${c.direction} call`;
      case 'InProgress':
        return 'Call in progress';
      case 'Ringing':
        return 'Ringing…';
      default:
        return 'Call failed';
    }
  }

  protected downloadRaw(m: ConversationMessageDto): void {
    this.api.raw(m.id).subscribe((blob) => saveBlob(blob, `message-${m.id.slice(0, 8)}.eml`));
  }

  protected accept(): void {
    this.busy.set(true);
    this.api.accept(this.conversationId()).subscribe({
      next: (d) => {
        this.detail.set(d);
        this.changed.emit(d);
        this.joinChat(d.summary.id);
        this.busy.set(false);
        this.toast.success('Chat accepted', 'You are now chatting with the visitor.');
      },
      error: (err: unknown) => {
        this.busy.set(false);
        const e = ApiError.from(err);
        // 409 chat_limit_reached / chat_taken are not toasted globally.
        if (e.status === 409) this.toast.warning(e.code === 'chat_limit_reached' ? 'Chat limit reached' : 'Could not accept the chat', e.userMessage);
        this.reload(true);
      },
    });
  }

  protected transfer(): void {
    const d = this.detail();
    if (!d || !this.canTransfer()) return;
    this.dialogs
      .open<ConversationDetailDto | null, ChatTransferData>(ChatTransferDialog, { data: { conversation: d }, size: 'sm' })
      .closed.subscribe((updated) => {
        if (!updated) return;
        this.detail.set(updated);
        this.changed.emit(updated);
      });
  }

  /** Loads the template list once (first chat with a ticket shown in this view). */
  protected loadTemplates(): void {
    if (this.templates() !== null) return;
    this.templatesError.set(null);
    this.workbench.templates().subscribe({
      next: (list) => this.templates.set(list),
      error: (err: unknown) => this.templatesError.set(ApiError.from(err).userMessage),
    });
  }

  /** Renders the template's placeholders against the chat's ticket and appends it to the message as plain text. */
  private async insertTemplate(t: ReplyTemplateDto): Promise<void> {
    const ticketId = this.detail()?.summary.ticketId;
    if (!ticketId) return;
    try {
      const rendered = await firstValueFrom(this.workbench.renderTemplate(ticketId, t.id));
      const text = templateText(rendered.bodyHtml);
      const current = this.draft();
      this.draft.set((current.trim() ? `${current.replace(/\s+$/, '')}\n` : '') + text);
    } catch (err) {
      this.toast.error('Could not insert the template', ApiError.from(err).userMessage);
    }
  }

  protected closeChat(): void {
    this.busy.set(true);
    this.api.close(this.conversationId(), 'Closed by agent').subscribe({
      next: (d) => {
        this.detail.set(d);
        this.changed.emit(d);
        this.leaveChat();
        this.busy.set(false);
        this.toast.success('Chat ended', d.summary.ticketId ? 'The transcript was added to the ticket.' : undefined);
      },
      error: () => this.busy.set(false),
    });
  }

  protected onDraft(value: string): void {
    this.draft.set(value);
    const now = Date.now();
    if (value && now - this.lastTypingSent > 3000) {
      this.lastTypingSent = now;
      this.chatHub.typing(this.conversationId(), true);
    }
  }

  protected onEnter(event: Event): void {
    const e = event as KeyboardEvent;
    if (e.shiftKey) return;
    e.preventDefault();
    this.send();
  }

  protected send(): void {
    const text = this.draft().trim();
    if (!text || this.sending()) return;
    this.sending.set(true);
    this.chatHub.typing(this.conversationId(), false);
    this.lastTypingSent = 0;
    this.api.send(this.conversationId(), { text, clientRequestId: crypto.randomUUID() }).subscribe({
      next: () => {
        this.draft.set('');
        this.sending.set(false);
        this.reload(true);
      },
      error: (err: unknown) => {
        this.sending.set(false);
        const e = ApiError.from(err);
        if (e.status === 409) this.toast.warning(e.code === 'chat_limit_reached' ? 'Chat limit reached' : 'Message not sent', e.userMessage);
      },
    });
  }

  private joinChat(id: string): void {
    if (this.joinedChat === id) return;
    this.leaveChat();
    this.joinedChat = id;
    void this.chatHub.join(id);
    const streams = this.chatHub.forConversation(id);
    const subs = new Subscription();
    subs.add(streams.message$.pipe(debounceTime(120)).subscribe(() => this.reload(true)));
    subs.add(streams.status$.subscribe(() => this.reload(true)));
    subs.add(
      streams.typing$.pipe(filter((t) => t.from === 'visitor')).subscribe((t) => {
        this.visitorTyping.set(t.isTyping);
        if (this.typingTimer) clearTimeout(this.typingTimer);
        if (t.isTyping) this.typingTimer = setTimeout(() => this.visitorTyping.set(false), 6000);
      }),
    );
    this.chatSubs = subs;
  }

  private leaveChat(): void {
    this.chatSubs?.unsubscribe();
    this.chatSubs = null;
    if (this.joinedChat) void this.chatHub.leave(this.joinedChat);
    this.joinedChat = null;
  }
}
