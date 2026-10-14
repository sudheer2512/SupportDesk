import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  AdminCustomFieldDto,
  AdminCustomFieldType,
  AdminFieldEntity,
  ApiError,
  JsonValue,
  MetaApi,
  SaveCustomFieldRequest,
  TicketConfigApi,
} from '@sd/core';
import { SdBadge, SdButton, SdCheckbox, SdCombobox, SdDialogService, SdDrawer, SdEmptyState, SdFormField, SdIcon, SdInput, SdOption, SdSkeleton, SdToggle, ToastService } from '@sd/ui';
import { ADMIN_EDITOR_STYLES, AdminLeaveGuarded, sameValue, saveErrors } from '../admin-shared';

interface OptionRow {
  value: string;
  label: string;
}

interface FieldForm {
  id: string | null;
  entity: AdminFieldEntity;
  key: string;
  keyTouched: boolean;
  label: string;
  fieldType: AdminCustomFieldType;
  options: OptionRow[];
  lookupEntity: string;
  isRequired: boolean;
  defaultValue: string;
  helpText: string;
  validationRegex: string;
  minValue: string;
  maxValue: string;
  maxLength: string;
  visibleInPortal: boolean;
  editableInPortal: boolean;
  departmentIds: string[];
  requiredForStatusIds: string[];
  isActive: boolean;
  rowVersion: string | null;
}

const TYPES: { value: AdminCustomFieldType; label: string }[] = [
  { value: 'Text', label: 'Single line text' },
  { value: 'TextArea', label: 'Multi-line text' },
  { value: 'Number', label: 'Whole number' },
  { value: 'Decimal', label: 'Decimal number' },
  { value: 'Currency', label: 'Currency' },
  { value: 'Date', label: 'Date' },
  { value: 'DateTime', label: 'Date and time' },
  { value: 'Checkbox', label: 'Checkbox' },
  { value: 'Picklist', label: 'Picklist' },
  { value: 'MultiPicklist', label: 'Multi-select picklist' },
  { value: 'Url', label: 'Web address' },
  { value: 'Email', label: 'E-mail address' },
  { value: 'Phone', label: 'Phone number' },
  { value: 'Lookup', label: 'Record lookup' },
];
const TEXT_TYPES: AdminCustomFieldType[] = ['Text', 'TextArea', 'Url', 'Email', 'Phone'];
const NUMBER_TYPES: AdminCustomFieldType[] = ['Number', 'Decimal', 'Currency'];
const ENTITIES: AdminFieldEntity[] = ['Ticket', 'Contact', 'Customer'];

function toKey(label: string): string {
  const k = label
    .toLowerCase()
    .normalize('NFKD')
    .replace(/[^a-z0-9]+/g, '_')
    .replace(/^_+|_+$/g, '')
    .slice(0, 60);
  return /^[a-z]/.test(k) ? k : k ? `f_${k}` : '';
}

/**
 * Custom fields for tickets, contacts and customers: type, options, validation, portal visibility, departments and
 * "required before entering a status" rules (enforced by the API when agents change status).
 */
@Component({
  selector: 'desk-admin-fields',
  imports: [FormsModule, SdButton, SdBadge, SdIcon, SdDrawer, SdFormField, SdInput, SdToggle, SdCheckbox, SdCombobox, SdEmptyState, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="toolbar">
      <div class="seg" role="tablist" aria-label="Record type">
        @for (e of entities; track e) {
          <button type="button" role="tab" [attr.aria-selected]="entity() === e" [class.on]="entity() === e" (click)="entity.set(e)">{{ e }} fields</button>
        }
      </div>
      <span class="sd-spacer"></span>
      <button sdButton variant="primary" icon="plus" (click)="edit(null)">New field</button>
    </div>
    @if (loading()) {
      <sd-skeleton [lines]="6" height="48px" />
    } @else if (error()) {
      <sd-empty-state icon="circle-alert" title="Fields could not be loaded" [message]="error()">
        <button sdButton icon="refresh" (click)="load()">Try again</button>
      </sd-empty-state>
    } @else if (visible().length === 0) {
      <sd-empty-state icon="list" [title]="'No ' + entity().toLowerCase() + ' fields yet'" message="Capture the extra information your process needs — ERP module, root cause, contract reference…">
        <button sdButton variant="primary" icon="plus" (click)="edit(null)">Add a field</button>
      </sd-empty-state>
    } @else {
      <ul class="rows">
        @for (f of visible(); track f.id; let i = $index; let last = $last) {
          <li [class.inactive]="!f.isActive">
            <div class="grow">
              <div class="title">
                <button type="button" class="link" (click)="edit(f)">{{ f.label }}</button>
                <span class="mono sd-muted">{{ f.key }}</span>
                <sd-badge size="sm" variant="outline">{{ typeLabel(f.fieldType) }}</sd-badge>
                @if (f.isRequired) {
                  <sd-badge size="sm" tone="danger">Required</sd-badge>
                }
                @if (f.requiredForStatusIds.length) {
                  <sd-badge size="sm" tone="warning">Required for {{ statusNames(f.requiredForStatusIds) }}</sd-badge>
                }
                @if (f.visibleInPortal) {
                  <sd-badge size="sm" tone="info" icon="globe">{{ f.editableInPortal ? 'Portal: editable' : 'Portal: visible' }}</sd-badge>
                }
                @if (!f.isActive) {
                  <sd-badge size="sm">Inactive</sd-badge>
                }
              </div>
              <span class="sd-muted small">
                {{ f.departmentIds.length ? 'Departments: ' + departmentNames(f.departmentIds) : 'All departments' }}
                · {{ f.usedInLayouts }} layout(s)
                @if (f.helpText) {
                  · {{ f.helpText }}
                }
              </span>
            </div>
            <button sdButton size="sm" variant="ghost" iconOnly icon="arrow-up" [disabled]="i === 0" [attr.aria-label]="'Move ' + f.label + ' up'" (click)="move(i, -1)"></button>
            <button sdButton size="sm" variant="ghost" iconOnly icon="arrow-down" [disabled]="last" [attr.aria-label]="'Move ' + f.label + ' down'" (click)="move(i, 1)"></button>
            <button sdButton size="sm" icon="pencil" [attr.aria-label]="'Edit ' + f.label" (click)="edit(f)">Edit</button>
          </li>
        }
      </ul>
    }

    <sd-drawer [(open)]="editorOpen" [title]="form().id ? 'Edit field' : 'New field'" width="640px">
      <form class="form" id="field-form" (ngSubmit)="save()">
        @if (formError()) {
          <div class="callout danger" role="alert"><sd-icon name="circle-alert" [size]="16" /><div>{{ formError() }}</div></div>
        }
        <div class="grid2">
          <sd-form-field label="Record type" required [error]="errors()['entity']">
            <select sdInput name="entity" [ngModel]="form().entity" (ngModelChange)="patch({ entity: $event })" [disabled]="!!form().id">
              @for (e of entities; track e) {
                <option [value]="e">{{ e }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Type" required [error]="errors()['fieldType']" [hint]="form().id ? 'The type cannot change once created.' : null">
            <select sdInput name="type" [ngModel]="form().fieldType" (ngModelChange)="patch({ fieldType: $event })" [disabled]="!!form().id">
              @for (t of types; track t.value) {
                <option [value]="t.value">{{ t.label }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Label" required [error]="errors()['label']">
            <input sdInput name="label" [ngModel]="form().label" (ngModelChange)="setLabel($event)" maxlength="100" />
          </sd-form-field>
          <sd-form-field label="Key" required [error]="errors()['key']" [hint]="form().id ? 'Stored values use the key; it cannot change.' : 'Lower-case letters, digits and underscores.'">
            <input sdInput name="key" class="mono" [ngModel]="form().key" (ngModelChange)="patch({ key: $event, keyTouched: true })" [disabled]="!!form().id" maxlength="63" />
          </sd-form-field>
        </div>

        @if (isPicklist()) {
          <fieldset [attr.aria-invalid]="!!errors()['options']">
            <legend>Options</legend>
            @for (o of form().options; track $index; let i = $index; let last = $last) {
              <div class="opt">
                <input sdInput size="sm" [name]="'ov' + i" placeholder="Value" [attr.aria-label]="'Option ' + (i + 1) + ' value'" [ngModel]="o.value" (ngModelChange)="setOption(i, { value: $event })" />
                <input sdInput size="sm" [name]="'ol' + i" placeholder="Label" [attr.aria-label]="'Option ' + (i + 1) + ' label'" [ngModel]="o.label" (ngModelChange)="setOption(i, { label: $event })" />
                <button sdButton type="button" size="sm" variant="ghost" iconOnly icon="arrow-up" [disabled]="i === 0" [attr.aria-label]="'Move option ' + (i + 1) + ' up'" (click)="moveOption(i, -1)"></button>
                <button sdButton type="button" size="sm" variant="ghost" iconOnly icon="arrow-down" [disabled]="last" [attr.aria-label]="'Move option ' + (i + 1) + ' down'" (click)="moveOption(i, 1)"></button>
                <button sdButton type="button" size="sm" variant="ghost" iconOnly icon="x" [attr.aria-label]="'Remove option ' + (i + 1)" (click)="removeOption(i)"></button>
              </div>
            }
            <button sdButton type="button" size="sm" icon="plus" (click)="addOption()">Add option</button>
            @if (errors()['options']) {
              <p class="err">{{ errors()['options'] }}</p>
            }
          </fieldset>
        }
        @if (form().fieldType === 'Lookup') {
          <sd-form-field label="Looks up" required [error]="errors()['options']">
            <select sdInput name="lookup" [ngModel]="form().lookupEntity" (ngModelChange)="patch({ lookupEntity: $event })">
              @for (l of lookupTargets; track l) {
                <option [value]="l">{{ l }}</option>
              }
            </select>
          </sd-form-field>
        }

        <div class="grid2">
          <sd-form-field label="Default value" optional [error]="errors()['defaultValue']">
            @if (isPicklist()) {
              <select sdInput name="default" [ngModel]="form().defaultValue" (ngModelChange)="patch({ defaultValue: $event })">
                <option value="">No default</option>
                @for (o of form().options; track $index) {
                  @if (o.value.trim()) {
                    <option [value]="o.value.trim()">{{ o.label || o.value }}</option>
                  }
                }
              </select>
            } @else {
              <input sdInput name="default" [ngModel]="form().defaultValue" (ngModelChange)="patch({ defaultValue: $event })" maxlength="400" />
            }
          </sd-form-field>
          <sd-form-field label="Help text" optional>
            <input sdInput name="help" [ngModel]="form().helpText" (ngModelChange)="patch({ helpText: $event })" maxlength="500" />
          </sd-form-field>
          @if (isText()) {
            <sd-form-field label="Validation pattern" optional hint="A regular expression the value must match." [error]="errors()['validationRegex']">
              <input sdInput name="regex" class="mono" [ngModel]="form().validationRegex" (ngModelChange)="patch({ validationRegex: $event })" maxlength="500" />
            </sd-form-field>
            <sd-form-field label="Maximum length" optional [error]="errors()['maxLength']">
              <input sdInput name="maxLength" type="number" min="1" max="10000" [ngModel]="form().maxLength" (ngModelChange)="patch({ maxLength: $event })" />
            </sd-form-field>
          }
          @if (isNumber()) {
            <sd-form-field label="Minimum" optional [error]="errors()['minValue']">
              <input sdInput name="min" type="number" [ngModel]="form().minValue" (ngModelChange)="patch({ minValue: $event })" />
            </sd-form-field>
            <sd-form-field label="Maximum" optional [error]="errors()['maxValue']">
              <input sdInput name="max" type="number" [ngModel]="form().maxValue" (ngModelChange)="patch({ maxValue: $event })" />
            </sd-form-field>
          }
        </div>

        <fieldset>
          <legend>Visibility</legend>
          <div class="stack">
            <sd-toggle [value]="form().visibleInPortal" (valueChange)="patch({ visibleInPortal: $event, editableInPortal: $event && form().editableInPortal })">Customers see it in the portal</sd-toggle>
            <sd-toggle [value]="form().editableInPortal" [disabled]="!form().visibleInPortal" (valueChange)="patch({ editableInPortal: $event })">Customers can fill it in when raising a ticket</sd-toggle>
            <span class="sd-muted small">Agents always see every active field.</span>
            @if (errors()['editableInPortal']) {
              <p class="err">{{ errors()['editableInPortal'] }}</p>
            }
          </div>
        </fieldset>

        <fieldset>
          <legend>Required</legend>
          <div class="stack">
            <sd-toggle [value]="form().isRequired" (valueChange)="patch({ isRequired: $event })">Always required when the record is created or edited</sd-toggle>
            @if (form().entity === 'Ticket') {
              <span class="sd-muted small">Or only before a ticket may enter these statuses:</span>
              <div class="statuses">
                @for (s of statuses(); track s.id) {
                  <sd-checkbox [value]="form().requiredForStatusIds.includes(s.id)" (valueChange)="toggleStatus(s.id, $event)">
                    <span class="swatch" [style.background]="s.color"></span> {{ s.name }}
                  </sd-checkbox>
                }
              </div>
              @if (errors()['requiredForStatusIds']) {
                <p class="err">{{ errors()['requiredForStatusIds'] }}</p>
              }
            }
          </div>
        </fieldset>

        @if (form().entity === 'Ticket') {
          <sd-form-field label="Departments" optional hint="Leave empty to use the field in every department." [error]="errors()['departmentIds']">
            <sd-combobox multiple [options]="departmentOptions()" [value]="form().departmentIds" (valueChange)="patch({ departmentIds: asList($event) })" placeholder="All departments" ariaLabel="Departments" />
          </sd-form-field>
        }
        <sd-toggle [value]="form().isActive" (valueChange)="patch({ isActive: $event })">Active</sd-toggle>
      </form>
      <div sdDrawerFooter class="footer">
        @if (form().id) {
          <button sdButton type="button" variant="ghost" icon="trash" class="del" (click)="remove()">Delete</button>
        }
        <span class="sd-spacer"></span>
        <button sdButton type="button" variant="ghost" (click)="close()">Cancel</button>
        <button sdButton type="submit" form="field-form" variant="primary" [loading]="saving()">{{ form().id ? 'Save field' : 'Create field' }}</button>
      </div>
    </sd-drawer>
  `,
  styles: `
    ${ADMIN_EDITOR_STYLES}
    .seg { display: inline-flex; padding: 3px; border-radius: var(--sd-radius-md); background: var(--sd-surface-sunken); }
    .seg button { padding: 6px 12px; border: 0; border-radius: var(--sd-radius-sm); background: none; color: var(--sd-text-2); font: inherit; font-weight: var(--sd-weight-medium); cursor: pointer; }
    .seg button.on { background: var(--sd-surface); color: var(--sd-text); box-shadow: var(--sd-shadow-xs); }
    .seg button:focus-visible { outline: none; box-shadow: var(--sd-focus-ring); }
    .title { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
    .rows li.inactive .link { color: var(--sd-text-3); }
    .link { padding: 0; border: 0; background: none; color: var(--sd-text); font: inherit; font-weight: var(--sd-weight-semibold); cursor: pointer; }
    .link:hover, .link:focus-visible { color: var(--sd-primary); text-decoration: underline; outline: none; }
    .opt { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) auto auto auto; gap: 6px; margin-bottom: 6px; }
    .statuses { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 6px; }
    .del { color: var(--sd-danger); }
    .footer { align-items: center; }
    @media (max-width: 760px) { .statuses { grid-template-columns: minmax(0, 1fr); } .opt { grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) auto; } .opt button:nth-of-type(1), .opt button:nth-of-type(2) { display: none; } }
  `,
})
export class FieldsPage implements OnInit, AdminLeaveGuarded {
  private readonly api = inject(TicketConfigApi);
  private readonly meta = inject(MetaApi);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(SdDialogService);

  protected readonly types = TYPES;
  protected readonly entities = ENTITIES;
  protected readonly lookupTargets = ['Product', 'Customer', 'Contact', 'User', 'Installation'];
  protected readonly entity = signal<AdminFieldEntity>('Ticket');
  protected readonly fields = signal<AdminCustomFieldDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected readonly editorOpen = signal(false);
  protected readonly form = signal<FieldForm>(this.blank('Ticket'));
  private readonly initial = signal<FieldForm>(this.blank('Ticket'));
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);

  protected readonly visible = computed(() => this.fields().filter((f) => f.entity === this.entity()));
  protected readonly statuses = computed(() => this.meta.ticketForm()?.statuses ?? []);
  protected readonly departmentOptions = computed<SdOption[]>(() => (this.meta.ticketForm()?.departments ?? []).map((d) => ({ value: d.id, label: d.name })));
  protected readonly isPicklist = computed(() => this.form().fieldType === 'Picklist' || this.form().fieldType === 'MultiPicklist');
  protected readonly isText = computed(() => TEXT_TYPES.includes(this.form().fieldType));
  protected readonly isNumber = computed(() => NUMBER_TYPES.includes(this.form().fieldType));
  private readonly dirty = computed(() => this.editorOpen() && !sameValue(this.form(), this.initial()));

  ngOnInit(): void {
    this.meta.load();
    this.load();
  }

  hasUnsavedChanges(): boolean {
    return this.dirty();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.fields().subscribe({
      next: (list) => {
        this.fields.set(list);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.error.set(ApiError.from(err).userMessage);
        this.loading.set(false);
      },
    });
  }

  protected typeLabel(t: string): string {
    return TYPES.find((x) => x.value === t)?.label ?? t;
  }

  protected statusNames(ids: string[]): string {
    const all = this.statuses();
    return ids.map((id) => all.find((s) => s.id === id)?.name ?? 'status').join(', ');
  }

  protected departmentNames(ids: string[]): string {
    const all = this.meta.ticketForm()?.departments ?? [];
    return ids.map((id) => all.find((d) => d.id === id)?.name ?? 'department').join(', ');
  }

  protected asList(v: string | string[] | null): string[] {
    return Array.isArray(v) ? v : v ? [v] : [];
  }

  protected edit(f: AdminCustomFieldDto | null): void {
    const form = f ? this.fromDto(f) : this.blank(this.entity());
    this.form.set(form);
    this.initial.set(structuredClone(form));
    this.errors.set({});
    this.formError.set(null);
    this.editorOpen.set(true);
  }

  protected patch(change: Partial<FieldForm>): void {
    this.form.update((f) => ({ ...f, ...change }));
  }

  protected setLabel(label: string): void {
    const f = this.form();
    this.patch({ label, key: !f.id && !f.keyTouched ? toKey(label) : f.key });
  }

  protected setOption(i: number, change: Partial<OptionRow>): void {
    this.patch({ options: this.form().options.map((o, j) => (j === i ? { ...o, ...change } : o)) });
  }

  protected addOption(): void {
    this.patch({ options: [...this.form().options, { value: '', label: '' }] });
  }

  protected removeOption(i: number): void {
    this.patch({ options: this.form().options.filter((_, j) => j !== i) });
  }

  protected moveOption(i: number, delta: number): void {
    const list = [...this.form().options];
    const [o] = list.splice(i, 1);
    list.splice(i + delta, 0, o);
    this.patch({ options: list });
  }

  protected toggleStatus(id: string, on: boolean): void {
    const next = this.form().requiredForStatusIds.filter((x) => x !== id);
    this.patch({ requiredForStatusIds: on ? [...next, id] : next });
  }

  protected async close(): Promise<void> {
    if (this.dirty() && !(await this.dialogs.confirm({ title: 'Discard changes to this field?', confirmLabel: 'Discard', danger: true }))) return;
    this.editorOpen.set(false);
  }

  protected save(): void {
    const f = this.form();
    const num = (v: string) => (v === '' || v === null || v === undefined ? null : Number(v));
    let options: JsonValue | null = null;
    if (this.isPicklist()) options = f.options.filter((o) => o.value.trim()).map((o) => ({ value: o.value.trim(), label: o.label.trim() || o.value.trim() }));
    if (f.fieldType === 'Lookup') options = { entity: f.lookupEntity };
    const request: SaveCustomFieldRequest = {
      entity: f.entity,
      key: f.key.trim(),
      label: f.label.trim(),
      fieldType: f.fieldType,
      options,
      isRequired: f.isRequired,
      defaultValue: f.defaultValue.trim() || null,
      helpText: f.helpText.trim() || null,
      validationRegex: this.isText() ? f.validationRegex.trim() || null : null,
      minValue: this.isNumber() ? num(f.minValue) : null,
      maxValue: this.isNumber() ? num(f.maxValue) : null,
      maxLength: this.isText() ? num(f.maxLength) : null,
      visibleInPortal: f.visibleInPortal,
      editableInPortal: f.visibleInPortal && f.editableInPortal,
      departmentIds: f.entity === 'Ticket' ? f.departmentIds : [],
      requiredForStatusIds: f.entity === 'Ticket' ? f.requiredForStatusIds : [],
      isActive: f.isActive,
      rowVersion: f.rowVersion,
    };
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    (f.id ? this.api.updateField(f.id, request) : this.api.createField(request)).subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.editorOpen.set(false);
        this.toast.success(f.id ? 'Field saved' : 'Field created', saved.label);
        this.load();
        this.meta.refresh();
      },
      error: (err: unknown) => {
        this.saving.set(false);
        if (ApiError.from(err).is('concurrency_conflict')) {
          this.formError.set('Someone else changed this field. Close the editor and open it again.');
          return;
        }
        const { fields, message } = saveErrors(err, 'The field could not be saved.');
        this.errors.set(fields);
        this.formError.set(message);
      },
    });
  }

  protected async remove(): Promise<void> {
    const f = this.form();
    if (!f.id) return;
    const ok = await this.dialogs.confirm({
      title: `Delete “${f.label}”?`,
      message: 'It is removed from every form layout. Values already stored on records are kept but no longer shown.',
      danger: true,
      confirmLabel: 'Delete field',
      typeToConfirm: f.key,
    });
    if (!ok) return;
    this.api.deleteField(f.id).subscribe({
      next: () => {
        this.editorOpen.set(false);
        this.toast.success('Field deleted', f.label);
        this.load();
        this.meta.refresh();
      },
      error: (err: unknown) => this.formError.set(ApiError.from(err).userMessage),
    });
  }

  protected move(index: number, delta: number): void {
    const list = this.visible().map((f) => f.id);
    const j = index + delta;
    [list[index], list[j]] = [list[j], list[index]];
    this.api.reorderFields(this.entity(), list).subscribe({
      next: (saved) => {
        this.fields.update((all) => [...all.filter((f) => f.entity !== this.entity()), ...saved]);
        this.meta.refresh();
      },
      error: (err: unknown) => this.toast.error('Could not reorder', ApiError.from(err).userMessage),
    });
  }

  private fromDto(f: AdminCustomFieldDto): FieldForm {
    const options = Array.isArray(f.options)
      ? f.options.map((o) => {
          if (o && typeof o === 'object' && !Array.isArray(o)) return { value: String(o['value'] ?? ''), label: String(o['label'] ?? o['value'] ?? '') };
          return { value: String(o), label: String(o) };
        })
      : [];
    const lookup = f.options && typeof f.options === 'object' && !Array.isArray(f.options) ? String(f.options['entity'] ?? 'Product') : 'Product';
    return {
      id: f.id,
      entity: f.entity,
      key: f.key,
      keyTouched: true,
      label: f.label,
      fieldType: f.fieldType,
      options,
      lookupEntity: lookup,
      isRequired: f.isRequired,
      defaultValue: f.defaultValue ?? '',
      helpText: f.helpText ?? '',
      validationRegex: f.validationRegex ?? '',
      minValue: f.minValue?.toString() ?? '',
      maxValue: f.maxValue?.toString() ?? '',
      maxLength: f.maxLength?.toString() ?? '',
      visibleInPortal: f.visibleInPortal,
      editableInPortal: f.editableInPortal,
      departmentIds: [...f.departmentIds],
      requiredForStatusIds: [...f.requiredForStatusIds],
      isActive: f.isActive,
      rowVersion: f.rowVersion,
    };
  }

  private blank(entity: AdminFieldEntity): FieldForm {
    return {
      id: null,
      entity,
      key: '',
      keyTouched: false,
      label: '',
      fieldType: 'Text',
      options: [
        { value: '', label: '' },
        { value: '', label: '' },
      ],
      lookupEntity: 'Product',
      isRequired: false,
      defaultValue: '',
      helpText: '',
      validationRegex: '',
      minValue: '',
      maxValue: '',
      maxLength: '',
      visibleInPortal: false,
      editableInPortal: false,
      departmentIds: [],
      requiredForStatusIds: [],
      isActive: true,
      rowVersion: null,
    };
  }
}
