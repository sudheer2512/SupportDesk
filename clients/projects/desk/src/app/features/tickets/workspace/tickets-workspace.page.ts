import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { ActivatedRoute, NavigationEnd, ParamMap, Router, RouterLink, RouterOutlet } from '@angular/router';
import { MeStore, MetaApi, QUEUE_KEYS, QueueKey, injectShortcuts } from '@sd/core';
import { CommandPaletteService, SdButton, SdCommand, SdIcon, SdSplitLayout, SdTooltip } from '@sd/ui';
import { filter } from 'rxjs';
import { ContextPanelComponent } from '../context/context-panel.component';
import { allowedStatuses } from '../shared/ticket-ui';
import { QueuePanelComponent } from './queue-panel.component';
import { RecycleBinComponent } from './recycle-bin.component';
import { TicketListComponent } from './ticket-list.component';
import {
  QueueSelection,
  STANDARD_QUEUES,
  TRASH_QUEUE_PARAM,
  TicketCommand,
  TicketWorkbenchStore,
  selectionQueryParams,
  selectionStorageKey,
} from './workbench.store';

/**
 * /tickets — the agent workspace: queues & saved views (left), the ticket list and open tickets as tabs
 * (centre; the routed ticket renders in the outlet), and the customer context panel (right). Registers the
 * workspace keyboard shortcuts (j/k, r, n, a, e, s, u, g q) and palette commands.
 */
@Component({
  selector: 'desk-tickets-workspace',
  imports: [RouterOutlet, RouterLink, SdSplitLayout, SdButton, SdIcon, SdTooltip, QueuePanelComponent, TicketListComponent, RecycleBinComponent, ContextPanelComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-split-layout
      class="split"
      storageKey="tickets.workspace"
      [defaultLeft]="250"
      [minLeft]="200"
      [maxLeft]="420"
      [defaultRight]="340"
      [showRight]="!!store.activeTicketId()"
      [(leftCollapsed)]="leftCollapsed"
      [(rightCollapsed)]="rightCollapsed"
    >
      <section sdSplitLeft class="left">
        <desk-queue-panel [columnsStorageKey]="listStorageKey()" (chosen)="onQueueChosen($event)" />
      </section>

      <section sdSplitMain class="main">
        <nav class="strip" aria-label="Open tickets">
          <a class="tab tab--list" routerLink="/tickets" [class.is-active]="!store.activeTicketId()" [attr.aria-current]="!store.activeTicketId() ? 'page' : null">
            <sd-icon name="list" [size]="14" />
            <span class="sd-truncate">{{ store.selectionLabel() }}</span>
          </a>
          <div class="tabs" role="list">
            @for (tab of store.tabs(); track tab.id) {
              <div class="tab" role="listitem" [class.is-active]="tab.id === store.activeTicketId()">
                <a class="tab-link" [routerLink]="['/tickets', tab.id]" [attr.aria-current]="tab.id === store.activeTicketId() ? 'page' : null" [title]="tab.subject">
                  <span class="tab-num">{{ tab.displayNumber }}</span>
                  <span class="tab-subject sd-truncate">{{ tab.subject }}</span>
                </a>
                <button type="button" class="tab-close" [attr.aria-label]="'Close ' + tab.displayNumber" (click)="close(tab.id)">
                  <sd-icon name="x" [size]="12" />
                </button>
              </div>
            }
          </div>
          @if (store.tabs().length > 1) {
            <button sdButton size="sm" variant="ghost" iconOnly icon="x" type="button" class="close-all" aria-label="Close all ticket tabs" sdTooltip="Close all tabs" (click)="closeAll()"></button>
          }
        </nav>
        <div class="content">
          <div class="list-host" [class.is-hidden]="!!store.activeTicketId()" [attr.inert]="store.activeTicketId() ? '' : null">
            @if (store.selection().kind === 'trash') {
              <desk-recycle-bin />
            } @else {
              <desk-ticket-list />
            }
          </div>
          <div class="ticket-host" [class.is-hidden]="!store.activeTicketId()">
            <router-outlet />
          </div>
        </div>
      </section>

      <aside sdSplitRight class="right" aria-label="Customer context">
        <desk-context-panel [ticketId]="store.activeTicketId()" />
      </aside>
    </sd-split-layout>
  `,
  styles: `
    :host {
      display: block;
      height: 100%;
    }
    .split {
      height: 100%;
    }
    .left,
    .right {
      height: 100%;
      min-height: 0;
    }
    .main {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 0;
    }
    .strip {
      display: flex;
      align-items: stretch;
      gap: 2px;
      height: 40px;
      flex: none;
      padding: 0 40px;
      border-bottom: 1px solid var(--sd-border);
      background: var(--sd-surface);
    }
    .tabs {
      display: flex;
      align-items: stretch;
      gap: 2px;
      min-width: 0;
      overflow-x: auto;
      scrollbar-width: thin;
    }
    .tab {
      display: flex;
      align-items: center;
      gap: 2px;
      min-width: 0;
      max-width: 220px;
      flex: none;
      margin-top: 4px;
      padding: 0 4px 0 10px;
      border: 1px solid transparent;
      border-bottom: 0;
      border-radius: var(--sd-radius-md) var(--sd-radius-md) 0 0;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
      text-decoration: none;
    }
    .tab:hover {
      background: var(--sd-surface-2);
      color: var(--sd-text);
    }
    .tab.is-active {
      border-color: var(--sd-border);
      background: var(--sd-bg);
      color: var(--sd-text);
      font-weight: var(--sd-weight-medium);
      box-shadow: 0 1px 0 var(--sd-bg);
    }
    .tab--list {
      gap: 6px;
      max-width: 200px;
      padding-right: 10px;
    }
    .tab-link {
      display: flex;
      align-items: center;
      gap: 6px;
      min-width: 0;
      height: 100%;
      color: inherit;
      text-decoration: none;
    }
    .tab-num {
      flex: none;
      color: var(--sd-text-3);
      font-variant-numeric: tabular-nums;
    }
    .tab-subject {
      min-width: 0;
    }
    .tab-close {
      display: inline-flex;
      padding: 3px;
      border: 0;
      border-radius: var(--sd-radius-xs);
      background: transparent;
      color: var(--sd-text-3);
      cursor: pointer;
    }
    .tab-close:hover {
      background: var(--sd-surface-3);
      color: var(--sd-text);
    }
    .close-all {
      align-self: center;
      margin-left: auto;
    }
    .content {
      position: relative;
      flex: 1;
      min-height: 0;
    }
    .list-host,
    .ticket-host {
      position: absolute;
      inset: 0;
      display: flex;
      flex-direction: column;
      min-height: 0;
    }
    .list-host > desk-ticket-list,
    .list-host > desk-recycle-bin {
      flex: 1;
      min-height: 0;
    }
    .is-hidden {
      visibility: hidden;
      pointer-events: none;
    }
    @media (max-width: 767px) {
      .strip {
        padding: 0 40px;
      }
      .tab {
        max-width: 150px;
      }
    }
  `,
})
export class TicketsWorkspacePage {
  protected readonly store = inject(TicketWorkbenchStore);
  private readonly router = inject(Router);
  private readonly me = inject(MeStore);
  private readonly meta = inject(MetaApi);
  private readonly palette = inject(CommandPaletteService);

  protected readonly leftCollapsed = signal(typeof window !== 'undefined' && window.innerWidth < 768);
  protected readonly rightCollapsed = signal(false);
  protected readonly listStorageKey = computed(() => (this.store.selection().kind === 'trash' ? null : selectionStorageKey(this.store.selection())));
  private readonly route = inject(ActivatedRoute);

  constructor() {
    this.store.init();
    this.meta.load();

    // The URL names the list (?queue= / ?view=) and wins over the default view and the last selection.
    const params = this.route.queryParamMap.subscribe((map) => this.store.resolveSelection(this.selectionFromUrl(map)));
    inject(DestroyRef).onDestroy(() => params.unsubscribe());

    // The list tab is "active" when no ticket route is open.
    const nav = this.router.events.pipe(filter((e) => e instanceof NavigationEnd)).subscribe(() => {
      if (!/^\/tickets\/[0-9a-f-]{36}/i.test(this.router.url)) {
        this.store.activeTicketId.set(null);
        this.store.activeTicket.set(null);
      }
    });
    // Floating side panels must not cover the ticket when the window shrinks to tablet / phone width.
    const tablet = typeof matchMedia !== 'undefined' ? matchMedia('(max-width: 1100px)') : null;
    const phone = typeof matchMedia !== 'undefined' ? matchMedia('(max-width: 767px)') : null;
    const onTablet = (e: MediaQueryListEvent) => e.matches && this.rightCollapsed.set(true);
    const onPhone = (e: MediaQueryListEvent) => e.matches && this.leftCollapsed.set(true);
    tablet?.addEventListener('change', onTablet);
    phone?.addEventListener('change', onPhone);
    if (tablet?.matches) this.rightCollapsed.set(true);
    inject(DestroyRef).onDestroy(() => {
      nav.unsubscribe();
      tablet?.removeEventListener('change', onTablet);
      phone?.removeEventListener('change', onPhone);
    });

    const active = () => !!this.store.activeTicketId();
    injectShortcuts([
      { id: 'tickets.next', keys: 'j', description: 'Next ticket in the list', group: 'Tickets', handler: () => this.step(1) },
      { id: 'tickets.prev', keys: 'k', description: 'Previous ticket in the list', group: 'Tickets', handler: () => this.step(-1) },
      { id: 'tickets.reply', keys: 'r', description: 'Reply', group: 'Tickets', when: active, handler: () => this.store.commands.next({ type: 'reply' }) },
      { id: 'tickets.note', keys: 'n', description: 'Internal note', group: 'Tickets', when: active, handler: () => this.store.commands.next({ type: 'note' }) },
      { id: 'tickets.assignMe', keys: 'a', description: 'Assign to me', group: 'Tickets', when: active, handler: () => this.store.commands.next({ type: 'assignToMe' }) },
      { id: 'tickets.escalate', keys: 'e', description: 'Escalate', group: 'Tickets', when: active, handler: () => this.store.commands.next({ type: 'escalate' }) },
      { id: 'tickets.status', keys: 's', description: 'Change status', group: 'Tickets', when: active, handler: () => this.store.commands.next({ type: 'status' }) },
      { id: 'tickets.list', keys: 'u', description: 'Back to the list', group: 'Tickets', when: active, handler: () => void this.router.navigate(['/tickets']) },
      { id: 'tickets.queue', keys: 'g q', description: 'Go to the queue list', group: 'Navigation', handler: () => this.goToQueue() },
      { id: 'tickets.closeTab', keys: 'shift+x', description: 'Close the current ticket tab', group: 'Tickets', when: active, handler: () => this.close(this.store.activeTicketId()!) },
    ]);

    // Palette: switch queue / view, and actions on the open ticket.
    effect((onCleanup) => {
      const views = this.store.views();
      const commands: SdCommand[] = [
        ...STANDARD_QUEUES.map<SdCommand>((q) => ({
          id: `tickets.queue.${q.key}`,
          title: `Switch view: ${q.label}`,
          group: 'Ticket views',
          icon: q.icon,
          keywords: ['queue', 'view', 'list'],
          run: () => this.selectAndShow({ kind: 'queue', key: q.key }),
        })),
        ...views.map<SdCommand>((v) => ({
          id: `tickets.view.${v.id}`,
          title: `Switch view: ${v.name}`,
          subtitle: v.visibility === 'Shared' ? 'Shared view' : 'My view',
          group: 'Ticket views',
          icon: 'bookmark',
          keywords: ['view', 'saved'],
          run: () => this.selectAndShow({ kind: 'view', id: v.id }),
        })),
        {
          id: 'tickets.queue.trash',
          title: 'Switch view: Deleted tickets',
          group: 'Ticket views',
          icon: 'trash',
          keywords: ['recycle bin', 'trash', 'restore', 'deleted', 'spam'],
          when: () => this.me.can('tickets.delete'),
          run: () => this.selectAndShow({ kind: 'trash' }),
        },
      ];
      const off = untracked(() => this.palette.register(commands));
      onCleanup(off);
    });

    effect((onCleanup) => {
      const t = this.store.activeTicket();
      if (!t) return;
      const send = (c: TicketCommand) => () => this.store.commands.next(c);
      const statuses = allowedStatuses(this.meta.ticketForm(), t.statusId).filter((s) => s.id !== t.statusId);
      const commands: SdCommand[] = [
        { id: 'ticket.current.reply', title: `Reply to ${t.displayNumber}`, group: 'Current ticket', icon: 'reply', shortcut: 'r', when: () => t.can.reply, run: send({ type: 'reply' }) },
        { id: 'ticket.current.note', title: `Add internal note to ${t.displayNumber}`, group: 'Current ticket', icon: 'sticky-note', shortcut: 'n', when: () => t.can.note, run: send({ type: 'note' }) },
        { id: 'ticket.current.assignMe', title: 'Assign to me', group: 'Current ticket', icon: 'user-check', shortcut: 'a', when: () => t.can.assign && t.assigneeId !== this.me.user()?.id, run: send({ type: 'assignToMe' }) },
        { id: 'ticket.current.escalate', title: 'Escalate…', group: 'Current ticket', icon: 'arrow-up-right', shortcut: 'e', when: () => t.can.escalate, run: send({ type: 'escalate' }) },
        { id: 'ticket.current.status', title: 'Change status…', group: 'Current ticket', icon: 'circle-dot', shortcut: 's', when: () => t.can.edit, run: send({ type: 'status' }) },
        { id: 'ticket.current.resolve', title: 'Resolve…', group: 'Current ticket', icon: 'circle-check', when: () => t.can.resolve, run: send({ type: 'resolve' }) },
        ...statuses.map<SdCommand>((s) => ({
          id: `ticket.current.status.${s.id}`,
          title: `Set status: ${s.name}`,
          subtitle: `${t.displayNumber} · currently ${t.status}`,
          group: 'Current ticket',
          icon: 'circle-dot',
          keywords: ['status', s.category],
          when: () => t.can.edit,
          run: send({ type: 'status', statusId: s.id }),
        })),
        { id: 'ticket.current.close-tab', title: `Close tab ${t.displayNumber}`, group: 'Current ticket', icon: 'x', shortcut: 'shift+x', run: () => this.close(t.id) },
      ];
      const off = untracked(() => this.palette.register(commands));
      onCleanup(off);
    });
  }

  protected onQueueChosen(selection: QueueSelection): void {
    if (typeof window !== 'undefined' && window.innerWidth < 768) this.leftCollapsed.set(true);
    // Reflect the choice in the URL (shareable, survives a reload: the URL has the highest precedence).
    void this.router.navigate(['/tickets'], { queryParams: selectionQueryParams(selection) });
  }

  /** `?view=<id>` or `?queue=<key>` (`?queue=deleted` = recycle bin, only with tickets.delete); null otherwise. */
  private selectionFromUrl(map: ParamMap): QueueSelection | null {
    const view = map.get('view');
    if (view && /^[0-9a-f-]{36}$/i.test(view)) return { kind: 'view', id: view };
    const queue = map.get('queue');
    if (queue === TRASH_QUEUE_PARAM) return this.me.can('tickets.delete') ? { kind: 'trash' } : null;
    if (queue && (QUEUE_KEYS as readonly string[]).includes(queue)) return { kind: 'queue', key: queue as QueueKey };
    return null;
  }

  protected close(id: string): void {
    const next = this.store.closeTab(id);
    if (this.store.activeTicketId() === id) void this.router.navigate(next ? ['/tickets', next] : ['/tickets']);
  }

  protected closeAll(): void {
    this.store.closeAllTabs();
    void this.router.navigate(['/tickets']);
  }

  private selectAndShow(selection: QueueSelection): void {
    this.store.select(selection);
    void this.router.navigate(['/tickets'], { queryParams: selectionQueryParams(selection) });
  }

  private goToQueue(): void {
    this.leftCollapsed.set(false);
    void this.router.navigate(['/tickets']);
  }

  private step(delta: 1 | -1): void {
    const next = this.store.neighbour(delta);
    if (!next) return;
    void this.router.navigate(['/tickets', next]);
  }
}
