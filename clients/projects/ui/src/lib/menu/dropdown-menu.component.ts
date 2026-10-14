import { CdkMenu, CdkMenuItem, CdkMenuTrigger } from '@angular/cdk/menu';
import { ConnectedPosition } from '@angular/cdk/overlay';
import { ChangeDetectionStrategy, Component, booleanAttribute, computed, input, output } from '@angular/core';
import { SdButton, SdButtonSize, SdButtonVariant } from '../button/button.component';
import { SdIcon } from '../icon/icon.component';

export interface SdMenuItem {
  id: string;
  label?: string;
  icon?: string;
  /** Right-aligned hint, e.g. a shortcut ("⌘K") or count. */
  hint?: string;
  danger?: boolean;
  disabled?: boolean;
  /** Renders a separator instead of an item. */
  divider?: boolean;
  /** Renders a non-interactive group heading. */
  heading?: boolean;
  /** Called when chosen (in addition to the `select` output). */
  run?: () => void;
}

/**
 * `<sd-dropdown-menu label="Actions" icon="ellipsis" [items]="items" (select)="on($event)" />`
 * Button + CDK menu (role=menu, arrow-key navigation, typeahead, Escape/outside click closes, focus
 * returns to the trigger). For custom triggers use CDK's `[cdkMenuTriggerFor]` with the global
 * `.sd-menu` / `.sd-menu-item` classes.
 */
@Component({
  selector: 'sd-dropdown-menu',
  imports: [CdkMenuTrigger, CdkMenu, CdkMenuItem, SdButton, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      sdButton
      type="button"
      [variant]="variant()"
      [size]="size()"
      [icon]="icon()"
      [iconRight]="caret() && !iconOnly() ? 'chevron-down' : null"
      [iconOnly]="iconOnly()"
      [disabled]="disabled()"
      [attr.aria-label]="ariaLabel() || label() || null"
      [cdkMenuTriggerFor]="menu"
      [cdkMenuPosition]="positions()"
    >
      {{ label() }}
    </button>
    <ng-template #menu>
      <div class="sd-menu" cdkMenu [attr.aria-label]="ariaLabel() || label() || null">
        @for (item of items(); track item.id) {
          @if (item.divider) {
            <div class="sd-menu-divider" role="separator"></div>
          } @else if (item.heading) {
            <div class="sd-menu-label" role="presentation">{{ item.label }}</div>
          } @else {
            <button
              type="button"
              class="sd-menu-item"
              cdkMenuItem
              [class.is-danger]="item.danger"
              [cdkMenuItemDisabled]="!!item.disabled"
              (cdkMenuItemTriggered)="choose(item)"
            >
              @if (item.icon) {
                <sd-icon [name]="item.icon" [size]="15" />
              }
              <span class="sd-truncate">{{ item.label }}</span>
              @if (item.hint) {
                <span class="sd-menu-hint">{{ item.hint }}</span>
              }
            </button>
          }
        }
      </div>
    </ng-template>
  `,
  styles: `
    :host {
      display: inline-flex;
    }
  `,
})
export class SdDropdownMenu {
  readonly items = input.required<SdMenuItem[]>();
  readonly label = input<string>('');
  readonly icon = input<string | null>(null);
  readonly iconOnly = input(false, { transform: booleanAttribute });
  readonly caret = input(true, { transform: booleanAttribute });
  readonly variant = input<SdButtonVariant>('secondary');
  readonly size = input<SdButtonSize>('md');
  readonly disabled = input(false, { transform: booleanAttribute });
  readonly ariaLabel = input<string | null>(null);
  readonly align = input<'start' | 'end'>('start');
  readonly select = output<SdMenuItem>();

  protected readonly positions = computed<ConnectedPosition[]>(() => {
    const x = this.align();
    return [
      { originX: x, originY: 'bottom', overlayX: x, overlayY: 'top', offsetY: 4 },
      { originX: x, originY: 'top', overlayX: x, overlayY: 'bottom', offsetY: -4 },
    ];
  });

  protected choose(item: SdMenuItem): void {
    item.run?.();
    this.select.emit(item);
  }
}
