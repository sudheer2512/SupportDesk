import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { rxResource, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { PortalArticleDto, PortalArticleSummaryDto, PortalContentApi } from '@sd/core';
import { SdButton, SdDrawer, SdIcon, SdSkeleton } from '@sd/ui';
import { catchError, debounceTime, distinctUntilChanged, map, of, switchMap } from 'rxjs';
import { BrandService } from '../brand.service';
import { ErrorStateComponent } from './error-state.component';
import { mediaQuery } from './portal-utils';
import { ProseLinksDirective } from './prose-links.directive';

/**
 * Ticket/topic deflection: while the customer describes the problem (≥3 characters, 400 ms debounce) it
 * suggests matching articles; each opens in a side drawer (or a new tab) without losing the form.
 * `<portal-deflection [query]="subject() + ' ' + descriptionText()" (solved)="onSolved()" />`
 */
@Component({
  selector: 'portal-deflection',
  imports: [RouterLink, SdIcon, SdButton, SdDrawer, SdSkeleton, ErrorStateComponent, ProseLinksDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p class="sd-visually-hidden" aria-live="polite">{{ announcement() }}</p>
    @if (suggestions().length) {
      <section class="deflect" aria-labelledby="deflect-title">
        <h2 id="deflect-title"><sd-icon name="lightbulb" [size]="17" />These articles might help</h2>
        <ul>
          @for (a of suggestions(); track a.id) {
            <li>
              <button type="button" class="article" (click)="open(a)">
                <sd-icon name="file-text" [size]="15" />
                <span class="text">
                  <span class="title">{{ a.title }}</span>
                  @if (a.summary) {
                    <span class="summary">{{ a.summary }}</span>
                  }
                </span>
              </button>
              <a class="ext" [routerLink]="[brand.base(), 'kb', a.slug]" target="_blank" rel="noopener" [attr.aria-label]="'Open ' + a.title + ' in a new tab'">
                <sd-icon name="external-link" [size]="15" />
              </a>
            </li>
          }
        </ul>
      </section>
    }

    <sd-drawer [(open)]="drawerOpen" [side]="narrow() ? 'bottom' : 'right'" width="560px" [title]="selected()?.title ?? 'Article'">
      @if (article.error()) {
        <portal-error-state [error]="article.error()" notFoundTitle="Article not available" compact (retry)="article.reload()" />
      } @else if (detail(); as d) {
        @if (d.summary) {
          <p class="lead">{{ d.summary }}</p>
        }
        <div class="sd-prose portal-prose portal-prose--compact" [innerHTML]="d.bodyHtml" portalProseLinks></div>
      } @else {
        <sd-skeleton [lines]="8" />
      }
      <div sdDrawerFooter class="foot">
        <button sdButton variant="primary" type="button" icon="circle-check" (click)="markSolved()">This solved my problem</button>
        <button sdButton type="button" (click)="drawerOpen.set(false)">Back to my request</button>
        @if (selected(); as s) {
          <a sdButton variant="ghost" icon="external-link" [routerLink]="[brand.base(), 'kb', s.slug]" target="_blank" rel="noopener">New tab</a>
        }
      </div>
    </sd-drawer>
  `,
  styles: `
    .deflect {
      padding: 14px 16px;
      border: 1px solid color-mix(in oklab, var(--sd-accent) 35%, var(--sd-border));
      border-radius: var(--sd-radius-lg);
      background: var(--sd-accent-soft);
      animation: portal-fade-in var(--sd-dur) var(--sd-ease);
    }
    @keyframes portal-fade-in {
      from {
        opacity: 0;
        transform: translateY(4px);
      }
    }
    h2 {
      display: flex;
      align-items: center;
      gap: 8px;
      margin-bottom: 8px;
      font-size: var(--sd-text-md);
    }
    h2 sd-icon {
      color: var(--sd-accent);
    }
    ul {
      display: grid;
      gap: 4px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    li {
      display: flex;
      align-items: stretch;
      gap: 4px;
    }
    .article {
      display: flex;
      align-items: flex-start;
      gap: 10px;
      flex: 1;
      min-width: 0;
      padding: 8px 10px;
      border: 0;
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      color: var(--sd-text);
      font: inherit;
      text-align: left;
      cursor: pointer;
    }
    .article:hover {
      background: var(--sd-primary-soft);
    }
    .article:focus-visible,
    .ext:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .article sd-icon {
      margin-top: 3px;
      color: var(--sd-primary);
    }
    .text {
      display: grid;
      min-width: 0;
    }
    .title {
      font-weight: var(--sd-weight-medium);
    }
    .summary {
      overflow: hidden;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .ext {
      display: inline-flex;
      align-items: center;
      padding: 0 10px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      color: var(--sd-text-3);
    }
    .ext:hover {
      color: var(--sd-primary);
    }
    .lead {
      margin: 0 0 14px;
      color: var(--sd-text-2);
    }
    .foot {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      padding: 12px 16px;
      border-top: 1px solid var(--sd-divider);
    }
  `,
})
export class DeflectionComponent {
  protected readonly brand = inject(BrandService);
  private readonly api = inject(PortalContentApi);

  readonly query = input('');
  /** The customer confirmed an article solved the problem. */
  readonly solved = output<PortalArticleSummaryDto>();

  protected readonly narrow = mediaQuery('(max-width: 640px)');
  protected readonly drawerOpen = signal(false);
  protected readonly selected = signal<PortalArticleSummaryDto | null>(null);

  protected readonly suggestions = toSignal(
    toObservable(this.query).pipe(
      map((q) => q.replace(/\s+/g, ' ').trim().slice(0, 200)),
      debounceTime(400),
      distinctUntilChanged(),
      switchMap((q) => {
        const slug = this.brand.slug();
        if (!slug || !this.brand.features().kb || q.length < 3) return of<PortalArticleSummaryDto[]>([]);
        return this.api.suggest(slug, q, 5).pipe(catchError(() => of<PortalArticleSummaryDto[]>([])));
      }),
    ),
    { initialValue: [] as PortalArticleSummaryDto[] },
  );
  protected readonly announcement = computed(() => {
    const n = this.suggestions().length;
    return n ? `${n} suggested ${n === 1 ? 'article' : 'articles'} that might help` : '';
  });

  protected readonly article = rxResource({
    params: () => {
      const slug = this.brand.slug();
      const selected = this.selected();
      return slug && selected && this.drawerOpen() ? { slug, article: selected.slug } : undefined;
    },
    stream: ({ params }) => this.api.article(params.slug, params.article),
  });
  protected readonly detail = computed<PortalArticleDto | null>(() => (this.article.hasValue() ? this.article.value() : null));

  protected open(article: PortalArticleSummaryDto): void {
    this.selected.set(article);
    this.drawerOpen.set(true);
  }

  protected markSolved(): void {
    const s = this.selected();
    this.drawerOpen.set(false);
    if (s) this.solved.emit(s);
  }
}
