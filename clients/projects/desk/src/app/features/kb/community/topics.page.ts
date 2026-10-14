import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { CommunityApi, PageResult, TopicListItemDto, TopicQuery } from '@sd/core';
import { SdBadge, SdButton, SdDateTimePipe, SdEmptyState, SdIcon, SdInput, SdRelativeTimePipe, SdSkeleton } from '@sd/ui';
import { KbTopicStatusBadge } from '../components/kb-badges.component';
import { KbPager } from '../components/kb-pager.component';
import { KB_BASE, KB_STYLES, registerKbIcons, remote } from '../kb-common';

const PAGE_SIZE = 20;
const STATUSES: readonly { value: string; label: string }[] = [
  { value: '', label: 'All visible' },
  { value: 'Open', label: 'Open' },
  { value: 'unanswered', label: 'Unanswered' },
  { value: 'Answered', label: 'Answered' },
  { value: 'Closed', label: 'Closed' },
  { value: 'Hidden', label: 'Hidden (moderated)' },
];
const SORTS: readonly { value: string; label: string }[] = [
  { value: 'activity', label: 'Latest activity' },
  { value: 'newest', label: 'Newest' },
  { value: 'votes', label: 'Most votes' },
  { value: 'unanswered', label: 'Unanswered first' },
];

/** /kb/community — moderation queue of community topics with status/search filters (query-param synced). */
@Component({
  selector: 'desk-kb-community-topics',
  imports: [FormsModule, RouterLink, SdButton, SdBadge, SdIcon, SdInput, SdSkeleton, SdEmptyState, SdRelativeTimePipe, SdDateTimePipe, KbTopicStatusBadge, KbPager],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section aria-labelledby="kb-com-title">
      <header class="kb-head">
        <div class="kb-head__titles">
          <h2 id="kb-com-title">Community</h2>
          <p class="kb-head__sub">Answer questions, accept answers and moderate the customer forums.</p>
        </div>
        <div class="kb-head__actions">
          <button sdButton variant="ghost" icon="refresh" type="button" [loading]="result.loading()" (click)="result.reload()">Refresh</button>
        </div>
      </header>

      <div class="kb-toolbar" role="search" aria-label="Filter topics">
        <div class="kb-filter kb-filter--grow">
          <label for="kb-com-q">Search</label>
          <div class="kb-search">
            <sd-icon name="search" [size]="15" />
            <input sdInput id="kb-com-q" type="search" placeholder="Title or content" [ngModel]="searchText()" (ngModelChange)="onSearch($event)" />
          </div>
        </div>
        <div class="kb-filter">
          <label for="kb-com-status">Status</label>
          <select sdInput id="kb-com-status" [ngModel]="status() ?? ''" (ngModelChange)="setParam('status', $event)">
            @for (s of statuses; track s.value) {
              <option [value]="s.value">{{ s.label }}</option>
            }
          </select>
        </div>
        <div class="kb-filter">
          <label for="kb-com-sort">Sort by</label>
          <select sdInput id="kb-com-sort" [ngModel]="sort() || 'activity'" (ngModelChange)="setParam('sort', $event === 'activity' ? null : $event)">
            @for (s of sorts; track s.value) {
              <option [value]="s.value">{{ s.label }}</option>
            }
          </select>
        </div>
        @if (tag()) {
          <div class="tag-filter">
            <sd-badge tone="primary" icon="tag">{{ tag() }}</sd-badge>
            <button sdButton size="sm" variant="ghost" icon="x" iconOnly type="button" aria-label="Remove tag filter" (click)="setParam('tag', null)"></button>
          </div>
        }
      </div>

      @if (result.error(); as err) {
        <div class="kb-error" role="alert">
          <sd-icon name="circle-alert" [size]="16" />
          <p>Could not load topics. {{ err }}</p>
          <button sdButton size="sm" type="button" icon="refresh" (click)="result.reload()">Retry</button>
        </div>
      } @else if (result.loading() && !result.value()) {
        <div class="skeleton" aria-busy="true" aria-label="Loading topics">
          @for (n of [1, 2, 3, 4, 5]; track n) {
            <sd-skeleton height="92px" radius="var(--sd-radius-md)" />
          }
        </div>
      } @else if (items().length === 0) {
        <sd-empty-state icon="messages" [title]="filtered() ? 'No topics match' : 'No community topics yet'" [message]="filtered() ? 'Try another status or search.' : 'Questions customers post in the portal community appear here.'" />
      } @else {
        <ul class="topics" aria-label="Topics" [attr.aria-busy]="result.loading() || null">
          @for (t of items(); track t.id) {
            <li class="topic" [class.is-hidden]="t.status === 'Hidden'">
              <div class="stats" aria-hidden="true">
                <span class="stat"><strong>{{ t.voteScore }}</strong><small>votes</small></span>
                <span class="stat" [class.answered]="t.isAnswered"><strong>{{ t.postCount }}</strong><small>{{ t.postCount === 1 ? 'reply' : 'replies' }}</small></span>
              </div>
              <div class="main">
                <div class="title-row">
                  @if (t.isPinned) {
                    <sd-icon name="pin" [size]="14" label="Pinned" class="pin" />
                  }
                  <a class="title" [routerLink]="[base, 'community', t.id]">{{ t.title }}</a>
                </div>
                <p class="excerpt">{{ t.excerpt }}</p>
                <div class="kb-meta">
                  <desk-kb-topic-status [status]="t.status" />
                  @if (t.isQuestion) {
                    <sd-badge size="sm" variant="outline" icon="help">Question</sd-badge>
                  }
                  @if (t.isAnswered) {
                    <sd-badge size="sm" tone="success" icon="circle-check">Answered</sd-badge>
                  }
                  @if (t.convertedTicketId) {
                    <sd-badge size="sm" tone="info" icon="ticket">Converted to ticket</sd-badge>
                  }
                  <span>
                    <sd-icon name="user" [size]="12" /> {{ t.authorName }}
                    @if (t.authorIsAgent) {
                      <sd-badge size="sm" tone="primary">Agent</sd-badge>
                    }
                  </span>
                  <span><sd-icon name="eye" [size]="12" /> {{ t.viewCount }} views</span>
                  <span class="sd-visually-hidden">{{ t.voteScore }} votes, {{ t.postCount }} replies.</span>
                  <span [attr.title]="t.lastActivityAt | sdDateTime">Active {{ t.lastActivityAt | sdRelativeTime }}</span>
                  @for (tg of t.tags; track tg) {
                    <button type="button" class="tag" (click)="setParam('tag', tg)" [attr.aria-label]="'Filter by tag ' + tg">#{{ tg }}</button>
                  }
                </div>
              </div>
            </li>
          }
        </ul>
      }

      @if (result.value(); as r) {
        @if (r.total > 0) {
          <desk-kb-pager [page]="r.page" [pageSize]="r.pageSize" [total]="r.total" [disabled]="result.loading()" label="Topic pages" (pageChange)="goToPage($event)" />
        }
      }
    </section>
  `,
  styles: [
    KB_STYLES,
    `
      .tag-filter {
        display: flex;
        align-items: center;
        gap: 2px;
        padding-bottom: 4px;
      }
      .skeleton {
        display: grid;
        gap: 6px;
      }
      .topics {
        display: grid;
        gap: 6px;
        margin: 0;
        padding: 0;
        list-style: none;
      }
      .topic {
        display: flex;
        gap: var(--sd-space-4);
        padding: var(--sd-space-3) var(--sd-space-4);
        border: 1px solid var(--sd-border);
        border-radius: var(--sd-radius-md);
        background: var(--sd-surface);
        transition: border-color var(--sd-dur-fast) var(--sd-ease);
      }
      .topic:hover {
        border-color: var(--sd-border-strong);
      }
      .topic.is-hidden {
        background: var(--sd-surface-2);
        border-style: dashed;
      }
      .stats {
        display: flex;
        flex-direction: column;
        gap: 6px;
        flex: none;
        width: 64px;
      }
      .stat {
        display: grid;
        justify-items: center;
        padding: 3px 0;
        border-radius: var(--sd-radius-sm);
        background: var(--sd-surface-2);
        color: var(--sd-text-2);
        line-height: 1.1;
      }
      .stat small {
        font-size: 10px;
        color: var(--sd-text-3);
      }
      .stat.answered {
        background: var(--sd-success-bg);
        color: var(--sd-success);
      }
      .main {
        display: grid;
        gap: 4px;
        flex: 1;
        min-width: 0;
      }
      .title-row {
        display: flex;
        align-items: center;
        gap: 6px;
      }
      .pin {
        color: var(--sd-accent);
      }
      .title {
        color: var(--sd-text);
        font-size: var(--sd-text-md);
        font-weight: var(--sd-weight-semibold);
      }
      .title:hover {
        color: var(--sd-primary);
      }
      .excerpt {
        color: var(--sd-text-2);
        font-size: var(--sd-text-sm);
        display: -webkit-box;
        -webkit-line-clamp: 2;
        -webkit-box-orient: vertical;
        overflow: hidden;
      }
      .tag {
        padding: 0 4px;
        border: 0;
        border-radius: var(--sd-radius-xs);
        background: transparent;
        color: var(--sd-primary);
        font: inherit;
        cursor: pointer;
      }
      .tag:hover {
        background: var(--sd-primary-soft);
      }
      .tag:focus-visible {
        outline: none;
        box-shadow: var(--sd-focus-ring);
      }
      @media (max-width: 560px) {
        .stats {
          display: none;
        }
      }
    `,
  ],
})
export class CommunityTopicsPage {
  readonly q = input<string>();
  readonly status = input<string>();
  readonly sort = input<string>();
  readonly tag = input<string>();
  readonly page = input<string>();

  private readonly api = inject(CommunityApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly base = KB_BASE;
  protected readonly statuses = STATUSES;
  protected readonly sorts = SORTS;
  protected readonly result = remote<PageResult<TopicListItemDto>>();
  protected readonly items = computed(() => this.result.value()?.items ?? []);
  protected readonly searchText = signal('');
  protected readonly filtered = computed(() => !!(this.q() || this.status() || this.tag()));

  private readonly query = computed<TopicQuery>(() => ({
    q: this.q()?.trim() || null,
    status: STATUSES.some((s) => s.value === this.status()) ? this.status() || null : null,
    sort: SORTS.some((s) => s.value === this.sort()) ? (this.sort() ?? null) : null,
    tag: this.tag() || null,
    page: Math.max(1, Number.parseInt(this.page() ?? '1', 10) || 1),
    pageSize: PAGE_SIZE,
  }));

  private lastSentQ: string | undefined;
  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    registerKbIcons();
    effect(() => {
      const query = this.query();
      untracked(() => this.result.load(() => this.api.topics(query), { keepValue: true }));
    });
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

  protected onSearch(text: string): void {
    this.searchText.set(text);
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      const value = text.trim();
      this.lastSentQ = value;
      this.setParam('q', value || null);
    }, 300);
  }

  protected setParam(key: 'q' | 'status' | 'sort' | 'tag', value: string | null): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: { [key]: value || null, page: null }, queryParamsHandling: 'merge', replaceUrl: true });
  }

  protected goToPage(page: number): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: { page: page > 1 ? page : null }, queryParamsHandling: 'merge' });
  }
}
