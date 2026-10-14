import { ChangeDetectionStrategy, Component, booleanAttribute, input } from '@angular/core';
import { SdIcon } from '../icon/icon.component';
import { SdSpinner } from '../spinner/spinner.component';

export type SdButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger' | 'subtle' | 'link';
export type SdButtonSize = 'sm' | 'md' | 'lg';

/**
 * `<button sdButton variant="primary" size="md" icon="plus" [loading]="saving()">Save</button>`
 * Also on `<a sdButton routerLink=…>`. While `loading` the button is disabled and aria-busy, with a spinner
 * replacing the leading icon. For icon-only buttons set `iconOnly` and an `aria-label`.
 */
@Component({
  selector: 'button[sdButton], a[sdButton]',
  imports: [SdIcon, SdSpinner],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading()) {
      <sd-spinner [size]="iconSize()" class="sd-btn__spinner" />
    } @else if (icon()) {
      <sd-icon [name]="icon()!" [size]="iconSize()" />
    }
    @if (!iconOnly()) {
      <span class="sd-btn__label"><ng-content /></span>
    }
    @if (iconRight() && !iconOnly()) {
      <sd-icon [name]="iconRight()!" [size]="iconSize()" />
    }
  `,
  host: {
    class: 'sd-btn',
    '[class]': '"sd-btn--" + variant() + " sd-btn--" + size()',
    '[class.sd-btn--icon-only]': 'iconOnly()',
    '[class.sd-btn--block]': 'block()',
    '[class.is-loading]': 'loading()',
    '[class.is-pressed]': 'pressed()',
    '[attr.disabled]': '(disabled() || loading()) ? "" : null',
    '[attr.aria-disabled]': '(disabled() || loading()) ? "true" : null',
    '[attr.aria-busy]': 'loading() ? "true" : null',
    '[attr.aria-pressed]': 'pressed() === null ? null : pressed()',
    '[attr.tabindex]': 'disabled() ? -1 : null',
  },
  styles: `
    :host {
      --_bg: var(--sd-surface);
      --_bg-hover: var(--sd-surface-3);
      --_fg: var(--sd-text);
      --_border: var(--sd-border-strong);
      position: relative;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      gap: 6px;
      height: var(--sd-control-h);
      padding: 0 12px;
      border: 1px solid var(--_border);
      border-radius: var(--sd-radius-md);
      background: var(--_bg);
      color: var(--_fg);
      font-size: var(--sd-text-md);
      font-weight: var(--sd-weight-medium);
      line-height: 1;
      white-space: nowrap;
      text-decoration: none;
      cursor: pointer;
      user-select: none;
      transition:
        background-color var(--sd-dur-fast) var(--sd-ease),
        border-color var(--sd-dur-fast) var(--sd-ease),
        color var(--sd-dur-fast) var(--sd-ease),
        box-shadow var(--sd-dur-fast) var(--sd-ease),
        transform var(--sd-dur-fast) var(--sd-ease);
    }
    :host(:hover) {
      background: var(--_bg-hover);
      text-decoration: none;
    }
    :host(:active:not([disabled])) {
      transform: translateY(0.5px);
    }
    :host(:focus-visible) {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    :host([disabled]) {
      cursor: not-allowed;
      opacity: 0.55;
      pointer-events: none;
    }
    :host(.is-loading) {
      opacity: 0.85;
      cursor: progress;
    }
    :host(.sd-btn--primary) {
      --_bg: var(--sd-primary);
      --_bg-hover: var(--sd-primary-hover);
      --_fg: var(--sd-on-primary);
      --_border: transparent;
      box-shadow: var(--sd-shadow-xs);
    }
    :host(.sd-btn--danger) {
      --_bg: var(--sd-danger);
      --_bg-hover: color-mix(in oklab, var(--sd-danger) 86%, var(--sd-mix-hover));
      --_fg: #fff;
      --_border: transparent;
    }
    :host(.sd-btn--ghost) {
      --_bg: transparent;
      --_bg-hover: var(--sd-surface-3);
      --_border: transparent;
      --_fg: var(--sd-text-2);
    }
    :host(.sd-btn--ghost:hover) {
      color: var(--sd-text);
    }
    :host(.sd-btn--subtle) {
      --_bg: var(--sd-primary-soft);
      --_bg-hover: var(--sd-primary-soft-hover);
      --_border: transparent;
      --_fg: var(--sd-primary);
    }
    :host(.sd-btn--link) {
      --_bg: transparent;
      --_bg-hover: transparent;
      --_border: transparent;
      --_fg: var(--sd-primary);
      padding: 0 2px;
      height: auto;
    }
    :host(.sd-btn--link:hover) {
      text-decoration: underline;
    }
    :host(.is-pressed) {
      --_bg: var(--sd-primary-soft);
      --_fg: var(--sd-primary);
    }
    :host(.sd-btn--sm) {
      height: var(--sd-control-h-sm);
      padding: 0 9px;
      font-size: var(--sd-text-sm);
      border-radius: var(--sd-radius-sm);
      gap: 5px;
    }
    :host(.sd-btn--lg) {
      height: var(--sd-control-h-lg);
      padding: 0 16px;
      font-size: var(--sd-text-lg);
    }
    :host(.sd-btn--icon-only) {
      width: var(--sd-control-h);
      padding: 0;
    }
    :host(.sd-btn--icon-only.sd-btn--sm) {
      width: var(--sd-control-h-sm);
    }
    :host(.sd-btn--icon-only.sd-btn--lg) {
      width: var(--sd-control-h-lg);
    }
    :host(.sd-btn--block) {
      display: flex;
      width: 100%;
    }
    .sd-btn__label {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      overflow: hidden;
      text-overflow: ellipsis;
    }
  `,
})
export class SdButton {
  readonly variant = input<SdButtonVariant>('secondary');
  readonly size = input<SdButtonSize>('md');
  readonly icon = input<string | null>(null);
  readonly iconRight = input<string | null>(null);
  readonly iconOnly = input(false, { transform: booleanAttribute });
  readonly block = input(false, { transform: booleanAttribute });
  readonly loading = input(false, { transform: booleanAttribute });
  readonly disabled = input(false, { transform: booleanAttribute });
  /** Toggle buttons: true/false sets aria-pressed; null (default) means not a toggle. */
  readonly pressed = input<boolean | null>(null);

  protected iconSize(): number {
    return this.size() === 'sm' ? 14 : this.size() === 'lg' ? 18 : 16;
  }
}
