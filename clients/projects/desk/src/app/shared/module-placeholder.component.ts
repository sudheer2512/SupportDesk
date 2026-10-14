import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { SdBreadcrumb, SdCard, SdIcon, SdPageHeader } from '@sd/ui';

/**
 * Temporary landing for a module whose screens are still being built. It states that honestly and
 * lists what the module will contain. Replace the module's home page with the real screens.
 */
@Component({
  selector: 'desk-module-placeholder',
  imports: [SdPageHeader, SdCard, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header [title]="title()" [subtitle]="subtitle()" [icon]="icon()" [breadcrumbs]="breadcrumbs()">
        <ng-content select="[sdPageActions]" />
      </sd-page-header>
      <div class="desk-page__body">
        <sd-card class="panel">
          <div class="state">
            <span class="badge"><sd-icon name="wand" [size]="18" /></span>
            <div>
              <h2>This module's screens are being built</h2>
              <p>
                The navigation, permissions and API client for this area are in place; its working screens arrive in an
                upcoming iteration. Nothing on this page is simulated.
              </p>
            </div>
          </div>
          @if (planned().length) {
            <h3>What this module will cover</h3>
            <ul>
              @for (item of planned(); track item) {
                <li><sd-icon name="check" [size]="14" />{{ item }}</li>
              }
            </ul>
          }
          <ng-content />
        </sd-card>
      </div>
    </div>
  `,
  styles: `
    .panel {
      max-width: 860px;
    }
    .state {
      display: flex;
      gap: 14px;
      align-items: flex-start;
    }
    .badge {
      display: inline-flex;
      padding: 10px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-accent-soft);
      color: var(--sd-accent);
      flex: none;
    }
    h2 {
      font-size: var(--sd-text-lg);
    }
    .state p {
      margin-top: 4px;
      color: var(--sd-text-2);
    }
    h3 {
      margin: 22px 0 10px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      text-transform: uppercase;
      letter-spacing: 0.05em;
    }
    ul {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 320px), 1fr));
      gap: 8px 20px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    li {
      display: flex;
      align-items: flex-start;
      gap: 8px;
      color: var(--sd-text-2);
    }
    li sd-icon {
      margin-top: 3px;
      color: var(--sd-success);
    }
  `,
})
export class ModulePlaceholderComponent {
  readonly title = input.required<string>();
  readonly subtitle = input<string | null>(null);
  readonly icon = input<string | null>(null);
  readonly planned = input<string[]>([]);
  readonly breadcrumbs = input<SdBreadcrumb[]>([]);
}
