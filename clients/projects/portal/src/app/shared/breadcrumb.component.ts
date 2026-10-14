import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { SdIcon } from '@sd/ui';

export interface Crumb {
  label: string;
  /** Router path; omitted for the current page. */
  link?: string | null;
}

/** `<portal-breadcrumb [items]="[{label:'Knowledge Base', link:'/acme/kb'}, {label: article.title}]" />` */
@Component({
  selector: 'portal-breadcrumb',
  imports: [RouterLink, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav aria-label="Breadcrumb">
      <ol class="crumbs">
        @for (c of items(); track $index; let last = $last) {
          <li>
            @if (c.link && !last) {
              <a [routerLink]="c.link">{{ c.label }}</a>
              <sd-icon name="chevron-right" [size]="13" />
            } @else {
              <span [attr.aria-current]="last ? 'page' : null">{{ c.label }}</span>
              @if (!last) {
                <sd-icon name="chevron-right" [size]="13" />
              }
            }
          </li>
        }
      </ol>
    </nav>
  `,
  styles: `
    .crumbs {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 4px;
      margin: 0;
      padding: 0;
      list-style: none;
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
    li {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      min-width: 0;
    }
    a {
      color: var(--sd-text-2);
    }
    span[aria-current] {
      color: var(--sd-text-2);
      max-width: 40ch;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
  `,
})
export class BreadcrumbComponent {
  readonly items = input.required<Crumb[]>();
}
