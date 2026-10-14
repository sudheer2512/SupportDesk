import { ChangeDetectionStrategy, Component, computed, input, model } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CustomFieldDto, JsonValue } from '@sd/core';
import { SdCheckbox, SdCombobox, SdDateInput, SdFormField, SdInput, SdOption } from '@sd/ui';

/**
 * Edits tenant-defined custom field values for one entity:
 * `<desk-custom-fields-editor [fields]="customerFields()" [(values)]="customFields" [errors]="errors()" />`.
 * Errors are keyed `customFields.<key>` (as returned by the API) or by the bare key.
 */
@Component({
  selector: 'desk-custom-fields-editor',
  imports: [FormsModule, SdFormField, SdInput, SdCheckbox, SdCombobox, SdDateInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="grid">
      @for (f of active(); track f.key) {
        <sd-form-field [label]="f.label" [hint]="f.helpText" [required]="f.isRequired" [error]="errorFor(f.key)">
          @switch (f.fieldType) {
            @case ('TextArea') {
              <textarea sdInput rows="3" [ngModel]="text(f.key)" (ngModelChange)="set(f.key, $event)" [name]="'cf-' + f.key"></textarea>
            }
            @case ('Number') {
              <input sdInput type="number" [ngModel]="values()[f.key]" (ngModelChange)="set(f.key, $event === '' ? null : +$event)" [name]="'cf-' + f.key" />
            }
            @case ('Decimal') {
              <input sdInput type="number" step="0.01" [ngModel]="values()[f.key]" (ngModelChange)="set(f.key, $event === '' ? null : +$event)" [name]="'cf-' + f.key" />
            }
            @case ('Currency') {
              <input sdInput type="number" step="0.01" [ngModel]="values()[f.key]" (ngModelChange)="set(f.key, $event === '' ? null : +$event)" [name]="'cf-' + f.key" />
            }
            @case ('Date') {
              <sd-date-input [value]="text(f.key) || null" (valueChange)="set(f.key, $event)" />
            }
            @case ('DateTime') {
              <sd-date-input type="datetime" [value]="text(f.key) || null" (valueChange)="set(f.key, $event)" />
            }
            @case ('Checkbox') {
              <sd-checkbox [value]="values()[f.key] === true" (valueChange)="set(f.key, $event)">{{ f.label }}</sd-checkbox>
            }
            @case ('Picklist') {
              <select sdInput [ngModel]="text(f.key)" (ngModelChange)="set(f.key, $event || null)" [name]="'cf-' + f.key">
                <option value="">—</option>
                @for (o of options(f); track o.value) {
                  <option [value]="o.value">{{ o.label }}</option>
                }
              </select>
            }
            @case ('MultiPicklist') {
              <sd-combobox multiple [options]="options(f)" [value]="list(f.key)" (valueChange)="set(f.key, $event)" />
            }
            @default {
              <input
                sdInput
                [type]="f.fieldType === 'Email' ? 'email' : f.fieldType === 'Url' ? 'url' : f.fieldType === 'Phone' ? 'tel' : 'text'"
                [ngModel]="text(f.key)"
                (ngModelChange)="set(f.key, $event)"
                [name]="'cf-' + f.key"
              />
            }
          }
        </sd-form-field>
      }
    </div>
  `,
  styles: `
    .grid {
      display: grid;
      gap: var(--sd-space-3) var(--sd-space-4);
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 220px), 1fr));
    }
  `,
})
export class CustomFieldsEditorComponent {
  readonly fields = input<CustomFieldDto[]>([]);
  readonly values = model<Record<string, JsonValue>>({});
  readonly errors = input<Record<string, string>>({});

  protected readonly active = computed(() => this.fields().filter((f) => f.isActive).sort((a, b) => a.sortOrder - b.sortOrder));

  protected errorFor(key: string): string | null {
    const e = this.errors();
    return e[`customFields.${key}`] ?? e[key] ?? null;
  }

  protected text(key: string): string {
    const v = this.values()[key];
    return v === null || v === undefined ? '' : String(v);
  }

  protected list(key: string): string[] {
    const v = this.values()[key];
    return Array.isArray(v) ? v.map(String) : [];
  }

  protected options(f: CustomFieldDto): SdOption[] {
    const raw = f.options as unknown;
    if (!Array.isArray(raw)) return [];
    return raw.map((o) =>
      typeof o === 'string' ? { value: o, label: o } : { value: String((o as { value: unknown }).value), label: String((o as { label?: unknown }).label ?? (o as { value: unknown }).value) },
    );
  }

  protected set(key: string, value: JsonValue | string | string[] | null): void {
    this.values.update((v) => ({ ...v, [key]: value as JsonValue }));
  }
}
