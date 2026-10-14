import { CdkDrag, CdkDragDrop, CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import {
  ApiError,
  EngineeringApi,
  EngineeringBoardDto,
  EngineeringWorkItemDto,
  Guid,
  LOCAL_WORK_ITEM_STATES,
  LocalWorkItemState,
  MeStore,
} from '@sd/core';
import { SdBadge, SdButton, SdDropdownMenu, SdEmptyState, SdIcon, SdInput, SdMenuItem, SdPageHeader, SdSkeleton, SdToggle, ToastService } from '@sd/ui';
import { ReleasesDrawerComponent } from './releases-drawer.component';
import { WorkItemDrawerComponent } from './work-item-drawer.component';

const STATE_HINT: Record<LocalWorkItemState, string> = {
  New: 'Escalated, not started',
  Active: 'Being worked on',
  Resolved: 'Fixed, waiting for a release',
  Closed: 'Released or done',
};

/**
 * Built-in engineering board (`/engineering`): kanban of the local tracker's work items (New → Active → Resolved →
 * Closed) with drag and drop plus a keyboard "Move to" menu, filters, keyset paging ("Load more"), the work item
 * drawer (fields, release, linked tickets) and releases (publish → linked tickets are notified).
 */
@Component({
  selector: 'desk-engineering-board-page',
  imports: [
    FormsModule,
    RouterLink,
    CdkDropListGroup,
    CdkDropList,
    CdkDrag,
    SdPageHeader,
    SdButton,
    SdBadge,
    SdIcon,
    SdInput,
    SdToggle,
    SdEmptyState,
    SdSkeleton,
    SdDropdownMenu,
    WorkItemDrawerComponent,
    ReleasesDrawerComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Engineering board" icon="bug" [subtitle]="board()?.trackerName ?? 'Built-in engineering tracker'">
        @if (board()?.trackerId) {
          <ng-container sdPageActions>
            <button sdButton icon="rocket" (click)="releasesOpen.set(true)">Releases</button>
            @if (canManage()) {
              <button sdButton variant="primary" icon="plus" (click)="openNew()">New work item</button>
            }
          </ng-container>
        }
      </sd-page-header>
      <div class="desk-page__body">
        @if (loadingBoard()) {
          <sd-skeleton [lines]="6" height="40px" />
        } @else if (boardError()) {
          <div class="error" role="alert">
            <sd-icon name="circle-alert" [size]="16" />
            <span>{{ boardError() }}</span>
            <button sdButton size="sm" icon="refresh" (click)="reload()">Retry</button>
          </div>
        } @else if (!board()?.trackerId) {
          <sd-empty-state icon="bug" title="The built-in tracker is not set up" message="Tenants without Azure DevOps or GitHub can track engineering work here. An administrator enables it under Administration → Engineering.">
            @if (isAdmin()) {
              <a sdButton variant="primary" icon="settings" routerLink="/admin/engineering">Set up engineering</a>
            }
          </sd-empty-state>
        } @else {
          <div class="toolbar" role="search">
            <input sdInput class="search" type="search" placeholder="Search title, ENG-12 or version" aria-label="Search work items" [ngModel]="q()" (ngModelChange)="q.set($event)" (keyup.enter)="reload(false)" />
            <select sdInput aria-label="Assignee" [ngModel]="assigneeId()" (ngModelChange)="assigneeId.set($event); reload(false)">
              <option [ngValue]="null">All engineers</option>
              @for (a of board()!.assignees; track a.id) {
                <option [ngValue]="a.id">{{ a.name }}</option>
              }
            </select>
            <select sdInput aria-label="Release" [ngModel]="releaseId()" (ngModelChange)="releaseId.set($event); reload(false)">
              <option [ngValue]="null">All releases</option>
              @for (r of board()!.releases; track r.id) {
                <option [ngValue]="r.id">{{ r.name }}{{ r.releasedAt ? ' (released)' : '' }}</option>
              }
            </select>
            <sd-toggle [value]="includeClosed()" (valueChange)="includeClosed.set($event); reload(false)">Show closed</sd-toggle>
            <span class="sd-spacer"></span>
            <span class="sd-muted small" aria-live="polite">{{ total() }} item{{ total() === 1 ? '' : 's' }}</span>
            <button sdButton size="sm" variant="ghost" icon="refresh" iconOnly aria-label="Reload the board" (click)="reload(false)"></button>
          </div>

          @if (loadingItems() && items().length === 0) {
            <sd-skeleton [lines]="6" height="56px" />
          } @else if (itemsError()) {
            <div class="error" role="alert">
              <sd-icon name="circle-alert" [size]="16" />
              <span>{{ itemsError() }}</span>
              <button sdButton size="sm" icon="refresh" (click)="reload(false)">Retry</button>
            </div>
          } @else if (items().length === 0) {
            <sd-empty-state icon="kanban" title="No work items" [message]="q() || assigneeId() || releaseId() ? 'Nothing matches the filters.' : 'Escalate a ticket to engineering or create a work item to start.'" />
          } @else {
            <div class="board" cdkDropListGroup>
              @for (col of columns(); track col.state) {
                <section class="column" [attr.aria-label]="col.state + ' (' + col.items.length + ')'">
                  <header class="col-head">
                    <span class="dot" [attr.data-state]="col.state" aria-hidden="true"></span>
                    <h2>{{ col.state }}</h2>
                    <sd-badge size="sm" variant="outline">{{ col.items.length }}</sd-badge>
                    <span class="sd-muted small hint">{{ hint(col.state) }}</span>
                  </header>
                  <ul class="cards" cdkDropList [cdkDropListData]="col.state" [cdkDropListDisabled]="!canManage()" (cdkDropListDropped)="drop($event)">
                    @for (w of col.items; track w.id) {
                      <li class="card" cdkDrag [cdkDragData]="w" [cdkDragDisabled]="!canManage() || moving() === w.id" [class.moving]="moving() === w.id">
                        <div class="card-top">
                          <sd-icon [name]="w.type === 'Feature' ? 'lightbulb' : w.type === 'Task' ? 'list-checks' : 'bug'" [size]="14" />
                          <span class="key">{{ w.key }}</span>
                          <span class="sd-spacer"></span>
                          @if (canManage()) {
                            <sd-dropdown-menu iconOnly icon="ellipsis" variant="ghost" size="sm" align="end" [ariaLabel]="'Move ' + w.key" [items]="moveMenu(w)" (select)="onMove(w, $event)" />
                          }
                        </div>
                        <button type="button" class="card-title" (click)="open(w.id)">{{ w.title }}</button>
                        <div class="card-meta">
                          <sd-badge size="sm" [tone]="priorityTone(w.priority)">{{ w.priority }}</sd-badge>
                          @if (w.severity) {
                            <sd-badge size="sm" variant="outline">{{ w.severity }}</sd-badge>
                          }
                          @if (w.fixVersion) {
                            <sd-badge size="sm" [tone]="w.isReleased ? 'success' : 'neutral'" [icon]="w.isReleased ? 'rocket' : null">{{ w.fixVersion }}</sd-badge>
                          }
                          @if (w.linkedTicketCount > 0) {
                            <sd-badge size="sm" tone="info" icon="ticket">{{ w.linkedTicketCount }}</sd-badge>
                          }
                        </div>
                        <div class="assignee sd-muted small">
                          <sd-icon name="user" [size]="12" />
                          {{ w.assigneeName ?? 'Unassigned' }}
                        </div>
                      </li>
                    }
                  </ul>
                </section>
              }
            </div>
            @if (nextCursor()) {
              <div class="more">
                <button sdButton icon="chevron-down" [loading]="loadingItems()" (click)="loadMore()">Load more</button>
              </div>
            }
          }
        }
      </div>
    </div>

    @if (board(); as b) {
      <desk-work-item-drawer
        [(open)]="drawerOpen"
        [workItemId]="selectedId()"
        [board]="b"
        [canManage]="canManage()"
        (saved)="upsert($event)"
        (deleted)="remove($event)"
      />
      <desk-releases-drawer [(open)]="releasesOpen" [board]="b" [canManage]="canManage()" (changed)="reload()" />
    }
  `,
  styles: `
    .small {
      font-size: var(--sd-text-xs);
    }
    .error {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 12px;
      border: 1px solid var(--sd-danger-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-danger-bg);
      color: var(--sd-danger);
    }
    .toolbar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      margin-bottom: 12px;
    }
    .toolbar .search {
      width: 260px;
      max-width: 100%;
    }
    .toolbar select {
      width: 170px;
    }
    .board {
      display: grid;
      grid-template-columns: repeat(4, minmax(0, 1fr));
      gap: 12px;
      align-items: start;
    }
    @media (max-width: 1100px) {
      .board {
        grid-template-columns: repeat(2, minmax(0, 1fr));
      }
    }
    @media (max-width: 640px) {
      .board {
        grid-template-columns: 1fr;
      }
      .toolbar select,
      .toolbar .search {
        width: 100%;
      }
    }
    .column {
      display: flex;
      flex-direction: column;
      min-width: 0;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface-sunken);
    }
    .col-head {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
      padding: 10px 10px 6px;
    }
    .col-head h2 {
      margin: 0;
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-semibold);
    }
    .hint {
      flex-basis: 100%;
    }
    .dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: var(--sd-text-3);
    }
    .dot[data-state='Active'] {
      background: var(--sd-warning);
    }
    .dot[data-state='Resolved'] {
      background: var(--sd-info, var(--sd-primary));
    }
    .dot[data-state='Closed'] {
      background: var(--sd-success);
    }
    .cards {
      display: flex;
      flex-direction: column;
      gap: 8px;
      min-height: 60px;
      margin: 0;
      padding: 6px 8px 10px;
      list-style: none;
    }
    .card {
      display: flex;
      flex-direction: column;
      gap: 6px;
      padding: 8px 10px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-sm);
      background: var(--sd-surface);
      box-shadow: var(--sd-shadow-xs, none);
      cursor: grab;
    }
    .card.moving {
      opacity: 0.6;
    }
    .card-top {
      display: flex;
      align-items: center;
      gap: 6px;
      color: var(--sd-text-3);
    }
    .key {
      font-family: var(--sd-font-mono);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      color: var(--sd-text-2);
    }
    .card-title {
      padding: 0;
      border: 0;
      background: transparent;
      color: var(--sd-text);
      font: inherit;
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-medium);
      text-align: left;
      cursor: pointer;
      overflow-wrap: anywhere;
    }
    .card-title:hover {
      color: var(--sd-primary);
    }
    .card-title:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
      border-radius: var(--sd-radius-xs);
    }
    .card-meta {
      display: flex;
      flex-wrap: wrap;
      gap: 4px;
    }
    .assignee {
      display: flex;
      align-items: center;
      gap: 4px;
    }
    .cdk-drag-preview {
      box-shadow: var(--sd-shadow-lg, 0 8px 24px rgb(0 0 0 / 0.2));
    }
    .cdk-drag-placeholder {
      opacity: 0.35;
    }
    .more {
      display: flex;
      justify-content: center;
      margin-top: 12px;
    }
  `,
})
export class EngineeringBoardPage implements OnInit {
  /** ?item=12 (from a ticket's Engineering panel) opens that work item. */
  readonly item = input<string | undefined>();

  private readonly api = inject(EngineeringApi);
  private readonly toast = inject(ToastService);
  private readonly me = inject(MeStore);

  protected readonly board = signal<EngineeringBoardDto | null>(null);
  protected readonly loadingBoard = signal(true);
  protected readonly boardError = signal<string | null>(null);
  protected readonly items = signal<EngineeringWorkItemDto[]>([]);
  protected readonly loadingItems = signal(false);
  protected readonly itemsError = signal<string | null>(null);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly total = signal(0);

  protected readonly q = signal('');
  protected readonly assigneeId = signal<Guid | null>(null);
  protected readonly releaseId = signal<Guid | null>(null);
  protected readonly includeClosed = signal(false);
  protected readonly moving = signal<Guid | null>(null);

  protected readonly drawerOpen = signal(false);
  protected readonly selectedId = signal<Guid | null>(null);
  protected readonly releasesOpen = signal(false);

  protected readonly canManage = computed(() => !!this.board()?.canManage);
  protected readonly isAdmin = computed(() => this.me.can('admin.integrations'));
  protected readonly columns = computed(() =>
    LOCAL_WORK_ITEM_STATES.filter((s) => s !== 'Closed' || this.includeClosed()).map((state) => ({
      state,
      items: this.items()
        .filter((w) => w.state === state)
        .sort((a, b) => a.rank - b.rank),
    })),
  );

  private pendingItemNumber: number | null = null;

  constructor() {
    effect(() => {
      const n = Number(this.item());
      if (!Number.isInteger(n) || n <= 0) return;
      untracked(() => {
        this.pendingItemNumber = n;
        this.openPending();
      });
    });
  }

  ngOnInit(): void {
    this.reload();
  }

  protected reload(withBoard = true): void {
    if (withBoard) {
      this.loadingBoard.set(true);
      this.boardError.set(null);
      this.api.board().subscribe({
        next: (b) => {
          this.board.set(b);
          this.loadingBoard.set(false);
          if (b.trackerId) this.loadItems(null);
        },
        error: (err: unknown) => {
          this.loadingBoard.set(false);
          this.boardError.set(err instanceof ApiError ? err.userMessage : 'The board could not be loaded.');
        },
      });
    } else if (this.board()?.trackerId) {
      this.loadItems(null);
    }
  }

  private loadItems(cursor: string | null): void {
    this.loadingItems.set(true);
    this.itemsError.set(null);
    this.api
      .workItems({
        trackerId: this.board()?.trackerId,
        q: this.q().trim() || null,
        assigneeId: this.assigneeId(),
        releaseId: this.releaseId(),
        includeClosed: this.includeClosed(),
        cursor,
        limit: 150,
      })
      .subscribe({
        next: (page) => {
          this.items.update((list) => (cursor ? [...list, ...page.items.filter((i) => !list.some((x) => x.id === i.id))] : page.items));
          this.nextCursor.set(page.nextCursor);
          this.total.set(page.total ?? this.items().length);
          this.loadingItems.set(false);
          this.openPending();
        },
        error: (err: unknown) => {
          this.loadingItems.set(false);
          this.itemsError.set(err instanceof ApiError ? err.userMessage : 'Work items could not be loaded.');
        },
      });
  }

  protected loadMore(): void {
    const cursor = this.nextCursor();
    if (cursor) this.loadItems(cursor);
  }

  private openPending(): void {
    const n = this.pendingItemNumber;
    if (n === null || !this.board()) return;
    const found = this.items().find((w) => w.number === n);
    if (found) {
      this.pendingItemNumber = null;
      this.open(found.id);
    } else if (!this.loadingItems() && this.board()?.trackerId) {
      // Not on the first page (or closed): look it up directly.
      this.pendingItemNumber = null;
      this.api.workItems({ trackerId: this.board()?.trackerId, q: `ENG-${n}`, includeClosed: true, limit: 1 }).subscribe({
        next: (page) => {
          if (page.items[0]) this.open(page.items[0].id);
          else this.toast.warning(`ENG-${n} was not found`, 'It may have been deleted.');
        },
        error: () => undefined,
      });
    }
  }

  protected hint(state: LocalWorkItemState): string {
    return STATE_HINT[state];
  }

  protected priorityTone(p: string): 'danger' | 'warning' | 'info' | 'neutral' {
    return p === 'Critical' ? 'danger' : p === 'High' ? 'warning' : p === 'Medium' ? 'info' : 'neutral';
  }

  protected open(id: Guid): void {
    this.selectedId.set(id);
    this.drawerOpen.set(true);
  }

  protected openNew(): void {
    this.selectedId.set(null);
    this.drawerOpen.set(true);
  }

  protected moveMenu(w: EngineeringWorkItemDto): SdMenuItem[] {
    return [
      { id: 'heading', label: 'Move to', heading: true },
      ...LOCAL_WORK_ITEM_STATES.filter((s) => s !== w.state).map((s) => ({ id: s, label: s, icon: 'arrow-right' })),
      { id: 'divider', divider: true },
      { id: 'open', label: 'Open details', icon: 'pencil' },
    ];
  }

  protected onMove(w: EngineeringWorkItemDto, item: SdMenuItem): void {
    if (item.id === 'open') this.open(w.id);
    else if ((LOCAL_WORK_ITEM_STATES as readonly string[]).includes(item.id)) this.move(w, item.id as LocalWorkItemState, null);
  }

  protected drop(event: CdkDragDrop<LocalWorkItemState, LocalWorkItemState, EngineeringWorkItemDto>): void {
    const w = event.item.data;
    const target = event.container.data;
    const column = this.items()
      .filter((x) => x.state === target && x.id !== w.id)
      .sort((a, b) => a.rank - b.rank);
    const before = column[event.currentIndex]?.id ?? null;
    if (target === w.state && event.previousIndex === event.currentIndex) return;
    this.move(w, target, before);
  }

  private move(w: EngineeringWorkItemDto, state: LocalWorkItemState, beforeId: Guid | null): void {
    const previous = this.items();
    // Optimistic: place the card at its new position immediately.
    const column = previous.filter((x) => x.state === state && x.id !== w.id).sort((a, b) => a.rank - b.rank);
    const index = beforeId ? column.findIndex((x) => x.id === beforeId) : -1;
    const rank =
      index < 0 ? (column.length ? column[column.length - 1].rank + 1 : 1) : index === 0 ? column[0].rank - 1 : (column[index - 1].rank + column[index].rank) / 2;
    this.items.set(previous.map((x) => (x.id === w.id ? { ...x, state, rank } : x)));
    this.moving.set(w.id);
    this.api.moveWorkItem(w.id, { state, beforeId, rowVersion: w.rowVersion }).subscribe({
      next: (updated) => {
        this.moving.set(null);
        this.upsert(updated);
        if (state !== w.state && updated.linkedTicketCount > 0)
          this.toast.info(`${w.key} → ${state}`, `${updated.linkedTicketCount} linked ticket${updated.linkedTicketCount === 1 ? '' : 's'} updated.`);
      },
      error: (err: unknown) => {
        this.moving.set(null);
        this.items.set(previous);
        this.toast.error(`Could not move ${w.key}`, err instanceof ApiError ? err.userMessage : undefined);
        if (err instanceof ApiError && err.is('concurrency_conflict')) this.reload(false);
      },
    });
  }

  protected upsert(w: EngineeringWorkItemDto): void {
    const isNew = !this.items().some((x) => x.id === w.id);
    const card = { ...w, descriptionHtml: null };
    this.items.update((list) => (isNew ? [card, ...list] : list.map((x) => (x.id === w.id ? card : x))));
    if (isNew) this.total.update((t) => t + 1);
  }

  protected remove(id: Guid): void {
    this.items.update((list) => list.filter((x) => x.id !== id));
    this.total.update((t) => Math.max(0, t - 1));
  }
}
