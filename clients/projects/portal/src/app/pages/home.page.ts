import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { PortalAuthService, PortalCategoryDto, PortalContentApi, PortalHomeDto } from '@sd/core';
import { SdButton, SdIcon, SdSkeleton, hasIcon } from '@sd/ui';
import { BrandService } from '../brand.service';
import { ArticleListComponent } from '../shared/article-list.component';
import { ErrorStateComponent } from '../shared/error-state.component';
import { SearchBoxComponent } from '../shared/search-box.component';
import { TopicListComponent } from '../shared/topic-list.component';

interface Tile {
  title: string;
  text: string;
  icon: string;
  path: string;
}

/**
 * Brand home: hero search, the signed-in contact's ticket stats, announcements, knowledge-base
 * categories, popular and recent articles, guided troubleshooting, recent community topics and quick
 * actions (GET /portal/{brand}/home).
 */
@Component({
  selector: 'portal-home-page',
  imports: [RouterLink, SdIcon, SdButton, SdSkeleton, SearchBoxComponent, ArticleListComponent, TopicListComponent, ErrorStateComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="hero" aria-labelledby="home-title">
      <div class="portal-container hero-inner">
        <h1 id="home-title">{{ heading() }}</h1>
        <p class="welcome">{{ welcome() }}</p>
        @if (features().kb || features().community) {
          <portal-search-box size="lg" class="hero-search" />
        }
        <div class="hero-actions">
          <a sdButton variant="primary" icon="file-plus" [routerLink]="brand.base() + '/submit'">Submit a ticket</a>
          @if (auth.isAuthenticated()) {
            <a sdButton icon="ticket" [routerLink]="brand.base() + '/tickets'">My tickets</a>
          }
          @if (hasFlows()) {
            <a sdButton variant="ghost" icon="signpost" [routerLink]="brand.base() + '/help'">Troubleshoot a problem</a>
          }
        </div>
      </div>
    </section>

    <div class="portal-container content">
      @if (auth.contact(); as contact) {
        <section class="panel stats-panel" aria-labelledby="stats-title">
          <div class="section-head">
            <h2 id="stats-title">Your tickets</h2>
            <a [routerLink]="brand.base() + '/tickets'" class="more">View all <sd-icon name="arrow-right" [size]="14" /></a>
          </div>
          @if (home(); as h) {
            @if (h.myTickets; as s) {
              <div class="stats">
                <a class="stat" [routerLink]="brand.base() + '/tickets'" [queryParams]="{ status: 'open' }">
                  <span class="stat-ic"><sd-icon name="inbox" [size]="18" /></span>
                  <span class="stat-num">{{ s.open }}</span>
                  <span class="stat-lbl">Open</span>
                </a>
                <a class="stat" [class.is-hot]="s.awaitingYou > 0" [routerLink]="brand.base() + '/tickets'" [queryParams]="{ status: 'awaiting' }">
                  <span class="stat-ic"><sd-icon name="reply" [size]="18" /></span>
                  <span class="stat-num">{{ s.awaitingYou }}</span>
                  <span class="stat-lbl">Awaiting your reply</span>
                </a>
                <a class="stat" [class.is-hot]="s.awaitingConfirmation > 0" [routerLink]="brand.base() + '/tickets'" [queryParams]="{ status: 'awaiting' }">
                  <span class="stat-ic"><sd-icon name="circle-check" [size]="18" /></span>
                  <span class="stat-num">{{ s.awaitingConfirmation }}</span>
                  <span class="stat-lbl">Awaiting your confirmation</span>
                </a>
              </div>
            }
          } @else if (res.isLoading()) {
            <div class="stats" aria-hidden="true">
              @for (i of [1, 2, 3]; track i) {
                <sd-skeleton height="84px" radius="var(--sd-radius-lg)" />
              }
            </div>
          }
        </section>
      }

      @if (res.error()) {
        <portal-error-state [error]="res.error()" errorTitle="The help center could not be loaded" (retry)="res.reload()" />
      } @else if (home(); as h) {
        @if (h.announcements.length) {
          <section class="announcements" aria-labelledby="ann-title">
            <h2 id="ann-title" class="sd-visually-hidden">Announcements</h2>
            @for (a of h.announcements; track a.id) {
              <a class="announcement" [routerLink]="[brand.base(), 'kb', a.slug]">
                <span class="ann-ic" aria-hidden="true"><sd-icon name="megaphone" [size]="18" /></span>
                <span class="ann-text">
                  <strong>{{ a.title }}</strong>
                  @if (a.summary) {
                    <span>{{ a.summary }}</span>
                  }
                </span>
                <sd-icon name="chevron-right" [size]="16" class="ann-go" />
              </a>
            }
          </section>
        }

        @if (features().kb && h.categories.length) {
          <section aria-labelledby="cat-title">
            <div class="section-head">
              <h2 id="cat-title">Browse by topic</h2>
              <a [routerLink]="brand.base() + '/kb'" class="more">All articles <sd-icon name="arrow-right" [size]="14" /></a>
            </div>
            <ul class="categories">
              @for (c of h.categories; track c.id) {
                <li>
                  <a class="category" [routerLink]="[brand.base(), 'kb', 'c', c.slug]">
                    <span class="cat-ic" aria-hidden="true"><sd-icon [name]="categoryIcon(c)" [size]="20" /></span>
                    <span class="cat-name">{{ c.name }}</span>
                    @if (c.description) {
                      <span class="cat-desc">{{ c.description }}</span>
                    }
                    <span class="cat-count">{{ c.articleCount }} {{ c.articleCount === 1 ? 'article' : 'articles' }}</span>
                  </a>
                </li>
              }
            </ul>
          </section>
        }

        @if (features().kb && (h.popular.length || h.recent.length)) {
          <div class="two-col">
            @if (h.popular.length) {
              <section class="panel" aria-labelledby="pop-title">
                <h2 id="pop-title" class="panel-title"><sd-icon name="trending-up" [size]="18" />Popular articles</h2>
                <portal-article-list [articles]="h.popular" compact />
              </section>
            }
            @if (h.recent.length) {
              <section class="panel" aria-labelledby="recent-title">
                <h2 id="recent-title" class="panel-title"><sd-icon name="clock" [size]="18" />Recently updated</h2>
                <portal-article-list [articles]="h.recent" compact />
              </section>
            }
          </div>
        }

        @if (h.flows.length) {
          <section aria-labelledby="flows-title">
            <div class="section-head">
              <h2 id="flows-title">Fix it yourself, step by step</h2>
              <a [routerLink]="brand.base() + '/help'" class="more">All guides <sd-icon name="arrow-right" [size]="14" /></a>
            </div>
            <ul class="flows">
              @for (f of h.flows.slice(0, 6); track f.id) {
                <li>
                  <a class="flow" [routerLink]="[brand.base(), 'troubleshoot', f.id]">
                    <span class="flow-ic" aria-hidden="true"><sd-icon name="signpost" [size]="18" /></span>
                    <span class="flow-text">
                      <span class="flow-name">{{ f.name }}</span>
                      @if (f.description) {
                        <span class="flow-desc">{{ f.description }}</span>
                      }
                      @if (f.productName) {
                        <span class="flow-product">{{ f.productName }}</span>
                      }
                    </span>
                    <span class="flow-go">Start <sd-icon name="arrow-right" [size]="14" /></span>
                  </a>
                </li>
              }
            </ul>
          </section>
        }

        @if (features().community && h.recentTopics.length) {
          <section class="panel" aria-labelledby="topics-title">
            <div class="section-head">
              <h2 id="topics-title" class="panel-title"><sd-icon name="messages" [size]="18" />From the community</h2>
              <a [routerLink]="brand.base() + '/community'" class="more">Visit the community <sd-icon name="arrow-right" [size]="14" /></a>
            </div>
            <portal-topic-list [topics]="h.recentTopics.slice(0, 5)" compact />
          </section>
        }
      } @else {
        <div class="loading" aria-busy="true" aria-label="Loading the help center">
          <div class="categories">
            @for (i of [1, 2, 3, 4, 5, 6]; track i) {
              <sd-skeleton height="120px" radius="var(--sd-radius-lg)" />
            }
          </div>
          <div class="two-col">
            <sd-skeleton height="220px" radius="var(--sd-radius-lg)" />
            <sd-skeleton height="220px" radius="var(--sd-radius-lg)" />
          </div>
        </div>
      }

      <section aria-labelledby="actions-title">
        <h2 id="actions-title" class="section-title">What would you like to do?</h2>
        <ul class="tiles">
          @for (tile of tiles(); track tile.path) {
            <li>
              <a class="tile" [routerLink]="brand.base() + tile.path">
                <span class="tile-ic" aria-hidden="true"><sd-icon [name]="tile.icon" [size]="20" /></span>
                <span class="tile-title">{{ tile.title }}</span>
                <span class="tile-text">{{ tile.text }}</span>
              </a>
            </li>
          }
        </ul>
      </section>
    </div>
  `,
  styles: `
    .hero {
      padding: clamp(36px, 7vw, 72px) 0 clamp(32px, 6vw, 56px);
      background:
        radial-gradient(900px 340px at 8% -10%, color-mix(in oklab, var(--sd-primary) 20%, transparent), transparent 70%),
        radial-gradient(700px 300px at 95% 0%, color-mix(in oklab, var(--sd-accent) 16%, transparent), transparent 70%),
        var(--sd-surface-2);
      border-bottom: 1px solid var(--sd-border);
    }
    .hero-inner {
      display: grid;
      justify-items: center;
      text-align: center;
    }
    h1 {
      max-width: 22ch;
      font-size: clamp(1.75rem, 4.6vw, 2.75rem);
      font-weight: var(--sd-weight-bold);
      letter-spacing: -0.03em;
    }
    .welcome {
      max-width: 640px;
      margin: 12px 0 0;
      color: var(--sd-text-2);
      font-size: var(--sd-text-lg);
      white-space: pre-line;
    }
    .hero-search {
      width: min(680px, 100%);
      margin-top: 26px;
    }
    .hero-actions {
      display: flex;
      flex-wrap: wrap;
      justify-content: center;
      gap: 10px;
      margin-top: 20px;
    }
    .content {
      display: grid;
      gap: 36px;
      padding-top: 32px;
    }
    .section-head {
      display: flex;
      align-items: baseline;
      justify-content: space-between;
      gap: 12px;
      margin-bottom: 14px;
    }
    .section-head h2,
    .section-title {
      font-size: var(--sd-text-xl);
    }
    .section-title {
      margin-bottom: 14px;
    }
    .more {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-medium);
      white-space: nowrap;
    }
    .panel {
      padding: 18px 18px 10px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
      box-shadow: var(--sd-shadow-xs);
    }
    .panel-title {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      margin-bottom: 6px;
      font-size: var(--sd-text-lg);
    }
    .panel-title sd-icon {
      color: var(--sd-primary);
    }
    .stats-panel {
      padding-bottom: 18px;
    }
    .stats {
      display: grid;
      grid-template-columns: repeat(3, minmax(0, 1fr));
      gap: 12px;
    }
    .stat {
      display: grid;
      grid-template-columns: auto 1fr;
      grid-template-rows: auto auto;
      align-items: center;
      gap: 2px 12px;
      padding: 14px 16px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface-2);
      color: var(--sd-text);
      text-decoration: none;
    }
    .stat:hover {
      border-color: var(--sd-primary-border);
      background: var(--sd-primary-soft);
      text-decoration: none;
    }
    .stat-ic {
      grid-row: span 2;
      display: inline-flex;
      padding: 9px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    .stat.is-hot .stat-ic {
      background: var(--sd-warning-bg);
      color: var(--sd-warning);
    }
    .stat-num {
      font-size: var(--sd-text-2xl);
      font-weight: var(--sd-weight-bold);
      line-height: 1.1;
      font-variant-numeric: tabular-nums;
    }
    .stat-lbl {
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .announcements {
      display: grid;
      gap: 10px;
    }
    .announcement {
      display: flex;
      align-items: center;
      gap: 14px;
      padding: 14px 16px;
      border: 1px solid color-mix(in oklab, var(--sd-accent) 35%, var(--sd-border));
      border-radius: var(--sd-radius-lg);
      background: var(--sd-accent-soft);
      color: var(--sd-text);
      text-decoration: none;
    }
    .announcement:hover {
      text-decoration: none;
      box-shadow: var(--sd-shadow-sm);
    }
    .ann-ic {
      display: inline-flex;
      padding: 8px;
      border-radius: 50%;
      background: var(--sd-surface);
      color: var(--sd-accent);
    }
    .ann-text {
      display: grid;
      gap: 2px;
      flex: 1;
      min-width: 0;
    }
    .ann-text span {
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .ann-go {
      color: var(--sd-text-3);
    }
    .categories,
    .flows,
    .tiles {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 250px), 1fr));
      gap: 14px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .category,
    .tile {
      display: flex;
      flex-direction: column;
      gap: 5px;
      height: 100%;
      padding: 18px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
      color: var(--sd-text);
      box-shadow: var(--sd-shadow-xs);
      text-decoration: none;
      transition:
        box-shadow var(--sd-dur) var(--sd-ease),
        border-color var(--sd-dur) var(--sd-ease),
        transform var(--sd-dur) var(--sd-ease);
    }
    .category:hover,
    .tile:hover,
    .flow:hover {
      border-color: var(--sd-primary-border);
      box-shadow: var(--sd-shadow-md);
      transform: translateY(-1px);
      text-decoration: none;
    }
    .cat-ic,
    .tile-ic {
      display: inline-flex;
      width: fit-content;
      margin-bottom: 6px;
      padding: 9px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    .cat-name,
    .tile-title {
      font-size: var(--sd-text-lg);
      font-weight: var(--sd-weight-semibold);
    }
    .cat-desc,
    .tile-text {
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .cat-count {
      margin-top: auto;
      padding-top: 6px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .two-col {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 18px;
    }
    .flow {
      display: flex;
      align-items: flex-start;
      gap: 12px;
      height: 100%;
      padding: 16px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
      color: var(--sd-text);
      text-decoration: none;
      transition:
        box-shadow var(--sd-dur) var(--sd-ease),
        border-color var(--sd-dur) var(--sd-ease),
        transform var(--sd-dur) var(--sd-ease);
    }
    .flow-ic {
      display: inline-flex;
      padding: 8px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-accent-soft);
      color: var(--sd-accent);
    }
    .flow-text {
      display: grid;
      gap: 3px;
      flex: 1;
      min-width: 0;
    }
    .flow-name {
      font-weight: var(--sd-weight-semibold);
    }
    .flow-desc {
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .flow-product {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .flow-go {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      color: var(--sd-primary);
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-medium);
      white-space: nowrap;
    }
    .loading {
      display: grid;
      gap: 24px;
    }
    @media (max-width: 760px) {
      .two-col {
        grid-template-columns: 1fr;
      }
      .stats {
        grid-template-columns: 1fr;
      }
    }
    @media (prefers-reduced-motion: reduce) {
      .category:hover,
      .tile:hover,
      .flow:hover {
        transform: none;
      }
    }
  `,
})
export class HomePage {
  protected readonly brand = inject(BrandService);
  protected readonly auth = inject(PortalAuthService);
  private readonly api = inject(PortalContentApi);

  protected readonly features = this.brand.features;
  protected readonly res = rxResource({
    params: () => {
      const slug = this.brand.slug();
      return slug ? { slug, signedIn: this.auth.isAuthenticated() } : undefined;
    },
    stream: ({ params }) => this.api.home(params.slug),
  });
  protected readonly home = computed<PortalHomeDto | null>(() => (this.res.hasValue() ? this.res.value() : null));
  protected readonly hasFlows = computed(() => (this.home()?.flows.length ?? 0) > 0);

  protected readonly heading = computed(() => {
    const first = this.auth.contact()?.fullName.trim().split(/\s+/)[0];
    return first ? `Hi ${first}, how can we help?` : 'How can we help you today?';
  });
  protected readonly welcome = computed(
    () => this.brand.config()?.portalWelcome || 'Search our help articles, ask the community or get in touch with our support team.',
  );

  protected readonly tiles = computed<Tile[]>(() => {
    const f = this.features();
    const tiles: Tile[] = [];
    if (f.kb) tiles.push({ title: 'Knowledge Base', text: 'Answers, how-to guides and FAQs.', icon: 'book-open', path: '/kb' });
    if (f.community) tiles.push({ title: 'Community', text: 'Ask other customers and our experts.', icon: 'messages', path: '/community' });
    tiles.push({ title: 'Submit a ticket', text: 'Tell us what you need help with.', icon: 'file-plus', path: '/submit' });
    tiles.push({ title: 'My tickets', text: 'Follow up on your requests.', icon: 'ticket', path: '/tickets' });
    tiles.push({ title: 'Help & contact', text: 'Guided troubleshooting and ways to reach us.', icon: 'life-buoy', path: '/help' });
    return tiles;
  });

  protected categoryIcon(c: PortalCategoryDto): string {
    return c.icon && hasIcon(c.icon) ? c.icon : 'folder';
  }
}
