import { ChangeDetectionStrategy, Component, computed, input, model } from '@angular/core';
import { PortalFieldDefDto } from '@sd/core';
import { SdCheckbox, SdCombobox, SdDateInput, SdFormField, SdInput, SdOption } from '@sd/ui';

/** Value of a custom field while editing (serialised to JSON on submit). */
export type FieldValue = string | string[] | boolean | null;

/**
 * One admin-defined ticket field rendered by its type (Text, TextArea, Number, Decimal, Currency, Date,
 * DateTime, Checkbox, Picklist, MultiPicklist, Url, Email, Phone) inside a labelled sd-form-field.
 * `<portal-custom-field [field]="def" [(value)]="values()[def.key]" [error]="errors()[def.key]" />`
 */
@Component({
  selector: 'portal-custom-field',
  imports: [SdFormField, SdInput, SdCheckbox, SdCombobox, SdDateInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @switch (kind()) {
      @case ('checkbox') {
        <sd-form-field [hint]="field().helpText" [error]="error()">
          <sd-checkbox [value]="value() === true" (valueChange)="value.set($event)">
            {{ field().label }}
            @if (field().required) {
              <span class="req" aria-hidden="true">*</span>
            }
          </sd-checkbox>
        </sd-form-field>
      }
      @default {
        <sd-form-field [label]="field().label" [required]="field().required" [hint]="field().helpText" [error]="error()">
          @switch (kind()) {
            @case ('textarea') {
              <textarea sdInput rows="4" [attr.maxlength]="field().maxLength" [value]="text()" (input)="onText($event)"></textarea>
            }
            @case ('picklist') {
              <select sdInput (change)="onText($event)">
                <option value="" [selected]="!text()">Select…</option>
                @for (o of field().options; track o.value) {
                  <option [value]="o.value" [selected]="o.value === text()">{{ o.label }}</option>
                }
              </select>
            }
            @case ('multi') {
              <sd-combobox multiple [options]="options()" [value]="list()" (valueChange)="onList($event)" placeholder="Select one or more…" />
            }
            @case ('date') {
              <sd-date-input type="date" [value]="text() || null" (valueChange)="value.set($event)" />
            }
            @case ('datetime') {
              <sd-date-input type="datetime" [value]="text() || null" (valueChange)="value.set($event)" />
            }
            @case ('number') {
              <input
                sdInput
                type="number"
                [attr.step]="step()"
                [attr.min]="field().min"
                [attr.max]="field().max"
                [attr.inputmode]="step() === '1' ? 'numeric' : 'decimal'"
                [value]="text()"
                (input)="onText($event)"
              />
            }
            @default {
              <input
                sdInput
                [type]="inputType()"
                [attr.maxlength]="field().maxLength"
                [attr.autocomplete]="inputType() === 'email' ? 'email' : inputType() === 'tel' ? 'tel' : 'off'"
                [value]="text()"
                (input)="onText($event)"
              />
            }
          }
        </sd-form-field>
      }
    }
  `,
  styles: `
    .req {
      margin-left: 2px;
      color: var(--sd-danger);
    }
  `,
})
export class CustomFieldComponent {
  readonly field = input.required<PortalFieldDefDto>();
  readonly value = model<FieldValue>(null);
  readonly error = input<string | null>(null);

  protected readonly kind = computed(() => {
    switch (this.field().fieldType.toLowerCase()) {
      case 'checkbox':
        return 'checkbox';
      case 'textarea':
        return 'textarea';
      case 'picklist':
        return 'picklist';
      case 'multipicklist':
        return 'multi';
      case 'date':
        return 'date';
      case 'datetime':
        return 'datetime';
      case 'number':
      case 'decimal':
      case 'currency':
        return 'number';
      default:
        return 'text';
    }
  });
  protected readonly inputType = computed(() => {
    switch (this.field().fieldType.toLowerCase()) {
      case 'email':
        return 'email';
      case 'url':
        return 'url';
      case 'phone':
        return 'tel';
      default:
        return 'text';
    }
  });
  protected readonly step = computed(() => (this.field().fieldType.toLowerCase() === 'number' ? '1' : 'any'));
  protected readonly options = computed<SdOption[]>(() => this.field().options.map((o) => ({ value: o.value, label: o.label })));
  protected readonly text = computed(() => {
    const v = this.value();
    return typeof v === 'string' ? v : '';
  });
  protected readonly list = computed(() => {
    const v = this.value();
    return Array.isArray(v) ? v : [];
  });

  protected onText(event: Event): void {
    this.value.set((event.target as HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement).value);
  }

  protected onList(value: string | string[] | null): void {
    this.value.set(Array.isArray(value) ? value : value ? [value] : []);
  }
}
