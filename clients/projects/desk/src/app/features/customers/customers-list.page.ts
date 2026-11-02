import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import {
  CustomerDetailDto,
  CustomerFacetsDto,
  CustomerListItemDto,
  CustomerQuery,
  CustomersApi,
  FilterCondition,
  FilterGroup,
  MeStore,
  SavedViewDto,
  ViewsApi,
  cursorList,
  saveErrorMessage,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdColumn,
  SdCombobox,
  SdDataTable,
  SdDialogService,
  SdDropdownMenu,
  SdIcon,
  SdInput,
  SdMenuItem,
  SdOption,
  SdPageHeader,
  SdRelativeTimePipe,
  SdSort,
  SdCellDef,
  SdToggle,
  ToastService,
} from '@sd/ui';
import { CustomerFormDialog, CustomerFormData } from './customer-form.dialog';
import { CUSTOMER_TABS, ModuleTabsComponent } from './shared/module-tabs.component';
import { humanize, statusTone, tierTone } from './shared/customer-ui';
import { TextPromptData, TextPromptDialog } from './shared/text-prompt.dialog';

interface Filters {
  q: string;
  status: string[];
  tier: string[];
  region: string[];
  industry: string[];
  groupId: string | null;
  accountManagerId: string | null;
  mine: boolean;
  hasOpenTickets: boolean;
  topLevelOnly: boolean;
}

const EMPTY: Filters = {
  q: '',
  status: [],
  tier: [],
  region: [],
  industry: [],
  groupId: null,
  accountManagerId: null,
  mine: false,
  hasOpenTickets: false,
  topLevelOnly: false,
};

const VIEW_SCOPE = 'customers';

/** /customers — searchable, filterable, keyset-paged customer list with saved views. */
@Component({
  selector: 'desk-customers-list-page',
  imports: [
    FormsModule,
    SdPageHeader,
    SdButton,
    SdDataTable,
    SdCellDef,
    SdBadge,
    SdCombobox,
    SdToggle,
    SdInput,
    SdIcon,
    SdDropdownMenu,
    SdRelativeTimePipe,
    ModuleTabsComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Customers" subtitle="Organisations, their hierarchy, contracts and support history." icon="building">
        @if (me.can('customers.manage')) {
          <button sdButton sdPageActions variant="primary" icon="plus" (click)="create()">New customer</button>
        }
      </sd-page-header>
      <desk-module-tabs [tabs]="tabs" ariaLabel="Customers sections" />
      <div class="desk-page__body">
        <div class="toolbar">
          <label class="search">
            <sd-icon name="search" [size]="16" />
            <input
              sdInput
              type="search"
              placeholder="Search name, code, domain, e-mail or phone"
              aria-label="Search customers"
              [ngModel]="filters().q"
              (ngModelChange)="onSearch($event)"
            />
          </label>
          <sd-combobox class="f" multiple [options]="statusOptions()" [value]="filters().status" (valueChange)="patch({ status: asList($event) })" placeholder="Status" ariaLabel="Status" size="sm" />
          <sd-combobox class="f" multiple [options]="tierOptions()" [value]="filters().tier" (valueChange)="patch({ tier: asList($event) })" placeholder="Tier" ariaLabel="Tier" size="sm" />
          <sd-combobox class="f" multiple [options]="regionOptions()" [value]="filters().region" (valueChange)="patch({ region: asList($event) })" placeholder="Region" ariaLabel="Region" size="sm" />
          <sd-combobox class="f" multiple [options]="industryOptions()" [value]="filters().industry" (valueChange)="patch({ industry: asList($event) })" placeholder="Industry" ariaLabel="Industry" size="sm" />
          <sd-combobox class="f" [options]="groupOptions()" [value]="filters().groupId" (valueChange)="patch({ groupId: asOne($event) })" placeholder="Group" ariaLabel="Group" size="sm" />
          <sd-combobox class="f" [options]="managerOptions()" [value]="filters().accountManagerId" (valueChange)="patch({ accountManagerId: asOne($event) })" placeholder="Account manager" ariaLabel="Account manager" size="sm" />
          <sd-toggle [value]="filters().mine" (valueChange)="patch({ mine: $event })">My accounts</sd-toggle>
          <sd-toggle [value]="filters().hasOpenTickets" (valueChange)="patch({ hasOpenTickets: $event })">Open tickets</sd-toggle>
          <sd-toggle [value]="filters().topLevelOnly" (valueChange)="patch({ topLevelOnly: $event })">Top level</sd-toggle>
          <span class="sd-spacer"></span>
          @if (activeFilterCount() > 0) {
            <button sdButton size="sm" variant="ghost" icon="x" (click)="reset()">Clear</button>
          }
          <sd-dropdown-menu label="Views" icon="bookmark" size="sm" align="end" [items]="viewItems()" (select)="onViewMenu($event)" />
        </div>
        @if (currentView(); as v) {
          <p class="view-note sd-small sd-muted">View: <strong>{{ v.name }}</strong></p>
        }
        <div class="table-host">
          <sd-data-table
            [columns]="columns"
            [rows]="list.items()"
            storageKey="customers.list"
            [(sort)]="sort"
            [loading]="list.loading()"
            [loadingMore]="list.loadingMore()"
            [hasMore]="list.hasMore()"
            [total]="list.total()"
            [error]="list.error()?.userMessage ?? null"
            emptyTitle="No customers match"
            emptyMessage="Try other filters or create a customer."
            emptyIcon="building"
            ariaLabel="Customers"
            (loadMore)="list.loadMore()"
            (rowClick)="open($event)"
          >
            <ng-template sdCell="name" [sdCellRows]="list.items()" let-row>
              <span class="name sd-truncate">
                <strong>{{ row.name }}</strong>
                @if (row.parentName) {
                  <span class="sd-xs sd-muted"> · {{ row.parentName }}</span>
                }
              </span>
            </ng-template>
            <ng-template sdCell="tier" [sdCellRows]="list.items()" let-row>
              <sd-badge [tone]="tierTone(row.tier)" size="sm">{{ row.tier }}</sd-badge>
            </ng-template>
            <ng-template sdCell="status" [sdCellRows]="list.items()" let-row>
              <sd-badge [tone]="statusTone(row.status)" size="sm" dot>{{ statusLabel(row.status) }}</sd-badge>
            </ng-template>
            <ng-template sdCell="openTickets" [sdCellRows]="list.items()" let-row>
              @if (row.openTickets > 0) {
                <sd-badge tone="warning" size="sm">{{ row.openTickets }}</sd-badge>
              } @else {
                <span class="sd-muted">0</span>
              }
            </ng-template>
            <ng-template sdCell="updatedAt" [sdCellRows]="list.items()" let-row>
              <span class="sd-small sd-muted">{{ row.updatedAt | sdRelativeTime }}</span>
            </ng-template>
          </sd-data-table>
        </div>
      </div>
    </div>
  `,
  styles: `
    .toolbar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--sd-space-2);
      margin-bottom: var(--sd-space-3);
    }
    .search {
      position: relative;
      flex: 1 1 260px;
      max-width: 380px;
    }
    .search sd-icon {
      position: absolute;
      left: 10px;
      top: 50%;
      transform: translateY(-50%);
      color: var(--sd-text-3);
    }
    .search input {
      padding-left: 32px;
    }
    .f {
      width: 150px;
    }
    .view-note {
      margin-bottom: var(--sd-space-2);
    }
    .table-host {
      height: max(420px, calc(100dvh - 330px));
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      overflow: hidden;
      background: var(--sd-surface);
    }
    .name {
      display: block;
    }
    @media (max-width: 640px) {
      .f {
        width: calc(50% - var(--sd-space-1));
      }
      .search {
        max-width: none;
      }
    }
  `,
})
export class CustomersListPage {
  protected readonly me = inject(MeStore);
  private readonly api = inject(CustomersApi);
  private readonly views = inject(ViewsApi);
  private readonly dialog = inject(SdDialogService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  protected readonly tabs = CUSTOMER_TABS;
  protected readonly tierTone = tierTone;
  protected readonly statusTone = statusTone;
  protected readonly statusLabel = humanize;
  protected readonly filters = signal<Filters>({ ...EMPTY });
  protected readonly sort = signal<SdSort | null>({ key: 'name', dir: 'asc' });
  protected readonly facets = signal<CustomerFacetsDto | null>(null);
  protected readonly savedViews = signal<SavedViewDto[]>([]);
  protected readonly currentView = signal<SavedViewDto | null>(null);
  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  protected readonly columns: SdColumn<CustomerListItemDto>[] = [
    { key: 'name', header: 'Customer', sortable: true, flex: 3, minWidth: 220, locked: true },
    { key: 'code', header: 'Code', sortable: true, width: 110 },
    { key: 'tier', header: 'Tier', width: 110 },
    { key: 'status', header: 'Status', width: 120 },
    { key: 'industry', header: 'Industry', flex: 1, minWidth: 120 },
    { key: 'serviceRegion', header: 'Region', width: 100 },
    { key: 'accountManagerName', header: 'Account manager', flex: 1, minWidth: 140 },
    { key: 'groupName', header: 'Group', width: 130, hiddenByDefault: true },
    { key: 'contactCount', header: 'Contacts', width: 90, align: 'end' },
    { key: 'openTickets', header: 'Open', width: 80, align: 'end' },
    { key: 'childCount', header: 'Subsidiaries', width: 110, align: 'end', hiddenByDefault: true },
    { key: 'erpCode', header: 'ERP code', width: 110, hiddenByDefault: true },
    { key: 'updatedAt', header: 'Updated', sortable: true, sortKey: 'updated', width: 120 },
  ];

  protected readonly list = cursorList<CustomerListItemDto>((cursor) => this.api.list({ ...this.query(), cursor, limit: 50, includeTotal: !cursor }));

  protected readonly statusOptions = computed<SdOption[]>(() => (this.facets()?.statuses ?? []).map((s) => ({ value: s, label: humanize(s) })));
  protected readonly tierOptions = computed<SdOption[]>(() => (this.facets()?.tiers ?? []).map((s) => ({ value: s, label: s })));
  protected readonly regionOptions = computed<SdOption[]>(() => (this.facets()?.regions ?? []).map((s) => ({ value: s, label: s })));
  protected readonly industryOptions = computed<SdOption[]>(() => (this.facets()?.industries ?? []).map((s) => ({ value: s, label: s })));
  protected readonly groupOptions = computed<SdOption[]>(() => (this.facets()?.groups ?? []).map((g) => ({ value: g.id, label: g.name })));
  protected readonly managerOptions = computed<SdOption[]>(() => (this.facets()?.accountManagers ?? []).map((g) => ({ value: g.id, label: g.name, avatar: g.name })));
  protected readonly activeFilterCount = computed(() => {
    const f = this.filters();
    return [f.q, ...f.status, ...f.tier, ...f.region, ...f.industry, f.groupId, f.accountManagerId, f.mine || null, f.hasOpenTickets || null, f.topLevelOnly || null].filter(
      (v) => v !== null && v !== '',
    ).length;
  });

  protected readonly viewItems = computed<SdMenuItem[]>(() => {
    const views = this.savedViews();
    const items: SdMenuItem[] = [{ id: 'heading', label: 'Saved views', heading: true }];
    if (views.length === 0) items.push({ id: 'none', label: 'No saved views yet', disabled: true });
    for (const v of views) items.push({ id: `view:${v.id}`, label: v.name, icon: v.visibility === 'Shared' ? 'users' : 'bookmark', hint: v.isMine ? undefined : 'shared' });
    items.push({ id: 'divider', divider: true });
    items.push({ id: 'save', label: 'Save current filters as view…', icon: 'plus' });
    const current = this.currentView();
    if (current?.isMine) items.push({ id: `delete:${current.id}`, label: `Delete “${current.name}”`, icon: 'trash', danger: true });
    return items;
  });

  constructor() {
    this.api.facets().subscribe((f) => this.facets.set(f));
    this.loadViews();
    effect(() => {
      this.filters();
      this.sort();
      untracked(() => this.list.load());
    });
  }

  private query(): CustomerQuery {
    const f = this.filters();
    const s = this.sort();
    const sortKey = s?.key === 'updatedAt' ? 'updated' : s?.key === 'code' ? 'code' : s?.key === 'updated' ? 'updated' : 'name';
    return {
      q: f.q || null,
      status: f.status,
      tier: f.tier,
      region: f.region,
      industry: f.industry,
      groupId: f.groupId,
      accountManagerId: f.accountManagerId,
      mine: f.mine || null,
      hasOpenTickets: f.hasOpenTickets || null,
      topLevelOnly: f.topLevelOnly || null,
      sort: s ? sortKey : 'name',
      dir: s?.dir ?? 'asc',
    };
  }

  protected asList(value: string | string[] | null): string[] {
    return Array.isArray(value) ? value : value ? [value] : [];
  }

  protected asOne(value: string | string[] | null): string | null {
    return Array.isArray(value) ? (value[0] ?? null) : value;
  }

  protected patch(change: Partial<Filters>): void {
    this.filters.update((f) => ({ ...f, ...change }));
    this.currentView.set(null);
  }

  protected onSearch(q: string): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.patch({ q }), 250);
  }

  protected reset(): void {
    this.filters.set({ ...EMPTY });
    this.currentView.set(null);
  }

  protected open(row: CustomerListItemDto): void {
    void this.router.navigate(['/customers', row.id]);
  }

  protected create(): void {
    const ref = this.dialog.open<CustomerDetailDto, CustomerFormData>(CustomerFormDialog, { data: {}, size: 'lg', disableClose: true });
    ref.closed.subscribe((saved) => {
      if (!saved) return;
      this.toast.success('Customer created', saved.name);
      void this.router.navigate(['/customers', saved.id]);
    });
  }

  // ───── Saved views (scope "customers": the filter is stored as a condition group) ─────

  private loadViews(): void {
    this.views.list(VIEW_SCOPE).subscribe((v) => this.savedViews.set(v));
  }

  protected onViewMenu(item: SdMenuItem): void {
    if (item.id === 'save') return this.saveView();
    if (item.id.startsWith('view:')) {
      const view = this.savedViews().find((v) => v.id === item.id.slice(5));
      if (view) this.applyView(view);
      return;
    }
    if (item.id.startsWith('delete:')) {
      const id = item.id.slice(7);
      void this.dialog.confirm({ title: 'Delete this view?', danger: true, confirmLabel: 'Delete' }).then((ok) => {
        if (!ok) return;
        this.views.delete(id).subscribe(() => {
          this.currentView.set(null);
          this.loadViews();
        });
      });
    }
  }

  private applyView(view: SavedViewDto): void {
    const next: Filters = { ...EMPTY };
    for (const c of view.filter?.conditions ?? []) {
      const values = (Array.isArray(c.value) ? c.value : [c.value]).filter((v) => v !== null).map(String);
      switch (c.field) {
        case 'q':
          next.q = values[0] ?? '';
          break;
        case 'status':
        case 'tier':
        case 'region':
        case 'industry':
          next[c.field] = values;
          break;
        case 'groupId':
        case 'accountManagerId':
          next[c.field] = values[0] ?? null;
          break;
        case 'mine':
        case 'hasOpenTickets':
        case 'topLevelOnly':
          next[c.field] = values[0] === 'true';
          break;
      }
    }
    this.filters.set(next);
    this.currentView.set(view);
  }

  private saveView(): void {
    this.dialog
      .open<string, TextPromptData>(TextPromptDialog, { data: { title: 'Save view', label: 'View name', hint: 'Saves the current search and filters (personal view).' }, size: 'sm' })
      .closed.subscribe((name) => {
        if (name) this.createView(name);
      });
  }

  private createView(name: string): void {
    const f = this.filters();
    const conditions: FilterCondition[] = [];
    if (f.q) conditions.push({ field: 'q', op: 'contains', value: f.q });
    for (const key of ['status', 'tier', 'region', 'industry'] as const) if (f[key].length) conditions.push({ field: key, op: 'in', value: f[key] });
    if (f.groupId) conditions.push({ field: 'groupId', op: 'eq', value: f.groupId });
    if (f.accountManagerId) conditions.push({ field: 'accountManagerId', op: 'eq', value: f.accountManagerId });
    for (const key of ['mine', 'hasOpenTickets', 'topLevelOnly'] as const) if (f[key]) conditions.push({ field: key, op: 'eq', value: true });
    const filter: FilterGroup = { logic: 'and', conditions, groups: [] };
    this.views
      .create({ scope: VIEW_SCOPE, name, visibility: 'Personal', filter, isDefault: false, sortOrder: this.savedViews().length })
      .subscribe({
        next: (v) => {
          this.toast.success('View saved', v.name);
          this.loadViews();
          this.currentView.set(v);
        },
        error: (err: unknown) => this.toast.error('Could not save the view', saveErrorMessage(err)),
      });
  }
}
