import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { PortalArticleSummaryDto, PortalAuthService, PortalCategoryDto, PortalCategoryPageDto, PortalContentApi } from '@sd/core';
import { SdButton, SdEmptyState, SdIcon, SdSkeleton, hasIcon } from '@sd/ui';
import { BrandService } from '../../brand.service';
import { ArticleListComponent } from '../../shared/article-list.component';
import { BreadcrumbComponent, Crumb } from '../../shared/breadcrumb.component';
import { ErrorStateComponent } from '../../shared/error-state.component';

type ArticleSort = 'popular' | 'recent' | 'title';

/** One knowledge-base category: breadcrumb, sub-categories and its articles (sortable). */
@Component({
  selector: 'portal-kb-category-page',
  imports: [FormsModule, RouterLink, SdIcon, SdSkeleton, SdEmptyState, SdButton, BreadcrumbComponent, ArticleListComponent, ErrorStateComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="portal-container portal-page">
      @if (res.error()) {
        <portal-breadcrumb [items]="[{ label: 'Home', link: brand.base() }, { label: 'Knowledge Base', link: brand.base() + '/kb' }, { label: 'Category' }]" />
        <portal-error-state
          [error]="res.error()"
          notFoundTitle="Category not found"
          notFoundMessage="This category does not exist or is not available to you."
          (retry)="res.reload()"
        >
          <a sdButton [routerLink]="brand.base() + '/kb'">Browse the knowledge base</a>
        </portal-error-state>
      } @else if (page(); as p) {
        <portal-breadcrumb [items]="crumbs()" />
        <header class="portal-page-head head">
          <span class="ic" aria-hidden="true"><sd-icon [name]="icon(p.category)" [size]="22" /></span>
          <div class="titles">
            <h1>{{ p.category.name }}</h1>
            @if (p.category.description) {
              <p class="portal-sub">{{ p.category.description }}</p>
            }
          </div>
        </header>

        @if (p.category.children.length) {
          <section aria-labelledby="subs-title" class="block">
            <h2 id="subs-title" class="block-title">Sections</h2>
            <ul class="subs">
              @for (s of p.category.children; track s.id) {
                <li>
                  <a class="sub" [routerLink]="[brand.base(), 'kb', 'c', s.slug]">
                    <sd-icon [name]="icon(s)" [size]="18" />
                    <span class="sub-text">
                      <span class="sub-name">{{ s.name }}</span>
                      <span class="sub-count">{{ s.articleCount }} {{ s.articleCount === 1 ? 'article' : 'articles' }}</span>
                    </span>
                    <sd-icon name="chevron-right" [size]="16" class="go" />
                  </a>
                </li>
              }
            </ul>
          </section>
        }

        <section aria-labelledby="articles-title" class="block panel">
          <div class="list-head">
            <h2 id="articles-title" class="block-title">{{ p.articles.length }} {{ p.articles.length === 1 ? 'article' : 'articles' }}</h2>
            @if (p.articles.length > 1) {
              <label class="sort">
                <span>Sort by</span>
                <select class="sd-input sd-input-sm" [ngModel]="sort()" (ngModelChange)="sort.set($event)" name="sort">
                  <option value="popular">Most viewed</option>
                  <option value="recent">Recently updated</option>
                  <option value="title">Title (A–Z)</option>
                </select>
              </label>
            }
          </div>
          @if (sorted().length) {
            <portal-article-list [articles]="sorted()" showMeta />
          } @else {
            <sd-empty-state icon="file-text" title="No articles here yet" message="Try another section, or search the knowledge base." compact>
              <a sdButton [routerLink]="brand.base() + '/kb'">All categories</a>
            </sd-empty-state>
          }
        </section>
      } @else {
        <div aria-busy="true" aria-label="Loading category" class="loading">
          <sd-skeleton width="220px" />
          <sd-skeleton height="36px" width="50%" />
          <sd-skeleton [lines]="6" />
        </div>
      }
    </div>
  `,
  styles: `
    .head {
      align-items: center;
      flex-wrap: nowrap;
    }
    .ic {
      display: inline-flex;
      flex: none;
      padding: 12px;
      border-radius: var(--sd-radius-lg);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    .titles {
      min-width: 0;
    }
    .block {
      margin-bottom: 22px;
    }
    .block-title {
      font-size: var(--sd-text-lg);
      margin-bottom: 10px;
    }
    .subs {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 260px), 1fr));
      gap: 10px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .sub {
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 12px 14px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      color: var(--sd-text);
      text-decoration: none;
    }
    .sub:hover {
      border-color: var(--sd-primary-border);
      background: var(--sd-primary-soft);
      text-decoration: none;
    }
    .sub > sd-icon:first-child {
      color: var(--sd-primary);
    }
    .sub-text {
      display: grid;
      flex: 1;
      min-width: 0;
    }
    .sub-name {
      font-weight: var(--sd-weight-medium);
    }
    .sub-count {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .go {
      color: var(--sd-text-3);
    }
    .panel {
      padding: 16px 12px 8px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    .list-head {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
      padding: 0 8px 6px;
    }
    .list-head .block-title {
      margin: 0;
    }
    .sort {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .sort select {
      width: auto;
    }
    .loading {
      display: grid;
      gap: 16px;
      padding-top: 8px;
    }
  `,
})
export class KbCategoryPage {
  readonly slug = input.required<string>();

  protected readonly brand = inject(BrandService);
  private readonly auth = inject(PortalAuthService);
  private readonly api = inject(PortalContentApi);

  protected readonly sort = signal<ArticleSort>('popular');
  protected readonly res = rxResource({
    params: () => {
      const brand = this.brand.slug();
      return brand ? { brand, slug: this.slug(), signedIn: this.auth.isAuthenticated() } : undefined;
    },
    stream: ({ params }) => this.api.category(params.brand, params.slug),
  });
  protected readonly page = computed<PortalCategoryPageDto | null>(() => (this.res.hasValue() ? this.res.value() : null));

  protected readonly crumbs = computed<Crumb[]>(() => {
    const p = this.page();
    const base = this.brand.base();
    const items: Crumb[] = [
      { label: 'Home', link: base },
      { label: 'Knowledge Base', link: `${base}/kb` },
    ];
    if (!p) return items;
    for (const c of p.breadcrumb) {
      if (c.id !== p.category.id) items.push({ label: c.name, link: `${base}/kb/c/${c.slug}` });
    }
    items.push({ label: p.category.name });
    return items;
  });

  protected readonly sorted = computed<PortalArticleSummaryDto[]>(() => {
    const list = [...(this.page()?.articles ?? [])];
    switch (this.sort()) {
      case 'recent':
        return list.sort((a, b) => Date.parse(b.updatedAt) - Date.parse(a.updatedAt));
      case 'title':
        return list.sort((a, b) => a.title.localeCompare(b.title));
      default:
        return list.sort((a, b) => b.viewCount - a.viewCount);
    }
  });

  constructor() {
    effect(() => {
      const p = this.page();
      if (p) this.brand.setPageTitle(p.category.name);
    });
  }

  protected icon(c: PortalCategoryDto): string {
    return c.icon && hasIcon(c.icon) ? c.icon : 'folder';
  }
}
