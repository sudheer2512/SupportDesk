import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ApiError,
  ConditionFieldDto,
  FilterGroup,
  JsonValue,
  MeStore,
  MetaApi,
  STORAGE_KEYS,
  SavedViewDto,
  ViewsApi,
  WorkbenchApi,
  countConditions,
  emptyFilterGroup,
  safeStorage,
} from '@sd/core';
import { SdButton, SdCheckbox, SdDialog, SdFilterBuilder, SdFormField, SdInput, SdRadio, SdRadioGroup, SdSkeleton } from '@sd/ui';
import { VIEW_SORTS, buildViewFields } from './view-fields';

export interface ViewEditorData {
  /** Edit this view (or copy it when `duplicate`). Omit for a new view. */
  view?: SavedViewDto | null;
  duplicate?: boolean;
  /** Local table-prefs key of the list being looked at (to save its column layout with the view). */
  columnsStorageKey?: string | null;
}

/** Create / edit a saved ticket view: name, visibility, condition tree (filter builder), sort and columns. */
@Component({
  selector: 'desk-view-editor-dialog',
  imports: [FormsModule, SdDialog, SdButton, SdFormField, SdInput, SdFilterBuilder, SdRadioGroup, SdRadio, SdCheckbox, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="title()" description="Views show every ticket that matches the conditions — within what you are allowed to see.">
      <form id="view-form" class="form" (ngSubmit)="save()" novalidate>
        <sd-form-field label="Name" required [error]="errors()['name']">
          <input sdInput name="name" [ngModel]="name()" (ngModelChange)="name.set($event)" maxlength="120" autocomplete="off" />
        </sd-form-field>

        <sd-form-field label="Who can use it">
          <sd-radio-group [value]="visibility()" (valueChange)="visibility.set($event ?? 'Personal')" direction="row" ariaLabel="Visibility">
            <sd-radio value="Personal" description="Only you">Personal</sd-radio>
            <sd-radio value="Shared" [disabled]="!canShare" [description]="canShare ? 'Everyone in the help desk' : 'Needs supervisor rights'">Shared</sd-radio>
          </sd-radio-group>
        </sd-form-field>

        <fieldset class="conditions">
          <legend>Conditions <span class="sd-muted sd-small">({{ conditionCount() }})</span></legend>
          @if (catalogError()) {
            <p class="error" role="alert">{{ catalogError() }}</p>
          } @else if (fields().length === 0) {
            <sd-skeleton [lines]="3" />
          } @else {
            <sd-filter-builder [fields]="fields()" [value]="filter()" (valueChange)="filter.set($event)" [maxDepth]="2" />
          }
          @if (errors()['filter']; as e) {
            <p class="error" role="alert">{{ e }}</p>
          }
        </fieldset>

        <div class="row">
          <sd-form-field label="Sort by">
            <select sdInput name="sort" [ngModel]="sortKey()" (ngModelChange)="sortKey.set($event)">
              @for (s of sorts; track s.value) {
                <option [value]="s.value">{{ s.label }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Position" hint="Lower numbers are listed first">
            <input sdInput type="number" name="order" min="0" max="999" [ngModel]="sortOrder()" (ngModelChange)="sortOrder.set(+$event || 0)" />
          </sd-form-field>
        </div>

        @if (data.columnsStorageKey) {
          <sd-checkbox [value]="saveColumns()" (valueChange)="saveColumns.set($event)">Save the current column layout with this view</sd-checkbox>
        }
        <sd-checkbox [value]="isDefault()" (valueChange)="isDefault.set($event)">Open this view by default</sd-checkbox>
        <p class="default-hint sd-small sd-muted">
          Opens when you load the ticket workspace without a queue or view in the link
          ({{ visibility() === 'Shared' ? 'for everyone without a personal default' : 'replaces your previous default view' }}).
        </p>

        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="view-form" [loading]="saving()">{{ isEdit() ? 'Save view' : 'Create view' }}</button>
      </div>
    </sd-dialog>
  `,
  styles: `
    .default-hint {
      margin-top: calc(-1 * var(--sd-space-2));
      padding-left: 26px;
    }
    .form {
      display: grid;
      gap: var(--sd-space-3);
    }
    .conditions {
      margin: 0;
      padding: var(--sd-space-3);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      min-width: 0;
    }
    legend {
      padding: 0 4px;
      font-weight: var(--sd-weight-semibold);
    }
    .row {
      display: grid;
      grid-template-columns: 2fr 1fr;
      gap: var(--sd-space-3);
    }
    @media (max-width: 560px) {
      .row {
        grid-template-columns: 1fr;
      }
    }
    .error {
      color: var(--sd-danger);
      margin-top: 6px;
    }
  `,
})
export class ViewEditorDialog {
  protected readonly data = inject<ViewEditorData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<SavedViewDto>>(DialogRef);
  private readonly views = inject(ViewsApi);
  private readonly workbench = inject(WorkbenchApi);
  private readonly meta = inject(MetaApi);
  private readonly me = inject(MeStore);

  protected readonly canShare = this.me.can('supervisor.monitor');
  protected readonly sorts = VIEW_SORTS;
  private readonly source = this.data.view ?? null;
  protected readonly isEdit = computed(() => !!this.source && !this.data.duplicate);
  protected readonly title = computed(() => (this.isEdit() ? 'Edit view' : this.data.duplicate ? 'Duplicate view' : 'New view'));

  protected readonly name = signal(this.source ? (this.data.duplicate ? `${this.source.name} (copy)` : this.source.name) : '');
  protected readonly visibility = signal<string>(this.source && !this.data.duplicate ? this.source.visibility : 'Personal');
  protected readonly filter = signal<FilterGroup>(normalise(this.source?.filter));
  protected readonly sortKey = signal<string>(((this.source?.sort as { key?: string } | null)?.key as string) ?? 'lastActivity');
  protected readonly sortOrder = signal(this.source?.sortOrder ?? 100);
  protected readonly isDefault = signal(this.source && !this.data.duplicate ? this.source.isDefault : false);
  protected readonly saveColumns = signal(!this.source);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly catalogError = signal<string | null>(null);
  private readonly catalog = signal<ConditionFieldDto[]>([]);

  protected readonly fields = computed(() => buildViewFields(this.catalog(), this.meta.ticketForm(), this.me.displayName() || 'Me'));
  protected readonly conditionCount = computed(() => countConditions(this.filter()));

  constructor() {
    this.meta.load();
    this.workbench.conditionFields().subscribe({
      next: (c) => this.catalog.set(c.fields),
      error: (err: unknown) => this.catalogError.set(ApiError.from(err).userMessage),
    });
  }

  protected save(): void {
    const errors: Record<string, string> = {};
    if (!this.name().trim()) errors['name'] = 'Give the view a name.';
    this.errors.set(errors);
    if (Object.keys(errors).length) return;

    const sort = VIEW_SORTS.find((s) => s.value === this.sortKey()) ?? VIEW_SORTS[0];
    let columns: JsonValue | null = this.source?.columns ?? null;
    if (this.saveColumns() && this.data.columnsStorageKey) {
      columns = (safeStorage.getJson<JsonValue>(STORAGE_KEYS.tablePrefix + this.data.columnsStorageKey) ?? null) as JsonValue | null;
    }
    const request = {
      scope: 'tickets',
      name: this.name().trim(),
      visibility: this.visibility(),
      filter: this.filter(),
      columns,
      sort: { key: sort.value, dir: sort.dir },
      isDefault: this.isDefault(),
      sortOrder: this.sortOrder(),
    };
    this.saving.set(true);
    this.formError.set(null);
    const call = this.isEdit() && this.source ? this.views.update(this.source.id, request) : this.views.create(request);
    call.subscribe({
      next: (view) => {
        this.saving.set(false);
        // Keep the saved column layout as this view's local layout too.
        if (columns && typeof columns === 'object') safeStorage.setJson(STORAGE_KEYS.tablePrefix + `tickets.view.${view.id}`, columns);
        this.ref.close(view);
      },
      error: (err: unknown) => {
        this.saving.set(false);
        const e = ApiError.from(err);
        this.errors.set({ name: e.fieldError('name') ?? '', filter: e.fieldError('filter') ?? '' });
        this.formError.set(e.userMessage);
      },
    });
  }
}

function normalise(value: unknown): FilterGroup {
  const g = value as Partial<FilterGroup> | null | undefined;
  if (!g || typeof g !== 'object') return emptyFilterGroup();
  return {
    logic: g.logic === 'or' ? 'or' : 'and',
    conditions: Array.isArray(g.conditions) ? g.conditions : [],
    groups: Array.isArray(g.groups) ? g.groups.map((x) => normalise(x)) : [],
  };
}
