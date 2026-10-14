import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { PortalAuthService, PortalContentApi, PortalSearchResultDto } from '@sd/core';
import { SdButton, SdEmptyState, SdIcon, SdSkeleton } from '@sd/ui';
import { BrandService } from '../../brand.service';
import { ArticleListComponent } from '../../shared/article-list.component';
import { BreadcrumbComponent } from '../../shared/breadcrumb.component';
import { ErrorStateComponent } from '../../shared/error-state.component';
import { SearchBoxComponent } from '../../shared/search-box.component';
import { TopicListComponent } from '../../shared/topic-list.component';

/** Help-center search results: articles (highlighted snippets) and matching community topics. */
@Component({
  selector: 'portal-search-page',
  imports: [
    RouterLink,
    SdIcon,
    SdButton,
    SdSkeleton,
    SdEmptyState,
    BreadcrumbComponent,
    SearchBoxComponent,
    ArticleListComponent,
    TopicListComponent,
    ErrorStateComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="portal-container portal-page">
      <portal-breadcrumb [items]="[{ label: 'Home', link: brand.base() }, { label: 'Search' }]" />
      <header class="portal-page-head head">
        <h1>{{ query() ? 'Search results' : 'Search the help center' }}</h1>
        <portal-search-box class="search" [initial]="query()" [suggest]="false" />
      </header>

      <p class="sd-visually-hidden" aria-live="polite">{{ announcement() }}</p>

      @if (!query()) {
        <sd-empty-state icon="search" title="What are you looking for?" message="Type a few words about your question — for example an error message or a feature name." />
      } @else if (res.error()) {
        <portal-error-state [error]="res.error()" errorTitle="Search is unavailable right now" (retry)="res.reload()" />
      } @else if (result(); as r) {
        @if (r.articles.length || r.topics.length) {
          <p class="summary">
            {{ r.total }} {{ r.total === 1 ? 'result' : 'results' }} for <strong>“{{ r.query }}”</strong>
          </p>
          <div class="layout" [class.single]="!r.topics.length || !r.articles.length">
            @if (r.articles.length) {
              <section class="panel" aria-labelledby="res-articles">
                <h2 id="res-articles" class="panel-title"><sd-icon name="book-open" [size]="17" />Articles <span class="count">{{ r.articles.length }}</span></h2>
                <portal-article-list [articles]="r.articles" showMeta />
              </section>
            }
            @if (r.topics.length) {
              <section class="panel" aria-labelledby="res-topics">
                <h2 id="res-topics" class="panel-title"><sd-icon name="messages" [size]="17" />Community <span class="count">{{ r.topics.length }}</span></h2>
                <portal-topic-list [topics]="r.topics" compact />
              </section>
            }
          </div>
          <div class="still">
            <span>Didn't find what you need?</span>
            <a sdButton variant="primary" size="sm" icon="file-plus" [routerLink]="brand.base() + '/submit'">Submit a ticket</a>
            @if (brand.features().community) {
              <a sdButton size="sm" icon="message-plus" [routerLink]="brand.base() + '/community/new'">Ask the community</a>
            }
          </div>
        } @else {
          <sd-empty-state icon="search" [title]="'No results for “' + r.query + '”'" message="Check the spelling, try fewer or different words, or get in touch with us.">
            @if (brand.features().kb) {
              <a sdButton [routerLink]="brand.base() + '/kb'" icon="book-open">Browse articles</a>
            }
            @if (brand.features().community) {
              <a sdButton [routerLink]="brand.base() + '/community/new'" icon="message-plus">Ask the community</a>
            }
            <a sdButton variant="primary" [routerLink]="brand.base() + '/submit'" icon="file-plus">Submit a ticket</a>
          </sd-empty-state>
        }
      } @else {
        <div class="loading" aria-busy="true" aria-label="Searching">
          <sd-skeleton width="200px" />
          <sd-skeleton height="80px" />
          <sd-skeleton height="80px" />
          <sd-skeleton height="80px" />
        </div>
      }
    </div>
  `,
  styles: `
    .head {
      justify-content: space-between;
    }
    .search {
      width: min(520px, 100%);
    }
    .summary {
      margin: 0 0 14px;
      color: var(--sd-text-2);
    }
    .layout {
      display: grid;
      grid-template-columns: minmax(0, 3fr) minmax(0, 2fr);
      gap: 20px;
      align-items: start;
    }
    .layout.single {
      grid-template-columns: minmax(0, 1fr);
      max-width: 820px;
    }
    .panel {
      padding: 16px 14px 8px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    .panel-title {
      display: flex;
      align-items: center;
      gap: 8px;
      margin: 0 0 6px 6px;
      font-size: var(--sd-text-lg);
    }
    .panel-title sd-icon {
      color: var(--sd-primary);
    }
    .count {
      padding: 0 8px;
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface-3);
      color: var(--sd-text-2);
      font-size: var(--sd-text-xs);
      line-height: 20px;
    }
    .still {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 10px;
      margin-top: 22px;
      padding: 14px 16px;
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface-2);
      color: var(--sd-text-2);
    }
    .still span {
      margin-right: auto;
    }
    .loading {
      display: grid;
      gap: 14px;
    }
    @media (max-width: 860px) {
      .layout {
        grid-template-columns: minmax(0, 1fr);
      }
    }
  `,
})
export class SearchPage {
  readonly q = input<string | undefined>();

  protected readonly brand = inject(BrandService);
  private readonly auth = inject(PortalAuthService);
  private readonly api = inject(PortalContentApi);

  protected readonly query = computed(() => (this.q() ?? '').trim());
  protected readonly res = rxResource({
    params: () => {
      const slug = this.brand.slug();
      const q = this.query();
      return slug && q ? { slug, q, signedIn: this.auth.isAuthenticated() } : undefined;
    },
    stream: ({ params }) => this.api.search(params.slug, params.q),
  });
  protected readonly result = computed<PortalSearchResultDto | null>(() => (this.res.hasValue() ? this.res.value() : null));
  protected readonly announcement = computed(() => {
    const r = this.result();
    if (!this.query()) return '';
    if (this.res.isLoading()) return 'Searching…';
    if (!r) return '';
    return r.total ? `${r.total} results for ${r.query}` : `No results for ${r.query}`;
  });

  constructor() {
    effect(() => {
      const q = this.query();
      this.brand.setPageTitle(q ? `Search: ${q}` : 'Search');
    });
  }
}
