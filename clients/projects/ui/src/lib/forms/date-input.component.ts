import { ChangeDetectionStrategy, Component, computed, forwardRef, input, model } from '@angular/core';
import { NG_VALUE_ACCESSOR } from '@angular/forms';
import { fromDateTimeInputValue, toDateTimeInputValue } from '@sd/core';
import { SdControlBase } from './control-base';

/**
 * `<sd-date-input type="date" [(value)]="due" />` → value "YYYY-MM-DD" (calendar date, no time zone).
 * `<sd-date-input type="datetime" [(value)]="remindAt" />` → value is a UTC ISO string; the user edits
 * it in local time. `min`/`max` use the same formats as the value. Native pickers keep it accessible.
 */
@Component({
  selector: 'sd-date-input',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => SdDateInput), multi: true }],
  template: `
    <input
      class="sd-input"
      [class.sd-input-sm]="size() === 'sm'"
      [type]="type() === 'datetime' ? 'datetime-local' : 'date'"
      [id]="controlId()"
      [value]="display()"
      [min]="minDisplay()"
      [max]="maxDisplay()"
      [disabled]="isDisabled()"
      [attr.aria-describedby]="describedBy()"
      [attr.aria-invalid]="invalid() ? 'true' : null"
      [attr.aria-label]="ariaLabel()"
      (change)="onChange_($event)"
      (blur)="touched()"
    />
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
  `,
})
export class SdDateInput extends SdControlBase<string | null> {
  readonly value = model<string | null>(null);
  readonly type = input<'date' | 'datetime'>('date');
  readonly min = input<string | null>(null);
  readonly max = input<string | null>(null);
  readonly size = input<'sm' | 'md'>('md');
  readonly ariaLabel = input<string | null>(null);

  protected readonly display = computed(() => this.toDisplay(this.value()));
  protected readonly minDisplay = computed(() => this.toDisplay(this.min()) || null);
  protected readonly maxDisplay = computed(() => this.toDisplay(this.max()) || null);

  protected emptyValue(): string | null {
    return null;
  }

  protected onChange_(event: Event): void {
    const raw = (event.target as HTMLInputElement).value;
    this.commit(this.type() === 'datetime' ? fromDateTimeInputValue(raw) : raw || null);
  }

  private toDisplay(value: string | null): string {
    if (!value) return '';
    if (this.type() === 'datetime') return toDateTimeInputValue(value);
    return value.length >= 10 ? value.slice(0, 10) : value;
  }
}
