import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import {
  BulkAction,
  MeStore,
  RealtimeService,
  STORAGE_KEYS,
  SavedViewDto,
  TicketListItemDto,
  TicketQuery,
  TicketSort,
  TicketsApi,
  cursorList,
  safeStorage,
} from '@sd/core';
import {
  SdAvatar,
  SdButton,
  SdCellDef,
  SdColumn,
  SdDataTable,
  SdDialogService,
  SdDropdownMenu,
  SdIcon,
  SdMenuItem,
  SdPriorityBadge,
  SdRelativeTimePipe,
  SdSlaCountdown,
  SdSort,
  SdStatusPill,
  SdTablePrefs,
  SdTooltip,
} from '@sd/ui';
import { Subject, debounceTime, filter, merge } from 'rxjs';
import { channelIcon, channelLabel } from '../shared/ticket-ui';
import { BulkActionDialog, BulkDialogData, BulkDialogResult } from './bulk-action.dialog';
import { TicketWorkbenchStore, queueDef } from './workbench.store';

const PAGE = 50;

/** Seeds a view's saved column layout into local table prefs (first time the view is opened on this browser). */
export function seedViewColumns(view: SavedViewDto | null): void {
  if (!view?.columns || typeof view.columns !== 'object' || Array.isArray(view.columns)) return;
  const key = STORAGE_KEYS.tablePrefix + `tickets.view.${view.id}`;
  if (safeStorage.get(key) !== null) return;
  const prefs = view.columns as unknown as SdTablePrefs;
  if (Array.isArray(prefs.order) && Array.isArray(prefs.hidden)) safeStorage.setJson(key, { order: prefs.order, hidden: prefs.hidden });
}

/**
 * Centre list of the workspace: the selected queue or saved view as a virtual-scroll table with server-side
 * keyset paging, sortable and customisable columns (persisted per queue/view), quick search, row selection
 * and bulk actions (POST /tickets/bulk) with per-ticket failures. Patches itself from realtime changes.
 */
@Component({
  selector: 'desk-ticket-list',
  imports: [
    FormsModule,
    SdDataTable,
    SdCellDef,
    SdStatusPill,
    SdPriorityBadge,
    SdSlaCountdown,
    SdAvatar,
    SdIcon,
    SdButton,
    SdTooltip,
    SdDropdownMenu,
    SdRelativeTimePipe,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="toolbar">
      <div class="heading">
        <h2>{{ store.selectionLabel() }}</h2>
        @if (list.total() !== null) {
          <span class="total" aria-label="Tickets in this list">{{ list.total() }}</span>
        }
      </div>
      <span class="sd-spacer"></span>
      @if (updatesPending()) {
        <button sdButton size="sm" variant="subtle" icon="refresh" type="button" (click)="reload()">New activity</button>
      }
      <label class="search">
        <sd-icon name="search" [size]="14" />
        <span class="sd-visually-hidden">Filter this list</span>
        <input
          type="search"
          [ngModel]="query()"
          (ngModelChange)="onQuery($event)"
          placeholder="Filter by #, subject, customer…"
          (keydown.escape)="onQuery(''); $any($event.target).blur()"
        />
      </label>
      <button sdButton size="sm" variant="ghost" iconOnly icon="refresh" type="button" aria-label="Refresh list" sdTooltip="Refresh" (click)="reload()"></button>
    </div>

    @if (selection().length > 0) {
      <div class="bulk" role="toolbar" aria-label="Bulk actions">
        <strong>{{ selection().length }} selected</strong>
        <button sdButton size="sm" variant="link" type="button" (click)="selection.set([])">Clear</button>
        <span class="sd-spacer"></span>
        @if (me.can('tickets.bulk')) {
          <button sdButton size="sm" icon="user-check" type="button" (click)="bulk('assign')">Assign</button>
          <button sdButton size="sm" icon="circle-dot" type="button" (click)="bulk('status')">Status</button>
          <button sdButton size="sm" icon="flag" type="button" class="hide-sm" (click)="bulk('priority')">Priority</button>
          <sd-dropdown-menu size="sm" label="More" icon="ellipsis" [items]="moreBulk()" align="end" />
        } @else {
          <span class="sd-muted sd-small">Bulk actions need the “tickets.bulk” permission.</span>
        }
      </div>
    }

    <sd-data-table
      class="table"
      [columns]="columns"
      [rows]="list.items()"
      [storageKey]="storageKey()"
      [selectable]="me.can('tickets.bulk')"
      [(selection)]="selection"
      [sort]="sort()"
      (sortChange)="onSort($event)"
      [loading]="list.loading()"
      [loadingMore]="list.loadingMore()"
      [hasMore]="list.hasMore()"
      [total]="list.total()"
      [error]="list.error()?.userMessage ?? null"
      [activeKey]="store.activeTicketId()"
      [rowClass]="rowClass"
      [emptyTitle]="query() ? 'No tickets match your filter' : 'No tickets here'"
      [emptyMessage]="query() ? 'Try a different number, subject or customer.' : 'Nothing is waiting in this queue right now.'"
      emptyIcon="inbox"
      ariaLabel="Tickets"
      (loadMore)="list.loadMore()"
      (rowClick)="open($event)"
    >
      <ng-template sdCell="number" [sdCellRows]="list.items()" let-row>
        <span class="num sd-num">{{ row.displayNumber }}</span>
      </ng-template>
      <ng-template sdCell="subject" [sdCellRows]="list.items()" let-row>
        @if (row.hasUnreadCustomerReply) {
          <span class="unread" aria-label="Unread customer reply" sdTooltip="New customer reply"></span>
        }
        @if (row.isEscalated) {
          <sd-icon class="esc" name="arrow-up-right" [size]="14" label="Escalated" />
        }
        <span class="subject sd-truncate" [class.is-unread]="row.hasUnreadCustomerReply">{{ row.subject }}</span>
      </ng-template>
      <ng-template sdCell="status" [sdCellRows]="list.items()" let-row>
        <sd-status-pill [label]="row.status" [color]="row.statusColor" />
      </ng-template>
      <ng-template sdCell="priority" [sdCellRows]="list.items()" let-row>
        <sd-priority-badge [priority]="row.priority" />
      </ng-template>
      <ng-template sdCell="dueAt" [sdCellRows]="list.items()" let-row>
        @if (row.dueAt && row.statusCategory !== 'Resolved' && row.statusCategory !== 'Closed') {
          <sd-sla-countdown [dueAt]="row.dueAt" [state]="row.slaState" compact />
        } @else {
          <span class="sd-muted sd-small">{{ row.slaState === 'Met' ? 'SLA met' : '—' }}</span>
        }
      </ng-template>
      <ng-template sdCell="assignee" [sdCellRows]="list.items()" let-row>
        @if (row.assigneeName) {
          <sd-avatar [name]="row.assigneeName" size="xs" />
          <span class="sd-truncate">{{ row.assigneeName }}</span>
        } @else {
          <span class="sd-muted">Unassigned</span>
        }
      </ng-template>
      <ng-template sdCell="customer" [sdCellRows]="list.items()" let-row>
        <span class="stack">
          <span class="sd-truncate">{{ row.customerName ?? '—' }}</span>
          @if (row.contactName) {
            <span class="sd-truncate sd-muted sd-xs">{{ row.contactName }}</span>
          }
        </span>
      </ng-template>
      <ng-template sdCell="channel" [sdCellRows]="list.items()" let-row>
        <sd-icon [name]="channelIcon(row.channel)" [size]="14" />
        <span class="sd-truncate">{{ channelLabel(row.channel) }}</span>
      </ng-template>
      <ng-template sdCell="lastActivity" [sdCellRows]="list.items()" let-row>
        <span class="sd-small">{{ row.lastActivityAt | sdRelativeTime }}</span>
      </ng-template>
      <ng-template sdCell="created" [sdCellRows]="list.items()" let-row>
        <span class="sd-small">{{ row.createdAt | sdRelativeTime }}</span>
      </ng-template>
      <ng-template sdCell="tags" [sdCellRows]="list.items()" let-row>
        <span class="tags sd-truncate">
          @for (t of row.tags; track t) {
            <span class="tag">{{ t }}</span>
          }
        </span>
      </ng-template>
    </sd-data-table>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      min-height: 0;
      height: 100%;
      gap: 8px;
      padding: 8px 12px 12px;
    }
    .toolbar {
      display: flex;
      align-items: center;
      gap: 8px;
      min-height: 36px;
      flex-wrap: wrap;
    }
    .heading {
      display: flex;
      align-items: baseline;
      gap: 8px;
      min-width: 0;
    }
    h2 {
      font-size: var(--sd-text-lg);
      font-weight: var(--sd-weight-semibold);
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .total {
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
    .search {
      display: flex;
      align-items: center;
      gap: 6px;
      width: min(280px, 100%);
      height: 32px;
      padding: 0 8px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      color: var(--sd-text-3);
    }
    .search:focus-within {
      border-color: var(--sd-primary);
      box-shadow: var(--sd-focus-ring);
    }
    .search input {
      flex: 1;
      min-width: 0;
      border: 0;
      outline: none;
      background: transparent;
      color: var(--sd-text);
      font: inherit;
      font-size: var(--sd-text-sm);
    }
    .bulk {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: 6px;
      padding: 6px 10px;
      border: 1px solid var(--sd-primary-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-primary-soft);
    }
    .table {
      flex: 1;
      min-height: 0;
    }
    .num {
      color: var(--sd-text-2);
      font-weight: var(--sd-weight-medium);
    }
    .subject.is-unread {
      font-weight: var(--sd-weight-semibold);
    }
    .unread {
      flex: none;
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: var(--sd-primary);
    }
    .esc {
      flex: none;
      color: var(--sd-danger);
    }
    .stack {
      display: flex;
      flex-direction: column;
      min-width: 0;
      line-height: 1.25;
    }
    .tags {
      display: inline-flex;
      gap: 4px;
    }
    .tag {
      padding: 1px 6px;
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface-3);
      color: var(--sd-text-2);
      font-size: var(--sd-text-xs);
    }
    :host ::ng-deep .row--body.is-breached .cell:first-child {
      box-shadow: inset 3px 0 0 var(--sd-danger);
    }
    @media (max-width: 767px) {
      :host {
        padding: 8px;
      }
      .search {
        width: 100%;
        order: 5;
      }
      .hide-sm {
        display: none;
      }
    }
  `,
})
export class TicketListComponent {
  protected readonly store = inject(TicketWorkbenchStore);
  protected readonly me = inject(MeStore);
  private readonly tickets = inject(TicketsApi);
  private readonly router = inject(Router);
  private readonly dialogs = inject(SdDialogService);
  private readonly realtime = inject(RealtimeService);

  protected readonly channelIcon = channelIcon;
  protected readonly channelLabel = channelLabel;
  protected readonly selection = signal<string[]>([]);
  protected readonly query = signal('');
  protected readonly sort = signal<SdSort | null>(null);
  protected readonly updatesPending = signal(false);
  private readonly query$ = new Subject<string>();

  protected readonly columns: SdColumn<TicketListItemDto>[] = [
    { key: 'number', header: '#', width: 84, sortable: true, sortKey: 'number' },
    { key: 'subject', header: 'Subject', minWidth: 240, flex: 3, locked: true },
    { key: 'status', header: 'Status', minWidth: 130, flex: 1 },
    { key: 'priority', header: 'Priority', width: 110, sortable: true, sortKey: 'priority' },
    { key: 'dueAt', header: 'Due', width: 150, sortable: true, sortKey: 'due' },
    { key: 'assignee', header: 'Assignee', minWidth: 150, flex: 1 },
    { key: 'customer', header: 'Customer', minWidth: 170, flex: 1.2 },
    { key: 'lastActivity', header: 'Updated', width: 110, sortable: true, sortKey: 'lastActivity' },
    { key: 'channel', header: 'Channel', width: 120, hiddenByDefault: true },
    { key: 'departmentName', header: 'Department', minWidth: 140, hiddenByDefault: true },
    { key: 'teamName', header: 'Team', minWidth: 120, hiddenByDefault: true },
    { key: 'productName', header: 'Product', minWidth: 140, hiddenByDefault: true },
    { key: 'categoryName', header: 'Category', minWidth: 140, hiddenByDefault: true },
    { key: 'severity', header: 'Severity', width: 100, hiddenByDefault: true },
    { key: 'created', header: 'Created', width: 110, sortable: true, sortKey: 'created', hiddenByDefault: true },
    { key: 'tags', header: 'Tags', minWidth: 140, hiddenByDefault: true },
    {
      key: 'replies',
      header: 'Replies',
      width: 90,
      align: 'end',
      hiddenByDefault: true,
      value: (r) => `${r.agentReplyCount} / ${r.customerReplyCount}`,
    },
  ];

  protected readonly rowClass = (row: TicketListItemDto): string | null =>
    row.slaState === 'Breached' && row.statusCategory !== 'Resolved' && row.statusCategory !== 'Closed' ? 'is-breached' : null;

  protected readonly storageKey = computed(() => {
    const s = this.store.selection();
    return s.kind === 'queue' ? `tickets.queue.${s.key}` : `tickets.view.${s.id}`;
  });

  private readonly baseQuery = computed<TicketQuery>(() => {
    const s = this.store.selection();
    return s.kind === 'queue' ? queueDef(s.key).query : { viewId: s.id };
  });

  protected readonly list = cursorList<TicketListItemDto>(() => this.tickets.list({}, { silent: true }));

  protected readonly moreBulk = computed<SdMenuItem[]>(() => [
    { id: 'priority', label: 'Change priority', icon: 'flag', run: () => this.bulk('priority') },
    { id: 'tags_add', label: 'Add tags', icon: 'tag', run: () => this.bulk('tags_add') },
    { id: 'tags_remove', label: 'Remove tags', icon: 'tag', run: () => this.bulk('tags_remove') },
    { id: 'transfer', label: 'Transfer…', icon: 'share', run: () => this.bulk('transfer') },
    { id: 'watch', label: 'Watch', icon: 'eye', run: () => this.bulk('watch') },
    { id: 'd1', divider: true },
    { id: 'close', label: 'Close', icon: 'circle-check', run: () => this.bulk('close') },
    { id: 'spam', label: 'Mark as spam', icon: 'shield-off', danger: true, run: () => this.bulk('spam') },
  ]);

  constructor() {
    // Rebuild the fetcher whenever the queue/view, quick filter or sort changes.
    effect(() => {
      const base = this.baseQuery();
      const q = this.query().trim();
      const sort = this.sort();
      untracked(() => {
        const apiSort = (sort?.key as TicketSort | undefined) ?? null;
        const dir = apiSort === 'due' ? 'asc' : apiSort === 'priority' ? 'desc' : (sort?.dir ?? 'desc');
        this.selection.set([]);
        this.updatesPending.set(false);
        this.list.setFetcher((cursor) =>
          this.tickets.list(
            { ...base, q: q || null, sort: apiSort ?? 'lastActivity', dir, cursor, limit: PAGE, includeTotal: cursor === null },
            { silent: true },
          ),
        );
      });
    });
    // A view's saved sort applies when the view is selected.
    effect(() => {
      const view = this.store.selectedView();
      const sel = this.store.selection();
      untracked(() => {
        if (sel.kind === 'view') {
          seedViewColumns(view);
          const vs = view?.sort as { key?: string; dir?: string } | null | undefined;
          this.sort.set(vs?.key ? { key: vs.key, dir: vs.dir === 'asc' ? 'asc' : 'desc' } : null);
        } else {
          this.sort.set(null);
        }
      });
    });
    effect(() => {
      const ids = this.list.items().map((t) => t.id);
      untracked(() => this.store.listIds.set(ids));
    });

    const sub = this.query$.pipe(debounceTime(300)).subscribe((q) => this.query.set(q));
    // Live updates: refresh silently while only the first page is loaded and nothing is selected; otherwise offer a refresh.
    const live = merge(this.realtime.ticketChanged$, this.realtime.ticketThread$)
      .pipe(
        filter(() => this.list.loaded()),
        debounceTime(2500),
      )
      .subscribe(() => {
        if (this.selection().length === 0 && this.list.items().length <= PAGE) this.list.load();
        else this.updatesPending.set(true);
      });
    const reload = merge(this.store.listReload, this.realtime.reconnected$).subscribe(() => this.reload());
    inject(DestroyRef).onDestroy(() => {
      sub.unsubscribe();
      live.unsubscribe();
      reload.unsubscribe();
    });
  }

  protected onQuery(value: string): void {
    this.query$.next(value);
  }

  protected onSort(sort: SdSort | null): void {
    if (sort?.key === 'due') sort = { key: 'due', dir: 'asc' };
    if (sort?.key === 'priority') sort = { key: 'priority', dir: 'desc' };
    this.sort.set(sort);
  }

  reload(): void {
    this.updatesPending.set(false);
    this.list.load();
    this.store.refreshCounts();
  }

  protected open(row: TicketListItemDto): void {
    this.store.openTab({ id: row.id, displayNumber: row.displayNumber, subject: row.subject });
    void this.router.navigate(['/tickets', row.id]);
  }

  protected bulk(action: BulkAction): void {
    const ids = this.selection();
    if (ids.length === 0) return;
    const labels: Record<string, string> = {};
    for (const t of this.list.items()) if (ids.includes(t.id)) labels[t.id] = `${t.displayNumber} · ${t.subject}`;
    const ref = this.dialogs.open<BulkDialogResult, BulkDialogData>(BulkActionDialog, { data: { action, ticketIds: ids, labels }, size: 'sm' });
    ref.closed.subscribe((outcome) => {
      if (!outcome) return;
      const failed = new Set(outcome.result.failures.map((f) => f.id));
      this.selection.set(ids.filter((id) => failed.has(id)));
      this.reload();
    });
  }
}
