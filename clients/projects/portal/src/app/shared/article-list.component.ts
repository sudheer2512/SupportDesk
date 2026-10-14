import { ChangeDetectionStrategy, Component, booleanAttribute, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { PortalArticleSummaryDto } from '@sd/core';
import { SdIcon, SdRelativeTimePipe } from '@sd/ui';
import { BrandService } from '../brand.service';

/**
 * List of knowledge-base articles linking to /:brand/kb/:slug. Search results show the highlighted
 * `snippetHtml` (escaped server-side, matches in <mark>); otherwise the summary.
 * `<portal-article-list [articles]="popular" showMeta />`
 */
@Component({
  selector: 'portal-article-list',
  imports: [RouterLink, SdIcon, SdRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ul class="list" [class.is-compact]="compact()">
      @for (a of articles(); track a.id) {
        <li class="item">
          <span class="ic" aria-hidden="true"><sd-icon [name]="a.articleType === 'Troubleshooting' ? 'wrench' : 'file-text'" [size]="compact() ? 15 : 17" /></span>
          <div class="body">
            <a
              class="title"
              [routerLink]="[brand.base(), 'kb', a.slug]"
              [attr.target]="newTab() ? '_blank' : null"
              [attr.rel]="newTab() ? 'noopener' : null"
              >{{ a.title }}@if (newTab()) {<span class="sd-visually-hidden"> (opens in a new tab)</span>}</a
            >
            @if (a.snippetHtml) {
              <p class="text portal-snippet" [innerHTML]="a.snippetHtml"></p>
            } @else if (a.summary && !compact()) {
              <p class="text">{{ a.summary }}</p>
            }
            @if (showMeta()) {
              <p class="meta">
                @if (a.categoryName) {
                  <span><sd-icon name="folder" [size]="12" />{{ a.categoryName }}</span>
                }
                <span><sd-icon name="clock" [size]="12" />Updated {{ a.updatedAt | sdRelativeTime }}</span>
                @if (a.helpfulPercent !== null) {
                  <span><sd-icon name="thumbs-up" [size]="12" />{{ a.helpfulPercent }}% found this helpful</span>
                }
              </p>
            }
          </div>
        </li>
      }
    </ul>
  `,
  styles: `
    .list {
      display: grid;
      gap: 2px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .item {
      display: flex;
      gap: 12px;
      padding: 12px 10px;
      border-radius: var(--sd-radius-md);
    }
    .item:hover {
      background: var(--sd-surface-2);
    }
    .is-compact .item {
      padding: 8px 6px;
      gap: 10px;
    }
    .ic {
      display: inline-flex;
      flex: none;
      margin-top: 2px;
      color: var(--sd-primary);
    }
    .body {
      display: grid;
      gap: 3px;
      min-width: 0;
    }
    .title {
      color: var(--sd-text);
      font-weight: var(--sd-weight-medium);
      overflow-wrap: anywhere;
    }
    .title:hover {
      color: var(--sd-primary);
    }
    .text {
      margin: 0;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
      display: -webkit-box;
      -webkit-line-clamp: 2;
      -webkit-box-orient: vertical;
      overflow: hidden;
    }
    .meta {
      display: flex;
      flex-wrap: wrap;
      gap: 4px 14px;
      margin: 2px 0 0;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .meta span {
      display: inline-flex;
      align-items: center;
      gap: 4px;
    }
  `,
})
export class ArticleListComponent {
  protected readonly brand = inject(BrandService);
  readonly articles = input.required<PortalArticleSummaryDto[]>();
  readonly showMeta = input(false, { transform: booleanAttribute });
  readonly compact = input(false, { transform: booleanAttribute });
  readonly newTab = input(false, { transform: booleanAttribute });
}
