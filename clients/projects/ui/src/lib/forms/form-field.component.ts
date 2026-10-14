import {
  ChangeDetectionStrategy,
  Component,
  Directive,
  InjectionToken,
  booleanAttribute,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { uniqueId } from '@sd/core';
import { SdIcon } from '../icon/icon.component';

/** What a control inside <sd-form-field> reads to wire up its id, aria-describedby and aria-invalid. */
export interface SdFormFieldContext {
  controlId(): string;
  labelId: string;
  describedBy(): string | null;
  invalid(): boolean;
  required(): boolean;
  registerControlId(id: string): void;
}

export const SD_FORM_FIELD = new InjectionToken<SdFormFieldContext>('SD_FORM_FIELD');

/**
 * `<sd-form-field label="Subject" hint="Shown to the customer" [error]="errors.subject" required>
 *    <input sdInput [(ngModel)]="subject" />
 *  </sd-form-field>`
 * Renders label / hint / error and links them to the projected control (id, aria-describedby, aria-invalid).
 * Works with native inputs (`sdInput`) and every @sd/ui control (combobox, date, chips, …).
 */
@Component({
  selector: 'sd-form-field',
  imports: [SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: SD_FORM_FIELD, useExisting: SdFormField }],
  template: `
    @if (label()) {
      <label class="label" [id]="labelId" [attr.for]="controlId()">
        {{ label() }}
        @if (required()) {
          <span class="req" aria-hidden="true">*</span>
        }
        @if (optional()) {
          <span class="opt">(optional)</span>
        }
      </label>
    }
    <div class="control"><ng-content /></div>
    @if (error()) {
      <p class="error" [id]="errorId" role="alert"><sd-icon name="circle-alert" [size]="13" />{{ error() }}</p>
    } @else if (hint()) {
      <p class="hint" [id]="hintId">{{ hint() }}</p>
    }
  `,
  host: { '[class.is-invalid]': '!!error()', '[class.is-inline]': 'inline()' },
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: 6px;
      min-width: 0;
    }
    :host(.is-inline) {
      flex-direction: row;
      align-items: center;
      flex-wrap: wrap;
    }
    :host(.is-inline) .label {
      min-width: 140px;
    }
    .label {
      color: var(--sd-text);
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-medium);
    }
    .req {
      color: var(--sd-danger);
      margin-left: 2px;
    }
    .opt {
      color: var(--sd-text-3);
      font-weight: var(--sd-weight-regular);
      margin-left: 4px;
    }
    .control {
      min-width: 0;
    }
    :host(.is-inline) .control {
      flex: 1;
    }
    .hint,
    .error {
      display: flex;
      align-items: flex-start;
      gap: 4px;
      font-size: var(--sd-text-xs);
      line-height: 1.4;
    }
    .hint {
      color: var(--sd-text-3);
    }
    .error {
      color: var(--sd-danger);
    }
    .error sd-icon {
      margin-top: 1px;
    }
  `,
})
export class SdFormField implements SdFormFieldContext {
  readonly label = input<string | null>(null);
  readonly hint = input<string | null>(null);
  readonly error = input<string | null | undefined>(null);
  readonly required = input(false, { transform: booleanAttribute });
  readonly optional = input(false, { transform: booleanAttribute });
  readonly inline = input(false, { transform: booleanAttribute });

  readonly labelId = uniqueId('sd-label');
  protected readonly hintId = uniqueId('sd-hint');
  protected readonly errorId = uniqueId('sd-error');
  private readonly _controlId = signal(uniqueId('sd-control'));

  readonly controlId = this._controlId.asReadonly();
  readonly invalid = computed(() => !!this.error());
  readonly describedBy = computed(() => (this.error() ? this.errorId : this.hint() ? this.hintId : null));

  registerControlId(id: string): void {
    this._controlId.set(id);
  }
}

/**
 * Styles a native `<input>`, `<textarea>` or `<select>` and wires it to the surrounding sd-form-field.
 * Use with ngModel / formControl as usual: `<input sdInput type="number" [(ngModel)]="minutes" />`.
 */
@Directive({
  selector: 'input[sdInput], textarea[sdInput], select[sdInput]',
  host: {
    class: 'sd-input',
    '[class.sd-input-sm]': 'size() === "sm"',
    '[id]': 'resolvedId()',
    '[attr.aria-describedby]': 'field?.describedBy() ?? null',
    '[attr.aria-invalid]': 'invalid() ? "true" : null',
    '[attr.aria-required]': 'field?.required() ? "true" : null',
  },
})
export class SdInput {
  readonly id = input<string | null>(null);
  readonly size = input<'sm' | 'md'>('md');
  /** Force the invalid style without a form-field error. */
  readonly sdInvalid = input(false, { transform: booleanAttribute });

  protected readonly field = inject(SD_FORM_FIELD, { optional: true });
  private readonly ownId = uniqueId('sd-input');
  protected readonly resolvedId = computed(() => this.id() ?? this.field?.controlId() ?? this.ownId);
  protected readonly invalid = computed(() => this.sdInvalid() || (this.field?.invalid() ?? false));

  constructor() {
    effect(() => {
      const explicit = this.id();
      if (explicit) this.field?.registerControlId(explicit);
    });
  }
}
