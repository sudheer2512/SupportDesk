import { ChangeDetectionStrategy, Component, computed, effect, inject, input, linkedSignal, untracked } from '@angular/core';
import { rxResource, takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { PageResult, PagedResult, PortalAuthService, PortalContentApi, TopicListItemDto, cursorList } from '@sd/core';
import { SdButton, SdEmptyState, SdIcon, SdSkeleton } from '@sd/ui';
import { catchError, debounceTime, distinctUntilChanged, map, of } from 'rxjs';
import { BrandService } from '../../brand.service';
import { BreadcrumbComponent } from '../../shared/breadcrumb.component';
import { ErrorStateComponent } from '../../shared/error-state.component';
import { TopicListComponent } from '../../shared/topic-list.component';

const PAGE_SIZE = 20;

interface Choice {
  value: string;
  label: string;
}

const STATUS_CHOICES: Choice[] = [
  { value: '', label: 'All' },
  { value: 'unanswered', label: 'Unanswered' },
  { value: 'Answered', label: 'Answered' },
  { value: 'Open', label: 'Open' },
  { value: 'Closed', label: 'Closed' },
];

const SORT_CHOICES: Choice[] = [
  { value: 'activity', label: 'Recent activity' },
  { value: 'newest', label: 'Newest' },
  { value: 'votes', label: 'Most votes' },
  { value: 'unanswered', label: 'Unanswered first' },
];

/** Community topics with search, tag, status and sort filters (kept in the URL) and "Load more" paging. */
@Component({
  selector: 'portal-community-list-page',
  imports: [RouterLink, SdButton, SdIcon, SdSkeleton, SdEmptyState, BreadcrumbComponent, ErrorStateComponent, TopicListComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="portal-container portal-page">
      <portal-breadcrumb [items]="[{ label: 'Home', link: brand.base() }, { label: 'Community' }]" />
      <header class="portal-page-head head">
        <div>
          <h1>Community</h1>
          <p class="portal-sub">Ask questions, share ideas and learn from other customers and our experts.</p>
        </div>
        <a sdButton variant="primary" icon="message-plus" [routerLink]="newTopicLink()" [queryParams]="newTopicQuery()">Start a topic</a>
      </header>

      <div class="layout">
        <section class="main" aria-labelledby="topics-title">
          <h2 id="topics-title" class="sd-visually-hidden">Topics</h2>
          <div class="filters">
            <form class="search" role="search" aria-label="Search the community" (submit)="$event.preventDefault(); applySearch()">
              <sd-icon name="search" [size]="16" />
              <label for="community-q" class="sd-visually-hidden">Search topics</label>
              <input id="community-q" type="search" class="field" placeholder="Search topics…" [value]="search()" (input)="search.set($any($event.target).value)" />
            </form>
            <div class="selects">
              <label class="select">
                <span>Sort</span>
                <select class="sd-input sd-input-sm" (change)="setParam('sort', $any($event.target).value)">
                  @for (s of sortChoices; track s.value) {
                    <option [value]="s.value" [selected]="s.value === activeSort()">{{ s.label }}</option>
                  }
                </select>
              </label>
              @if (tags().length) {
                <label class="select">
                  <span>Tag</span>
                  <select class="sd-input sd-input-sm" (change)="setParam('tag', $any($event.target).value)">
                    <option value="" [selected]="!tag()">All tags</option>
                    @for (t of tags(); track t) {
                      <option [value]="t" [selected]="t === tag()">#{{ t }}</option>
                    }
                  </select>
                </label>
              }
            </div>
          </div>
          <div class="chips" role="group" aria-label="Filter by status">
            @for (s of statusChoices; track s.value) {
              <button type="button" class="chip" [attr.aria-pressed]="(status() ?? '') === s.value" (click)="setParam('status', s.value)">{{ s.label }}</button>
            }
          </div>

          <p class="sd-visually-hidden" aria-live="polite">{{ announcement() }}</p>

          <div class="panel">
            @if (list.error() && !list.items().length) {
              <portal-error-state [error]="list.error()" (retry)="list.load()" />
            } @else if (list.loading() && !list.items().length) {
              <div class="loading" aria-busy="true" aria-label="Loading topics">
                @for (i of [1, 2, 3, 4, 5]; track i) {
                  <sd-skeleton height="64px" />
                }
              </div>
            } @else if (list.isEmpty()) {
              <sd-empty-state
                icon="messages"
                [title]="filtered() ? 'No topics match these filters' : 'No topics yet'"
                [message]="filtered() ? 'Try other words or clear the filters.' : 'Be the first to start a conversation.'"
              >
                @if (filtered()) {
                  <button sdButton type="button" (click)="clearFilters()">Clear filters</button>
                }
                <a sdButton variant="primary" [routerLink]="newTopicLink()" [queryParams]="newTopicQuery()">Start a topic</a>
              </sd-empty-state>
            } @else {
              <portal-topic-list [topics]="list.items()" />
              @if (list.hasMore()) {
                <div class="more">
                  <button sdButton type="button" [loading]="list.loadingMore()" (click)="list.loadMore()">Load more topics</button>
                  @if (list.total() !== null) {
                    <span class="count">Showing {{ list.items().length }} of {{ list.total() }}</span>
                  }
                </div>
              }
            }
          </div>
        </section>

        <aside class="side" aria-label="About the community">
          @if (!auth.isAuthenticated()) {
            <section class="card">
              <h2 class="card-title"><sd-icon name="log-in" [size]="16" />Join the conversation</h2>
              <p>Anyone can read the community. Sign in to post, reply and vote.</p>
              <a sdButton variant="primary" block [routerLink]="brand.base() + '/login'" [queryParams]="{ returnUrl: brand.base() + '/community' }">Sign in</a>
            </section>
          }
          <section class="card">
            <h2 class="card-title"><sd-icon name="lightbulb" [size]="16" />Before you post</h2>
            <ul class="tips">
              <li>Search first — your question may already be answered.</li>
              <li>Use a clear title and include steps, versions and error messages.</li>
              <li>Mark the reply that solved it as the accepted answer.</li>
              <li>Never post passwords, licence keys or personal data.</li>
            </ul>
          </section>
          @if (tags().length) {
            <section class="card">
              <h2 class="card-title"><sd-icon name="tag" [size]="16" />Tags</h2>
              <ul class="tag-cloud">
                @for (t of tags().slice(0, 24); track t) {
                  <li>
                    <button type="button" class="tag" [attr.aria-pressed]="t === tag()" (click)="setParam('tag', t === tag() ? '' : t)">#{{ t }}</button>
                  </li>
                }
              </ul>
            </section>
          }
        </aside>
      </div>
    </div>
  `,
  styles: `
    .head {
      justify-content: space-between;
    }
    .layout {
      display: grid;
      grid-template-columns: minmax(0, 1fr) 300px;
      gap: 24px;
      align-items: start;
    }
    .filters {
      display: flex;
      flex-wrap: wrap;
      gap: 10px;
      align-items: center;
      margin-bottom: 10px;
    }
    .search {
      display: flex;
      align-items: center;
      gap: 8px;
      flex: 1 1 240px;
      height: var(--sd-control-h);
      padding: 0 12px;
      border: 1px solid var(--sd-border-strong);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      color: var(--sd-text-3);
    }
    .search:focus-within {
      border-color: var(--sd-primary);
      box-shadow: 0 0 0 3px var(--sd-focus);
    }
    .field {
      flex: 1;
      min-width: 0;
      border: 0;
      background: transparent;
      color: var(--sd-text);
      font: inherit;
      outline: none;
    }
    .selects {
      display: flex;
      flex-wrap: wrap;
      gap: 10px;
    }
    .select {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .select select {
      width: auto;
      max-width: 180px;
    }
    .chips {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      margin-bottom: 12px;
    }
    .chip,
    .tag {
      height: 30px;
      padding: 0 12px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface);
      color: var(--sd-text-2);
      font: inherit;
      font-size: var(--sd-text-sm);
      cursor: pointer;
    }
    .chip:hover,
    .tag:hover {
      border-color: var(--sd-primary-border);
      color: var(--sd-text);
    }
    .chip[aria-pressed='true'],
    .tag[aria-pressed='true'] {
      border-color: var(--sd-primary);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
      font-weight: var(--sd-weight-medium);
    }
    .chip:focus-visible,
    .tag:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .panel {
      padding: 4px 16px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    .loading {
      display: grid;
      gap: 12px;
      padding: 14px 0;
    }
    .more {
      display: flex;
      align-items: center;
      gap: 12px;
      justify-content: center;
      padding: 14px 0;
      border-top: 1px solid var(--sd-divider);
    }
    .count {
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
    .side {
      display: grid;
      gap: 14px;
    }
    .card {
      display: grid;
      gap: 10px;
      padding: 16px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    .card p {
      margin: 0;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .card-title {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      font-size: var(--sd-text-md);
    }
    .card-title sd-icon {
      color: var(--sd-primary);
    }
    .tips {
      display: grid;
      gap: 6px;
      margin: 0;
      padding-left: 18px;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .tag-cloud {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .tag {
      height: 26px;
      padding: 0 10px;
      font-size: var(--sd-text-xs);
    }
    @media (max-width: 900px) {
      .layout {
        grid-template-columns: minmax(0, 1fr);
      }
    }
  `,
})
export class CommunityListPage {
  readonly q = input<string | undefined>();
  readonly tag = input<string | undefined>();
  readonly sort = input<string | undefined>();
  readonly status = input<string | undefined>();

  protected readonly brand = inject(BrandService);
  protected readonly auth = inject(PortalAuthService);
  private readonly api = inject(PortalContentApi);
  private readonly router = inject(Router);

  protected readonly statusChoices = STATUS_CHOICES;
  protected readonly sortChoices = SORT_CHOICES;
  protected readonly search = linkedSignal(() => this.q() ?? '');
  protected readonly activeSort = computed(() => this.sort() || 'activity');
  protected readonly filtered = computed(() => !!(this.q() || this.tag() || this.status()));
  protected readonly list = cursorList<TopicListItemDto>(() => of({ items: [], nextCursor: null }));

  private readonly tagsRes = rxResource({
    params: () => this.brand.slug() ?? undefined,
    stream: ({ params }) => this.api.communityTags(params).pipe(catchError(() => of<string[]>([]))),
  });
  protected readonly tags = computed(() => (this.tagsRes.hasValue() ? this.tagsRes.value() : []));

  protected readonly newTopicLink = computed(() => (this.auth.isAuthenticated() ? `${this.brand.base()}/community/new` : `${this.brand.base()}/login`));
  protected readonly newTopicQuery = computed(() => (this.auth.isAuthenticated() ? {} : { returnUrl: `${this.brand.base()}/community/new` }));
  protected readonly announcement = computed(() => {
    if (this.list.loading()) return 'Loading topics…';
    if (!this.list.loaded()) return '';
    const total = this.list.total() ?? this.list.items().length;
    return total === 1 ? '1 topic' : `${total} topics`;
  });

  constructor() {
    effect(() => {
      const slug = this.brand.slug();
      if (!slug) return;
      const query = {
        q: this.q() || null,
        tag: this.tag() || null,
        status: this.status() || null,
        sort: this.activeSort(),
      };
      this.auth.isAuthenticated();
      untracked(() =>
        this.list.setFetcher((cursor) =>
          this.api.topics(slug, { ...query, page: cursor ? Number(cursor) : 1, pageSize: PAGE_SIZE }).pipe(map(toCursorPage)),
        ),
      );
    });

    toObservable(this.search)
      .pipe(
        map((q) => q.trim()),
        debounceTime(400),
        distinctUntilChanged(),
        takeUntilDestroyed(),
      )
      .subscribe((q) => {
        if (q !== (this.q() ?? '')) this.setParam('q', q);
      });
  }

  protected applySearch(): void {
    this.setParam('q', this.search().trim());
  }

  protected setParam(key: 'q' | 'tag' | 'sort' | 'status', value: string): void {
    void this.router.navigate([], {
      queryParams: { [key]: value || null },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  protected clearFilters(): void {
    this.search.set('');
    void this.router.navigate([], { queryParams: { q: null, tag: null, status: null }, queryParamsHandling: 'merge', replaceUrl: true });
  }
}

/** Adapts the offset-paged community API to the keyset CursorList (cursor = next page number). */
function toCursorPage(page: PageResult<TopicListItemDto>): PagedResult<TopicListItemDto> {
  const more = page.page * page.pageSize < page.total;
  return { items: page.items, nextCursor: more ? String(page.page + 1) : null, total: page.total };
}
