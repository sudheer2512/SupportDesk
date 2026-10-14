import { ChangeDetectionStrategy, Component, booleanAttribute, input } from '@angular/core';

/**
 * `<sd-logo [size]="28" wordmark />` — the SupportDesk product mark (a speech bubble folded into a
 * check-tick inside a rounded tile) with optional wordmark. Uses the primary token, so it follows theme.
 */
@Component({
  selector: 'sd-logo',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <svg [attr.width]="size()" [attr.height]="size()" viewBox="0 0 32 32" aria-hidden="true">
      <rect width="32" height="32" rx="9" fill="var(--sd-primary)" />
      <path
        d="M9 10.5a2.5 2.5 0 0 1 2.5-2.5h9a2.5 2.5 0 0 1 2.5 2.5v7a2.5 2.5 0 0 1-2.5 2.5h-5.2L11.6 23.4a.6.6 0 0 1-1-.45V20h0a2.5 2.5 0 0 1-1.6-2.33z"
        fill="var(--sd-on-primary)"
        opacity="0.96"
      />
      <path d="M12.6 14.2l2.2 2.2 4.4-4.6" fill="none" stroke="var(--sd-primary)" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" />
    </svg>
    @if (wordmark()) {
      <span class="word">Support<strong>Desk</strong></span>
    }
  `,
  host: { role: 'img', '[attr.aria-label]': 'label()' },
  styles: `
    :host {
      display: inline-flex;
      align-items: center;
      gap: 9px;
      color: var(--sd-text);
    }
    svg {
      flex: none;
    }
    .word {
      font-size: 1.05rem;
      font-weight: 520;
      letter-spacing: -0.02em;
      white-space: nowrap;
    }
    .word strong {
      font-weight: 720;
      color: var(--sd-primary);
    }
  `,
})
export class SdLogo {
  readonly size = input(28);
  readonly wordmark = input(false, { transform: booleanAttribute });
  readonly label = input('SupportDesk');
}
