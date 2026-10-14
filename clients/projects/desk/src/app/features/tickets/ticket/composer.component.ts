import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  computed,
  effect,
  inject,
  input,
  runInInjectionContext,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import {
  ApiError,
  DraftKind,
  FilesApi,
  Guid,
  MeStore,
  RealtimeService,
  ReplyCollisionDto,
  ReplyTemplateDto,
  ThreadDto,
  TicketsApi,
  WorkbenchApi,
  newClientRequestId,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdChipsInput,
  SdDropdownMenu,
  SdIcon,
  SdMenuItem,
  SdMentionItem,
  SdMentionSource,
  SdRelativeTimePipe,
  SdRichTextEditor,
  SdTooltip,
  ToastService,
} from '@sd/ui';
import { Subject, Subscription, debounceTime, firstValueFrom } from 'rxjs';
import { COMPOSER_ACTIONS, ComposerActionDef, ComposerMode } from '../extensions/composer-actions';
import { allowedStatuses, channelLabel, htmlToText, toastApiError } from '../shared/ticket-ui';
import { TicketContext } from './ticket-context.service';

interface Upload {
  key: number;
  name: string;
  size: number;
  percent: number | null;
  fileId: Guid | null;
  error: string | null;
  sub: Subscription | null;
}

interface DraftState {
  body: string;
  to: string[];
  cc: string[];
  templateId: Guid | null;
  basedOnSeq: number | null;
  mentions: Guid[];
}

const EMPTY: DraftState = { body: '', to: [], cc: [], templateId: null, basedOnSeq: null, mentions: [] };
const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
const IDLE_MS = 20_000;

/**
 * Reply / internal note / handover composer: TipTap editor (with @mentions for notes), e-mail recipients,
 * reply templates rendered server-side, attachments with upload progress, "send and set status", auto-saved
 * drafts per kind, live "replying/noting" presence while typing and reply-collision review (409 → show the new
 * entries → send anyway with `force`). Contributed actions come from extensions/composer-actions.ts.
 */
@Component({
  selector: 'desk-composer',
  imports: [SdRichTextEditor, SdButton, SdIcon, SdChipsInput, SdDropdownMenu, SdBadge, SdTooltip, SdRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class.is-expanded]': 'expanded()' },
  template: `
    @if (!expanded()) {
      <div class="bar">
        @if (can()?.reply) {
          <button sdButton variant="primary" icon="reply" type="button" (click)="open('reply')">Reply</button>
        }
        @if (can()?.note) {
          <button sdButton icon="sticky-note" type="button" (click)="open('note')">Internal note</button>
          <button sdButton variant="ghost" icon="users" type="button" class="hide-sm" (click)="open('handover')">Handover</button>
        }
        @if (!can()?.reply && !can()?.note) {
          <span class="sd-muted sd-small">You can view this ticket but not reply to it.</span>
        }
        <span class="sd-spacer"></span>
        @if (hasDraft()) {
          <sd-badge tone="info" icon="pencil" size="sm">Draft saved</sd-badge>
        }
      </div>
    } @else {
      <section class="composer" [class.is-note]="mode() !== 'reply'" [attr.aria-label]="modeLabel()">
        <div class="modes" role="radiogroup" aria-label="Message type">
          @if (can()?.reply) {
            <button type="button" role="radio" class="mode" [attr.aria-checked]="mode() === 'reply'" (click)="switchMode('reply')">
              <sd-icon name="reply" [size]="14" />Reply
            </button>
          }
          @if (can()?.note) {
            <button type="button" role="radio" class="mode" [attr.aria-checked]="mode() === 'note'" (click)="switchMode('note')">
              <sd-icon name="lock" [size]="14" />Internal note
            </button>
            <button type="button" role="radio" class="mode" [attr.aria-checked]="mode() === 'handover'" (click)="switchMode('handover')">
              <sd-icon name="users" [size]="14" />Handover
            </button>
          }
          <span class="sd-spacer"></span>
          <span class="draft-state sd-xs sd-muted" aria-live="polite">
            @switch (draftStatus()) {
              @case ('saving') { Saving draft… }
              @case ('saved') { Draft saved {{ draftSavedAt() | sdRelativeTime }} }
              @case ('error') { <span class="err">Draft not saved</span> }
            }
          </span>
          <button sdButton size="sm" variant="ghost" iconOnly icon="chevron-down" type="button" aria-label="Minimise composer" sdTooltip="Minimise" (click)="minimise()"></button>
        </div>

        @if (collision(); as c) {
          <div class="collision" role="alert">
            <p class="collision-title">
              <sd-icon name="triangle-alert" [size]="16" />
              {{ c.newEntries.length }} new {{ c.newEntries.length === 1 ? 'entry was' : 'entries were' }} added while you were writing.
            </p>
            <ul>
              @for (e of c.newEntries; track e.id) {
                <li [class.is-internal]="e.visibility === 'Internal'">
                  <strong>{{ e.authorName ?? e.fromAddress ?? 'Someone' }}</strong>
                  <span class="sd-muted sd-xs">{{ e.visibility === 'Internal' ? 'internal note' : 'message' }} · {{ e.createdAt | sdRelativeTime }}</span>
                  <p>{{ excerpt(e) }}</p>
                </li>
              }
            </ul>
            <div class="collision-actions">
              <button sdButton size="sm" type="button" (click)="reviewCollision()">I've reviewed them — keep editing</button>
              <button sdButton size="sm" variant="primary" type="button" [loading]="sending()" (click)="send(null, true)">Send anyway</button>
            </div>
          </div>
        }

        @if (mode() === 'reply') {
          @if (isEmail()) {
            <div class="recipients">
              <span class="rlabel" id="to-label">To</span>
              <sd-chips-input class="chips" ariaLabel="To" [value]="state().to" (valueChange)="patch({ to: $event })" [validate]="isEmailAddress" placeholder="Add recipient" />
              @if (!showCc() && state().cc.length === 0) {
                <button sdButton size="sm" variant="link" type="button" (click)="showCc.set(true)">Cc</button>
              }
            </div>
            @if (showCc() || state().cc.length > 0) {
              <div class="recipients">
                <span class="rlabel">Cc</span>
                <sd-chips-input class="chips" ariaLabel="Cc" [value]="state().cc" (valueChange)="patch({ cc: $event })" [validate]="isEmailAddress" placeholder="Add Cc" />
              </div>
            }
          } @else {
            <p class="via sd-small sd-muted">
              <sd-icon name="send" [size]="13" /> Sent to {{ ctx.ticket()?.contactName ?? 'the customer' }} via {{ channelName() }}.
            </p>
          }
        } @else {
          <p class="via sd-small note-hint">
            <sd-icon name="lock" [size]="13" /> Only agents see {{ mode() === 'handover' ? 'handover notes' : 'internal notes' }}. Type &#64; to mention a colleague.
          </p>
        }

        <sd-rich-text-editor
          #editor
          [value]="state().body"
          (valueChange)="onBody($event)"
          (mentionsChange)="patch({ mentions: $event }, false)"
          (submitted)="send()"
          [mentionSource]="mode() === 'reply' ? null : mentionSource"
          [placeholder]="placeholder()"
          [ariaLabel]="modeLabel()"
          toolbar="compact"
          minHeight="110px"
          maxHeight="320px"
        />

        @if (uploads().length) {
          <ul class="uploads" aria-label="Attachments">
            @for (u of uploads(); track u.key) {
              <li [class.is-error]="!!u.error">
                <sd-icon name="paperclip" [size]="13" />
                <span class="sd-truncate">{{ u.name }}</span>
                @if (u.error) {
                  <span class="err sd-xs">{{ u.error }}</span>
                } @else if (!u.fileId) {
                  <progress max="100" [value]="u.percent ?? 0" [attr.aria-label]="'Uploading ' + u.name"></progress>
                }
                <button type="button" class="x" [attr.aria-label]="'Remove ' + u.name" (click)="removeUpload(u.key)"><sd-icon name="x" [size]="13" /></button>
              </li>
            }
          </ul>
        }
        @if (notice(); as n) {
          <p class="notice" [class.err]="n.kind === 'error'" role="status">{{ n.message }}</p>
        }

        <div class="actions">
          <input #fileInput type="file" multiple hidden (change)="onFiles($event)" />
          <button sdButton size="sm" variant="ghost" iconOnly icon="paperclip" type="button" aria-label="Attach files" sdTooltip="Attach files" (click)="fileInput.click()"></button>
          @if (mode() === 'reply') {
            <sd-dropdown-menu size="sm" variant="ghost" icon="file-text" label="Templates" [items]="templateItems()" (select)="onTemplateMenu()" />
          }
          @for (a of extensionActions(); track a.key) {
            <button sdButton size="sm" variant="ghost" [icon]="a.icon" type="button" [loading]="runningAction() === a.key" (click)="runAction(a)">{{ a.label }}</button>
          }
          <span class="sd-spacer"></span>
          <button sdButton size="sm" variant="ghost" type="button" [disabled]="sending()" (click)="discard()">Discard</button>
          <div class="send">
            <button sdButton variant="primary" size="sm" [icon]="mode() === 'reply' ? 'send' : 'check'" type="button" [loading]="sending()" [disabled]="!canSend()" (click)="send()">
              {{ mode() === 'reply' ? 'Send' : mode() === 'handover' ? 'Add handover' : 'Add note' }}
            </button>
            @if (mode() === 'reply') {
              <sd-dropdown-menu size="sm" variant="primary" iconOnly icon="chevron-down" [caret]="false" align="end" ariaLabel="Send and set status" [items]="sendAndStatusItems()" [disabled]="!canSend() || sending()" />
            }
          </div>
        </div>
      </section>
    }
  `,
  styles: `
    :host {
      display: block;
      flex: none;
      border-top: 1px solid var(--sd-border);
      background: var(--sd-surface);
    }
    .bar {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: 8px;
      padding: 10px 16px;
    }
    .composer {
      display: flex;
      flex-direction: column;
      gap: 8px;
      padding: 8px 16px 12px;
    }
    .composer.is-note {
      background: color-mix(in srgb, var(--sd-note-bg) 55%, var(--sd-surface));
    }
    .modes {
      display: flex;
      align-items: center;
      gap: 4px;
      flex-wrap: wrap;
    }
    .mode {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      height: 30px;
      padding: 0 10px;
      border: 1px solid transparent;
      border-radius: var(--sd-radius-md);
      background: transparent;
      color: var(--sd-text-2);
      font: inherit;
      font-size: var(--sd-text-sm);
      cursor: pointer;
    }
    .mode:hover {
      background: var(--sd-surface-3);
    }
    .mode[aria-checked='true'] {
      border-color: var(--sd-primary-border);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
      font-weight: var(--sd-weight-medium);
    }
    .draft-state .err,
    .err {
      color: var(--sd-danger);
    }
    .recipients {
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .rlabel {
      width: 24px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
    .chips {
      flex: 1;
      min-width: 0;
    }
    .via {
      display: flex;
      align-items: center;
      gap: 6px;
    }
    .note-hint {
      color: var(--sd-note-fg);
    }
    .collision {
      padding: 10px 12px;
      border: 1px solid var(--sd-warning-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-warning-bg);
    }
    .collision-title {
      display: flex;
      align-items: center;
      gap: 6px;
      font-weight: var(--sd-weight-semibold);
      color: var(--sd-warning);
    }
    .collision ul {
      display: grid;
      gap: 6px;
      margin: 8px 0;
      padding: 0;
      list-style: none;
      max-height: 180px;
      overflow: auto;
    }
    .collision li {
      padding: 6px 8px;
      border-radius: var(--sd-radius-sm);
      background: var(--sd-surface);
    }
    .collision li.is-internal {
      background: var(--sd-note-bg);
    }
    .collision li p {
      margin-top: 2px;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .collision-actions {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      justify-content: flex-end;
    }
    .uploads {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .uploads li {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      max-width: 260px;
      padding: 4px 6px 4px 8px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface-2);
      font-size: var(--sd-text-sm);
    }
    .uploads li.is-error {
      border-color: var(--sd-danger-border);
      background: var(--sd-danger-bg);
    }
    progress {
      width: 60px;
      height: 6px;
    }
    .x {
      display: inline-flex;
      padding: 2px;
      border: 0;
      border-radius: var(--sd-radius-xs);
      background: transparent;
      color: var(--sd-text-3);
      cursor: pointer;
    }
    .x:hover {
      color: var(--sd-danger);
    }
    .notice {
      font-size: var(--sd-text-sm);
      color: var(--sd-text-2);
    }
    .actions {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: 4px;
    }
    .send {
      display: inline-flex;
      gap: 2px;
    }
    @media (max-width: 767px) {
      .bar,
      .composer {
        padding-left: 10px;
        padding-right: 10px;
      }
      .hide-sm {
        display: none;
      }
    }
  `,
})
export class ComposerComponent {
  readonly ticketId = input.required<Guid>();

  protected readonly ctx = inject(TicketContext);
  private readonly tickets = inject(TicketsApi);
  private readonly workbench = inject(WorkbenchApi);
  private readonly filesApi = inject(FilesApi);
  private readonly realtime = inject(RealtimeService);
  private readonly me = inject(MeStore);
  private readonly toast = inject(ToastService);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly editor = viewChild<SdRichTextEditor>('editor');

  protected readonly can = this.ctx.can;
  protected readonly mode = signal<ComposerMode>('reply');
  protected readonly expanded = signal(false);
  private readonly drafts = signal<Record<DraftKind, DraftState>>({ reply: { ...EMPTY }, note: { ...EMPTY } });
  protected readonly uploads = signal<Upload[]>([]);
  protected readonly showCc = signal(false);
  protected readonly collision = signal<ReplyCollisionDto | null>(null);
  protected readonly sending = signal(false);
  protected readonly draftStatus = signal<'idle' | 'saving' | 'saved' | 'error'>('idle');
  protected readonly draftSavedAt = signal<string | null>(null);
  protected readonly notice = signal<{ message: string; kind: 'info' | 'error' } | null>(null);
  protected readonly runningAction = signal<string | null>(null);
  private readonly templates = signal<ReplyTemplateDto[] | null>(null);
  private readonly templatesError = signal<string | null>(null);
  private readonly dirty = new Set<DraftKind>();
  private readonly save$ = new Subject<DraftKind>();
  private uploadKey = 0;
  private idleTimer: ReturnType<typeof setTimeout> | null = null;
  private activity: 'viewing' | 'replying' | 'noting' = 'viewing';

  private readonly kind = computed<DraftKind>(() => (this.mode() === 'reply' ? 'reply' : 'note'));
  protected readonly state = computed(() => this.drafts()[this.kind()]);
  protected readonly hasDraft = computed(() => !!this.drafts().reply.body || !!this.drafts().note.body);
  protected readonly isEmail = computed(() => this.ctx.ticket()?.channel === 'Email' || (!!this.ctx.ticket()?.contactEmail && this.ctx.ticket()?.channel === 'Portal'));
  protected readonly channelName = computed(() => channelLabel(this.ctx.ticket()?.channel));
  protected readonly modeLabel = computed(() => (this.mode() === 'reply' ? 'Reply to customer' : this.mode() === 'handover' ? 'Handover note' : 'Internal note'));
  protected readonly placeholder = computed(() =>
    this.mode() === 'reply' ? 'Write your reply… (Ctrl+Enter to send)' : this.mode() === 'handover' ? 'What should the next agent know?' : 'Add an internal note… @mention to notify',
  );
  protected readonly canSend = computed(() => {
    const s = this.state();
    const uploading = this.uploads().some((u) => !u.fileId && !u.error);
    const hasBody = !!htmlToText(s.body, 10) || /<img/i.test(s.body);
    if (uploading || !hasBody) return false;
    if (this.mode() === 'reply' && this.isEmail() && s.to.length === 0) return false;
    return true;
  });

  protected readonly isEmailAddress = (value: string) => EMAIL_RE.test(value);

  protected readonly mentionSource: SdMentionSource = (query: string) => {
    const q = query.toLowerCase();
    const items: SdMentionItem[] = (this.ctx.meta.ticketForm()?.agents ?? [])
      .filter((a) => a.userId !== this.me.user()?.id && a.name.toLowerCase().includes(q))
      .slice(0, 8)
      .map((a) => ({ id: a.userId, label: a.name, description: a.email ?? a.availability, avatarUrl: a.avatarUrl }));
    return Promise.resolve(items);
  };

  protected readonly templateItems = computed<SdMenuItem[]>(() => {
    const list = this.templates();
    if (this.templatesError()) return [{ id: 'err', label: this.templatesError()!, disabled: true }];
    if (list === null) return [{ id: 'loading', label: 'Loading templates…', disabled: true }];
    if (list.length === 0) return [{ id: 'none', label: 'No templates available', disabled: true }];
    const items: SdMenuItem[] = [];
    let category: string | null | undefined;
    for (const t of [...list].sort((a, b) => (a.category ?? '').localeCompare(b.category ?? ''))) {
      if (t.category !== category) {
        category = t.category;
        if (category) items.push({ id: `h-${category}`, label: category, heading: true });
      }
      items.push({ id: t.id, label: t.name, icon: 'file-text', run: () => void this.insertTemplate(t) });
    }
    return items;
  });

  protected readonly sendAndStatusItems = computed<SdMenuItem[]>(() => {
    const t = this.ctx.ticket();
    const statuses = allowedStatuses(this.ctx.meta.ticketForm(), t?.statusId ?? null).filter((s) => s.id !== t?.statusId && s.category !== 'Closed');
    return [
      { id: 'h', label: 'Send and set status', heading: true },
      ...statuses.map<SdMenuItem>((s) => ({ id: s.id, label: s.name, icon: 'circle-dot', run: () => void this.send(s.id) })),
    ];
  });

  protected readonly extensionActions = computed(() =>
    COMPOSER_ACTIONS.filter(
      (a) =>
        (!a.modes || a.modes.includes(this.mode())) &&
        (!a.permission || this.me.canAny(typeof a.permission === 'string' ? [a.permission] : a.permission)) &&
        (!a.feature || this.me.hasFeature(a.feature)),
    ),
  );

  constructor() {
    // New ticket → reset and load its drafts.
    effect(() => {
      const id = this.ticketId();
      untracked(() => this.resetFor(id));
    });
    // Pre-fill "To" with the contact's address once the ticket is loaded (unless a draft already set recipients).
    effect(() => {
      const email = this.ctx.ticket()?.contactEmail;
      untracked(() => {
        if (email && this.drafts().reply.to.length === 0 && !this.dirty.has('reply')) {
          this.drafts.update((d) => ({ ...d, reply: { ...d.reply, to: [email] } }));
        }
      });
    });
    const saveSub = this.save$.pipe(debounceTime(1200)).subscribe((kind) => void this.saveDraft(kind));
    inject(DestroyRef).onDestroy(() => {
      saveSub.unsubscribe();
      for (const kind of this.dirty) void this.saveDraft(kind);
      this.setActivity('viewing');
      if (this.idleTimer) clearTimeout(this.idleTimer);
      for (const u of this.uploads()) u.sub?.unsubscribe();
    });
  }

  /** Opens the composer in a mode and focuses the editor (shortcuts r / n). */
  open(mode: ComposerMode): void {
    const can = this.can();
    if (mode === 'reply' && !can?.reply) return;
    if (mode !== 'reply' && !can?.note) return;
    this.switchMode(mode);
    this.expanded.set(true);
    if (mode === 'reply' && this.templates() === null) this.loadTemplates();
    this.focusEditor();
  }

  /** The editor (TipTap) is created after render: retry briefly until it can take focus. */
  private focusEditor(attempt = 0): void {
    setTimeout(() => {
      const el = this.host.nativeElement.querySelector<HTMLElement>('[contenteditable="true"]');
      if (el) {
        el.focus({ preventScroll: true });
        this.editor()?.focus();
        this.host.nativeElement.scrollIntoView({ block: 'nearest' });
      } else if (attempt < 20) {
        this.focusEditor(attempt + 1);
      }
    }, attempt === 0 ? 0 : 40);
  }

  protected switchMode(mode: ComposerMode): void {
    const previous = this.kind();
    if (this.dirty.has(previous)) void this.saveDraft(previous);
    this.mode.set(mode);
    this.collision.set(null);
    this.notice.set(null);
    if (mode === 'reply' && this.templates() === null && this.expanded()) this.loadTemplates();
  }

  protected minimise(): void {
    for (const kind of this.dirty) void this.saveDraft(kind);
    this.expanded.set(false);
    this.setActivity('viewing');
  }

  protected patch(changes: Partial<DraftState>, markDirty = true): void {
    const kind = this.kind();
    this.drafts.update((d) => ({ ...d, [kind]: { ...d[kind], ...changes } }));
    if (markDirty) this.touch(kind);
  }

  protected onBody(html: string): void {
    const kind = this.kind();
    const s = this.drafts()[kind];
    if (html === s.body) return;
    // The conversation the agent replies to is what existed when they started writing.
    const basedOnSeq = s.basedOnSeq ?? this.ctx.ticket()?.lastThreadSeq ?? null;
    this.drafts.update((d) => ({ ...d, [kind]: { ...d[kind], body: html, basedOnSeq } }));
    this.touch(kind);
    this.setActivity(this.mode() === 'reply' ? 'replying' : 'noting');
    if (this.idleTimer) clearTimeout(this.idleTimer);
    this.idleTimer = setTimeout(() => this.setActivity('viewing'), IDLE_MS);
  }

  protected excerpt(t: ThreadDto): string {
    return htmlToText(t.bodyHtml ?? t.bodyText, 220);
  }

  protected reviewCollision(): void {
    const c = this.collision();
    if (!c) return;
    this.patch({ basedOnSeq: c.currentSeq });
    this.collision.set(null);
    this.ctx.loadTimeline();
    this.ctx.refresh();
    this.editor()?.focus();
  }

  protected onTemplateMenu(): void {
    if (this.templates() === null) this.loadTemplates();
  }

  protected onFiles(event: Event): void {
    const inputEl = event.target as HTMLInputElement;
    const files = Array.from(inputEl.files ?? []);
    inputEl.value = '';
    for (const file of files) this.upload(file);
  }

  protected removeUpload(key: number): void {
    this.uploads.update((list) => {
      list.find((u) => u.key === key)?.sub?.unsubscribe();
      return list.filter((u) => u.key !== key);
    });
  }

  protected async discard(): Promise<void> {
    const kind = this.kind();
    const id = this.ticketId();
    this.dirty.delete(kind);
    this.drafts.update((d) => ({ ...d, [kind]: { ...EMPTY, to: kind === 'reply' ? this.defaultTo() : [] } }));
    for (const u of this.uploads()) u.sub?.unsubscribe();
    this.uploads.set([]);
    this.collision.set(null);
    this.draftStatus.set('idle');
    this.expanded.set(false);
    this.setActivity('viewing');
    try {
      await firstValueFrom(this.tickets.deleteDraft(id, kind));
    } catch (err) {
      const e = ApiError.from(err);
      if (e.status !== 404) toastApiError(this.toast, e, 'Could not delete the draft');
    }
  }

  /** Sends the reply / note. `statusId` = "send and set status"; `force` = send despite a reply collision. */
  async send(statusId: Guid | null = null, force = false): Promise<void> {
    if (!this.canSend() || this.sending()) return;
    const id = this.ticketId();
    const mode = this.mode();
    const kind = this.kind();
    const s = this.state();
    const fileIds = this.uploads()
      .filter((u) => u.fileId)
      .map((u) => u.fileId!);
    this.sending.set(true);
    this.notice.set(null);
    try {
      if (mode === 'reply') {
        await firstValueFrom(
          this.tickets.reply(id, {
            bodyHtml: s.body,
            to: this.isEmail() ? s.to : null,
            cc: this.isEmail() && s.cc.length ? s.cc : null,
            attachmentFileIds: fileIds.length ? fileIds : null,
            statusId,
            basedOnSeq: s.basedOnSeq ?? this.ctx.ticket()?.lastThreadSeq ?? null,
            force,
            templateId: s.templateId,
            clientRequestId: newClientRequestId(),
          }),
        );
      } else {
        await firstValueFrom(
          this.tickets.addNote(id, {
            bodyHtml: s.body,
            kind: mode === 'handover' ? 'handover' : 'note',
            mentionUserIds: s.mentions.length ? s.mentions : null,
            attachmentFileIds: fileIds.length ? fileIds : null,
            clientRequestId: newClientRequestId(),
          }),
        );
      }
      this.dirty.delete(kind);
      this.drafts.update((d) => ({ ...d, [kind]: { ...EMPTY, to: kind === 'reply' ? this.defaultTo() : [] } }));
      this.uploads.set([]);
      this.collision.set(null);
      this.draftStatus.set('idle');
      this.expanded.set(false);
      this.setActivity('viewing');
      this.toast.success(mode === 'reply' ? (statusId ? 'Reply sent and status updated' : 'Reply sent') : mode === 'handover' ? 'Handover note added' : 'Note added');
      this.ctx.scrollToEnd.set(true);
      this.ctx.loadTimeline();
      this.ctx.refresh();
    } catch (err) {
      const e = ApiError.from(err);
      if (e.is('reply_collision') && e.replyCollision) {
        this.collision.set(e.replyCollision);
        this.ctx.loadTimeline();
      } else {
        toastApiError(this.toast, e, mode === 'reply' ? 'The reply was not sent' : 'The note was not added');
        this.notice.set({ message: `${e.userMessage} Your text is kept — try again.`, kind: 'error' });
      }
    } finally {
      this.sending.set(false);
    }
  }

  protected async runAction(action: ComposerActionDef): Promise<void> {
    const ticket = this.ctx.ticket();
    if (!ticket) return;
    this.runningAction.set(action.key);
    try {
      await runInInjectionContext(this.injector, () =>
        action.run({
          ticket,
          mode: this.mode(),
          html: () => this.state().body,
          insertHtml: (html) => this.editor()?.insertHtml(html),
          replaceHtml: (html) => this.patch({ body: html }),
          notify: (message, kind = 'info') => this.notice.set({ message, kind }),
        }),
      );
    } catch (err) {
      this.notice.set({ message: err instanceof Error ? err.message : 'The action failed.', kind: 'error' });
    } finally {
      this.runningAction.set(null);
    }
  }

  private async insertTemplate(t: ReplyTemplateDto): Promise<void> {
    try {
      const rendered = await firstValueFrom(this.workbench.renderTemplate(this.ticketId(), t.id));
      this.editor()?.insertHtml(rendered.bodyHtml);
      this.patch({ templateId: t.id });
    } catch (err) {
      toastApiError(this.toast, err, 'Could not insert the template');
    }
  }

  private loadTemplates(): void {
    this.templatesError.set(null);
    this.workbench.templates().subscribe({
      next: (list) => this.templates.set(list),
      error: (err: unknown) => this.templatesError.set(ApiError.from(err).userMessage),
    });
  }

  private upload(file: File): void {
    const key = ++this.uploadKey;
    const item: Upload = { key, name: file.name, size: file.size, percent: 0, fileId: null, error: null, sub: null };
    const update = (changes: Partial<Upload>) => this.uploads.update((list) => list.map((u) => (u.key === key ? { ...u, ...changes } : u)));
    this.uploads.update((list) => [...list, item]);
    const sub = this.filesApi.upload(file).subscribe({
      next: (e) => {
        if (e.kind === 'progress') update({ percent: e.percent });
        else update({ fileId: e.file.fileId, percent: 100, error: e.file.scanStatus === 'Infected' ? 'Blocked by the virus scanner' : null });
      },
      error: (err: unknown) => update({ error: ApiError.from(err).userMessage }),
    });
    update({ sub });
  }

  private touch(kind: DraftKind): void {
    this.dirty.add(kind);
    this.save$.next(kind);
  }

  private async saveDraft(kind: DraftKind): Promise<void> {
    if (!this.dirty.has(kind)) return;
    const id = this.ticketId();
    const s = this.drafts()[kind];
    this.dirty.delete(kind);
    this.draftStatus.set('saving');
    try {
      if (!htmlToText(s.body, 5)) {
        await firstValueFrom(this.tickets.deleteDraft(id, kind));
        this.draftStatus.set('idle');
        return;
      }
      const saved = await firstValueFrom(
        this.tickets.saveDraft(id, {
          kind,
          bodyHtml: s.body,
          to: kind === 'reply' ? s.to : null,
          cc: kind === 'reply' ? s.cc : null,
          templateId: s.templateId,
          basedOnSeq: s.basedOnSeq ?? this.ctx.ticket()?.lastThreadSeq ?? 0,
        }),
      );
      if (id === this.ticketId()) {
        this.draftStatus.set('saved');
        this.draftSavedAt.set(saved.updatedAt);
      }
    } catch {
      if (id === this.ticketId()) this.draftStatus.set('error');
    }
  }

  private resetFor(id: Guid): void {
    this.collision.set(null);
    this.notice.set(null);
    this.uploads.set([]);
    this.expanded.set(false);
    this.draftStatus.set('idle');
    this.dirty.clear();
    this.showCc.set(false);
    this.mode.set(this.can()?.reply === false && this.can()?.note ? 'note' : 'reply');
    this.drafts.set({ reply: { ...EMPTY, to: this.defaultTo() }, note: { ...EMPTY } });
    for (const kind of ['reply', 'note'] as DraftKind[]) {
      this.tickets.getDraft(id, kind).subscribe({
        next: (d) => {
          if (!d || id !== this.ticketId()) return;
          this.drafts.update((all) => ({
            ...all,
            [kind]: {
              body: d.bodyHtml ?? '',
              to: d.to.length ? d.to : all[kind].to,
              cc: d.cc,
              templateId: d.templateId,
              basedOnSeq: d.basedOnSeq,
              mentions: [],
            },
          }));
          if (d.cc.length) this.showCc.set(true);
          this.draftStatus.set('saved');
          this.draftSavedAt.set(d.updatedAt);
        },
        // A missing draft is normal; other failures only mean we start empty.
        error: () => undefined,
      });
    }
  }

  private defaultTo(): string[] {
    const email = this.ctx.ticket()?.contactEmail;
    return email ? [email] : [];
  }

  private setActivity(activity: 'viewing' | 'replying' | 'noting'): void {
    if (this.activity === activity) return;
    this.activity = activity;
    const id = this.ticketId();
    if (id) this.realtime.setActivity(id, activity);
  }
}
