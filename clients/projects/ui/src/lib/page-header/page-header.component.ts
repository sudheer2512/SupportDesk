import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { SdIcon } from '../icon/icon.component';

export interface SdBreadcrumb {
  label: string;
  link?: string | unknown[] | null;
}

/**
 * `<sd-page-header title="Tickets" subtitle="All open work" icon="ticket" [breadcrumbs]="crumbs">
 *    <button sdButton sdPageActions …>New</button>
 *  </sd-page-header>` — page title block (h1) with breadcrumbs and right-aligned actions.
 */
@Component({
  selector: 'sd-page-header',
  imports: [RouterLink, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (breadcrumbs().length) {
      <nav class="crumbs" aria-label="Breadcrumb">
        <ol>
          @for (crumb of breadcrumbs(); track $index; let last = $last) {
            <li>
              @if (crumb.link && !last) {
                <a [routerLink]="crumb.link">{{ crumb.label }}</a>
              } @else {
                <span [attr.aria-current]="last ? 'page' : null">{{ crumb.label }}</span>
              }
              @if (!last) {
                <sd-icon name="chevron-right" [size]="12" />
              }
            </li>
          }
        </ol>
      </nav>
    }
    <div class="row">
      @if (icon()) {
        <span class="icon"><sd-icon [name]="icon()!" [size]="20" /></span>
      }
      <div class="titles">
        <h1>{{ title() }}</h1>
        @if (subtitle()) {
          <p class="subtitle">{{ subtitle() }}</p>
        }
      </div>
      <div class="actions"><ng-content select="[sdPageActions]" /></div>
    </div>
    <ng-content />
  `,
  styles: `
    :host {
      display: block;
      padding: var(--sd-space-5) var(--sd-space-6) var(--sd-space-4);
    }
    @media (max-width: 640px) {
      :host {
        padding: var(--sd-space-4) var(--sd-space-4) var(--sd-space-3);
      }
    }
    .crumbs ol {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 4px;
      margin: 0 0 6px;
      padding: 0;
      list-style: none;
      font-size: var(--sd-text-sm);
      color: var(--sd-text-3);
    }
    .crumbs li {
      display: inline-flex;
      align-items: center;
      gap: 4px;
    }
    .crumbs a {
      color: var(--sd-text-2);
    }
    .row {
      display: flex;
      align-items: center;
      gap: var(--sd-space-3);
      flex-wrap: wrap;
    }
    .icon {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 38px;
      height: 38px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
      flex: none;
    }
    .titles {
      flex: 1 1 240px;
      min-width: 0;
    }
    h1 {
      font-size: var(--sd-text-2xl);
      font-weight: var(--sd-weight-bold);
    }
    .subtitle {
      margin-top: 2px;
      color: var(--sd-text-3);
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--sd-space-2);
      align-items: center;
    }
  `,
})
export class SdPageHeader {
  readonly title = input.required<string>();
  readonly subtitle = input<string | null>(null);
  readonly icon = input<string | null>(null);
  readonly breadcrumbs = input<SdBreadcrumb[]>([]);
}
