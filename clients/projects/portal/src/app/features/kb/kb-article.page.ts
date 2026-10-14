import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { PortalArticleDto, PortalAuthService, PortalContentApi } from '@sd/core';
import { SdBadge, SdButton, SdIcon, SdProseHtmlPipe, SdRelativeTimePipe, SdSkeleton } from '@sd/ui';
import { BrandService } from '../../brand.service';
import { ArticleListComponent } from '../../shared/article-list.component';
import { AttachmentListComponent } from '../../shared/attachment-list.component';
import { BreadcrumbComponent, Crumb } from '../../shared/breadcrumb.component';
import { ErrorStateComponent } from '../../shared/error-state.component';
import { escapeHtml, isNotFound, localeName } from '../../shared/portal-utils';
import { ProseLinksDirective } from '../../shared/prose-links.directive';
import { SubmitPrefillService } from '../../shared/submit-prefill.service';
import { ArticleFeedbackComponent } from './article-feedback.component';

const TYPE_LABELS: Record<string, string> = {
  FAQ: 'FAQ',
  HowTo: 'How-to',
  Troubleshooting: 'Troubleshooting',
  Reference: 'Reference',
  ReleaseNote: 'Release note',
  ProductDoc: 'Product documentation',
};

/**
 * A knowledge-base article: breadcrumb, language switcher (with outdated / fallback translation notices),
 * sanitised body, attachments, tags, related articles, the feedback widget and a "still need help" path.
 */
@Component({
  selector: 'portal-kb-article-page',
  imports: [
    RouterLink,
    SdIcon,
    SdBadge,
    SdButton,
    SdSkeleton,
    SdRelativeTimePipe,
    SdProseHtmlPipe,
    BreadcrumbComponent,
    ArticleListComponent,
    AttachmentListComponent,
    ErrorStateComponent,
    ArticleFeedbackComponent,
    ProseLinksDirective,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="portal-container portal-page">
      @if (res.error()) {
        <portal-breadcrumb [items]="[{ label: 'Home', link: brand.base() }, { label: 'Knowledge Base', link: brand.base() + '/kb' }, { label: 'Article' }]" />
        <portal-error-state
          [error]="res.error()"
          notFoundTitle="Article not found"
          [notFoundMessage]="
            auth.isAuthenticated()
              ? 'This article does not exist, was unpublished, or is not available to your account.'
              : 'This article does not exist, was unpublished, or is only available to signed-in customers.'
          "
          (retry)="res.reload()"
        >
          @if (notFound() && !auth.isAuthenticated()) {
            <a sdButton variant="primary" icon="log-in" [routerLink]="brand.base() + '/login'" [queryParams]="{ returnUrl: router.url }">Sign in</a>
          }
          <a sdButton [routerLink]="brand.base() + '/kb'">Browse the knowledge base</a>
        </portal-error-state>
      } @else if (article(); as a) {
        <portal-breadcrumb [items]="crumbs()" />
        <div class="layout">
          <article class="main" aria-labelledby="article-title">
            <header class="head">
              <h1 id="article-title">{{ a.title }}</h1>
              <div class="meta">
                <sd-badge size="sm" tone="primary">{{ typeLabel(a.articleType) }}</sd-badge>
                @if (a.productName) {
                  <span><sd-icon name="package" [size]="13" />{{ a.productName }}</span>
                }
                <span><sd-icon name="clock" [size]="13" />Updated {{ a.updatedAt | sdRelativeTime }}</span>
                <span><sd-icon name="eye" [size]="13" />{{ a.viewCount }} views</span>
                @if (helpfulPercent(); as pct) {
                  <span><sd-icon name="thumbs-up" [size]="13" />{{ pct }}% found this helpful</span>
                }
              </div>
              @if (a.availableLocales.length > 1) {
                <label class="lang">
                  <sd-icon name="languages" [size]="15" />
                  <span>Language</span>
                  <select class="sd-input sd-input-sm" (change)="switchLocale($event)">
                    @for (l of a.availableLocales; track l) {
                      <option [value]="l" [selected]="l === a.locale">{{ languageLabel(l) }}</option>
                    }
                  </select>
                </label>
              }
            </header>

            @if (a.translationFallback) {
              <p class="portal-callout portal-callout--info notice" role="note">
                <sd-icon name="info" [size]="16" />
                <span>This article isn't available in {{ languageLabel(requestedLocale() ?? '') }} yet, so it is shown in {{ languageLabel(a.locale) }}.</span>
              </p>
            }
            @if (a.translationOutdated) {
              <p class="portal-callout portal-callout--warning notice" role="note">
                <sd-icon name="triangle-alert" [size]="16" />
                <span>
                  This translation may be out of date.
                  @if (a.primaryLocale !== a.locale) {
                    <button sdButton variant="link" type="button" (click)="goToLocale(a.primaryLocale)">Read the latest version in {{ languageLabel(a.primaryLocale) }}</button>
                  }
                </span>
              </p>
            }

            @if (a.summary) {
              <p class="summary">{{ a.summary }}</p>
            }
            <div class="sd-prose portal-prose body" [innerHTML]="a.bodyHtml | sdProseHtml" portalProseLinks></div>

            @if (a.attachments.length) {
              <section class="section" aria-labelledby="att-title">
                <h2 id="att-title" class="section-title">Attachments</h2>
                <portal-attachments [attachments]="a.attachments" />
              </section>
            }

            @if (a.tags.length) {
              <ul class="tags" aria-label="Tags">
                @for (t of a.tags; track t) {
                  <li>
                    <a [routerLink]="brand.base() + '/search'" [queryParams]="{ q: t }"><sd-icon name="tag" [size]="12" />{{ t }}</a>
                  </li>
                }
              </ul>
            }

            <portal-article-feedback class="section" [articleId]="a.id" [locale]="a.locale" />
          </article>

          <aside class="side" aria-label="More help">
            @if (a.related.length) {
              <section class="panel" aria-labelledby="related-title">
                <h2 id="related-title" class="panel-title">Related articles</h2>
                <portal-article-list [articles]="a.related" compact />
              </section>
            }
            <section class="panel help" aria-labelledby="help-title">
              <span class="help-ic" aria-hidden="true"><sd-icon name="life-buoy" [size]="20" /></span>
              <h2 id="help-title" class="panel-title">Still need help?</h2>
              <p>Our support team is happy to help with anything this article didn't cover.</p>
              <button sdButton variant="primary" type="button" icon="file-plus" block (click)="submitTicket(a)">Submit a ticket</button>
              @if (brand.features().community) {
                <a sdButton variant="ghost" icon="messages" block [routerLink]="brand.base() + '/community'">Ask the community</a>
              }
            </section>
          </aside>
        </div>
      } @else {
        <div class="loading" aria-busy="true" aria-label="Loading article">
          <sd-skeleton width="260px" />
          <sd-skeleton height="38px" width="70%" />
          <sd-skeleton width="40%" />
          <sd-skeleton [lines]="10" />
        </div>
      }
    </div>
  `,
  styles: `
    .layout {
      display: grid;
      grid-template-columns: minmax(0, 1fr) 300px;
      gap: 32px;
      align-items: start;
      margin-top: 14px;
    }
    .main {
      min-width: 0;
    }
    .head {
      display: grid;
      gap: 10px;
      padding-bottom: 16px;
      margin-bottom: 20px;
      border-bottom: 1px solid var(--sd-border);
    }
    h1 {
      font-size: clamp(1.6rem, 3.6vw, 2.25rem);
      font-weight: var(--sd-weight-bold);
      letter-spacing: -0.025em;
      overflow-wrap: anywhere;
    }
    .meta {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px 16px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
    .meta span {
      display: inline-flex;
      align-items: center;
      gap: 5px;
    }
    .lang {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      width: fit-content;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .lang select {
      width: auto;
    }
    .notice {
      margin: 0 0 14px;
    }
    .summary {
      margin: 0 0 18px;
      color: var(--sd-text-2);
      font-size: var(--sd-text-lg);
      line-height: 1.55;
    }
    .body {
      margin-bottom: 28px;
    }
    .section {
      display: block;
      margin-top: 28px;
    }
    .section-title {
      margin-bottom: 10px;
      font-size: var(--sd-text-lg);
    }
    .tags {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      margin: 24px 0 0;
      padding: 0;
      list-style: none;
    }
    .tags a {
      display: inline-flex;
      align-items: center;
      gap: 5px;
      padding: 4px 10px;
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface-3);
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
      text-decoration: none;
    }
    .tags a:hover {
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    .side {
      position: sticky;
      top: 76px;
      display: grid;
      gap: 16px;
    }
    .panel {
      padding: 16px 14px 10px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    .panel-title {
      margin: 0 0 6px 6px;
      font-size: var(--sd-text-md);
    }
    .help {
      display: grid;
      gap: 8px;
      padding: 18px;
    }
    .help .panel-title {
      margin: 0;
    }
    .help p {
      margin: 0 0 6px;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .help-ic {
      display: inline-flex;
      width: fit-content;
      padding: 8px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    .loading {
      display: grid;
      gap: 16px;
    }
    @media (max-width: 960px) {
      .layout {
        grid-template-columns: 1fr;
      }
      .side {
        position: static;
      }
    }
  `,
})
export class KbArticlePage {
  readonly slug = input.required<string>();
  /** `?locale=fr` selects a translation. */
  readonly locale = input<string | undefined>();

  protected readonly brand = inject(BrandService);
  protected readonly auth = inject(PortalAuthService);
  protected readonly router = inject(Router);
  private readonly api = inject(PortalContentApi);
  private readonly prefill = inject(SubmitPrefillService);

  protected readonly requestedLocale = computed(() => this.locale() || null);
  protected readonly res = rxResource({
    params: () => {
      const brand = this.brand.slug();
      return brand ? { brand, slug: this.slug(), locale: this.requestedLocale(), signedIn: this.auth.isAuthenticated() } : undefined;
    },
    stream: ({ params }) => this.api.article(params.brand, params.slug, params.locale),
  });
  protected readonly article = computed<PortalArticleDto | null>(() => (this.res.hasValue() ? this.res.value() : null));
  protected readonly notFound = computed(() => isNotFound(this.res.error()));
  protected readonly helpfulPercent = computed(() => {
    const a = this.article();
    const total = (a?.helpfulCount ?? 0) + (a?.notHelpfulCount ?? 0);
    return a && total >= 3 ? Math.round((a.helpfulCount / total) * 100) : null;
  });

  protected readonly crumbs = computed<Crumb[]>(() => {
    const base = this.brand.base();
    const items: Crumb[] = [
      { label: 'Home', link: base },
      { label: 'Knowledge Base', link: `${base}/kb` },
    ];
    for (const c of this.article()?.breadcrumb ?? []) items.push({ label: c.name, link: `${base}/kb/c/${c.slug}` });
    items.push({ label: this.article()?.title ?? 'Article' });
    return items;
  });

  constructor() {
    effect(() => {
      const a = this.article();
      if (a) this.brand.setPageTitle(a.title);
    });
  }

  protected typeLabel(type: string): string {
    return TYPE_LABELS[type] ?? type;
  }

  protected languageLabel(code: string): string {
    return code ? localeName(code) : 'your language';
  }

  protected switchLocale(event: Event): void {
    this.goToLocale((event.target as HTMLSelectElement).value);
  }

  protected goToLocale(locale: string): void {
    void this.router.navigate([], { queryParams: { locale }, queryParamsHandling: 'merge', replaceUrl: true });
  }

  protected submitTicket(a: PortalArticleDto): void {
    this.prefill.set({ subject: null, descriptionHtml: `<p></p><p><em>I read “${escapeHtml(a.title)}” but still need help.</em></p>` });
    void this.router.navigate([this.brand.base(), 'submit']);
  }
}

