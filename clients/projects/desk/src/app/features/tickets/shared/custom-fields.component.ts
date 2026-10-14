import { ChangeDetectionStrategy, Component, booleanAttribute, input, model } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CustomFieldDto, Guid, JsonValue, LayoutDto, TicketFormMetaDto } from '@sd/core';
import { SdCheckbox, SdCombobox, SdDateInput, SdFormField, SdInput, SdOption } from '@sd/ui';

interface LayoutSection {
  title?: string;
  fields?: { key?: string; required?: boolean }[];
}

/** A custom field as it applies on a form (layout may make it required). */
export interface FormCustomField extends CustomFieldDto {
  requiredHere: boolean;
}

/**
 * Ticket custom fields that apply to a department, ordered (and possibly made required) by the department's
 * agent form layout (`cf:<key>` entries), falling back to the default layout and then to field sort order.
 */
export function applicableCustomFields(meta: TicketFormMetaDto | null, departmentId: Guid | null, kind: 'AgentForm' | 'PortalForm' = 'AgentForm'): FormCustomField[] {
  const fields = (meta?.customFields ?? []).filter(
    (f) => f.entity === 'Ticket' && f.isActive && (f.departmentIds.length === 0 || (departmentId !== null && f.departmentIds.includes(departmentId))),
  );
  const layouts = (meta?.layouts ?? []).filter((l) => l.entity === 'Ticket' && l.kind === kind);
  const layout: LayoutDto | undefined = layouts.find((l) => l.departmentId === departmentId) ?? layouts.find((l) => l.isDefault);
  const order = new Map<string, { index: number; required: boolean }>();
  if (layout && Array.isArray(layout.sections)) {
    let i = 0;
    for (const section of layout.sections as unknown as LayoutSection[]) {
      for (const f of section?.fields ?? []) {
        if (typeof f?.key === 'string' && f.key.startsWith('cf:')) order.set(f.key.slice(3), { index: i++, required: !!f.required });
      }
    }
  }
  return fields
    .map((f) => ({ ...f, requiredHere: f.isRequired || !!order.get(f.key)?.required }))
    .sort((a, b) => (order.get(a.key)?.index ?? 1000 + a.sortOrder) - (order.get(b.key)?.index ?? 1000 + b.sortOrder));
}

export function customFieldOptions(field: CustomFieldDto): SdOption[] {
  const raw = field.options;
  if (!Array.isArray(raw)) return [];
  return raw
    .map((o) => {
      if (typeof o === 'string') return { value: o, label: o };
      if (o && typeof o === 'object' && !Array.isArray(o)) {
        const value = String((o as Record<string, JsonValue>)['value'] ?? '');
        return { value, label: String((o as Record<string, JsonValue>)['label'] ?? value) };
      }
      return null;
    })
    .filter((o): o is SdOption => !!o && o.value !== '');
}

function isEmpty(v: JsonValue | undefined): boolean {
  return v === null || v === undefined || v === '' || (Array.isArray(v) && v.length === 0);
}

/** Client-side checks mirroring the server (required, regex, e-mail/URL shape). Returns key → message. */
export function validateCustomFields(fields: readonly FormCustomField[], values: Record<string, JsonValue>): Record<string, string> {
  const errors: Record<string, string> = {};
  for (const f of fields) {
    const v = values[f.key];
    if (isEmpty(v)) {
      if (f.requiredHere) errors[f.key] = `${f.label} is required.`;
      continue;
    }
    const text = typeof v === 'string' ? v : null;
    if (text !== null && f.validationRegex) {
      try {
        if (!new RegExp(f.validationRegex).test(text)) errors[f.key] = `${f.label} has an invalid format.`;
      } catch {
        /* an invalid admin regex is the server's problem to report */
      }
    }
    if (text !== null && f.fieldType === 'Email' && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(text)) errors[f.key] = 'Enter a valid e-mail address.';
    if (text !== null && f.fieldType === 'Url' && !/^https?:\/\/\S+$/i.test(text)) errors[f.key] = 'Enter a full URL (https://…).';
  }
  return errors;
}

/** Editors for ticket custom fields, by field type. `[(values)]` holds key → value. */
@Component({
  selector: 'desk-custom-fields',
  imports: [FormsModule, SdFormField, SdInput, SdCombobox, SdCheckbox, SdDateInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @for (f of fields(); track f.id) {
      <sd-form-field [label]="f.label" [hint]="f.helpText" [required]="f.requiredHere" [error]="errors()[f.key]">
        @switch (f.fieldType) {
          @case ('TextArea') {
            <textarea sdInput rows="3" [name]="f.key" [ngModel]="str(f.key)" (ngModelChange)="set(f.key, $event)" [disabled]="disabled()"></textarea>
          }
          @case ('Number') {
            <input sdInput type="number" [name]="f.key" [ngModel]="values()[f.key]" (ngModelChange)="set(f.key, num($event))" [disabled]="disabled()" />
          }
          @case ('Decimal') {
            <input sdInput type="number" step="any" [name]="f.key" [ngModel]="values()[f.key]" (ngModelChange)="set(f.key, num($event))" [disabled]="disabled()" />
          }
          @case ('Currency') {
            <input sdInput type="number" step="0.01" [name]="f.key" [ngModel]="values()[f.key]" (ngModelChange)="set(f.key, num($event))" [disabled]="disabled()" />
          }
          @case ('Date') {
            <sd-date-input type="date" [value]="strOrNull(f.key)" (valueChange)="set(f.key, $event)" [disabled]="disabled()" />
          }
          @case ('DateTime') {
            <sd-date-input type="datetime" [value]="strOrNull(f.key)" (valueChange)="set(f.key, $event)" [disabled]="disabled()" />
          }
          @case ('Checkbox') {
            <sd-checkbox [value]="values()[f.key] === true" (valueChange)="set(f.key, $event)" [disabled]="disabled()">{{ f.label }}</sd-checkbox>
          }
          @case ('Picklist') {
            <sd-combobox [options]="options(f)" [value]="strOrNull(f.key)" (valueChange)="set(f.key, $any($event))" [disabled]="disabled()" />
          }
          @case ('MultiPicklist') {
            <sd-combobox multiple [options]="options(f)" [value]="list(f.key)" (valueChange)="set(f.key, $any($event) ?? [])" [disabled]="disabled()" />
          }
          @default {
            <input
              sdInput
              [type]="f.fieldType === 'Email' ? 'email' : f.fieldType === 'Url' ? 'url' : f.fieldType === 'Phone' ? 'tel' : 'text'"
              [name]="f.key"
              [ngModel]="str(f.key)"
              (ngModelChange)="set(f.key, $event)"
              [disabled]="disabled()"
            />
          }
        }
      </sd-form-field>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--sd-space-3);
    }
  `,
})
export class CustomFieldsComponent {
  readonly fields = input.required<FormCustomField[]>();
  readonly values = model<Record<string, JsonValue>>({});
  readonly errors = input<Record<string, string>>({});
  readonly disabled = input(false, { transform: booleanAttribute });

  protected set(key: string, value: JsonValue): void {
    this.values.update((v) => ({ ...v, [key]: value === '' ? null : value }));
  }

  protected str(key: string): string {
    const v = this.values()[key];
    return v === null || v === undefined ? '' : String(v);
  }

  protected strOrNull(key: string): string | null {
    const v = this.values()[key];
    return v === null || v === undefined || v === '' ? null : String(v);
  }

  protected list(key: string): string[] {
    const v = this.values()[key];
    return Array.isArray(v) ? v.map(String) : [];
  }

  protected num(raw: unknown): number | null {
    if (raw === '' || raw === null || raw === undefined) return null;
    const n = Number(raw);
    return Number.isFinite(n) ? n : null;
  }

  protected options(f: CustomFieldDto): SdOption[] {
    return customFieldOptions(f);
  }
}
