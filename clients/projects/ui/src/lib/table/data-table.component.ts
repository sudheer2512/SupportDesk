import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDropList, moveItemInArray } from '@angular/cdk/drag-drop';
import { CdkConnectedOverlay, CdkOverlayOrigin, ConnectedPosition } from '@angular/cdk/overlay';
import { CdkFixedSizeVirtualScroll, CdkVirtualForOf, CdkVirtualScrollViewport } from '@angular/cdk/scrolling';
import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  TemplateRef,

  booleanAttribute,
  computed,
  contentChildren,
  effect,
  inject,
  input,
  model,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { STORAGE_KEYS, safeStorage, uniqueId } from '@sd/core';
import { SdButton } from '../button/button.component';
import { SdEmptyState } from '../empty-state/empty-state.component';
import { SdCheckbox } from '../forms/checkbox.component';
import { SdIcon } from '../icon/icon.component';
import { SdSkeleton } from '../skeleton/skeleton.component';
import { SdSpinner } from '../spinner/spinner.component';
import { SdCellContext, SdCellDef, SdColumn, SdSort, SdTablePrefs } from './data-table.types';

const SELECT_COL_PX = 44;
const CHOOSER_COL_PX = 44;
const CHOOSER_POSITIONS: ConnectedPosition[] = [
  { originX: 'end', originY: 'bottom', overlayX: 'end', overlayY: 'top', offsetY: 4 },
  { originX: 'end', originY: 'top', overlayX: 'end', overlayY: 'bottom', offsetY: -4 },
];

/**
 * High-volume data grid.
 * ```html
 * <sd-data-table [columns]="cols" [rows]="list.items()" storageKey="tickets.list" selectable
 *   [(selection)]="selected" [(sort)]="sort" [loading]="list.loading()" [loadingMore]="list.loadingMore()"
 *   [hasMore]="list.hasMore()" (loadMore)="list.loadMore()" (rowClick)="open($event)">
 *   <ng-template sdCell="priority" let-row><sd-priority-badge [priority]="row.priority" /></ng-template>
 * </sd-data-table>
 * ```
 * Fixed-height rows with CDK virtual scrolling, sticky header (outside the scroller, scroll-synced),
 * sortable headers (asc → desc → none), row selection with select-all, column chooser (show/hide +
 * drag or ↑/↓ reorder) persisted in localStorage per `storageKey`, cursor "load more" when the user
 * nears the end, skeleton/empty states, and keyboard navigation (↑/↓, PgUp/PgDn, Home/End, Enter opens,
 * Space toggles selection) using aria-activedescendant on the grid. The host needs a bounded height.
 */
@Component({
  selector: 'sd-data-table',
  imports: [
    NgTemplateOutlet,
    CdkVirtualScrollViewport,
    CdkFixedSizeVirtualScroll,
    CdkVirtualForOf,
    CdkOverlayOrigin,
    CdkConnectedOverlay,
    CdkDropList,
    CdkDrag,
    CdkDragHandle,
    SdCheckbox,
    SdIcon,
    SdButton,
    SdSpinner,
    SdSkeleton,
    SdEmptyState,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div
      class="grid"
      role="grid"
      tabindex="0"
      [attr.aria-label]="ariaLabel()"
      [attr.aria-rowcount]="(total() ?? rows().length) + 1"
      [attr.aria-colcount]="visibleColumns().length + (selectable() ? 1 : 0)"
      [attr.aria-multiselectable]="selectable() || null"
      [attr.aria-activedescendant]="activeRowDomId()"
      [attr.aria-busy]="loading() || null"
      [style.--sd-cols]="gridTemplate()"
      [style.--sd-min-width.px]="minWidth()"
      [style.--sd-row-h.px]="rowHeight()"
      (keydown)="onKeydown($event)"
    >
      <div class="head" #head role="rowgroup">
        <div class="row row--head" role="row" aria-rowindex="1">
          @if (selectable()) {
            <div class="cell cell--select" role="columnheader">
              <sd-checkbox
                [value]="allSelected()"
                [indeterminate]="someSelected()"
                [disabled]="rows().length === 0"
                ariaLabel="Select all loaded rows"
                (valueChange)="toggleAll($event)"
              />
            </div>
          }
          @for (col of visibleColumns(); track col.key) {
            <div class="cell" role="columnheader" [class]="'cell align-' + (col.align ?? 'start')" [attr.aria-sort]="ariaSort(col)">
              @if (col.sortable) {
                <button type="button" class="sort" (click)="toggleSort(col)" [attr.aria-label]="'Sort by ' + col.header">
                  <span class="sd-truncate">{{ col.header }}</span>
                  <sd-icon
                    class="sort-icon"
                    [class.is-on]="sortFor(col) !== null"
                    [name]="sortFor(col) === 'desc' ? 'arrow-down' : sortFor(col) === 'asc' ? 'arrow-up' : 'arrow-up-down'"
                    [size]="13"
                  />
                </button>
              } @else {
                <span class="sd-truncate">{{ col.header }}</span>
              }
            </div>
          }
          <div class="cell cell--chooser" role="columnheader">
            @if (columnChooser()) {
              <button
                type="button"
                class="chooser-btn"
                cdkOverlayOrigin
                #chooserOrigin="cdkOverlayOrigin"
                aria-label="Choose columns"
                [attr.aria-expanded]="chooserOpen()"
                (click)="chooserOpen.set(!chooserOpen())"
              >
                <sd-icon name="columns" [size]="15" />
              </button>
              <ng-template
                cdkConnectedOverlay
                [cdkConnectedOverlayOrigin]="chooserOrigin"
                [cdkConnectedOverlayOpen]="chooserOpen()"
                [cdkConnectedOverlayPositions]="chooserPositions"
                (overlayOutsideClick)="chooserOpen.set(false)"
                (detach)="chooserOpen.set(false)"
              >
                <div class="sd-popover chooser" role="dialog" aria-label="Columns" (keydown.escape)="chooserOpen.set(false)">
                  <div class="chooser-head">
                    <strong>Columns</strong>
                    <button sdButton variant="link" size="sm" type="button" (click)="resetPrefs()">Reset</button>
                  </div>
                  <ul cdkDropList (cdkDropListDropped)="onDrop($event)" class="chooser-list">
                    @for (col of orderedColumns(); track col.key; let i = $index, first = $first, last = $last) {
                      <li cdkDrag [cdkDragDisabled]="!!col.locked" class="chooser-item">
                        <span cdkDragHandle class="drag" [class.is-hidden]="col.locked" aria-hidden="true"><sd-icon name="grip" [size]="14" /></span>
                        <sd-checkbox [value]="!isHidden(col)" [disabled]="!!col.locked" (valueChange)="setVisible(col, $event)">
                          {{ col.header }}
                        </sd-checkbox>
                        <span class="sd-spacer"></span>
                        <button type="button" class="move" [disabled]="first" [attr.aria-label]="'Move ' + col.header + ' up'" (click)="move(i, -1)">
                          <sd-icon name="chevron-up" [size]="13" />
                        </button>
                        <button type="button" class="move" [disabled]="last" [attr.aria-label]="'Move ' + col.header + ' down'" (click)="move(i, 1)">
                          <sd-icon name="chevron-down" [size]="13" />
                        </button>
                      </li>
                    }
                  </ul>
                </div>
              </ng-template>
            }
          </div>
        </div>
      </div>

      @if (loading() && rows().length === 0) {
        <div class="body body--static" role="rowgroup" aria-label="Loading">
          @for (n of skeletonRows; track n) {
            <div class="row" role="row">
              @if (selectable()) {
                <div class="cell cell--select"><sd-skeleton width="16px" height="16px" /></div>
              }
              @for (col of visibleColumns(); track col.key) {
                <div class="cell"><sd-skeleton [width]="(50 + ((n * 7 + $index * 13) % 45)) + '%'" /></div>
              }
              <div class="cell cell--chooser"></div>
            </div>
          }
        </div>
      } @else if (rows().length === 0) {
        <div class="body body--static" role="rowgroup">
          <div role="row"><div role="gridcell" class="empty-cell">
            @if (error()) {
              <sd-empty-state icon="circle-alert" [title]="'Could not load data'" [message]="error()" compact />
            } @else {
              <sd-empty-state [icon]="emptyIcon()" [title]="emptyTitle()" [message]="emptyMessage()" compact>
                <ng-content select="[sdTableEmpty]" />
              </sd-empty-state>
            }
          </div></div>
        </div>
      } @else {
        <cdk-virtual-scroll-viewport
          class="body"
          role="rowgroup"
          [itemSize]="rowHeight()"
          [minBufferPx]="rowHeight() * 8"
          [maxBufferPx]="rowHeight() * 16"
          (scrolledIndexChange)="onScrolledIndex($event)"
        >
          <div
            *cdkVirtualFor="let row of rows(); let i = index; trackBy: trackBy"
            class="row row--body"
            role="row"
            [id]="rowDomId(i)"
            [attr.aria-rowindex]="i + 2"
            [attr.aria-selected]="selectable() ? isSelected(row) : null"
            [class]="rowClassOf(row)"
            [class.is-active]="i === activeIndex()"
            [class.is-current]="activeKey() !== null && keyOf(row) === activeKey()"
            [class.is-selected]="selectable() && isSelected(row)"
            (click)="onRowClick(row, i)"
          >
            @if (selectable()) {
              <div class="cell cell--select" role="gridcell" (click)="$event.stopPropagation()">
                <sd-checkbox [value]="isSelected(row)" ariaLabel="Select row" (valueChange)="toggleRow(row, $event)" />
              </div>
            }
            @for (col of visibleColumns(); track col.key) {
              <div class="cell" role="gridcell" [class]="'cell align-' + (col.align ?? 'start')">
                @if (templates().get(col.key); as tpl) {
                  <ng-container [ngTemplateOutlet]="tpl" [ngTemplateOutletContext]="contextFor(row, i)" />
                } @else {
                  <span class="sd-truncate">{{ textOf(col, row) }}</span>
                }
              </div>
            }
            <div class="cell cell--chooser" role="gridcell"></div>
          </div>
        </cdk-virtual-scroll-viewport>
      }
    </div>

    @if (rows().length > 0 && (hasMore() || loadingMore())) {
      <div class="foot">
        @if (loadingMore()) {
          <sd-spinner [size]="14" label="Loading more rows" /> <span>Loading more…</span>
        } @else {
          <button sdButton size="sm" variant="ghost" type="button" (click)="loadMore.emit()">Load more</button>
        }
      </div>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      min-height: 0;
      height: 100%;
      background: var(--sd-surface);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      overflow: hidden;
    }
    .grid {
      display: flex;
      flex-direction: column;
      flex: 1;
      min-height: 0;
      outline: none;
    }
    .grid:focus-visible {
      box-shadow: inset 0 0 0 2px var(--sd-focus);
    }
    .head {
      flex: none;
      overflow: hidden;
      scrollbar-gutter: stable;
      background: var(--sd-surface-2);
      border-bottom: 1px solid var(--sd-border);
    }
    .row {
      display: grid;
      grid-template-columns: var(--sd-cols);
      width: max(100%, var(--sd-min-width));
      align-items: center;
    }
    .row--head {
      height: 38px;
      color: var(--sd-text-2);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      text-transform: uppercase;
      letter-spacing: 0.035em;
    }
    .row--body,
    .body--static .row {
      height: var(--sd-row-h);
      border-bottom: 1px solid var(--sd-divider);
      cursor: pointer;
      transition: background-color var(--sd-dur-fast) var(--sd-ease);
    }
    .row--body:hover {
      background: var(--sd-row-hover);
    }
    .row--body.is-selected {
      background: var(--sd-row-selected);
    }
    .row--body.is-current {
      box-shadow: inset 3px 0 0 var(--sd-primary);
      background: var(--sd-row-selected);
    }
    .grid:focus-visible .row--body.is-active {
      box-shadow: inset 0 0 0 2px var(--sd-focus);
    }
    .cell {
      display: flex;
      align-items: center;
      gap: 6px;
      min-width: 0;
      height: 100%;
      padding: 0 10px;
    }
    .cell.align-end {
      justify-content: flex-end;
      text-align: right;
    }
    .cell.align-center {
      justify-content: center;
    }
    .cell--select {
      justify-content: center;
      padding: 0;
    }
    .cell--chooser {
      justify-content: center;
      padding: 0;
    }
    .sort {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      min-width: 0;
      max-width: 100%;
      padding: 4px 2px;
      border: 0;
      background: transparent;
      color: inherit;
      font: inherit;
      letter-spacing: inherit;
      text-transform: inherit;
      cursor: pointer;
      border-radius: var(--sd-radius-xs);
    }
    .sort:hover {
      color: var(--sd-text);
    }
    .sort-icon {
      opacity: 0.45;
    }
    .sort-icon.is-on {
      opacity: 1;
      color: var(--sd-primary);
    }
    .chooser-btn {
      display: inline-flex;
      padding: 5px;
      border: 0;
      border-radius: var(--sd-radius-sm);
      background: transparent;
      color: var(--sd-text-3);
      cursor: pointer;
    }
    .chooser-btn:hover,
    .chooser-btn[aria-expanded='true'] {
      background: var(--sd-surface-3);
      color: var(--sd-text);
    }
    .body {
      flex: 1;
      min-height: 0;
      scrollbar-gutter: stable;
    }
    .body--static {
      overflow: hidden;
    }
    .body ::ng-deep .cdk-virtual-scroll-content-wrapper {
      width: max(100%, var(--sd-min-width));
    }
    .empty-cell {
      padding: var(--sd-space-8) var(--sd-space-4);
    }
    .foot {
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 8px;
      height: 40px;
      flex: none;
      border-top: 1px solid var(--sd-divider);
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
    .chooser {
      width: 280px;
      max-height: 420px;
      display: flex;
      flex-direction: column;
      padding: 8px;
    }
    .chooser-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 2px 4px 8px;
    }
    .chooser-list {
      margin: 0;
      padding: 0;
      list-style: none;
      overflow: auto;
    }
    .chooser-item {
      display: flex;
      align-items: center;
      gap: 6px;
      padding: 4px;
      border-radius: var(--sd-radius-sm);
      background: var(--sd-surface);
    }
    .chooser-item:hover {
      background: var(--sd-surface-2);
    }
    .chooser-item.cdk-drag-preview {
      box-shadow: var(--sd-shadow-lg);
    }
    .chooser-item.cdk-drag-placeholder {
      opacity: 0.35;
    }
    .drag {
      display: inline-flex;
      color: var(--sd-text-3);
      cursor: grab;
    }
    .drag.is-hidden {
      visibility: hidden;
    }
    .move {
      display: inline-flex;
      padding: 3px;
      border: 0;
      border-radius: var(--sd-radius-xs);
      background: transparent;
      color: var(--sd-text-3);
      cursor: pointer;
    }
    .move:hover:not(:disabled) {
      background: var(--sd-surface-3);
      color: var(--sd-text);
    }
    .move:disabled {
      opacity: 0.3;
      cursor: default;
    }
  `,
})
export class SdDataTable<T> {
  readonly columns = input.required<SdColumn<T>[]>();
  readonly rows = input.required<readonly T[]>();
  /** Stable row key (default: `row.id`). */
  readonly rowKey = input<(row: T) => string>((row) => String((row as { id?: unknown }).id ?? ''));
  /** Persist column order/visibility under `sd.table.<storageKey>`. */
  readonly storageKey = input<string | null>(null);
  readonly selectable = input(false, { transform: booleanAttribute });
  readonly columnChooser = input(true, { transform: booleanAttribute });
  readonly rowHeight = input(44);
  readonly loading = input(false, { transform: booleanAttribute });
  readonly loadingMore = input(false, { transform: booleanAttribute });
  readonly hasMore = input(false, { transform: booleanAttribute });
  readonly total = input<number | null>(null);
  readonly error = input<string | null>(null);
  readonly emptyTitle = input('Nothing here yet');
  readonly emptyMessage = input<string | null>(null);
  readonly emptyIcon = input('inbox');
  readonly ariaLabel = input('Data table');
  /** Highlights the row whose key matches (e.g. the ticket open in the side panel). */
  readonly activeKey = input<string | null>(null);
  readonly rowClass = input<((row: T) => string | null) | null>(null);

  readonly selection = model<string[]>([]);
  readonly sort = model<SdSort | null>(null);
  readonly rowClick = output<T>();
  readonly loadMore = output<void>();

  private readonly cellDefs = contentChildren(SdCellDef);
  private readonly viewport = viewChild(CdkVirtualScrollViewport);
  private readonly head = viewChild.required<ElementRef<HTMLElement>>('head');
  protected readonly templates = computed(() => {
    const map = new Map<string, TemplateRef<SdCellContext<T>>>();
    for (const def of this.cellDefs()) map.set(def.column(), def.template as TemplateRef<SdCellContext<T>>);
    return map;
  });

  protected readonly chooserPositions = CHOOSER_POSITIONS;
  protected readonly chooserOpen = signal(false);
  protected readonly activeIndex = signal(-1);
  protected readonly skeletonRows = Array.from({ length: 8 }, (_, i) => i);
  private readonly domPrefix = uniqueId('sd-row');
  private readonly prefs = signal<SdTablePrefs | null>(null);

  protected readonly orderedColumns = computed(() => {
    const cols = this.columns();
    const order = this.prefs()?.order ?? [];
    const byKey = new Map(cols.map((c) => [c.key, c]));
    const ordered: SdColumn<T>[] = [];
    for (const key of order) {
      const c = byKey.get(key);
      if (c) {
        ordered.push(c);
        byKey.delete(key);
      }
    }
    for (const c of cols) if (byKey.has(c.key)) ordered.push(c);
    return ordered;
  });
  private readonly hiddenSet = computed(() => {
    const prefs = this.prefs();
    if (prefs) return new Set(prefs.hidden);
    return new Set(this.columns().filter((c) => c.hiddenByDefault).map((c) => c.key));
  });
  protected readonly visibleColumns = computed(() => this.orderedColumns().filter((c) => c.locked || !this.hiddenSet().has(c.key)));
  protected readonly gridTemplate = computed(() => {
    const tracks = this.visibleColumns().map((c) => (c.width ? `${c.width}px` : `minmax(${c.minWidth ?? 120}px, ${c.flex ?? 1}fr)`));
    if (this.selectable()) tracks.unshift(`${SELECT_COL_PX}px`);
    tracks.push(`${CHOOSER_COL_PX}px`);
    return tracks.join(' ');
  });
  protected readonly minWidth = computed(
    () =>
      this.visibleColumns().reduce((sum, c) => sum + (c.width ?? c.minWidth ?? 120), 0) +
      (this.selectable() ? SELECT_COL_PX : 0) +
      CHOOSER_COL_PX,
  );

  private readonly selectedSet = computed(() => new Set(this.selection()));
  protected readonly allSelected = computed(() => {
    const rows = this.rows();
    return rows.length > 0 && rows.every((r) => this.selectedSet().has(this.keyOf(r)));
  });
  protected readonly someSelected = computed(() => !this.allSelected() && this.rows().some((r) => this.selectedSet().has(this.keyOf(r))));
  protected readonly activeRowDomId = computed(() => (this.activeIndex() >= 0 ? this.rowDomId(this.activeIndex()) : null));

  protected readonly trackBy = (_: number, row: T) => this.keyOf(row);

  constructor() {
    effect(() => {
      const key = this.storageKey();
      untracked(() => this.prefs.set(key ? safeStorage.getJson<SdTablePrefs>(STORAGE_KEYS.tablePrefix + key) : null));
    });
    // Keep the active row within bounds when the data changes.
    effect(() => {
      const count = this.rows().length;
      untracked(() => {
        if (this.activeIndex() >= count) this.activeIndex.set(count - 1);
      });
    });
    // Sync the header's horizontal scroll with the body.

    effect((onCleanup) => {
      const viewport = this.viewport();
      if (!viewport) return;
      const sub = viewport.elementScrolled().subscribe(() => {
        this.head().nativeElement.scrollLeft = viewport.elementRef.nativeElement.scrollLeft;
      });
      onCleanup(() => sub.unsubscribe());
    });
    inject(DestroyRef).onDestroy(() => this.chooserOpen.set(false));
  }

  keyOf(row: T): string {
    return this.rowKey()(row);
  }

  protected rowDomId(index: number): string {
    return `${this.domPrefix}-${index}`;
  }

  protected rowClassOf(row: T): string {
    const extra = this.rowClass()?.(row);
    return extra ? `row row--body ${extra}` : 'row row--body';
  }

  protected textOf(col: SdColumn<T>, row: T): string {
    const value = col.value ? col.value(row) : (row as Record<string, unknown>)[col.key];
    return value === null || value === undefined ? '' : String(value);
  }

  protected contextFor(row: T, index: number): SdCellContext<T> {
    return { $implicit: row, row, index };
  }

  protected isSelected(row: T): boolean {
    return this.selectedSet().has(this.keyOf(row));
  }

  protected isHidden(col: SdColumn<T>): boolean {
    return !col.locked && this.hiddenSet().has(col.key);
  }

  protected sortFor(col: SdColumn<T>): 'asc' | 'desc' | null {
    const s = this.sort();
    return s && s.key === (col.sortKey ?? col.key) ? s.dir : null;
  }

  protected ariaSort(col: SdColumn<T>): string | null {
    if (!col.sortable) return null;
    const dir = this.sortFor(col);
    return dir === 'asc' ? 'ascending' : dir === 'desc' ? 'descending' : 'none';
  }

  protected toggleSort(col: SdColumn<T>): void {
    const key = col.sortKey ?? col.key;
    const current = this.sortFor(col);
    this.sort.set(current === null ? { key, dir: 'asc' } : current === 'asc' ? { key, dir: 'desc' } : null);
  }

  protected toggleRow(row: T, on: boolean): void {
    const key = this.keyOf(row);
    const set = new Set(this.selection());
    if (on) set.add(key);
    else set.delete(key);
    this.selection.set([...set]);
  }

  protected toggleAll(on: boolean): void {
    this.selection.set(on ? this.rows().map((r) => this.keyOf(r)) : []);
  }

  protected onRowClick(row: T, index: number): void {
    this.activeIndex.set(index);
    this.rowClick.emit(row);
  }

  protected onScrolledIndex(first: number): void {
    const viewport = this.viewport();
    const visible = viewport ? Math.ceil(viewport.getViewportSize() / this.rowHeight()) : 20;
    if (first + visible >= this.rows().length - 8) this.requestMore();
  }

  protected onKeydown(event: KeyboardEvent): void {
    const target = event.target as HTMLElement;
    if (target !== event.currentTarget) return; // let inner controls handle their own keys
    const count = this.rows().length;
    if (count === 0) return;
    const viewport = this.viewport();
    const page = viewport ? Math.max(1, Math.floor(viewport.getViewportSize() / this.rowHeight()) - 1) : 10;
    let next = this.activeIndex();
    switch (event.key) {
      case 'ArrowDown':
        next = Math.min(count - 1, next + 1);
        break;
      case 'ArrowUp':
        next = Math.max(0, next - 1);
        break;
      case 'PageDown':
        next = Math.min(count - 1, next + page);
        break;
      case 'PageUp':
        next = Math.max(0, next - page);
        break;
      case 'Home':
        next = 0;
        break;
      case 'End':
        next = count - 1;
        break;
      case 'Enter': {
        const row = this.rows()[next];
        if (row) {
          event.preventDefault();
          this.rowClick.emit(row);
        }
        return;
      }
      case ' ':
      case 'x': {
        const row = this.rows()[next];
        if (row && this.selectable()) {
          event.preventDefault();
          this.toggleRow(row, !this.isSelected(row));
        }
        return;
      }
      default:
        return;
    }
    event.preventDefault();
    this.activeIndex.set(next);
    this.ensureVisible(next);
    if (next >= count - 5) this.requestMore();
  }

  protected setVisible(col: SdColumn<T>, visible: boolean): void {
    const hidden = new Set(this.hiddenSet());
    if (visible) hidden.delete(col.key);
    else hidden.add(col.key);
    this.savePrefs({ order: this.orderedColumns().map((c) => c.key), hidden: [...hidden] });
  }

  protected move(index: number, delta: number): void {
    const keys = this.orderedColumns().map((c) => c.key);
    const target = index + delta;
    if (target < 0 || target >= keys.length) return;
    moveItemInArray(keys, index, target);
    this.savePrefs({ order: keys, hidden: [...this.hiddenSet()] });
  }

  protected onDrop(event: CdkDragDrop<unknown>): void {
    if (event.previousIndex === event.currentIndex) return;
    const keys = this.orderedColumns().map((c) => c.key);
    moveItemInArray(keys, event.previousIndex, event.currentIndex);
    this.savePrefs({ order: keys, hidden: [...this.hiddenSet()] });
  }

  protected resetPrefs(): void {
    this.prefs.set(null);
    const key = this.storageKey();
    if (key) safeStorage.remove(STORAGE_KEYS.tablePrefix + key);
  }

  /** Scrolls a row into view (e.g. after keyboard navigation from outside). */
  scrollToIndex(index: number): void {
    this.ensureVisible(index);
  }

  private ensureVisible(index: number): void {
    const viewport = this.viewport();
    if (!viewport) return;
    const h = this.rowHeight();
    const top = viewport.measureScrollOffset('top');
    const size = viewport.getViewportSize();
    const rowTop = index * h;
    if (rowTop < top) viewport.scrollToOffset(rowTop);
    else if (rowTop + h > top + size) viewport.scrollToOffset(rowTop + h - size);
  }

  private requestMore(): void {
    if (this.hasMore() && !this.loadingMore() && !this.loading()) this.loadMore.emit();
  }

  private savePrefs(prefs: SdTablePrefs): void {
    this.prefs.set(prefs);
    const key = this.storageKey();
    if (key) safeStorage.setJson(STORAGE_KEYS.tablePrefix + key, prefs);
  }
}
