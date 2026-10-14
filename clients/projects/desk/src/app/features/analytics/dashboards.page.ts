import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked, viewChildren } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import {
  AnalyticsApi,
  AnalyticsDashboardDto,
  AnalyticsDashboardSummaryDto,
  AnalyticsQueryParams,
  AnalyticsWidgetDto,
  ApiError,
  saveErrorMessage,
} from '@sd/core';
import { SdButton, SdDialogService, SdDropdownMenu, SdEmptyState, SdIcon, SdInput, SdMenuItem, SdSkeleton, ToastService } from '@sd/ui';
import { AnalyticsFilterState, DEFAULT_FILTER_STATE } from './analytics-format';
import { AnalyticsStore } from './analytics.store';
import { DashboardFormData, DashboardFormDialog } from './dashboard-form.dialog';
import { AnalyticsDrilldownComponent } from './drilldown-drawer.component';
import { AnalyticsFilterBarComponent } from './filter-bar.component';
import { AnalyticsDrill, AnalyticsWidgetComponent } from './widget.component';
import { WidgetEditorData, WidgetEditorDialog } from './widget-editor.dialog';


/**
 * /analytics/dashboards[/:id] — role-default, shared and personal dashboards. Widgets sit on a 12-column grid
 * (stacked on phones); edit mode moves/resizes/adds/removes widgets with buttons (keyboard friendly) and saves with
 * optimistic concurrency. Every value drills down to its tickets.
 */
@Component({
  selector: 'desk-analytics-dashboards',
  imports: [
    FormsModule,
    SdButton,
    SdDropdownMenu,
    SdEmptyState,
    SdIcon,
    SdInput,
    SdSkeleton,
    AnalyticsDrilldownComponent,
    AnalyticsFilterBarComponent,
    AnalyticsWidgetComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (listLoading() && dashboards().length === 0) {
      <div aria-busy="true" class="loading"><sd-skeleton height="36px" width="320px" /><sd-skeleton height="240px" /></div>
    } @else if (listError()) {
      <sd-empty-state icon="circle-alert" title="Dashboards could not be loaded" [message]="listError()">
        <button sdButton type="button" (click)="loadList()">Retry</button>
      </sd-empty-state>
    } @else if (dashboards().length === 0) {
      <sd-empty-state icon="dashboard" title="No dashboards yet" message="Create a dashboard from catalogue metrics.">
        <button sdButton variant="primary" icon="plus" type="button" (click)="create()">New dashboard</button>
      </sd-empty-state>
    } @else {
      <div class="toolbar">
        <label class="sd-visually-hidden" for="dash-select">Dashboard</label>
        <select sdInput id="dash-select" class="picker" [ngModel]="current()?.id ?? selectedId()" (ngModelChange)="open($event)" [disabled]="editing()">
          @for (g of groups(); track g.label) {
            @if (g.items.length) {
              <optgroup [label]="g.label">
                @for (d of g.items; track d.id) {
                  <option [value]="d.id">{{ d.name }}</option>
                }
              </optgroup>
            }
          }
        </select>
        @if (current(); as d) {
          <span class="badges sd-xs sd-muted">
            {{ d.isDefault ? 'Role default' : d.isShared ? 'Shared' : 'Personal' }} · {{ d.widgets.length }} widget{{ d.widgets.length === 1 ? '' : 's' }}
          </span>
        }
        <span class="spacer"></span>
        @if (editing()) {
          <button sdButton size="sm" icon="plus" type="button" (click)="addWidget()">Add widget</button>
          <button sdButton size="sm" type="button" (click)="cancelEdit()">Cancel</button>
          <button sdButton size="sm" variant="primary" icon="save" type="button" [loading]="saving()" (click)="saveLayout()">Save layout</button>
        } @else {
          <button sdButton size="sm" icon="refresh" type="button" (click)="refresh()" aria-label="Refresh widgets" iconOnly></button>
          @if (current()?.canEdit) {
            <button sdButton size="sm" icon="pencil" type="button" (click)="startEdit()">Edit layout</button>
          }
          <sd-dropdown-menu size="sm" icon="ellipsis" iconOnly ariaLabel="Dashboard actions" align="end" [items]="menu()" (select)="onMenu($event)" />
        }
      </div>
      @if (current()?.description) {
        <p class="sd-small sd-muted desc">{{ current()?.description }}</p>
      }
      <desk-analytics-filter-bar [(state)]="filterState" />
      @if (saveError()) {
        <p class="error" role="alert">{{ saveError() }}</p>
      }

      @if (dashLoading() && !current()) {
        <div aria-busy="true" class="grid"><sd-skeleton height="200px" /></div>
      } @else if (dashError()) {
        <sd-empty-state icon="circle-alert" title="This dashboard could not be loaded" [message]="dashError()">
          <button sdButton type="button" (click)="open(selectedId())">Retry</button>
        </sd-empty-state>
      } @else if (widgets().length === 0) {
        <sd-empty-state icon="chart-column" title="This dashboard is empty" message="Add widgets from the metric catalogue.">
          @if (current()?.canEdit) {
            <button sdButton variant="primary" icon="plus" type="button" (click)="editing() ? addWidget() : startEditAndAdd()">Add widget</button>
          }
        </sd-empty-state>
      } @else {
        <div class="grid" [class.is-editing]="editing()">
          @for (w of widgets(); track w.id; let i = $index) {
            <div class="cell" [style.grid-column]="(w.x + 1) + ' / span ' + w.w" [style.grid-row]="(w.y + 1) + ' / span ' + w.h" [style.--h]="w.h">
              <desk-analytics-widget
                [widget]="w"
                [period]="filterState().period"
                [from]="filterState().from"
                [to]="filterState().to"
                [compare]="filterState().compare"
                [filters]="filterState().filters"
                (drill)="openDrill($event)"
              >
                @if (editing()) {
                  <span class="tools" role="group" [attr.aria-label]="'Arrange ' + w.title">
                    <button type="button" class="tool" (click)="move(i, -1, 0)" [attr.aria-label]="'Move ' + w.title + ' left'" [disabled]="w.x === 0"><sd-icon name="arrow-left" [size]="14" /></button>
                    <button type="button" class="tool" (click)="move(i, 1, 0)" [attr.aria-label]="'Move ' + w.title + ' right'" [disabled]="w.x + w.w >= 12"><sd-icon name="arrow-right" [size]="14" /></button>
                    <button type="button" class="tool" (click)="move(i, 0, -1)" [attr.aria-label]="'Move ' + w.title + ' up'" [disabled]="w.y === 0"><sd-icon name="arrow-up" [size]="14" /></button>
                    <button type="button" class="tool" (click)="move(i, 0, 1)" [attr.aria-label]="'Move ' + w.title + ' down'"><sd-icon name="arrow-down" [size]="14" /></button>
                    <button type="button" class="tool" (click)="resize(i, -1, 0)" [attr.aria-label]="'Make ' + w.title + ' narrower'" [disabled]="w.w <= 2">W−</button>
                    <button type="button" class="tool" (click)="resize(i, 1, 0)" [attr.aria-label]="'Make ' + w.title + ' wider'" [disabled]="w.w >= 12">W+</button>
                    <button type="button" class="tool" (click)="resize(i, 0, -1)" [attr.aria-label]="'Make ' + w.title + ' shorter'" [disabled]="w.h <= 1">H−</button>
                    <button type="button" class="tool" (click)="resize(i, 0, 1)" [attr.aria-label]="'Make ' + w.title + ' taller'" [disabled]="w.h >= 8">H+</button>
                    <button type="button" class="tool" (click)="editWidget(i)" [attr.aria-label]="'Edit ' + w.title"><sd-icon name="pencil" [size]="14" /></button>
                    <button type="button" class="tool danger" (click)="removeWidget(i)" [attr.aria-label]="'Remove ' + w.title"><sd-icon name="trash" [size]="14" /></button>
                  </span>
                }
              </desk-analytics-widget>
            </div>
          }
        </div>
      }
    }
    <desk-analytics-drilldown [(open)]="drillOpen" [params]="drillParams()" />
  `,
  styles: `
    :host {
      display: grid;
      grid-template-columns: minmax(0, 1fr);
      gap: var(--sd-space-3);
    }
    .loading {
      display: grid;
      gap: var(--sd-space-3);
    }
    .toolbar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--sd-space-2);
    }
    .picker {
      width: auto;
      max-width: 100%;
      min-width: 220px;
      font-weight: var(--sd-weight-semibold);
    }
    .spacer {
      flex: 1;
    }
    .desc {
      margin: 0;
    }
    .error {
      margin: 0;
      color: var(--sd-danger);
    }
    .grid {
      display: grid;
      grid-template-columns: repeat(12, minmax(0, 1fr));
      grid-auto-rows: 76px;
      gap: var(--sd-space-3);
    }
    .cell {
      min-width: 0;
      min-height: 0;
    }
    .grid.is-editing .cell {
      outline: 2px dashed var(--sd-border-strong);
      outline-offset: 2px;
      border-radius: var(--sd-radius-lg);
    }
    .tools {
      display: inline-flex;
      flex-wrap: wrap;
      gap: 2px;
      margin-left: auto;
    }
    .tool {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      min-width: 26px;
      height: 26px;
      padding: 0 4px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-sm);
      background: var(--sd-surface);
      color: var(--sd-text-2);
      font: inherit;
      font-size: 11px;
      cursor: pointer;
    }
    .tool:hover:not(:disabled) {
      background: var(--sd-surface-2);
      color: var(--sd-text);
    }
    .tool:disabled {
      opacity: 0.4;
      cursor: default;
    }
    .tool:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .tool.danger:hover {
      color: var(--sd-danger);
    }
    @media (max-width: 900px) {
      .grid {
        grid-template-columns: minmax(0, 1fr);
        grid-auto-rows: auto;
      }
      .cell {
        grid-column: 1 / -1 !important;
        grid-row: auto !important;
        height: calc(max(var(--h), 2) * 76px);
      }
    }
  `,
})
export class AnalyticsDashboardsPage {
  private readonly api = inject(AnalyticsApi);
  private readonly router = inject(Router);
  private readonly dialogs = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  protected readonly store = inject(AnalyticsStore);
  private readonly widgetViews = viewChildren(AnalyticsWidgetComponent);

  /** Route parameter (component input binding). */
  readonly id = input<string | undefined>(undefined);
  /** Query parameter: open straight in edit mode (after creating a dashboard). */
  readonly edit = input<string | undefined>(undefined);

  protected readonly dashboards = signal<AnalyticsDashboardSummaryDto[]>([]);
  protected readonly listLoading = signal(false);
  protected readonly listError = signal<string | null>(null);
  protected readonly current = signal<AnalyticsDashboardDto | null>(null);
  protected readonly dashLoading = signal(false);
  protected readonly dashError = signal<string | null>(null);
  protected readonly selectedId = signal<string | null>(null);
  protected readonly editing = signal(false);
  protected readonly draft = signal<AnalyticsWidgetDto[]>([]);
  protected readonly saving = signal(false);
  protected readonly saveError = signal<string | null>(null);
  protected readonly filterState = signal<AnalyticsFilterState>({ ...DEFAULT_FILTER_STATE });
  protected readonly drillOpen = signal(false);
  protected readonly drillParams = signal<AnalyticsQueryParams | null>(null);

  protected readonly widgets = computed(() => {
    const list = this.editing() ? this.draft() : (this.current()?.widgets ?? []);
    return [...list].sort((a, b) => a.y - b.y || a.x - b.x);
  });

  protected readonly groups = computed(() => {
    const all = this.dashboards();
    return [
      { label: 'Role defaults', items: all.filter((d) => d.isDefault) },
      { label: 'Shared', items: all.filter((d) => d.isShared && !d.isDefault) },
      { label: 'Personal', items: all.filter((d) => !d.isShared) },
    ];
  });

  protected readonly menu = computed<SdMenuItem[]>(() => {
    const d = this.current();
    const items: SdMenuItem[] = [
      { id: 'new', label: 'New dashboard', icon: 'plus' },
      { id: 'duplicate', label: 'Duplicate as personal', icon: 'copy', disabled: !d },
    ];
    if (d?.canEdit) items.push({ id: 'settings', label: 'Settings', icon: 'settings' });
    if (d?.canEdit && !d.isDefault) items.push({ id: 'divider', divider: true }, { id: 'delete', label: 'Delete dashboard', icon: 'trash', danger: true });
    return items;
  });

  constructor() {
    this.store.ensure();
    this.loadList();
    effect(() => {
      const id = this.id();
      untracked(() => {
        if (id && id !== this.selectedId()) this.load(id);
      });
    });
  }

  /** Shows a dashboard returned by a save/create/duplicate without reloading it. */
  private show(d: AnalyticsDashboardDto): void {
    this.selectedId.set(d.id);
    this.current.set(d);
  }

  protected loadList(): void {
    this.listLoading.set(true);
    this.listError.set(null);
    this.api.dashboards().subscribe({
      next: (list) => {
        this.dashboards.set(list);
        this.listLoading.set(false);
        const target = this.selectedId() ?? this.id() ?? list[0]?.id;
        if (target && target !== this.current()?.id && target !== this.selectedId()) this.load(target);
        else if (target && !this.current() && !this.dashLoading()) this.load(target);
      },
      error: (e: unknown) => {
        this.listError.set(ApiError.from(e).userMessage);
        this.listLoading.set(false);
      },
    });
  }

  private load(id: string): void {
    this.selectedId.set(id);
    this.dashLoading.set(true);
    this.dashError.set(null);
    this.api.dashboard(id).subscribe({
      next: (d) => {
        this.current.set(d);
        this.editing.set(false);
        this.dashLoading.set(false);
        this.filterState.update((s) => ({ ...s, period: s.period === 'custom' ? s.period : d.period }));
        if (this.edit() === '1' && d.canEdit) {
          this.startEdit();
          void this.router.navigate([], { queryParams: { edit: null }, queryParamsHandling: 'merge', replaceUrl: true });
        }
      },
      error: (e: unknown) => {
        this.dashError.set(ApiError.from(e).userMessage);
        this.dashLoading.set(false);
      },
    });
  }

  protected open(id: string | null): void {
    if (!id) return;
    void this.router.navigate(['/analytics/dashboards', id]);
  }

  protected refresh(): void {
    for (const w of this.widgetViews()) w.reload();
  }

  protected openDrill(d: AnalyticsDrill): void {
    const s = this.filterState();
    const period = d.period ?? s.period;
    this.drillParams.set({
      metric: d.metric,
      dimension: d.dimension ?? undefined,
      key: d.key ?? undefined,
      bucket: d.bucket ?? undefined,
      period,
      from: period === 'custom' ? (s.from ?? undefined) : undefined,
      to: period === 'custom' ? (s.to ?? undefined) : undefined,
      ...d.filters,
    });
    this.drillOpen.set(true);
  }

  // ── Editing ──

  protected startEdit(): void {
    this.draft.set(structuredClone(this.current()?.widgets ?? []));
    this.saveError.set(null);
    this.editing.set(true);
  }

  protected startEditAndAdd(): void {
    this.startEdit();
    this.addWidget();
  }

  protected cancelEdit(): void {
    this.editing.set(false);
    this.saveError.set(null);
  }

  private update(index: number, change: (w: AnalyticsWidgetDto) => AnalyticsWidgetDto): void {
    const target = this.widgets()[index];
    this.draft.update((list) => list.map((w) => (w.id === target.id ? change(w) : w)));
  }

  protected move(index: number, dx: number, dy: number): void {
    this.update(index, (w) => ({ ...w, x: Math.max(0, Math.min(12 - w.w, w.x + dx)), y: Math.max(0, w.y + dy) }));
  }

  protected resize(index: number, dw: number, dh: number): void {
    this.update(index, (w) => {
      const width = Math.max(2, Math.min(12, w.w + dw));
      return { ...w, w: width, x: Math.min(w.x, 12 - width), h: Math.max(1, Math.min(8, w.h + dh)) };
    });
  }

  protected removeWidget(index: number): void {
    const target = this.widgets()[index];
    this.draft.update((list) => list.filter((w) => w.id !== target.id));
  }

  private nextY(): number {
    return this.draft().reduce((max, w) => Math.max(max, w.y + w.h), 0);
  }

  protected addWidget(): void {
    const ref = this.dialogs.open<AnalyticsWidgetDto, WidgetEditorData>(WidgetEditorDialog, {
      data: { widget: null, period: this.filterState().period === 'custom' ? 'last_30_days' : this.filterState().period, nextY: this.nextY() },
      size: 'lg',
    });
    ref.closed.subscribe((w) => {
      if (w) this.draft.update((list) => [...list, w]);
    });
  }

  protected editWidget(index: number): void {
    const target = this.widgets()[index];
    const ref = this.dialogs.open<AnalyticsWidgetDto, WidgetEditorData>(WidgetEditorDialog, {
      data: { widget: target, period: this.filterState().period === 'custom' ? 'last_30_days' : this.filterState().period, nextY: this.nextY() },
      size: 'lg',
    });
    ref.closed.subscribe((w) => {
      if (w) this.draft.update((list) => list.map((x) => (x.id === target.id ? w : x)));
    });
  }

  protected saveLayout(): void {
    const d = this.current();
    if (!d || this.saving()) return;
    this.saving.set(true);
    this.saveError.set(null);
    this.api
      .updateDashboard(d.id, { name: d.name, description: d.description, isShared: d.isShared, period: d.period, widgets: this.draft(), rowVersion: d.rowVersion })
      .subscribe({
        next: (saved) => {
          this.show(saved);
          this.editing.set(false);
          this.saving.set(false);
          this.toast.success('Dashboard saved');
          this.loadList();
        },
        error: (e: unknown) => {
          this.saving.set(false);
          const err = ApiError.from(e);
          this.saveError.set(err.is('concurrency_conflict') ? 'Someone else changed this dashboard. Cancel to reload their version, then apply your changes again.' : saveErrorMessage(e));
        },
      });
  }

  // ── Dashboard actions ──

  protected onMenu(item: SdMenuItem): void {
    switch (item.id) {
      case 'new':
        this.create();
        break;
      case 'duplicate':
        this.duplicate();
        break;
      case 'settings':
        this.settings();
        break;
      case 'delete':
        void this.remove();
        break;
    }
  }

  protected create(): void {
    const ref = this.dialogs.open<AnalyticsDashboardDto, DashboardFormData>(DashboardFormDialog, { data: { dashboard: null } });
    ref.closed.subscribe((d) => {
      if (!d) return;
      this.show(d);
      this.loadList();
      // A new page instance (from /analytics/dashboards) opens in edit mode through ?edit=1; a reused one starts here.
      void this.router.navigate(['/analytics/dashboards', d.id], { queryParams: { edit: 1 } });
      this.startEdit();
    });
  }

  private duplicate(): void {
    const d = this.current();
    if (!d) return;
    this.api.duplicateDashboard(d.id).subscribe((copy) => {
      this.toast.success('Personal copy created', copy.name);
      this.show(copy);
      this.loadList();
      void this.router.navigate(['/analytics/dashboards', copy.id]);
    });
  }

  private settings(): void {
    const d = this.current();
    if (!d) return;
    const ref = this.dialogs.open<AnalyticsDashboardDto, DashboardFormData>(DashboardFormDialog, { data: { dashboard: d } });
    ref.closed.subscribe((saved) => {
      if (!saved) return;
      this.show(saved);
      this.filterState.update((s) => ({ ...s, period: saved.period }));
      this.loadList();
    });
  }

  private async remove(): Promise<void> {
    const d = this.current();
    if (!d) return;
    const ok = await this.dialogs.confirm({ title: `Delete “${d.name}”?`, message: 'The dashboard and its layout are removed for everyone who can see it.', danger: true, confirmLabel: 'Delete' });
    if (!ok) return;
    this.api.deleteDashboard(d.id).subscribe({
      next: () => {
        this.toast.success('Dashboard deleted');
        this.current.set(null);
        void this.router.navigate(['/analytics/dashboards']);
        this.loadList();
      },
      error: (e: unknown) => this.toast.error('Could not delete the dashboard', ApiError.from(e).userMessage),
    });
  }
}
