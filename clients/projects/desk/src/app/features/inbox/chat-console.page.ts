import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ConversationDetailDto, ConversationListItemDto, ConversationsApi, MeStore, RealtimeService } from '@sd/core';
import { SdBadge, SdButton, SdEmptyState, SdRelativeTimePipe, SdSplitLayout, SdTooltip, ToastService } from '@sd/ui';
import { debounceTime, filter, forkJoin } from 'rxjs';
import { ChatAlertsService } from './chat-alerts.service';
import { ConversationContextComponent } from './conversation-context.component';
import { ConversationViewComponent } from './conversation-view.component';

/**
 * /inbox/chat — live-chat console: the queue of chats waiting for an agent (with chime + desktop notification for
 * new ones), my active chats and chats the bot is handling; accept, reply in real time with typing indicators,
 * verify the visitor and end the chat (the transcript lands on the ticket). Works alongside the ticket workbench.
 */
@Component({
  selector: 'desk-chat-console-page',
  imports: [RouterLink, SdSplitLayout, SdButton, SdBadge, SdEmptyState, SdTooltip, SdRelativeTimePipe, ConversationViewComponent, ConversationContextComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-split-layout storageKey="chat-console" [defaultLeft]="320" [defaultRight]="300">
      <section sdSplitLeft class="queue" aria-label="Chat queue">
        <div class="head">
          <a sdButton size="sm" variant="ghost" icon="arrow-left" iconOnly aria-label="Back to the inbox" routerLink="/inbox"></a>
          <h1>Live chat</h1>
          <span class="sd-spacer"></span>
          <button
            sdButton
            size="sm"
            variant="ghost"
            iconOnly
            [icon]="alerts.soundOn() ? 'volume-on' : 'volume-off'"
            [attr.aria-label]="alerts.soundOn() ? 'Mute new-chat sound' : 'Play a sound for new chats'"
            [sdTooltip]="alerts.soundOn() ? 'Sound on' : 'Sound off'"
            [pressed]="alerts.soundOn()"
            (click)="alerts.toggleSound()"
          ></button>
          @if (alerts.desktopPermission() === 'default') {
            <button sdButton size="sm" variant="ghost" icon="bell" iconOnly aria-label="Enable desktop alerts" sdTooltip="Enable desktop alerts" (click)="alerts.enableDesktop()"></button>
          }
        </div>

        <h2 class="section">
          Waiting <sd-badge size="sm" [tone]="waiting().length ? 'warning' : 'neutral'">{{ waiting().length }}</sd-badge>
        </h2>
        <ul class="items" aria-label="Waiting for an agent">
          @for (c of waiting(); track c.id) {
            <li>
              <button type="button" class="item waiting" [class.selected]="c.id === c_()" (click)="select(c.id)">
                <span class="name sd-truncate">{{ c.visitorName || c.contactName || 'Visitor' }}</span>
                <span class="sd-xs sd-muted">waiting {{ c.lastMessageAt | sdRelativeTime }}</span>
                <span class="sd-small sd-truncate preview">{{ c.lastMessagePreview || c.subject }}</span>
              </button>
              @if (me.can('tickets.reply')) {
                <button sdButton size="sm" variant="primary" class="accept" [loading]="accepting() === c.id" (click)="accept(c)">Accept</button>
              }
            </li>
          } @empty {
            <li class="empty sd-small sd-muted">No one is waiting.</li>
          }
        </ul>

        <h2 class="section">My chats <sd-badge size="sm">{{ mine().length }}</sd-badge></h2>
        <ul class="items" aria-label="My active chats">
          @for (c of mine(); track c.id) {
            <li>
              <button type="button" class="item" [class.selected]="c.id === c_()" (click)="select(c.id)">
                <span class="name sd-truncate">{{ c.visitorName || c.contactName || 'Visitor' }}</span>
                <span class="sd-xs sd-muted">{{ c.lastMessageAt | sdRelativeTime }}</span>
                <span class="sd-small sd-truncate preview">{{ c.lastMessagePreview || c.subject }}</span>
              </button>
            </li>
          } @empty {
            <li class="empty sd-small sd-muted">Accept a waiting chat to start.</li>
          }
        </ul>

        <h2 class="section">With the bot <sd-badge size="sm" tone="info">{{ bot().length }}</sd-badge></h2>
        <ul class="items" aria-label="Chats handled by the bot">
          @for (c of bot(); track c.id) {
            <li>
              <button type="button" class="item" [class.selected]="c.id === c_()" (click)="select(c.id)">
                <span class="name sd-truncate">{{ c.visitorName || c.contactName || 'Visitor' }}</span>
                <span class="sd-xs sd-muted">{{ c.lastMessageAt | sdRelativeTime }}</span>
                <span class="sd-small sd-truncate preview">{{ c.lastMessagePreview }}</span>
              </button>
            </li>
          } @empty {
            <li class="empty sd-small sd-muted">No bot conversations.</li>
          }
        </ul>
      </section>

      <section sdSplitMain class="main" aria-label="Chat">
        @if (c_(); as id) {
          <desk-conversation-view [conversationId]="id" (changed)="onChanged($event)" />
        } @else {
          <sd-empty-state icon="messages" title="No chat selected" message="Accept a waiting chat or pick one of yours. New chats chime and raise a desktop notification." />
        }
      </section>

      <aside sdSplitRight class="ctx" aria-label="Visitor">
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
    .queue,
    .main,
    .ctx {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 0;
    }
    .queue,
    .ctx {
      overflow-y: auto;
    }
    .ctx {
      background: var(--sd-surface-2);
    }
    .head {
      display: flex;
      align-items: center;
      gap: var(--sd-space-2);
      padding: var(--sd-space-3);
    }
    h1 {
      margin: 0;
      font-size: var(--sd-text-xl);
      font-weight: var(--sd-weight-semibold);
    }
    .section {
      display: flex;
      align-items: center;
      gap: var(--sd-space-2);
      margin: var(--sd-space-3) var(--sd-space-3) var(--sd-space-1);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      text-transform: uppercase;
      letter-spacing: 0.04em;
      color: var(--sd-text-3);
    }
    .items {
      list-style: none;
      margin: 0;
      padding: 0;
    }
    .items li {
      display: flex;
      align-items: center;
      gap: var(--sd-space-2);
      padding-right: var(--sd-space-3);
    }
    .item {
      display: grid;
      grid-template-columns: 1fr auto;
      gap: 2px var(--sd-space-2);
      flex: 1;
      min-width: 0;
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
    .name {
      font-weight: var(--sd-weight-medium);
    }
    .preview {
      grid-column: 1 / -1;
      color: var(--sd-text-2);
    }
    .empty {
      padding: var(--sd-space-1) var(--sd-space-3) var(--sd-space-2);
    }
    .accept {
      flex: none;
    }
  `,
})
export class ChatConsolePage {
  private readonly api = inject(ConversationsApi);
  private readonly realtime = inject(RealtimeService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  protected readonly alerts = inject(ChatAlertsService);
  protected readonly me = inject(MeStore);

  /** Selected chat (query param `?c=`). */
  readonly c = input<string | undefined>();
  protected readonly c_ = computed(() => this.c() ?? null);

  protected readonly waiting = signal<ConversationListItemDto[]>([]);
  protected readonly mine = signal<ConversationListItemDto[]>([]);
  protected readonly bot = signal<ConversationListItemDto[]>([]);
  protected readonly current = signal<ConversationDetailDto | null>(null);
  protected readonly accepting = signal<string | null>(null);
  private knownWaiting: Set<string> | null = null;

  constructor() {
    this.refresh();
    const sub = this.realtime.conversationUpdated$
      .pipe(
        filter((e) => !e['channel'] || e['channel'] === 'Chat'),
        debounceTime(300),
      )
      .subscribe(() => this.refresh());
    const reconnect = this.realtime.reconnected$.subscribe(() => this.refresh());
    // Fallback refresh in case realtime is unavailable (proxies, offline hub).
    const timer = setInterval(() => this.refresh(), 30_000);
    inject(DestroyRef).onDestroy(() => {
      sub.unsubscribe();
      reconnect.unsubscribe();
      clearInterval(timer);
    });
  }

  protected select(id: string): void {
    void this.router.navigate([], { queryParams: { c: id }, queryParamsHandling: 'merge' });
  }

  protected onChanged(detail: ConversationDetailDto): void {
    this.current.set(detail);
  }

  protected accept(c: ConversationListItemDto): void {
    this.accepting.set(c.id);
    this.api.accept(c.id).subscribe({
      next: () => {
        this.accepting.set(null);
        this.select(c.id);
        this.refresh();
      },
      error: () => {
        this.accepting.set(null);
        this.refresh();
      },
    });
  }

  private refresh(): void {
    forkJoin({
      waiting: this.api.list({ channel: 'Chat', status: 'WaitingForAgent', limit: 100 }),
      mine: this.api.list({ channel: 'Chat', status: 'AgentActive', mine: true, limit: 100 }),
      bot: this.api.list({ channel: 'Chat', status: 'BotActive', limit: 100 }),
    }).subscribe({
      next: ({ waiting, mine, bot }) => {
        this.notifyNew(waiting.items);
        // Oldest waiting first (fair queue).
        this.waiting.set([...waiting.items].sort((a, b) => a.lastMessageAt.localeCompare(b.lastMessageAt)));
        this.mine.set(mine.items);
        this.bot.set(bot.items);
      },
      error: () => undefined,
    });
  }

  private notifyNew(items: ConversationListItemDto[]): void {
    const ids = new Set(items.map((i) => i.id));
    if (this.knownWaiting) {
      for (const item of items) {
        if (this.knownWaiting.has(item.id)) continue;
        const visitor = item.visitorName || item.contactName || 'Visitor';
        this.alerts.newChat(visitor, item.lastMessagePreview ?? item.subject, () => this.select(item.id));
        this.toast.show({ kind: 'warning', title: `${visitor} is waiting`, message: item.lastMessagePreview ?? undefined, key: `chat-${item.id}` });
      }
    }
    this.knownWaiting = ids;
  }
}
