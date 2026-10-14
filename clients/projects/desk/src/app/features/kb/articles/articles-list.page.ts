import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, computed, effect, inject, input, signal, untracked, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  ArticleListItemDto,
  ArticleQuery,
  ArticleStatus,
  ArticleType,
  KbImportResultDto,
  KbVisibility,
  KnowledgeApi,
  MeStore,
  PageResult,
  ToastBridge,
  saveBlob,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCellDef,
  SdColumn,
  SdCombobox,
  SdDataTable,
  SdDateTimePipe,
  SdDialogService,
  SdDropdownMenu,
  SdIcon,
  SdInput,
  SdMenuItem,
  SdRelativeTimePipe,
  SdToggle,
} from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { KbStatusBadge, KbVisibilityBadge } from '../components/kb-badges.component';
import { KbPager } from '../components/kb-pager.component';
import {
  ALL_ARTICLE_STATUSES,
  ALL_ARTICLE_TYPES,
  ALL_VISIBILITIES,
  ARTICLE_STATUS_LABEL,
  ARTICLE_TYPE_LABEL,
  KB_BASE,
  KB_STYLES,
  VISIBILITY_META,
  comboOne,
  errorMessage,
  fileStamp,
  formatPercent,
  helpfulPercent,
  isPast,
  localeLabel,
  remote,
  toastedGlobally,
} from '../kb-common';
import { KbStore } from '../kb.store';
import { ImportResultDialog } from './import-result.dialog';

const PAGE_SIZE = 25;

const SORTS: readonly { value: string; label: string }[] = [
  { value: 'updated', label: 'Recently updated' },
  { value: 'title', label: 'Title (A–Z)' },
  { value: 'views', label: 'Most viewed' },
  { value: 'helpful', label: 'Most helpful' },
  { value: 'reviewDue', label: 'Review due soonest' },
];

type FilterKey = 'q' | 'status' | 'visibility' | 'category' | 'locale' | 'owner' | 'type' | 'product' | 'tag' | 'reviewDue' | 'live' | 'sort';

function pick<T extends string>(value: string | undefined, allowed: readonly T[]): T | null {
  return value && (allowed as readonly string[]).includes(value) ? (value as T) : null;
}

function flag(value: string | undefined): boolean {
  return value === '1' || value === 'true';
}

/** /kb — article list with query-param-synced filters, paging, export and import. */
@Component({
  selector: 'desk-kb-articles-list',
  imports: [
    FormsModule,
    RouterLink,
    SdButton,
    SdIcon,
    SdBadge,
    SdInput,
    SdCombobox,
    SdToggle,
    SdDataTable,
    SdCellDef,
    SdDropdownMenu,
    SdRelativeTimePipe,
    SdDateTimePipe,
    KbStatusBadge,
    KbVisibilityBadge,
    KbPager,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section aria-labelledby="kb-articles-title">
      <header class="kb-head">
        <div class="kb-head__titles">
          <h2 id="kb-articles-title">Articles</h2>
          <p class="kb-head__sub">Draft, review and publish help-center and internal articles.</p>
        </div>
        <div class="kb-head__actions">
          @if (selection().length) {
            <span class="sd-small sd-muted">{{ selection().length }} selected</span>
            <button sdButton variant="ghost" size="sm" type="button" (click)="selection.set([])">Clear selection</button>
          }
          <sd-dropdown-menu label="Import / export" icon="file-down" [items]="menuItems()" align="end" [disabled]="busy()" />
          @if (canAuthor()) {
            <a sdButton variant="primary" icon="plus" [routerLink]="newLink">New article</a>
          }
        </div>
      </header>
      <input #importInput type="file" class="sd-visually-hidden" accept=".json,.zip,.md,.markdown,.html,.htm" tabindex="-1" aria-hidden="true" (change)="onImportFile($event)" />

      <div class="kb-toolbar" role="search" aria-label="Filter articles">
        <div class="kb-filter kb-filter--grow">
          <label for="kb-q">Search</label>
          <div class="kb-search">
            <sd-icon name="search" [size]="15" />
            <input sdInput id="kb-q" type="search" placeholder="Title, content or keywords" [ngModel]="searchText()" (ngModelChange)="onSearch($event)" />
          </div>
        </div>
        <div class="kb-filter">
          <label for="kb-status">Status</label>
          <select sdInput id="kb-status" [ngModel]="status() ?? ''" (ngModelChange)="setFilter('status', $event)">
            <option value="">Any status</option>
            @for (s of statuses; track s) {
              <option [value]="s">{{ statusLabel[s] }}</option>
            }
          </select>
        </div>
        <div class="kb-filter">
          <label for="kb-visibility">Visibility</label>
          <select sdInput id="kb-visibility" [ngModel]="visibility() ?? ''" (ngModelChange)="setFilter('visibility', $event)">
            <option value="">Any visibility</option>
            @for (v of visibilities; track v) {
              <option [value]="v">{{ visibilityMeta[v].label }}</option>
            }
          </select>
        </div>
        <div class="kb-filter">
          <span class="kb-filter__label" id="kb-category-label">Category</span>
          <sd-combobox
            ariaLabel="Category"
            placeholder="Any category"
            [options]="store.categoryOptions()"
            [value]="category() ?? null"
            (valueChange)="setFilter('category', comboOne($event))"
          />
        </div>
        <div class="kb-filter">
          <label for="kb-sort">Sort by</label>
          <select sdInput id="kb-sort" [ngModel]="sort() || 'updated'" (ngModelChange)="setFilter('sort', $event === 'updated' ? null : $event)">
            @for (s of sorts; track s.value) {
              <option [value]="s.value">{{ s.label }}</option>
            }
          </select>
        </div>
        <button
          sdButton
          variant="ghost"
          type="button"
          icon="sliders"
          [attr.aria-expanded]="moreOpen()"
          aria-controls="kb-more-filters"
          (click)="showMore.set(!moreOpen())"
        >
          More filters
          @if (secondaryCount()) {
            <sd-badge tone="primary" size="sm">{{ secondaryCount() }}</sd-badge>
          }
        </button>
        @if (anyFilter()) {
          <button sdButton variant="link" type="button" (click)="clearFilters()">Clear all</button>
        }
      </div>

      @if (moreOpen()) {
        <div class="kb-toolbar more" id="kb-more-filters">
          <div class="kb-filter">
            <label for="kb-locale">Locale</label>
            <select sdInput id="kb-locale" [ngModel]="locale() ?? ''" (ngModelChange)="setFilter('locale', $event)">
              <option value="">Any locale</option>
              @for (l of store.options().locales; track l) {
                <option [value]="l">{{ localeName(l) }}</option>
              }
            </select>
          </div>
          <div class="kb-filter">
            <span class="kb-filter__label">Owner</span>
            <sd-combobox ariaLabel="Owner" placeholder="Anyone" [options]="store.ownerOptions()" [value]="owner() ?? null" (valueChange)="setFilter('owner', comboOne($event))" />
          </div>
          <div class="kb-filter">
            <label for="kb-type">Article type</label>
            <select sdInput id="kb-type" [ngModel]="type() ?? ''" (ngModelChange)="setFilter('type', $event)">
              <option value="">Any type</option>
              @for (t of types; track t) {
                <option [value]="t">{{ typeLabel[t] }}</option>
              }
            </select>
          </div>
          <div class="kb-filter">
            <span class="kb-filter__label">Product</span>
            <sd-combobox ariaLabel="Product" placeholder="Any product" [options]="store.productOptions()" [value]="product() ?? null" (valueChange)="setFilter('product', comboOne($event))" />
          </div>
          <div class="kb-filter">
            <span class="kb-filter__label">Tag</span>
            <sd-combobox ariaLabel="Tag" placeholder="Any tag" [options]="store.tagOptions()" [value]="tag() ?? null" (valueChange)="setFilter('tag', comboOne($event))" />
          </div>
          <div class="toggles">
            <sd-toggle [value]="reviewDueOnly()" (valueChange)="setFilter('reviewDue', $event ? '1' : null)">Review due</sd-toggle>
            <sd-toggle [value]="liveOnly()" (valueChange)="setFilter('live', $event ? '1' : null)">Live only</sd-toggle>
          </div>
        </div>
      }

      @if (result.error(); as err) {
        <div class="kb-error" role="alert">
          <sd-icon name="circle-alert" [size]="16" />
          <p>Could not load articles. {{ err }}</p>
          <button sdButton size="sm" type="button" icon="refresh" (click)="result.reload()">Retry</button>
        </div>
      }

      <div class="table-host">
        <sd-data-table
          [columns]="columns"
          [rows]="rows()"
          storageKey="kb.articles"
          selectable
          [(selection)]="selection"
          [loading]="result.loading()"
          [total]="total()"
          [emptyTitle]="anyFilter() ? 'No articles match these filters' : 'No articles yet'"
          [emptyMessage]="anyFilter() ? 'Try removing a filter or searching for something else.' : 'Create the first article, import existing content or turn a resolved ticket into an article.'"
          emptyIcon="file-text"
          ariaLabel="Articles"
          (rowClick)="open($event)"
        >
          <ng-template sdCell="title" [sdCellRows]="rows()" let-row>
            <span class="title-cell">
              <a class="title sd-truncate" [routerLink]="[base, 'articles', row.id]" (click)="$event.stopPropagation()" [attr.title]="row.title">{{ row.title }}</a>
              @if (row.isLive) {
                <sd-badge tone="success" size="sm" icon="globe">Live</sd-badge>
              }
              @if (row.hasPendingChanges) {
                <sd-badge tone="warning" size="sm" variant="outline">Pending changes</sd-badge>
              }
            </span>
          </ng-template>
          <ng-template sdCell="status" [sdCellRows]="rows()" let-row><desk-kb-status [status]="row.status" size="sm" /></ng-template>
          <ng-template sdCell="visibility" [sdCellRows]="rows()" let-row><desk-kb-visibility [visibility]="row.visibility" /></ng-template>
          <ng-template sdCell="type" [sdCellRows]="rows()" let-row><span class="sd-truncate">{{ typeLabel[row.articleType] }}</span></ng-template>
          <ng-template sdCell="category" [sdCellRows]="rows()" let-row>
            <span class="sd-truncate" [class.sd-muted]="!row.categoryName">{{ row.categoryName ?? 'Uncategorised' }}</span>
          </ng-template>
          <ng-template sdCell="owner" [sdCellRows]="rows()" let-row>
            <span class="sd-truncate" [class.sd-muted]="!row.ownerName">{{ row.ownerName ?? 'Unassigned' }}</span>
          </ng-template>
          <ng-template sdCell="locales" [sdCellRows]="rows()" let-row>
            <span class="locales" [attr.title]="localesTitle(row)">
              <sd-badge size="sm" variant="outline">{{ row.primaryLocale }}</sd-badge>
              @if (row.translationLocales.length) {
                <span class="sd-xs sd-muted">+{{ row.translationLocales.length }}</span>
              }
            </span>
          </ng-template>
          <ng-template sdCell="views" [sdCellRows]="rows()" let-row><span class="sd-num">{{ row.viewCount.toLocaleString() }}</span></ng-template>
          <ng-template sdCell="helpful" [sdCellRows]="rows()" let-row>
            <span class="sd-num" [attr.title]="row.helpfulCount + ' helpful · ' + row.notHelpfulCount + ' not helpful'">{{ helpful(row) }}</span>
          </ng-template>
          <ng-template sdCell="reviewDue" [sdCellRows]="rows()" let-row>
            @if (row.reviewDueAt) {
              @if (overdue(row.reviewDueAt)) {
                <sd-badge tone="danger" size="sm" icon="clock">{{ row.reviewDueAt | sdDateTime: 'date' : 'short' }}</sd-badge>
                <span class="sd-visually-hidden">overdue</span>
              } @else {
                <span class="sd-small">{{ row.reviewDueAt | sdDateTime: 'date' : 'short' }}</span>
              }
            } @else {
              <span class="sd-muted">—</span>
            }
          </ng-template>
          <ng-template sdCell="updated" [sdCellRows]="rows()" let-row>
            <span class="sd-small sd-muted" [attr.title]="row.updatedAt | sdDateTime">{{ row.updatedAt | sdRelativeTime }}</span>
          </ng-template>
          <div sdTableEmpty>
            @if (anyFilter()) {
              <button sdButton type="button" (click)="clearFilters()">Clear filters</button>
            } @else if (canAuthor()) {
              <a sdButton variant="primary" icon="plus" [routerLink]="newLink">New article</a>
            }
          </div>
        </sd-data-table>
      </div>

      @if (result.value(); as r) {
        <desk-kb-pager [page]="r.page" [pageSize]="r.pageSize" [total]="r.total" [disabled]="result.loading()" label="Article pages" (pageChange)="goToPage($event)" />
      }
    </section>
  `,
  styles: [
    KB_STYLES,
    `
      :host {
        display: block;
      }
      .more {
        padding: var(--sd-space-3);
        border: 1px dashed var(--sd-border);
        border-radius: var(--sd-radius-md);
        background: var(--sd-surface-2);
      }
      .toggles {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sd-space-4);
        align-self: center;
        padding-top: 18px;
      }
      .table-host {
        height: clamp(380px, calc(100dvh - 400px), 1300px);
      }
      .title-cell {
        display: flex;
        align-items: center;
        gap: 6px;
        min-width: 0;
      }
      .title {
        color: var(--sd-text);
        font-weight: var(--sd-weight-medium);
      }
      .title:hover {
        color: var(--sd-primary);
      }
      .locales {
        display: inline-flex;
        align-items: center;
        gap: 4px;
      }
    `,
  ],
})
export class ArticlesListPage {
  // Query params (component input binding).
  readonly q = input<string>();
  readonly status = input<string>();
  readonly visibility = input<string>();
  readonly category = input<string>();
  readonly locale = input<string>();
  readonly owner = input<string>();
  readonly type = input<string>();
  readonly product = input<string>();
  readonly tag = input<string>();
  readonly reviewDue = input<string>();
  readonly live = input<string>();
  readonly sort = input<string>();
  readonly page = input<string>();

  protected readonly store = inject(KbStore);
  private readonly api = inject(KnowledgeApi);
  private readonly me = inject(MeStore);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly toasts = inject(ToastBridge);
  private readonly dialogs = inject(SdDialogService);
  private readonly importInput = viewChild.required<ElementRef<HTMLInputElement>>('importInput');

  protected readonly base = KB_BASE;
  protected readonly newLink = `${KB_BASE}/articles/new`;
  protected readonly statuses = ALL_ARTICLE_STATUSES;
  protected readonly statusLabel = ARTICLE_STATUS_LABEL;
  protected readonly visibilities = ALL_VISIBILITIES;
  protected readonly visibilityMeta = VISIBILITY_META;
  protected readonly types = ALL_ARTICLE_TYPES;
  protected readonly typeLabel = ARTICLE_TYPE_LABEL;
  protected readonly sorts = SORTS;
  protected readonly comboOne = comboOne;

  protected readonly result = remote<PageResult<ArticleListItemDto>>();
  protected readonly rows = computed(() => this.result.value()?.items ?? []);
  protected readonly total = computed(() => this.result.value()?.total ?? null);
  protected readonly selection = signal<string[]>([]);
  protected readonly searchText = signal('');
  protected readonly showMore = signal(false);
  protected readonly exporting = signal(false);
  protected readonly importing = signal(false);
  protected readonly busy = computed(() => this.exporting() || this.importing());
  protected readonly canAuthor = computed(() => this.me.can('kb.author'));

  protected readonly reviewDueOnly = computed(() => flag(this.reviewDue()));
  protected readonly liveOnly = computed(() => flag(this.live()));
  protected readonly secondaryCount = computed(
    () =>
      [this.locale(), this.owner(), this.type(), this.product(), this.tag()].filter(Boolean).length +
      (this.reviewDueOnly() ? 1 : 0) +
      (this.liveOnly() ? 1 : 0),
  );
  protected readonly moreOpen = computed(() => this.showMore() || this.secondaryCount() > 0);
  protected readonly anyFilter = computed(
    () => !!(this.q() || this.status() || this.visibility() || this.category()) || this.secondaryCount() > 0,
  );

  private readonly query = computed<ArticleQuery>(() => ({
    q: this.q()?.trim() || null,
    status: pick<ArticleStatus>(this.status(), ALL_ARTICLE_STATUSES),
    visibility: pick<KbVisibility>(this.visibility(), ALL_VISIBILITIES),
    categoryId: this.category() || null,
    locale: this.locale() || null,
    ownerUserId: this.owner() || null,
    articleType: pick<ArticleType>(this.type(), ALL_ARTICLE_TYPES),
    productId: this.product() || null,
    tag: this.tag() || null,
    reviewDue: this.reviewDueOnly() || null,
    live: this.liveOnly() || null,
    sort: SORTS.some((s) => s.value === this.sort()) ? (this.sort() ?? null) : null,
    page: Math.max(1, Number.parseInt(this.page() ?? '1', 10) || 1),
    pageSize: PAGE_SIZE,
  }));

  protected readonly menuItems = computed<SdMenuItem[]>(() => {
    const scope = this.selection().length ? `${this.selection().length} selected` : 'all';
    const items: SdMenuItem[] = [
      { id: 'h-export', heading: true, label: `Export (${scope})` },
      { id: 'export-json', label: 'Export as JSON', icon: 'file-down', run: () => this.export('json') },
      { id: 'export-zip', label: 'Export as ZIP (Markdown + HTML)', icon: 'file-down', run: () => this.export('zip') },
    ];
    if (this.canAuthor()) {
      items.push(
        { id: 'd1', divider: true },
        { id: 'import', label: 'Import articles…', icon: 'file-up', hint: '.json .zip .md .html', run: () => this.importInput().nativeElement.click() },
      );
    }
    return items;
  });

  private lastSentQ: string | undefined = undefined;
  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    this.store.ensureOptions();
    this.store.ensureCategories();
    effect(() => {
      const query = this.query();
      untracked(() => this.result.load(() => this.api.articles(query), { keepValue: true }));
    });
    // Keep the search box in sync with ?q= when it changes from outside (back/forward, links).
    effect(() => {
      const q = this.q() ?? '';
      untracked(() => {
        if (q !== (this.lastSentQ ?? '')) {
          this.searchText.set(q);
          this.lastSentQ = q;
        }
      });
    });
    inject(DestroyRef).onDestroy(() => {
      if (this.searchTimer) clearTimeout(this.searchTimer);
    });
  }

  protected localeName(tag: string): string {
    return localeLabel(tag);
  }

  protected helpful(row: ArticleListItemDto): string {
    return formatPercent(helpfulPercent(row.helpfulCount, row.notHelpfulCount));
  }

  protected overdue(iso: string): boolean {
    return isPast(iso);
  }

  protected localesTitle(row: ArticleListItemDto): string {
    return [row.primaryLocale, ...row.translationLocales].map((l) => localeLabel(l)).join(', ');
  }

  protected onSearch(text: string): void {
    this.searchText.set(text);
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      const value = text.trim();
      this.lastSentQ = value;
      this.navigate({ q: value || null, page: null });
    }, 300);
  }

  protected setFilter(key: FilterKey, value: string | null): void {
    this.navigate({ [key]: value || null, page: null });
  }

  protected clearFilters(): void {
    this.searchText.set('');
    this.lastSentQ = '';
    this.showMore.set(false);
    void this.router.navigate([], { relativeTo: this.route, queryParams: {}, replaceUrl: true });
  }

  protected goToPage(page: number): void {
    this.navigate({ page: page > 1 ? String(page) : null });
  }

  protected open(row: ArticleListItemDto): void {
    void this.router.navigate([KB_BASE, 'articles', row.id]);
  }

  private navigate(params: Record<string, string | null>): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: params, queryParamsHandling: 'merge', replaceUrl: true });
  }

  private export(format: 'json' | 'zip'): void {
    const ids = this.selection();
    this.exporting.set(true);
    this.api.export(format, ids.length ? ids : undefined).subscribe({
      next: (blob) => {
        this.exporting.set(false);
        saveBlob(blob, `knowledge-base-${fileStamp()}.${format}`);
        this.toasts.success('Export ready', ids.length ? `${ids.length} article${ids.length === 1 ? '' : 's'} exported.` : 'All articles exported.');
      },
      error: (err: unknown) => {
        this.exporting.set(false);
        if (!toastedGlobally(err)) this.toasts.error('Export failed', errorMessage(err));
      },
    });
  }

  protected onImportFile(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    this.importing.set(true);
    this.toasts.info('Importing…', file.name);
    this.api.import(file).subscribe({
      next: (result) => {
        this.importing.set(false);
        this.store.invalidate();
        this.store.ensureCategories();
        this.store.ensureOptions();
        this.result.reload();
        void this.showImportResult(result);
      },
      error: (err: unknown) => {
        this.importing.set(false);
        this.toasts.error('Import failed', errorMessage(err));
      },
    });
  }

  private async showImportResult(result: KbImportResultDto): Promise<void> {
    const ref = this.dialogs.open<'open' | undefined, KbImportResultDto, ImportResultDialog>(ImportResultDialog, { data: result, size: 'md' });
    const action = await firstValueFrom(ref.closed);
    const first = result.articleIds[0];
    if (action === 'open' && first) void this.router.navigate([KB_BASE, 'articles', first]);
  }

  protected readonly columns: SdColumn<ArticleListItemDto>[] = [
    { key: 'title', header: 'Title', locked: true, minWidth: 300, flex: 3 },
    { key: 'status', header: 'Status', width: 130 },
    { key: 'visibility', header: 'Visibility', width: 170 },
    { key: 'type', header: 'Type', width: 130, hiddenByDefault: true },
    { key: 'category', header: 'Category', minWidth: 150 },
    { key: 'owner', header: 'Owner', minWidth: 140 },
    { key: 'locales', header: 'Locales', width: 110 },
    { key: 'views', header: 'Views', width: 90, align: 'end' },
    { key: 'helpful', header: 'Helpful', width: 96, align: 'end' },
    { key: 'reviewDue', header: 'Review due', width: 140 },
    { key: 'updated', header: 'Updated', width: 120 },
  ];
}
