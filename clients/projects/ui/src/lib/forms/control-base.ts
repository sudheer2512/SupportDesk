import { Directive, ModelSignal, booleanAttribute, computed, inject, input, signal } from '@angular/core';
import { ControlValueAccessor } from '@angular/forms';
import { uniqueId } from '@sd/core';
import { SD_FORM_FIELD } from './form-field.component';

/**
 * Shared ControlValueAccessor plumbing for @sd/ui form controls. Every control exposes a `value`
 * model (two-way `[(value)]`) AND works with ngModel / formControl. `disabled` combines the input
 * with the forms API state.
 */
@Directive()
export abstract class SdControlBase<T> implements ControlValueAccessor {
  abstract readonly value: ModelSignal<T>;
  readonly disabledInput = input(false, { alias: 'disabled', transform: booleanAttribute });

  protected readonly field = inject(SD_FORM_FIELD, { optional: true });
  protected readonly fallbackId = uniqueId('sd-control');
  protected readonly cvaDisabled = signal(false);
  readonly isDisabled = computed(() => this.disabledInput() || this.cvaDisabled());
  /** id for the focusable element (links the form-field label). */
  readonly controlId = computed(() => this.field?.controlId() ?? this.fallbackId);
  readonly describedBy = computed(() => this.field?.describedBy() ?? null);
  readonly invalid = computed(() => this.field?.invalid() ?? false);

  protected onChange: (value: T) => void = () => undefined;
  protected onTouched: () => void = () => undefined;

  protected abstract emptyValue(): T;

  writeValue(value: T | null | undefined): void {
    this.value.set(value === null || value === undefined ? this.emptyValue() : value);
  }

  registerOnChange(fn: (value: T) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(isDisabled: boolean): void {
    this.cvaDisabled.set(isDisabled);
  }

  /** Sets the value from user interaction and notifies forms. */
  protected commit(value: T): void {
    this.value.set(value);
    this.onChange(value);
  }

  protected touched(): void {
    this.onTouched();
  }
}
