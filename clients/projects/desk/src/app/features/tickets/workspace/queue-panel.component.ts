import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';
import { MeStore, SavedViewDto, ViewsApi } from '@sd/core';
import { SdButton, SdDialogService, SdDropdownMenu, SdIcon, SdMenuItem, SdSkeleton, SdTooltip, ToastService } from '@sd/ui';
import { toastApiError } from '../shared/ticket-ui';
import { seedViewColumns } from './ticket-list.component';
import { ViewEditorData, ViewEditorDialog } from './view-editor.dialog';
import { QueueSelection, STANDARD_QUEUES, TicketWorkbenchStore } from './workbench.store';

/** Left panel: standard queues and saved views (personal + shared) with live counts; create/edit/delete views. */
@Component({
  selector: 'desk-queue-panel',
  imports: [NgTemplateOutlet, SdButton, SdIcon, SdSkeleton, SdDropdownMenu, SdTooltip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav class="panel" aria-label="Ticket queues and views">
      <div class="section-head">
        <h2>Queues</h2>
      </div>
      <ul class="list" role="list">
        @for (q of queues; track q.key) {
          <li>
            <button
              type="button"
              class="item"
              [class.is-active]="isQueue(q.key)"
              [attr.aria-current]="isQueue(q.key) ? 'page' : null"
              (click)="choose({ kind: 'queue', key: q.key })"
            >
              <sd-icon [name]="q.icon" [size]="15" />
              <span class="label sd-truncate">{{ q.label }}</span>
              @if (store.countsLoaded()) {
                <span class="count" [class.is-alert]="(q.key === 'overdue' || q.key === 'atRisk') && (store.counts()[q.key] ?? 0) > 0">
                  {{ store.counts()[q.key] ?? 0 }}
                </span>
              }
            </button>
          </li>
        }
        @if (canSeeTrash()) {
          <li>
            <button
              type="button"
              class="item"
              [class.is-active]="isTrash()"
              [attr.aria-current]="isTrash() ? 'page' : null"
              (click)="choose({ kind: 'trash' })"
            >
              <sd-icon name="trash" [size]="15" />
              <span class="label sd-truncate">Deleted tickets</span>
            </button>
          </li>
        }
      </ul>

      <div class="section-head">
        <h2>Views</h2>
        <button sdButton size="sm" variant="ghost" icon="plus" iconOnly type="button" aria-label="New view" sdTooltip="New view" (click)="create()"></button>
      </div>
      @if (store.viewsLoading() && store.views().length === 0) {
        <div class="pad"><sd-skeleton [lines]="3" /></div>
      } @else if (store.viewsError()) {
        <p class="pad error" role="alert">
          {{ store.viewsError() }}
          <button sdButton variant="link" size="sm" type="button" (click)="store.loadViews()">Retry</button>
        </p>
      } @else {
        @if (store.myViews().length) {
          <h3 class="group">Mine</h3>
          <ul class="list" role="list">
            @for (v of store.myViews(); track v.id) {
              <li class="view-row">
                <ng-container [ngTemplateOutlet]="viewItem" [ngTemplateOutletContext]="{ $implicit: v }" />
              </li>
            }
          </ul>
        }
        @if (store.sharedViews().length) {
          <h3 class="group">Shared</h3>
          <ul class="list" role="list">
            @for (v of store.sharedViews(); track v.id) {
              <li class="view-row">
                <ng-container [ngTemplateOutlet]="viewItem" [ngTemplateOutletContext]="{ $implicit: v }" />
              </li>
            }
          </ul>
        }
        @if (store.views().length === 0) {
          <p class="pad sd-muted sd-small">No saved views yet. Create one to keep a filtered list one click away.</p>
        }
      }
    </nav>

    <ng-template #viewItem let-v>
      <button
        type="button"
        class="item"
        [class.is-active]="isView(v.id)"
        [attr.aria-current]="isView(v.id) ? 'page' : null"
        (click)="choose({ kind: 'view', id: v.id })"
      >
        <sd-icon [name]="v.visibility === 'Shared' ? 'users' : 'bookmark'" [size]="15" />
        <span class="label sd-truncate">{{ v.name }}</span>
        @if (v.id === store.defaultView()?.id) {
          <sd-icon class="default" name="star" [size]="12" label="Opens by default" />
        }
        @if (v.count !== null) {
          <span class="count">{{ v.count }}</span>
        }
      </button>
      <sd-dropdown-menu
        class="view-menu"
        size="sm"
        variant="ghost"
        icon="ellipsis"
        iconOnly
        [caret]="false"
        align="end"
        [ariaLabel]="'Actions for view ' + v.name"
        [items]="viewMenu(v)"
      />
    </ng-template>
  `,
  styles: `
    :host {
      display: block;
      height: 100%;
      overflow-y: auto;
    }
    .panel {
      padding: 10px 8px 16px;
    }
    .section-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 8px 8px 4px;
      min-height: 32px;
    }
    h2 {
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      color: var(--sd-text-3);
      text-transform: uppercase;
      letter-spacing: 0.05em;
    }
    .group {
      margin: 6px 8px 2px;
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-medium);
      color: var(--sd-text-3);
    }
    .list {
      display: grid;
      gap: 1px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .item {
      display: flex;
      align-items: center;
      gap: 9px;
      width: 100%;
      min-height: 34px;
      padding: 0 10px;
      border: 0;
      border-radius: var(--sd-radius-md);
      background: transparent;
      color: var(--sd-text-2);
      font: inherit;
      font-size: var(--sd-text-sm);
      text-align: left;
      cursor: pointer;
    }
    .item:hover {
      background: var(--sd-surface-3);
      color: var(--sd-text);
    }
    .item.is-active {
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
      font-weight: var(--sd-weight-medium);
    }
    .label {
      flex: 1;
      min-width: 0;
    }
    .count {
      min-width: 22px;
      padding: 0 6px;
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface-3);
      color: var(--sd-text-2);
      font-size: var(--sd-text-xs);
      font-variant-numeric: tabular-nums;
      text-align: center;
    }
    .item.is-active .count {
      background: var(--sd-surface);
    }
    .default {
      flex: none;
      color: var(--sd-warning);
    }
    .count.is-alert {
      background: var(--sd-danger-bg);
      color: var(--sd-danger);
    }
    .view-row {
      position: relative;
      display: flex;
      align-items: center;
    }
    .view-row .item {
      padding-right: 34px;
    }
    .view-menu {
      position: absolute;
      right: 2px;
      opacity: 0;
    }
    .view-row:hover .view-menu,
    .view-row:focus-within .view-menu {
      opacity: 1;
    }
    @media (hover: none) {
      .view-menu {
        opacity: 1;
      }
    }
    .pad {
      padding: 4px 10px;
    }
    .error {
      color: var(--sd-danger);
      font-size: var(--sd-text-sm);
    }
  `,
})
export class QueuePanelComponent {
  /** Table-prefs key of the list currently shown (for "save columns with view"). */
  readonly columnsStorageKey = input<string | null>(null);
  /** Emitted after a queue/view is chosen (the workspace returns to the list, closes the phone drawer). */
  readonly chosen = output<QueueSelection>();

  protected readonly store = inject(TicketWorkbenchStore);
  private readonly me = inject(MeStore);
  private readonly views = inject(ViewsApi);
  private readonly dialogs = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  protected readonly queues = STANDARD_QUEUES;

  protected isQueue(key: string): boolean {
    const s = this.store.selection();
    return s.kind === 'queue' && s.key === key;
  }

  protected isView(id: string): boolean {
    const s = this.store.selection();
    return s.kind === 'view' && s.id === id;
  }

  /** The recycle bin (deleted + spam tickets) is offered to agents who may delete and restore tickets. */
  protected canSeeTrash(): boolean {
    return this.me.can('tickets.delete');
  }

  protected isTrash(): boolean {
    return this.store.selection().kind === 'trash';
  }

  protected choose(selection: QueueSelection): void {
    if (selection.kind === 'view') seedViewColumns(this.store.views().find((v) => v.id === selection.id) ?? null);
    this.store.select(selection);
    this.chosen.emit(selection);
  }

  protected viewMenu(v: SavedViewDto): SdMenuItem[] {
    const canEdit = v.isMine || this.me.can('supervisor.monitor');
    return [
      { id: 'edit', label: 'Edit…', icon: 'pencil', disabled: !canEdit, run: () => this.edit(v) },
      { id: 'dup', label: 'Duplicate…', icon: 'copy', run: () => this.edit(v, true) },
      { id: 'd', divider: true },
      { id: 'delete', label: 'Delete', icon: 'trash', danger: true, disabled: !canEdit, run: () => void this.remove(v) },
    ];
  }

  create(): void {
    this.open({ columnsStorageKey: this.columnsStorageKey() });
  }

  private edit(view: SavedViewDto, duplicate = false): void {
    const key = this.isView(view.id) ? this.columnsStorageKey() : null;
    this.open({ view, duplicate, columnsStorageKey: key });
  }

  private open(data: ViewEditorData): void {
    const ref = this.dialogs.open<SavedViewDto, ViewEditorData>(ViewEditorDialog, { data, size: 'lg', disableClose: true });
    ref.closed.subscribe((view) => {
      if (!view) return;
      this.store.upsertView(view);
      this.store.select({ kind: 'view', id: view.id });
      this.store.listReload.next();
      this.store.loadViews(true);
      this.chosen.emit({ kind: 'view', id: view.id });
      this.toast.success(data.view && !data.duplicate ? 'View saved' : 'View created', view.name);
    });
  }

  private async remove(view: SavedViewDto): Promise<void> {
    const ok = await this.dialogs.confirm({
      title: `Delete “${view.name}”?`,
      message: view.visibility === 'Shared' ? 'This shared view disappears for everyone.' : 'This only removes the view, not its tickets.',
      confirmLabel: 'Delete view',
      danger: true,
    });
    if (!ok) return;
    this.views.delete(view.id).subscribe({
      next: () => {
        this.store.removeView(view.id);
        this.toast.success('View deleted', view.name);
      },
      error: (err: unknown) => toastApiError(this.toast, err, 'Could not delete the view'),
    });
  }
}
