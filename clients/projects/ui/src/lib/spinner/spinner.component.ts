import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** `<sd-spinner [size]="16" label="Loading tickets" />` — indeterminate spinner; announces `label` when given. */
@Component({
  selector: 'sd-spinner',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <svg viewBox="0 0 24 24" [attr.width]="size()" [attr.height]="size()" aria-hidden="true">
      <circle cx="12" cy="12" r="9" fill="none" stroke="currentColor" stroke-width="2.5" opacity="0.2" />
      <path d="M21 12a9 9 0 0 0-9-9" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" />
    </svg>
    @if (label()) {
      <span class="sd-visually-hidden">{{ label() }}</span>
    }
  `,
  host: { '[attr.role]': 'label() ? "status" : null' },
  styles: `
    :host {
      display: inline-flex;
      line-height: 0;
      color: inherit;
    }
    svg {
      animation: sd-spin 0.8s linear infinite;
    }
    @keyframes sd-spin {
      to {
        transform: rotate(360deg);
      }
    }
    @media (prefers-reduced-motion: reduce) {
      svg {
        animation-duration: 2.4s !important;
        animation-iteration-count: infinite !important;
      }
    }
  `,
})
export class SdSpinner {
  readonly size = input<number>(16);
  readonly label = input<string | null>(null);
}
