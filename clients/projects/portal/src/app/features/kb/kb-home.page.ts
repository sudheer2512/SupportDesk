import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { PortalArticleSummaryDto, PortalAuthService, PortalCategoryDto, PortalContentApi } from '@sd/core';
import { SdIcon, SdSkeleton, hasIcon } from '@sd/ui';
import { BrandService } from '../../brand.service';
import { ArticleListComponent } from '../../shared/article-list.component';
import { BreadcrumbComponent } from '../../shared/breadcrumb.component';
import { ErrorStateComponent } from '../../shared/error-state.component';
import { SearchBoxComponent } from '../../shared/search-box.component';

/** Knowledge-base index: search, the category tree with article counts, popular and recent articles. */
@Component({
  selector: 'portal-kb-home-page',
  imports: [RouterLink, SdIcon, SdSkeleton, BreadcrumbComponent, SearchBoxComponent, ArticleListComponent, ErrorStateComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="portal-container portal-page">
      <portal-breadcrumb [items]="[{ label: 'Home', link: brand.base() }, { label: 'Knowledge Base' }]" />
      <header class="portal-page-head head">
        <div>
          <h1>Knowledge Base</h1>
          <p class="portal-sub">Answers, how-to guides and FAQs from our support team.</p>
        </div>
        <portal-search-box class="search" placeholder="Search articles…" />
      </header>

      <div class="layout">
        <section aria-labelledby="kb-cats" class="main">
          <h2 id="kb-cats" class="sd-visually-hidden">Categories</h2>
          @if (categories.error()) {
            <portal-error-state [error]="categories.error()" (retry)="categories.reload()" />
          } @else if (categoryList(); as list) {
            @if (list.length) {
              <ul class="cats">
                @for (c of list; track c.id) {
                  <li class="cat">
                    <a class="cat-head" [routerLink]="[brand.base(), 'kb', 'c', c.slug]">
                      <span class="cat-ic" aria-hidden="true"><sd-icon [name]="icon(c)" [size]="20" /></span>
                      <span class="cat-title">
                        <span class="cat-name">{{ c.name }}</span>
                        <span class="cat-count">{{ c.articleCount }} {{ c.articleCount === 1 ? 'article' : 'articles' }}</span>
                      </span>
                    </a>
                    @if (c.description) {
                      <p class="cat-desc">{{ c.description }}</p>
                    }
                    @if (c.children.length) {
                      <ul class="subs" [attr.aria-label]="'Sections in ' + c.name">
                        @for (s of c.children.slice(0, 6); track s.id) {
                          <li>
                            <a [routerLink]="[brand.base(), 'kb', 'c', s.slug]">
                              <sd-icon name="folder" [size]="14" />
                              <span class="sub-name">{{ s.name }}</span>
                              <span class="sub-count">{{ s.articleCount }}</span>
                            </a>
                          </li>
                        }
                        @if (c.children.length > 6) {
                          <li>
                            <a class="all" [routerLink]="[brand.base(), 'kb', 'c', c.slug]">View all {{ c.children.length }} sections</a>
                          </li>
                        }
                      </ul>
                    }
                  </li>
                }
              </ul>
            } @else {
              <p class="empty">No categories have been published yet.</p>
            }
          } @else {
            <ul class="cats" aria-hidden="true">
              @for (i of [1, 2, 3, 4]; track i) {
                <li><sd-skeleton height="150px" radius="var(--sd-radius-lg)" /></li>
              }
            </ul>
          }
        </section>

        <aside class="side" aria-label="Featured articles">
          <section class="panel" aria-labelledby="kb-popular">
            <h2 id="kb-popular" class="panel-title"><sd-icon name="trending-up" [size]="17" />Popular articles</h2>
            @if (popular(); as list) {
              @if (list.length) {
                <portal-article-list [articles]="list" compact />
              } @else {
                <p class="empty">Nothing here yet.</p>
              }
            } @else if (!popularRes.error()) {
              <sd-skeleton [lines]="5" />
            }
          </section>
          <section class="panel" aria-labelledby="kb-recent">
            <h2 id="kb-recent" class="panel-title"><sd-icon name="clock" [size]="17" />Recently updated</h2>
            @if (recent(); as list) {
              @if (list.length) {
                <portal-article-list [articles]="list" compact />
              } @else {
                <p class="empty">Nothing here yet.</p>
              }
            } @else if (!recentRes.error()) {
              <sd-skeleton [lines]="5" />
            }
          </section>
        </aside>
      </div>
    </div>
  `,
  styles: `
    .head {
      justify-content: space-between;
    }
    .search {
      width: min(440px, 100%);
    }
    .layout {
      display: grid;
      grid-template-columns: minmax(0, 1fr) 340px;
      gap: 24px;
      align-items: start;
    }
    .cats {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 280px), 1fr));
      gap: 14px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .cat {
      display: flex;
      flex-direction: column;
      gap: 8px;
      padding: 18px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
      box-shadow: var(--sd-shadow-xs);
    }
    .cat-head {
      display: flex;
      align-items: center;
      gap: 12px;
      color: var(--sd-text);
      text-decoration: none;
    }
    .cat-head:hover .cat-name {
      color: var(--sd-primary);
    }
    .cat-ic {
      display: inline-flex;
      padding: 9px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    .cat-title {
      display: grid;
    }
    .cat-name {
      font-size: var(--sd-text-lg);
      font-weight: var(--sd-weight-semibold);
    }
    .cat-count {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .cat-desc {
      margin: 0;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .subs {
      display: grid;
      gap: 2px;
      margin: 4px 0 0;
      padding: 8px 0 0;
      border-top: 1px solid var(--sd-divider);
      list-style: none;
    }
    .subs a {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 5px 6px;
      border-radius: var(--sd-radius-sm);
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
      text-decoration: none;
    }
    .subs a:hover {
      background: var(--sd-surface-2);
      color: var(--sd-text);
    }
    .sub-name {
      flex: 1;
      min-width: 0;
    }
    .sub-count {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .subs .all {
      color: var(--sd-primary);
    }
    .side {
      display: grid;
      gap: 16px;
    }
    .panel {
      padding: 16px 14px 8px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    .panel-title {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      margin: 0 0 6px 6px;
      font-size: var(--sd-text-md);
    }
    .panel-title sd-icon {
      color: var(--sd-primary);
    }
    .empty {
      margin: 0 6px 10px;
      color: var(--sd-text-3);
    }
    @media (max-width: 900px) {
      .layout {
        grid-template-columns: 1fr;
      }
    }
  `,
})
export class KbHomePage {
  protected readonly brand = inject(BrandService);
  private readonly auth = inject(PortalAuthService);
  private readonly api = inject(PortalContentApi);

  private readonly key = computed(() => {
    const slug = this.brand.slug();
    return slug ? { slug, signedIn: this.auth.isAuthenticated() } : undefined;
  });
  protected readonly categories = rxResource({ params: this.key, stream: ({ params }) => this.api.categories(params.slug) });
  protected readonly popularRes = rxResource({
    params: this.key,
    stream: ({ params }) => this.api.articles(params.slug, { sort: 'popular', limit: 8 }),
  });
  protected readonly recentRes = rxResource({
    params: this.key,
    stream: ({ params }) => this.api.articles(params.slug, { sort: 'recent', limit: 6 }),
  });

  /** Top-level categories (the API returns a tree; tolerate a flat list too). */
  protected readonly categoryList = computed<PortalCategoryDto[] | null>(() => {
    if (!this.categories.hasValue()) return null;
    const all = this.categories.value();
    const roots = all.filter((c) => !c.parentId);
    return roots.length ? roots : all;
  });
  protected readonly popular = computed<PortalArticleSummaryDto[] | null>(() => (this.popularRes.hasValue() ? this.popularRes.value() : null));
  protected readonly recent = computed<PortalArticleSummaryDto[] | null>(() => (this.recentRes.hasValue() ? this.recentRes.value() : null));

  protected icon(c: PortalCategoryDto): string {
    return c.icon && hasIcon(c.icon) ? c.icon : 'folder';
  }
}
