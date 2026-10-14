import { ChangeDetectionStrategy, Component, computed, effect, inject, linkedSignal, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Guid, InstallationLookupDto, JsonValue, SEVERITIES, Severity, TicketDetailDto, WorkbenchApi } from '@sd/core';
import { SdButton, SdChipsInput, SdCombobox, SdDateTimePipe, SdDurationPipe, SdFormField, SdInput, SdOption } from '@sd/ui';
import { CustomFieldsComponent, applicableCustomFields, validateCustomFields } from '../../shared/custom-fields.component';
import { TicketContext } from '../ticket-context.service';

interface PropsForm {
  severity: string | null;
  productId: Guid | null;
  moduleId: Guid | null;
  versionId: Guid | null;
  installationId: Guid | null;
  categoryId: Guid | null;
  subcategoryId: Guid | null;
  issueTypeId: Guid | null;
  language: string;
  customFields: Record<string, JsonValue>;
}

const FIELDS: (keyof Omit<PropsForm, 'customFields'>)[] = [
  'severity',
  'productId',
  'moduleId',
  'versionId',
  'installationId',
  'categoryId',
  'subcategoryId',
  'issueTypeId',
  'language',
];

function fromTicket(t: TicketDetailDto | null): PropsForm {
  return {
    severity: t?.severity ?? null,
    productId: t?.productId ?? null,
    moduleId: t?.moduleId ?? null,
    versionId: t?.versionId ?? null,
    installationId: t?.installationId ?? null,
    categoryId: t?.categoryId ?? null,
    subcategoryId: t?.subcategoryId ?? null,
    issueTypeId: t?.issueTypeId ?? null,
    language: t?.language ?? 'en',
    customFields: { ...(t?.customFields ?? {}) },
  };
}

/**
 * Ticket properties: classification, product/module/version/installation, severity, language, the department's
 * custom fields (layout order + validation) and tags. Saved with PATCH + rowVersion (409 → conflict dialog).
 */
@Component({
  selector: 'desk-properties-panel',
  imports: [FormsModule, SdFormField, SdInput, SdCombobox, SdChipsInput, SdButton, SdDateTimePipe, SdDurationPipe, CustomFieldsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (ctx.ticket(); as t) {
      <form class="props" (ngSubmit)="save()" novalidate>
        <sd-form-field label="Tags">
          <sd-chips-input [value]="tags()" (valueChange)="onTags($event)" [suggestions]="tagSuggestions()" lowercase placeholder="Add tag" [disabled]="!t.can.edit" />
        </sd-form-field>

        <div class="grid">
          <sd-form-field label="Category">
            <sd-combobox [options]="categories()" [value]="form().categoryId" (valueChange)="setCategory($any($event))" [disabled]="!t.can.edit" />
          </sd-form-field>
          <sd-form-field label="Subcategory">
            <sd-combobox [options]="subcategories()" [value]="form().subcategoryId" (valueChange)="patch({ subcategoryId: $any($event) })" [disabled]="!t.can.edit || !form().categoryId" />
          </sd-form-field>
          <sd-form-field label="Issue type">
            <sd-combobox [options]="issueTypes()" [value]="form().issueTypeId" (valueChange)="patch({ issueTypeId: $any($event) })" [disabled]="!t.can.edit" />
          </sd-form-field>
          <sd-form-field label="Severity">
            <sd-combobox [options]="severities" [searchable]="false" [value]="form().severity" (valueChange)="patch({ severity: $any($event) })" [disabled]="!t.can.edit" />
          </sd-form-field>
          <sd-form-field label="Product">
            <sd-combobox [options]="products()" [value]="form().productId" (valueChange)="setProduct($any($event))" [disabled]="!t.can.edit" />
          </sd-form-field>
          <sd-form-field label="Module">
            <sd-combobox [options]="modules()" [value]="form().moduleId" (valueChange)="patch({ moduleId: $any($event) })" [disabled]="!t.can.edit || !form().productId" />
          </sd-form-field>
          <sd-form-field label="Version">
            <sd-combobox [options]="versions()" [value]="form().versionId" (valueChange)="patch({ versionId: $any($event) })" [disabled]="!t.can.edit || !form().productId" />
          </sd-form-field>
          <sd-form-field label="Installation" [hint]="t.customerId ? null : 'Link a customer first'">
            <sd-combobox [options]="installationOptions()" [value]="form().installationId" (valueChange)="setInstallation($any($event))" [disabled]="!t.can.edit || !t.customerId" />
          </sd-form-field>
          <sd-form-field label="Language">
            <input sdInput name="language" maxlength="10" [ngModel]="form().language" (ngModelChange)="patch({ language: $event })" [disabled]="!t.can.edit" />
          </sd-form-field>
        </div>

        @if (customFields().length) {
          <h3>Additional fields</h3>
          <desk-custom-fields [fields]="customFields()" [values]="form().customFields" (valuesChange)="patch({ customFields: $event })" [errors]="cfErrors()" [disabled]="!t.can.edit" />
        }

        @if (t.can.edit) {
          <div class="save-bar" [class.is-dirty]="dirty()">
            <span class="sd-small sd-muted">{{ dirty() ? 'Unsaved changes' : 'All changes saved' }}</span>
            <span class="sd-spacer"></span>
            <button sdButton size="sm" type="button" [disabled]="!dirty() || ctx.busy()" (click)="reset()">Undo</button>
            <button sdButton size="sm" variant="primary" type="submit" [disabled]="!dirty()" [loading]="ctx.busy()">Save</button>
          </div>
        }

        <h3>Facts</h3>
        <dl class="facts">
          <dt>Created</dt>
          <dd>{{ t.createdAt | sdDateTime }}</dd>
          <dt>Updated</dt>
          <dd>{{ t.updatedAt | sdDateTime }}</dd>
          @if (t.firstResponseAt) {
            <dt>First response</dt>
            <dd>{{ t.firstResponseAt | sdDateTime }}</dd>
          }
          @if (t.resolvedAt) {
            <dt>Resolved</dt>
            <dd>{{ t.resolvedAt | sdDateTime }}</dd>
          }
          <dt>SLA policy</dt>
          <dd>{{ t.slaPolicyName ?? 'None' }}</dd>
          <dt>Contract</dt>
          <dd>{{ t.contractName ?? '—' }}</dd>
          @if (t.brandName) {
            <dt>Brand</dt>
            <dd>{{ t.brandName }}</dd>
          }
          @if (t.locationName) {
            <dt>Location</dt>
            <dd>{{ t.locationName }}</dd>
          }
          <dt>Time spent</dt>
          <dd>{{ t.timeSpentMinutes | sdDuration }} ({{ t.billableMinutes | sdDuration }} billable)</dd>
          @if (t.reopenCount) {
            <dt>Reopened</dt>
            <dd>{{ t.reopenCount }}×</dd>
          }
          @if (t.csatScore !== null) {
            <dt>CSAT</dt>
            <dd>{{ t.csatScore }}/5 {{ t.csatComment ? '— ' + t.csatComment : '' }}</dd>
          }
          @if (t.resolutionCode) {
            <dt>Resolution</dt>
            <dd>{{ t.resolutionCode }}{{ t.rootCause ? ' · ' + t.rootCause : '' }}</dd>
          }
        </dl>
      </form>
    }
  `,
  styles: `
    .props {
      display: grid;
      gap: var(--sd-space-3);
    }
    .grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 200px), 1fr));
      gap: var(--sd-space-3);
    }
    h3 {
      margin-top: var(--sd-space-2);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      color: var(--sd-text-3);
      text-transform: uppercase;
      letter-spacing: 0.05em;
    }
    .save-bar {
      position: sticky;
      bottom: 0;
      display: flex;
      align-items: center;
      gap: 6px;
      padding: 8px 10px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface-2);
    }
    .save-bar.is-dirty {
      border-color: var(--sd-primary-border);
      background: var(--sd-primary-soft);
    }
    .facts {
      display: grid;
      grid-template-columns: auto 1fr;
      gap: 4px 12px;
      margin: 0;
      font-size: var(--sd-text-sm);
    }
    dt {
      color: var(--sd-text-3);
    }
    dd {
      margin: 0;
      min-width: 0;
      overflow-wrap: anywhere;
    }
  `,
})
export class PropertiesPanelComponent {
  protected readonly ctx = inject(TicketContext);
  private readonly workbench = inject(WorkbenchApi);
  private readonly meta = this.ctx.meta;

  protected readonly severities: SdOption[] = SEVERITIES.map((s) => ({ value: s, label: s }));
  /** Edits snap back to the server's values whenever the ticket changes (save, realtime, conflict). */
  protected readonly form = linkedSignal(() => fromTicket(this.ctx.ticket()));
  protected readonly tags = linkedSignal(() => this.ctx.ticket()?.tags ?? []);
  protected readonly cfErrors = signal<Record<string, string>>({});
  private readonly installations = signal<InstallationLookupDto[]>([]);

  protected readonly dirty = computed(() => {
    const now = this.form();
    const base = fromTicket(this.ctx.ticket());
    return FIELDS.some((k) => (now[k] ?? null) !== (base[k] ?? null)) || JSON.stringify(now.customFields) !== JSON.stringify(base.customFields);
  });

  protected readonly tagSuggestions = computed(() => this.meta.ticketForm()?.tags ?? []);
  protected readonly categories = computed(() => {
    const dept = this.ctx.ticket()?.departmentId ?? null;
    return (this.meta.ticketForm()?.categories ?? [])
      .filter((c) => !c.parentId && (!c.departmentId || c.departmentId === dept))
      .map((c) => ({ value: c.id, label: c.name }));
  });
  protected readonly subcategories = computed(() =>
    (this.meta.ticketForm()?.categories ?? []).filter((c) => c.parentId && c.parentId === this.form().categoryId).map((c) => ({ value: c.id, label: c.name })),
  );
  protected readonly issueTypes = computed(() => (this.meta.ticketForm()?.issueTypes ?? []).map((i) => ({ value: i.id, label: i.name })));
  protected readonly products = computed(() => (this.meta.ticketForm()?.products ?? []).map((p) => ({ value: p.id, label: p.name, description: p.code })));
  private readonly product = computed(() => this.meta.ticketForm()?.products.find((p) => p.id === this.form().productId) ?? null);
  protected readonly modules = computed(() => (this.product()?.modules ?? []).map((m) => ({ value: m.id, label: m.name })));
  protected readonly versions = computed(() => (this.product()?.versions ?? []).map((v) => ({ value: v.id, label: v.name })));
  protected readonly installationOptions = computed<SdOption[]>(() =>
    this.installations()
      .filter((i) => !this.form().productId || i.productId === this.form().productId)
      .map((i) => ({ value: i.id, label: i.name, description: `${i.productName}${i.version ? ' ' + i.version : ''} · ${i.environment}` })),
  );
  protected readonly customFields = computed(() => applicableCustomFields(this.meta.ticketForm(), this.ctx.ticket()?.departmentId ?? null));

  constructor() {
    effect(() => {
      const customerId = this.ctx.ticket()?.customerId ?? null;
      untracked(() => {
        this.installations.set([]);
        if (customerId) this.workbench.installations(customerId).subscribe({ next: (list) => this.installations.set(list), error: () => undefined });
      });
    });
  }

  protected patch(changes: Partial<PropsForm>): void {
    this.form.update((f) => ({ ...f, ...changes }));
  }

  protected setCategory(id: string | null): void {
    this.patch({ categoryId: id, subcategoryId: null });
  }

  protected setProduct(id: string | null): void {
    this.patch({ productId: id, moduleId: null, versionId: null, installationId: id && this.installationMatches(id) ? this.form().installationId : null });
  }

  protected setInstallation(id: string | null): void {
    const inst = this.installations().find((i) => i.id === id);
    this.patch(inst ? { installationId: id, productId: inst.productId, versionId: inst.versionId ?? this.form().versionId } : { installationId: id });
  }

  protected reset(): void {
    this.form.set(fromTicket(this.ctx.ticket()));
    this.cfErrors.set({});
  }

  protected async onTags(tags: string[]): Promise<void> {
    this.tags.set(tags);
    const ok = await this.ctx.setTags(tags);
    if (!ok) this.tags.set(this.ctx.ticket()?.tags ?? []);
  }

  protected async save(): Promise<void> {
    const now = this.form();
    const base = fromTicket(this.ctx.ticket());
    const errors = validateCustomFields(this.customFields(), now.customFields);
    this.cfErrors.set(errors);
    if (Object.keys(errors).length) return;

    const changes: Record<string, unknown> = {};
    const clear: string[] = [];
    const labels: Record<string, string> = {};
    for (const key of FIELDS) {
      if ((now[key] ?? null) === (base[key] ?? null)) continue;
      if (now[key] === null || now[key] === '') clear.push(key);
      else changes[key] = now[key];
      labels[key] = this.labelOf(key, now[key]);
    }
    if (JSON.stringify(now.customFields) !== JSON.stringify(base.customFields)) {
      const delta: Record<string, JsonValue> = {};
      for (const [k, v] of Object.entries(now.customFields)) if (JSON.stringify(v) !== JSON.stringify(base.customFields[k])) delta[k] = v;
      changes['customFields'] = delta;
      labels['customFields'] = Object.entries(delta)
        .map(([k, v]) => `${k}: ${v ?? '—'}`)
        .join(', ');
    }
    await this.ctx.update(
      { changes: { ...(changes as { severity?: Severity }), clear: clear.length ? clear : null }, labels },
      'Ticket updated',
    );
  }

  private installationMatches(productId: string): boolean {
    return this.installations().some((i) => i.id === this.form().installationId && i.productId === productId);
  }

  private labelOf(key: string, value: unknown): string {
    if (value === null || value === undefined || value === '') return '(cleared)';
    const m = this.meta.ticketForm();
    const id = String(value);
    switch (key) {
      case 'productId':
        return m?.products.find((p) => p.id === id)?.name ?? id;
      case 'moduleId':
        return this.modules().find((o) => o.value === id)?.label ?? id;
      case 'versionId':
        return this.versions().find((o) => o.value === id)?.label ?? id;
      case 'installationId':
        return this.installations().find((i) => i.id === id)?.name ?? id;
      case 'categoryId':
      case 'subcategoryId':
        return m?.categories.find((c) => c.id === id)?.name ?? id;
      case 'issueTypeId':
        return m?.issueTypes.find((i) => i.id === id)?.name ?? id;
      default:
        return id;
    }
  }
}
