import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import {
  ConversationChangedEvent,
  ConversationDetailDto,
  ConversationListItemDto,
  ConversationQuery,
  ConversationsApi,
  MeStore,
  MetaApi,
  RealtimeService,
  cursorList,
  safeStorage,
} from '@sd/core';
import { SdBadge, SdButton, SdEmptyState, SdIcon, SdInput, SdRelativeTimePipe, SdSkeleton, SdSplitLayout } from '@sd/ui';
import { bufferTime, filter } from 'rxjs';
import { INBOX_CHANNELS, channelIcon, channelLabel, conversationStatusLabel, conversationStatusTone } from '../../shared/channel-ui';
import { ConversationContextComponent } from './conversation-context.component';
import { ConversationViewComponent } from './conversation-view.component';

interface InboxFilters {
  channel: string;
  status: string;
  assignee: string;
  q: string;
}

const FILTERS_KEY = 'sd.inbox.filters';
const DEFAULT_FILTERS: InboxFilters = { channel: '', status: 'active', assignee: '', q: '' };

/**
 * /inbox — the unified omnichannel inbox: every e-mail thread, WhatsApp/SMS/Messenger conversation, live chat and call
 * in one keyset-paged list (filters: channel, status, assignee, waiting for agent, search), live-updated over
 * SignalR; the selected conversation (`?c=`) opens in the middle with its customer/ticket context on the right.
 */
@Component({
  selector: 'desk-inbox-page',
  imports: [
    FormsModule,
    RouterLink,
    SdSplitLayout,
    SdButton,
    SdBadge,
    SdIcon,
    SdInput,
    SdEmptyState,
    SdSkeleton,
    SdRelativeTimePipe,
    ConversationViewComponent,
    ConversationContextComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-split-layout storageKey="inbox" [defaultLeft]="360" [defaultRight]="300">
      <section sdSplitLeft class="list-panel" aria-label="Conversations">
        <div class="list-head">
          <h1>Inbox</h1>
          <span class="sd-spacer"></span>
          <a sdButton size="sm" variant="secondary" icon="messages" routerLink="/inbox/chat">Live chat</a>
          @if (me.can('admin.channels')) {
            <a sdButton size="sm" variant="ghost" icon="settings" iconOnly aria-label="Channel settings" routerLink="/admin/channels"></a>
          }
        </div>
        <div class="filters">
          <label class="sd-visually-hidden" for="inbox-q">Search conversations</label>
          <input id="inbox-q" sdInput size="sm" type="search" placeholder="Search name, subject, address…" [ngModel]="filters().q" (ngModelChange)="setFilter('q', $event)" />
          <div class="row">
            <label class="sd-visually-hidden" for="inbox-channel">Channel</label>
            <select id="inbox-channel" sdInput size="sm" [ngModel]="filters().channel" (ngModelChange)="setFilter('channel', $event)">
              <option value="">All channels</option>
              @for (c of channels; track c) {
                <option [value]="c">{{ channelLabel(c) }}</option>
              }
            </select>
            <label class="sd-visually-hidden" for="inbox-status">Status</label>
            <select id="inbox-status" sdInput size="sm" [ngModel]="filters().status" (ngModelChange)="setFilter('status', $event)">
              <option value="active">Active</option>
              <option value="WaitingForAgent">Waiting for agent</option>
              <option value="AgentActive">With agent</option>
              <option value="BotActive">Bot</option>
              <option value="Open">Open</option>
              <option value="Closed">Closed</option>
              <option value="">All statuses</option>
            </select>
            <label class="sd-visually-hidden" for="inbox-assignee">Assignee</label>
            <select id="inbox-assignee" sdInput size="sm" [ngModel]="filters().assignee" (ngModelChange)="setFilter('assignee', $event)">
              <option value="">Anyone</option>
              <option value="me">Me</option>
              <option value="unassigned">Unassigned</option>
              @for (a of agents(); track a.userId) {
                <option [value]="a.userId">{{ a.name }}</option>
              }
            </select>
          </div>
          @if (waitingCount() > 0 && filters().status !== 'WaitingForAgent') {
            <button type="button" class="waiting-pill" (click)="setFilter('status', 'WaitingForAgent')">
              <sd-icon name="bell" [size]="13" /> {{ waitingCount() }} waiting for an agent
            </button>
          }
        </div>

        <div class="list" role="listbox" aria-label="Conversations" [attr.aria-busy]="list.loading()">
          @if (list.loading() && !list.loaded()) {
            @for (i of [1, 2, 3, 4, 5]; track i) {
              <div class="skeleton"><sd-skeleton [lines]="2" /></div>
            }
          } @else if (list.error(); as e) {
            <sd-empty-state compact icon="circle-alert" title="Could not load conversations" [message]="e.userMessage">
              <button sdButton size="sm" (click)="list.load()">Retry</button>
            </sd-empty-state>
          } @else {
            @for (c of list.items(); track c.id) {
              <button
                type="button"
                role="option"
                class="item"
                [class.selected]="c.id === c_()"
                [class.waiting]="c.status === 'WaitingForAgent'"
                [attr.aria-selected]="c.id === c_()"
                (click)="select(c.id)"
              >
                <span class="ch" [attr.data-channel]="c.channel" aria-hidden="true"><sd-icon [name]="channelIcon(c.channel)" [size]="15" /></span>
                <span class="main">
                  <span class="line1">
                    <strong class="sd-truncate">{{ c.contactName || c.visitorName || 'Unknown' }}</strong>
                    <time class="sd-xs sd-muted" [attr.datetime]="c.lastMessageAt">{{ c.lastMessageAt | sdRelativeTime }}</time>
                  </span>
                  <span class="line2 sd-small sd-truncate">{{ c.subject || channelLabel(c.channel) }}</span>
                  <span class="line3 sd-xs sd-muted sd-truncate">
                    @if (c.lastMessageDirection === 'Outbound') { You: }{{ c.lastMessagePreview || '—' }}
                  </span>
                  <span class="tags">
                    <sd-badge size="sm" [tone]="statusTone(c.status)">{{ statusLabel(c.status) }}</sd-badge>
                    @if (c.ticketNumber) {
                      <sd-badge size="sm" variant="outline" icon="ticket">#{{ c.ticketNumber }}</sd-badge>
                    }
                    @if (c.assignedUserName) {
                      <span class="sd-xs sd-muted sd-truncate">{{ c.assignedUserName }}</span>
                    }
                  </span>
                </span>
              </button>
            } @empty {
              <sd-empty-state compact icon="inbox" title="No conversations" message="Nothing matches these filters." />
            }
            @if (list.hasMore()) {
              <button sdButton size="sm" variant="ghost" block [loading]="list.loadingMore()" (click)="list.loadMore()">Load more</button>
            }
          }
        </div>
      </section>

      <section sdSplitMain class="main-panel" aria-label="Conversation">
        @if (c_(); as id) {
          <desk-conversation-view [conversationId]="id" (changed)="onChanged($event)" />
        } @else {
          <sd-empty-state icon="inbox" title="Select a conversation" message="E-mail, WhatsApp, SMS, Messenger, live chat and calls arrive here in one place." />
        }
      </section>

      <aside sdSplitRight class="ctx-panel" aria-label="Conversation context">
        <desk-conversation-context [detail]="current()" />
      </aside>
    </sd-split-layout>
  `,
  styles: `
    :host {
      display: block;
      height: 100%;
    }
    sd-split-layout {
      height: 100%;
    }
    .list-panel,
    .main-panel,
    .ctx-panel {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 0;
    }
    .ctx-panel {
      overflow-y: auto;
      background: var(--sd-surface-2);
    }
    .list-head {
      display: flex;
      align-items: center;
      gap: var(--sd-space-2);
      padding: var(--sd-space-3) var(--sd-space-3) var(--sd-space-2);
    }
    h1 {
      margin: 0;
      font-size: var(--sd-text-xl);
      font-weight: var(--sd-weight-semibold);
    }
    .filters {
      display: flex;
      flex-direction: column;
      gap: var(--sd-space-2);
      padding: 0 var(--sd-space-3) var(--sd-space-2);
      border-bottom: 1px solid var(--sd-border);
    }
    .row {
      display: grid;
      grid-template-columns: repeat(3, minmax(0, 1fr));
      gap: var(--sd-space-2);
    }
    .waiting-pill {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      align-self: flex-start;
      padding: 3px 10px;
      border: 1px solid var(--sd-warning-border);
      border-radius: var(--sd-radius-pill);
      background: var(--sd-warning-bg);
      color: var(--sd-text);
      font: inherit;
      font-size: var(--sd-text-xs);
      cursor: pointer;
    }
    .list {
      flex: 1;
      min-height: 0;
      overflow-y: auto;
      padding: var(--sd-space-1) 0;
    }
    .skeleton {
      padding: var(--sd-space-3);
    }
    .item {
      display: flex;
      gap: var(--sd-space-2);
      width: 100%;
      padding: var(--sd-space-2) var(--sd-space-3);
      border: 0;
      border-left: 3px solid transparent;
      background: transparent;
      color: inherit;
      font: inherit;
      text-align: left;
      cursor: pointer;
    }
    .item:hover {
      background: var(--sd-row-hover);
    }
    .item.selected {
      background: var(--sd-row-selected);
      border-left-color: var(--sd-primary);
    }
    .item.waiting:not(.selected) {
      border-left-color: var(--sd-warning);
    }
    .item:focus-visible {
      outline: none;
      box-shadow: inset var(--sd-focus-ring);
    }
    .ch {
      display: grid;
      place-items: center;
      width: 28px;
      height: 28px;
      flex: none;
      border-radius: var(--sd-radius-md);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    .ch[data-channel='WhatsApp'] {
      background: var(--sd-success-bg);
      color: var(--sd-success);
    }
    .ch[data-channel='Phone'] {
      background: var(--sd-info-bg);
      color: var(--sd-info);
    }
    .ch[data-channel='Chat'] {
      background: var(--sd-accent-soft);
      color: var(--sd-accent);
    }
    .main {
      display: flex;
      flex-direction: column;
      gap: 1px;
      min-width: 0;
      flex: 1;
    }
    .line1 {
      display: flex;
      justify-content: space-between;
      gap: var(--sd-space-2);
    }
    .tags {
      display: flex;
      align-items: center;
      gap: 6px;
      margin-top: 3px;
      min-width: 0;
    }
  `,
})
export class InboxPage {
  private readonly api = inject(ConversationsApi);
  private readonly realtime = inject(RealtimeService);
  private readonly router = inject(Router);
  private readonly meta = inject(MetaApi);
  protected readonly me = inject(MeStore);

  /** Selected conversation (query param `?c=`). */
  readonly c = input<string | undefined>();
  protected readonly c_ = computed(() => this.c() ?? null);

  protected readonly channels = INBOX_CHANNELS;
  protected readonly channelIcon = channelIcon;
  protected readonly channelLabel = channelLabel;
  protected readonly statusLabel = conversationStatusLabel;
  protected readonly statusTone = conversationStatusTone;

  protected readonly filters = signal<InboxFilters>({ ...DEFAULT_FILTERS, ...(safeStorage.getJson<InboxFilters>(FILTERS_KEY) ?? {}) });
  protected readonly current = signal<ConversationDetailDto | null>(null);
  protected readonly agents = computed(() => (this.meta.ticketForm()?.agents ?? []).filter((a) => !a.isLightAgent));
  protected readonly waitingCount = signal(0);

  protected readonly list = cursorList<ConversationListItemDto>((cursor) => this.api.list({ ...this.query(), cursor }));

  constructor() {
    this.meta.ticketForm$().subscribe({ error: () => undefined });
    effect(() => {
      const f = this.filters();
      untracked(() => {
        safeStorage.setJson(FILTERS_KEY, f);
        this.list.load();
      });
    });
    effect(() => {
      if (!this.c()) this.current.set(null);
    });
    this.refreshWaiting();

    const destroyRef = inject(DestroyRef);
    const sub = this.realtime.conversationUpdated$
      .pipe(
        bufferTime(500),
        filter((batch) => batch.length > 0),
      )
      .subscribe((batch) => this.applyRealtime(batch as unknown as ConversationChangedEvent[]));
    const reconnect = this.realtime.reconnected$.subscribe(() => {
      this.list.load();
      this.refreshWaiting();
    });
    destroyRef.onDestroy(() => {
      sub.unsubscribe();
      reconnect.unsubscribe();
    });
  }

  protected setFilter(key: keyof InboxFilters, value: string): void {
    this.filters.update((f) => ({ ...f, [key]: value ?? '' }));
  }

  protected select(id: string): void {
    void this.router.navigate([], { queryParams: { c: id }, queryParamsHandling: 'merge' });
  }

  protected onChanged(detail: ConversationDetailDto): void {
    this.current.set(detail);
    if (this.list.items().some((i) => i.id === detail.summary.id)) this.list.upsert(detail.summary);
  }

  private query(): ConversationQuery {
    const f = this.filters();
    return {
      channel: f.channel || null,
      status: (f.status || null) as ConversationQuery['status'],
      mine: f.assignee === 'me',
      unassigned: f.assignee === 'unassigned',
      assigneeId: f.assignee && f.assignee !== 'me' && f.assignee !== 'unassigned' ? f.assignee : null,
      q: f.q.trim() || null,
      limit: 40,
    };
  }

  private matches(s: ConversationListItemDto): boolean {
    const f = this.filters();
    if (f.q.trim()) return false; // searches are not patched live (the server does the matching)
    if (f.channel && s.channel !== f.channel) return false;
    if (f.status === 'active' && s.status === 'Closed') return false;
    if (f.status && f.status !== 'active' && s.status !== f.status) return false;
    if (f.assignee === 'me' && s.assignedUserId !== this.me.user()?.id) return false;
    if (f.assignee === 'unassigned' && s.assignedUserId) return false;
    if (f.assignee && !['me', 'unassigned'].includes(f.assignee) && s.assignedUserId !== f.assignee) return false;
    return true;
  }

  /** Patches the list from `conversation.updated` events: refetch the summary, move it to the top or drop it. */
  private applyRealtime(events: ConversationChangedEvent[]): void {
    const ids = [...new Set(events.map((e) => e.conversationId).filter((id): id is string => !!id))];
    if (events.some((e) => e.status === 'WaitingForAgent' || e.kind === 'status')) this.refreshWaiting();
    for (const id of ids) {
      this.api.get(id).subscribe({
        next: (d) => {
          const s = d.summary;
          const present = this.list.items().some((i) => i.id === id);
          if (this.matches(s)) {
            if (present) this.list.remove(id);
            this.list.upsert(s);
          } else if (present && !this.filters().q.trim()) {
            this.list.remove(id);
          }
        },
        error: () => undefined,
      });
    }
  }

  private refreshWaiting(): void {
    this.api.list({ status: 'WaitingForAgent', limit: 100 }).subscribe({
      next: (page) => this.waitingCount.set(page.items.length),
      error: () => undefined,
    });
  }
}
