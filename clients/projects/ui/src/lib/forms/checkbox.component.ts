import { ChangeDetectionStrategy, Component, booleanAttribute, forwardRef, input, model } from '@angular/core';
import { NG_VALUE_ACCESSOR } from '@angular/forms';
import { SdIcon } from '../icon/icon.component';
import { SdControlBase } from './control-base';

/**
 * `<sd-checkbox [(value)]="agree">I agree</sd-checkbox>` — native checkbox (keyboard + screen reader
 * semantics intact) with custom visuals. Supports `indeterminate` and ngModel/formControl.
 */
@Component({
  selector: 'sd-checkbox',
  imports: [SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => SdCheckbox), multi: true }],
  template: `
    <label class="wrap" [class.is-disabled]="isDisabled()">
      <input
        type="checkbox"
        [id]="controlId()"
        [checked]="value()"
        [indeterminate]="indeterminate()"
        [disabled]="isDisabled()"
        [attr.aria-label]="ariaLabel()"
        [attr.aria-describedby]="describedBy()"
        (change)="onToggle($event)"
        (blur)="touched()"
      />
      <span class="box" aria-hidden="true">
        @if (indeterminate()) {
          <sd-icon name="minus" [size]="12" [strokeWidth]="3" />
        } @else if (value()) {
          <sd-icon name="check" [size]="12" [strokeWidth]="3" />
        }
      </span>
      <span class="text"><ng-content /></span>
    </label>
  `,
  styles: `
    :host {
      display: inline-flex;
    }
    .wrap {
      position: relative;
      display: inline-flex;
      align-items: center;
      gap: 8px;
      cursor: pointer;
      user-select: none;
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
      margin: 0;
      cursor: inherit;
    }
    .box {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 16px;
      height: 16px;
      flex: none;
      border: 1.5px solid var(--sd-border-strong);
      border-radius: 4px;
      background: var(--sd-surface);
      color: var(--sd-on-primary);
      transition: background-color var(--sd-dur-fast) var(--sd-ease), border-color var(--sd-dur-fast) var(--sd-ease);
    }
    input:checked + .box,
    input:indeterminate + .box {
      background: var(--sd-primary);
      border-color: var(--sd-primary);
    }
    input:focus-visible + .box {
      box-shadow: var(--sd-focus-ring);
    }
    input:disabled + .box {
      opacity: 0.5;
    }
    .text:empty {
      display: none;
    }
  `,
})
export class SdCheckbox extends SdControlBase<boolean> {
  readonly value = model(false);
  readonly indeterminate = input(false, { transform: booleanAttribute });
  readonly ariaLabel = input<string | null>(null);

  protected emptyValue(): boolean {
    return false;
  }

  protected onToggle(event: Event): void {
    this.commit((event.target as HTMLInputElement).checked);
  }
}

/**
 * `<sd-toggle [(value)]="enabled">Email alerts</sd-toggle>` — on/off switch (role="switch").
 */
@Component({
  selector: 'sd-toggle',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => SdToggle), multi: true }],
  template: `
    <button
      type="button"
      role="switch"
      class="switch"
      [id]="controlId()"
      [attr.aria-checked]="value()"
      [attr.aria-label]="ariaLabel()"
      [attr.aria-describedby]="describedBy()"
      [disabled]="isDisabled()"
      (click)="commit(!value())"
      (blur)="touched()"
    >
      <span class="track" aria-hidden="true"><span class="thumb"></span></span>
      <span class="text"><ng-content /></span>
    </button>
  `,
  styles: `
    :host {
      display: inline-flex;
    }
    .switch {
      display: inline-flex;
      align-items: center;
      gap: 10px;
      padding: 2px 0;
      border: 0;
      background: transparent;
      color: var(--sd-text);
      cursor: pointer;
      text-align: left;
    }
    .switch:disabled {
      cursor: not-allowed;
      color: var(--sd-text-disabled);
    }
    .track {
      position: relative;
      width: 32px;
      height: 18px;
      flex: none;
      border-radius: 999px;
      background: var(--sd-border-strong);
      transition: background-color var(--sd-dur) var(--sd-ease);
    }
    .thumb {
      position: absolute;
      top: 2px;
      left: 2px;
      width: 14px;
      height: 14px;
      border-radius: 50%;
      background: #fff;
      box-shadow: 0 1px 2px rgba(0, 0, 0, 0.25);
      transition: transform var(--sd-dur) var(--sd-ease);
    }
    .switch[aria-checked='true'] .track {
      background: var(--sd-primary);
    }
    .switch[aria-checked='true'] .thumb {
      transform: translateX(14px);
    }
    .switch:focus-visible {
      outline: none;
    }
    .switch:focus-visible .track {
      box-shadow: var(--sd-focus-ring);
    }
    .switch:disabled .track {
      opacity: 0.5;
    }
    .text:empty {
      display: none;
    }
  `,
})
export class SdToggle extends SdControlBase<boolean> {
  readonly value = model(false);
  readonly ariaLabel = input<string | null>(null);

  protected emptyValue(): boolean {
    return false;
  }

  protected override commit(value: boolean): void {
    super.commit(value);
  }
}
