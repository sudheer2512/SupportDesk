import { ChangeDetectionStrategy, Component, booleanAttribute, forwardRef, inject, input, model } from '@angular/core';
import { NG_VALUE_ACCESSOR } from '@angular/forms';
import { uniqueId } from '@sd/core';
import { SdControlBase } from './control-base';

/**
 * `<sd-radio-group [(value)]="mode" ariaLabel="Delivery"><sd-radio value="email">E-mail</sd-radio>…</sd-radio-group>`
 * Native radios (arrow keys move the selection) inside role="radiogroup"; ngModel/formControl capable.
 */
@Component({
  selector: 'sd-radio-group',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => SdRadioGroup), multi: true }],
  template: `<ng-content />`,
  host: {
    role: 'radiogroup',
    '[attr.aria-labelledby]': 'ariaLabel() ? null : (field?.labelId ?? null)',
    '[attr.aria-label]': 'ariaLabel()',
    '[attr.aria-describedby]': 'describedBy()',
    '[class.is-row]': 'direction() === "row"',
  },
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    :host(.is-row) {
      flex-direction: row;
      flex-wrap: wrap;
      gap: 16px;
    }
  `,
})
export class SdRadioGroup extends SdControlBase<string | null> {
  readonly value = model<string | null>(null);
  readonly ariaLabel = input<string | null>(null);
  readonly direction = input<'column' | 'row'>('column');
  readonly name = uniqueId('sd-radio');

  protected emptyValue(): string | null {
    return null;
  }

  choose(value: string): void {
    this.commit(value);
  }

  markTouched(): void {
    this.touched();
  }
}

/** One option of `<sd-radio-group>`. Optional `description` renders under the label. */
@Component({
  selector: 'sd-radio',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <label class="wrap" [class.is-disabled]="isDisabled()">
      <input
        type="radio"
        [name]="group.name"
        [value]="value()"
        [checked]="group.value() === value()"
        [disabled]="isDisabled()"
        (change)="group.choose(value())"
        (blur)="group.markTouched()"
      />
      <span class="dot" aria-hidden="true"></span>
      <span class="text">
        <span><ng-content /></span>
        @if (description()) {
          <span class="desc">{{ description() }}</span>
        }
      </span>
    </label>
  `,
  styles: `
    .wrap {
      position: relative;
      display: inline-flex;
      align-items: flex-start;
      gap: 8px;
      cursor: pointer;
    }
    .wrap.is-disabled {
      cursor: not-allowed;
      color: var(--sd-text-disabled);
    }
    input {
      position: absolute;
      opacity: 0;
      width: 16px;
      height: 16px;
      margin: 2px 0 0;
    }
    .dot {
      width: 16px;
      height: 16px;
      margin-top: 2px;
      flex: none;
      border: 1.5px solid var(--sd-border-strong);
      border-radius: 50%;
      background: var(--sd-surface);
      transition: border-color var(--sd-dur-fast) var(--sd-ease), box-shadow var(--sd-dur-fast) var(--sd-ease);
    }
    input:checked + .dot {
      border-color: var(--sd-primary);
      box-shadow: inset 0 0 0 3.5px var(--sd-surface), inset 0 0 0 8px var(--sd-primary);
    }
    input:focus-visible + .dot {
      box-shadow: var(--sd-focus-ring);
    }
    input:checked:focus-visible + .dot {
      box-shadow: inset 0 0 0 3.5px var(--sd-surface), inset 0 0 0 8px var(--sd-primary), var(--sd-focus-ring);
    }
    .text {
      display: flex;
      flex-direction: column;
    }
    .desc {
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
  `,
})
export class SdRadio {
  readonly value = input.required<string>();
  readonly description = input<string | null>(null);
  readonly disabled = input(false, { transform: booleanAttribute });

  protected readonly group = inject(SdRadioGroup);
  protected isDisabled(): boolean {
    return this.disabled() || this.group.isDisabled();
  }
}
