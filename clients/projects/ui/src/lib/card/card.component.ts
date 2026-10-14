import { ChangeDetectionStrategy, Component, booleanAttribute, input } from '@angular/core';

/**
 * `<sd-card title="SLA health" subtitle="Last 7 days">…<div sdCardActions>…</div></sd-card>`
 * Surface container with optional header (title/subtitle + projected actions) and padding presets.
 * `interactive` adds hover elevation (for clickable cards — put the click handler on the card).
 */
@Component({
  selector: 'sd-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (title() || subtitle()) {
      <header class="head">
        <div class="titles">
          @if (title()) {
            <h3 class="title">{{ title() }}</h3>
          }
          @if (subtitle()) {
            <p class="subtitle">{{ subtitle() }}</p>
          }
        </div>
        <div class="actions"><ng-content select="[sdCardActions]" /></div>
      </header>
    }
    <div class="body"><ng-content /></div>
    <ng-content select="[sdCardFooter]" />
  `,
  host: {
    '[class]': '"sd-card sd-card--pad-" + padding()',
    '[class.sd-card--interactive]': 'interactive()',
    '[class.sd-card--flat]': 'flat()',
  },
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      min-width: 0;
      background: var(--sd-surface);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      box-shadow: var(--sd-shadow-xs);
      transition:
        box-shadow var(--sd-dur) var(--sd-ease),
        border-color var(--sd-dur) var(--sd-ease),
        transform var(--sd-dur) var(--sd-ease);
    }
    :host(.sd-card--flat) {
      box-shadow: none;
    }
    :host(.sd-card--interactive) {
      cursor: pointer;
    }
    :host(.sd-card--interactive:hover) {
      border-color: var(--sd-border-strong);
      box-shadow: var(--sd-shadow-md);
    }
    .head {
      display: flex;
      align-items: flex-start;
      gap: var(--sd-space-3);
      padding: var(--sd-space-4) var(--sd-space-4) 0;
    }
    .titles {
      flex: 1;
      min-width: 0;
    }
    .title {
      font-size: var(--sd-text-md);
      font-weight: var(--sd-weight-semibold);
    }
    .subtitle {
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
      margin-top: 2px;
    }
    .actions {
      display: flex;
      gap: var(--sd-space-1);
      align-items: center;
    }
    .body {
      flex: 1;
      min-height: 0;
      padding: var(--sd-space-4);
    }
    :host(.sd-card--pad-none) .body {
      padding: 0;
    }
    :host(.sd-card--pad-sm) .body {
      padding: var(--sd-space-3);
    }
    :host(.sd-card--pad-lg) .body {
      padding: var(--sd-space-6);
    }
  `,
})
export class SdCard {
  readonly title = input<string | null>(null);
  readonly subtitle = input<string | null>(null);
  readonly padding = input<'none' | 'sm' | 'md' | 'lg'>('md');
  readonly interactive = input(false, { transform: booleanAttribute });
  readonly flat = input(false, { transform: booleanAttribute });
}
