import { ChangeDetectionStrategy, Component, booleanAttribute, input } from '@angular/core';
import { SdIcon } from '../icon/icon.component';

/**
 * `<sd-empty-state icon="inbox" title="No tickets match" message="Try clearing filters.">
 *    <button sdButton …>Clear filters</button>
 *  </sd-empty-state>` — centred placeholder for empty lists, errors and not-yet-built areas.
 */
@Component({
  selector: 'sd-empty-state',
  imports: [SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="art" aria-hidden="true"><sd-icon [name]="icon()" [size]="compact() ? 22 : 28" /></div>
    <h3 class="title">{{ title() }}</h3>
    @if (message()) {
      <p class="message">{{ message() }}</p>
    }
    <div class="actions"><ng-content /></div>
  `,
  host: { '[class.is-compact]': 'compact()', role: 'status' },
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      gap: var(--sd-space-2);
      padding: var(--sd-space-9) var(--sd-space-5);
      text-align: center;
    }
    :host(.is-compact) {
      padding: var(--sd-space-5) var(--sd-space-4);
    }
    .art {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 56px;
      height: 56px;
      margin-bottom: var(--sd-space-1);
      border-radius: 50%;
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    :host(.is-compact) .art {
      width: 44px;
      height: 44px;
    }
    .title {
      font-size: var(--sd-text-lg);
    }
    :host(.is-compact) .title {
      font-size: var(--sd-text-md);
    }
    .message {
      max-width: 460px;
      color: var(--sd-text-2);
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--sd-space-2);
      justify-content: center;
      margin-top: var(--sd-space-2);
    }
    .actions:empty {
      display: none;
    }
  `,
})
export class SdEmptyState {
  readonly icon = input('inbox');
  readonly title = input.required<string>();
  readonly message = input<string | null>(null);
  readonly compact = input(false, { transform: booleanAttribute });
}
