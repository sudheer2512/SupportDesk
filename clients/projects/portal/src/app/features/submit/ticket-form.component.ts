import { ChangeDetectionStrategy, Component, computed, effect, inject, output, signal, untracked } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import {
  ApiError,
  JsonValue,
  PortalCategoryOptionDto,
  PortalFieldDefDto,
  PortalTicketCreatedDto,
  PortalTicketFormDto,
  PortalTicketsApi,
  newClientRequestId,
} from '@sd/core';
import { SdButton, SdFormField, SdIcon, SdInput, SdRichTextEditor, SdSkeleton, ToastService, isHtmlEmpty } from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { BrandService } from '../../brand.service';
import { AttachmentUploaderComponent } from '../../shared/attachment-uploader.component';
import { DeflectionComponent } from '../../shared/deflection.component';
import { ErrorStateComponent } from '../../shared/error-state.component';
import { errorMessage, htmlToText } from '../../shared/portal-utils';
import { SubmitPrefillService } from '../../shared/submit-prefill.service';
import { CustomFieldComponent, FieldValue } from './custom-field.component';

interface FormSection {
  title: string;
  columns: number;
  fields: string[];
}

const STANDARD_KEYS = ['departmentId', 'categoryId', 'productId', 'installationId', 'priority'];

/**
 * Signed-in ticket form driven by GET /portal/{brand}/tickets/form: subject and description first, then
 * the brand's sections (department, category → subcategory, product, installation, priority and custom
 * fields, shown only for the departments they apply to), attachments with progress, and article
 * suggestions while typing. A random `clientRequestId` protects against double submits.
 */
@Component({
  selector: 'portal-ticket-form',
  imports: [SdButton, SdIcon, SdFormField, SdInput, SdSkeleton, SdRichTextEditor, ErrorStateComponent, AttachmentUploaderComponent, DeflectionComponent, CustomFieldComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (formRes.error()) {
      <portal-error-state [error]="formRes.error()" errorTitle="The ticket form couldn't be loaded" (retry)="formRes.reload()" />
    } @else {
      <form class="form" (submit)="$event.preventDefault(); submit()" novalidate>
        @if (error()) {
          <p class="portal-callout portal-callout--danger" role="alert"><sd-icon name="circle-alert" [size]="15" />{{ error() }}</p>
        }
        <sd-form-field label="Subject" required [error]="errors()['subject']" hint="A short summary, e.g. “Invoice export fails with error 504”.">
          <input sdInput name="subject" maxlength="250" autocomplete="off" [value]="subject()" (input)="subject.set($any($event.target).value)" />
        </sd-form-field>

        <sd-form-field label="Description" required [error]="errors()['descriptionHtml']">
          @defer (on idle) {
            <sd-rich-text-editor
              [(value)]="description"
              toolbar="compact"
              minHeight="160px"
              ariaLabel="Description"
              placeholder="What happened? What did you expect? Include steps, error messages and when it started."
            />
          } @placeholder {
            <sd-skeleton height="200px" radius="var(--sd-radius-md)" />
          }
        </sd-form-field>

        <portal-deflection [query]="deflectionQuery()" (solved)="solvedByArticle()" />

        @if (form(); as f) {
          @for (section of sections(); track $index) {
            @if (visibleKeys(section).length) {
              <fieldset class="section">
                <legend>{{ section.title }}</legend>
                <div class="grid" [style.--cols]="section.columns">
                  @for (key of visibleKeys(section); track key) {
                    @switch (key) {
                      @case ('departmentId') {
                        <sd-form-field label="Department" [error]="errors()['departmentId']">
                          <select sdInput (change)="setDepartment($any($event.target).value)">
                            <option value="" [selected]="!departmentId()">Choose a department…</option>
                            @for (d of f.departments; track d.id) {
                              <option [value]="d.id" [selected]="d.id === departmentId()">{{ d.name }}</option>
                            }
                          </select>
                        </sd-form-field>
                      }
                      @case ('categoryId') {
                        <sd-form-field label="Category" [error]="errors()['categoryId']">
                          <select sdInput (change)="setCategory($any($event.target).value)">
                            <option value="" [selected]="!categoryId()">Choose a category…</option>
                            @for (c of topCategories(); track c.id) {
                              <option [value]="c.id" [selected]="c.id === categoryId()">{{ c.name }}</option>
                            }
                          </select>
                        </sd-form-field>
                        @if (subcategories().length) {
                          <sd-form-field label="Subcategory" optional [error]="errors()['subcategoryId']">
                            <select sdInput (change)="subcategoryId.set($any($event.target).value || null)">
                              <option value="" [selected]="!subcategoryId()">Choose a subcategory…</option>
                              @for (c of subcategories(); track c.id) {
                                <option [value]="c.id" [selected]="c.id === subcategoryId()">{{ c.name }}</option>
                              }
                            </select>
                          </sd-form-field>
                        }
                      }
                      @case ('productId') {
                        <sd-form-field label="Product" [error]="errors()['productId']">
                          <select sdInput (change)="setProduct($any($event.target).value)">
                            <option value="" [selected]="!productId()">Choose a product…</option>
                            @for (p of f.products; track p.id) {
                              <option [value]="p.id" [selected]="p.id === productId()">{{ p.name }}</option>
                            }
                          </select>
                        </sd-form-field>
                      }
                      @case ('installationId') {
                        <sd-form-field label="Installation / site" [error]="errors()['installationId']" hint="Which of your installations is affected?">
                          <select sdInput (change)="setInstallation($any($event.target).value)">
                            <option value="" [selected]="!installationId()">Not sure / not applicable</option>
                            @for (i of installations(); track i.id) {
                              <option [value]="i.id" [selected]="i.id === installationId()">
                                {{ i.name }} — {{ i.productName }}{{ i.version ? ' ' + i.version : '' }}{{ i.locationName ? ' · ' + i.locationName : '' }}
                              </option>
                            }
                          </select>
                        </sd-form-field>
                      }
                      @case ('priority') {
                        <sd-form-field label="Priority" [error]="errors()['priority']" hint="How much is this affecting your work?">
                          <select sdInput (change)="priority.set($any($event.target).value || null)">
                            <option value="" [selected]="!priority()">Normal (let support decide)</option>
                            @for (p of f.priorities; track p) {
                              <option [value]="p" [selected]="p === priority()">{{ p }}</option>
                            }
                          </select>
                        </sd-form-field>
                      }
                      @default {
                        @if (fieldDef(key); as def) {
                          <portal-custom-field
                            [class.wide]="def.fieldType === 'TextArea'"
                            [field]="def"
                            [value]="cf()[def.key] ?? null"
                            (valueChange)="setCustom(def.key, $event)"
                            [error]="errors()[def.key] ?? null"
                          />
                        }
                      }
                    }
                  }
                </div>
              </fieldset>
            }
          }

          <sd-form-field label="Attachments" optional hint="Screenshots, logs or documents that help explain the problem.">
            <portal-attachment-uploader [maxMb]="f.maxAttachmentMb" [(fileIds)]="fileIds" (busyChange)="uploading.set($event)" />
          </sd-form-field>
        } @else {
          <div class="loading" aria-busy="true" aria-label="Loading form fields">
            <sd-skeleton height="64px" />
            <sd-skeleton height="64px" />
          </div>
        }

        <div class="actions">
          <span class="hint">We'll reply by e-mail and you can follow up under My tickets.</span>
          <button sdButton variant="primary" size="lg" type="submit" icon="send" [loading]="busy()" [disabled]="uploading() || !form()">Submit ticket</button>
        </div>
      </form>
    }
  `,
  styles: `
    .form {
      display: grid;
      gap: 18px;
    }
    .section {
      min-width: 0;
      margin: 0;
      padding: 16px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
    }
    legend {
      padding: 0 6px;
      font-weight: var(--sd-weight-semibold);
    }
    .grid {
      display: grid;
      grid-template-columns: repeat(var(--cols, 2), minmax(0, 1fr));
      gap: 14px 16px;
    }
    .grid .wide {
      grid-column: 1 / -1;
    }
    .loading {
      display: grid;
      gap: 12px;
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      padding-top: 4px;
    }
    .hint {
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
    @media (max-width: 640px) {
      .grid {
        grid-template-columns: minmax(0, 1fr);
      }
      .actions button {
        width: 100%;
      }
    }
  `,
})
export class TicketFormComponent {
  private readonly api = inject(PortalTicketsApi);
  private readonly brand = inject(BrandService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  readonly created = output<PortalTicketCreatedDto>();

  protected readonly formRes = rxResource({
    params: () => this.brand.slug() ?? undefined,
    stream: ({ params }) => this.api.form(params),
  });
  protected readonly form = computed<PortalTicketFormDto | null>(() => (this.formRes.hasValue() ? this.formRes.value() : null));

  protected readonly subject = signal('');
  protected readonly description = signal('');
  protected readonly departmentId = signal<string | null>(null);
  protected readonly categoryId = signal<string | null>(null);
  protected readonly subcategoryId = signal<string | null>(null);
  protected readonly productId = signal<string | null>(null);
  protected readonly installationId = signal<string | null>(null);
  protected readonly priority = signal<string | null>(null);
  protected readonly cf = signal<Record<string, FieldValue>>({});
  protected readonly fileIds = signal<string[]>([]);
  protected readonly uploading = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly errors = signal<Record<string, string | null>>({});
  private sourceFlowId: string | null = null;
  private requestId = newClientRequestId();

  protected readonly deflectionQuery = computed(() => {
    const subject = this.subject().trim();
    return subject.length >= 3 ? subject : htmlToText(this.description()).slice(0, 200);
  });

  /** The brand's layout, or a sensible default; custom fields missing from the layout are appended. */
  protected readonly sections = computed<FormSection[]>(() => {
    const f = this.form();
    if (!f) return [];
    const configured: FormSection[] = f.sections
      .filter((s) => s.fields.length)
      .map((s) => ({ title: s.title || 'Details', columns: Math.min(Math.max(s.columns || 2, 1), 3), fields: s.fields }));
    const sections = configured.length
      ? configured
      : [{ title: 'Details', columns: 2, fields: [...STANDARD_KEYS, ...f.fields.map((d) => `cf:${d.key}`)] }];
    const placed = new Set(sections.flatMap((s) => s.fields));
    const missing = f.fields.filter((d) => !placed.has(`cf:${d.key}`)).map((d) => `cf:${d.key}`);
    return missing.length ? [...sections, { title: 'Additional information', columns: 2, fields: missing }] : sections;
  });

  protected readonly topCategories = computed<PortalCategoryOptionDto[]>(() => {
    const dept = this.departmentId();
    return (this.form()?.categories ?? []).filter((c) => !c.parentId && (!c.departmentId || !dept || c.departmentId === dept));
  });
  protected readonly subcategories = computed<PortalCategoryOptionDto[]>(() => {
    const parent = this.categoryId();
    return parent ? (this.form()?.categories ?? []).filter((c) => c.parentId === parent) : [];
  });
  protected readonly installations = computed(() => {
    const product = this.productId();
    return (this.form()?.installations ?? []).filter((i) => !product || i.productId === product);
  });
  private readonly fieldMap = computed(() => new Map((this.form()?.fields ?? []).map((d) => [d.key, d] as const)));

  constructor() {
    const prefill = inject(SubmitPrefillService).take();
    if (prefill) {
      this.subject.set(prefill.subject ?? '');
      this.description.set(prefill.descriptionHtml ?? '');
      this.sourceFlowId = prefill.sourceFlowId ?? null;
    }
    effect(() => {
      const f = this.form();
      if (f) untracked(() => this.applyDefaults(f));
    });
  }

  protected fieldDef(key: string): PortalFieldDefDto | null {
    return key.startsWith('cf:') ? (this.fieldMap().get(key.slice(3)) ?? null) : null;
  }

  /** Keys of a section that exist and apply right now (custom fields by department, empty option lists hidden). */
  protected visibleKeys(section: FormSection): string[] {
    const f = this.form();
    if (!f) return [];
    return section.fields.filter((key) => {
      switch (key) {
        case 'departmentId':
          return f.departments.length > 1;
        case 'categoryId':
          return this.topCategories().length > 0;
        case 'productId':
          return f.products.length > 0;
        case 'installationId':
          return f.installations.length > 0;
        case 'priority':
          return f.priorities.length > 0;
        default: {
          const def = this.fieldDef(key);
          return !!def && def.editable !== false && this.appliesToDepartment(def);
        }
      }
    });
  }

  protected setDepartment(id: string): void {
    this.departmentId.set(id || null);
    const cat = this.form()?.categories.find((c) => c.id === this.categoryId());
    if (cat?.departmentId && cat.departmentId !== id) {
      this.categoryId.set(null);
      this.subcategoryId.set(null);
    }
  }

  protected setCategory(id: string): void {
    this.categoryId.set(id || null);
    this.subcategoryId.set(null);
  }

  protected setProduct(id: string): void {
    this.productId.set(id || null);
    const inst = this.form()?.installations.find((i) => i.id === this.installationId());
    if (inst && id && inst.productId !== id) this.installationId.set(null);
  }

  protected setInstallation(id: string): void {
    this.installationId.set(id || null);
    const inst = this.form()?.installations.find((i) => i.id === id);
    if (inst) this.productId.set(inst.productId);
  }

  protected setCustom(key: string, value: FieldValue): void {
    this.cf.update((values) => ({ ...values, [key]: value }));
  }

  protected solvedByArticle(): void {
    this.toast.success("Great, you're all set!", 'No ticket was created.');
    void this.router.navigate([this.brand.base()]);
  }

  protected async submit(): Promise<void> {
    const slug = this.brand.slug();
    const f = this.form();
    if (!slug || !f || this.busy() || this.uploading()) return;
    const errors = this.validate(f);
    this.errors.set(errors);
    this.error.set(null);
    if (Object.values(errors).some(Boolean)) {
      this.error.set('Please check the highlighted fields.');
      return;
    }
    this.busy.set(true);
    try {
      const created = await firstValueFrom(
        this.api.create(slug, {
          subject: this.subject().trim(),
          descriptionHtml: this.description(),
          departmentId: this.departmentId(),
          categoryId: this.categoryId(),
          subcategoryId: this.subcategoryId(),
          productId: this.productId(),
          installationId: this.installationId(),
          priority: this.priority(),
          customFields: this.customFieldPayload(f),
          attachmentFileIds: this.fileIds().length ? this.fileIds() : null,
          clientRequestId: this.requestId,
          sourceFlowId: this.sourceFlowId,
        }),
      );
      this.requestId = newClientRequestId();
      this.created.emit(created);
    } catch (err) {
      const e = ApiError.from(err);
      const fieldErrors: Record<string, string | null> = {
        subject: e.fieldError('subject'),
        descriptionHtml: e.fieldError('descriptionHtml') ?? e.fieldError('description'),
        departmentId: e.fieldError('departmentId'),
        categoryId: e.fieldError('categoryId'),
        subcategoryId: e.fieldError('subcategoryId'),
        productId: e.fieldError('productId'),
        installationId: e.fieldError('installationId'),
        priority: e.fieldError('priority'),
      };
      for (const def of f.fields) {
        fieldErrors[def.key] = e.fieldError(def.key) ?? e.fieldError(`customFields.${def.key}`) ?? e.fieldError(`cf:${def.key}`);
      }
      this.errors.set(fieldErrors);
      this.error.set(
        Object.values(fieldErrors).some(Boolean) ? 'Please check the highlighted fields.' : errorMessage(err, 'Your ticket could not be submitted. Please try again.'),
      );
    } finally {
      this.busy.set(false);
    }
  }

  private appliesToDepartment(def: PortalFieldDefDto): boolean {
    const dept = this.departmentId();
    return !def.departmentIds.length || (!!dept && def.departmentIds.includes(dept));
  }

  /** Pre-selects a single department and custom-field defaults once the form arrives. */
  private applyDefaults(f: PortalTicketFormDto): void {
    if (f.departments.length === 1 && !this.departmentId()) this.departmentId.set(f.departments[0].id);
    const defaults: Record<string, FieldValue> = {};
    for (const d of f.fields) {
      if (d.defaultValue === null || d.defaultValue === undefined || d.defaultValue === '') continue;
      const type = d.fieldType.toLowerCase();
      defaults[d.key] = type === 'checkbox' ? d.defaultValue === 'true' : type === 'multipicklist' ? d.defaultValue.split(',').map((v) => v.trim()) : d.defaultValue;
    }
    if (Object.keys(defaults).length) this.cf.update((values) => ({ ...defaults, ...values }));
  }

  private validate(f: PortalTicketFormDto): Record<string, string | null> {
    const errors: Record<string, string | null> = {};
    const subject = this.subject().trim();
    if (!subject) errors['subject'] = 'Enter a subject.';
    else if (subject.length < 5) errors['subject'] = 'Use at least 5 characters.';
    if (isHtmlEmpty(this.description())) errors['descriptionHtml'] = 'Describe the problem so we can help.';
    for (const def of f.fields) {
      if (!def.required || def.editable === false || !this.appliesToDepartment(def)) continue;
      if (isBlank(this.cf()[def.key] ?? null, def)) errors[def.key] = `${def.label} is required.`;
    }
    return errors;
  }

  private customFieldPayload(f: PortalTicketFormDto): Record<string, JsonValue> | null {
    const out: Record<string, JsonValue> = {};
    for (const def of f.fields) {
      if (def.editable === false || !this.appliesToDepartment(def)) continue;
      const value = this.cf()[def.key] ?? null;
      const type = def.fieldType.toLowerCase();
      if (type === 'checkbox') {
        if (value !== null) out[def.key] = value === true;
        continue;
      }
      if (isBlank(value, def)) continue;
      if (type === 'number' || type === 'decimal' || type === 'currency') {
        const n = Number(value);
        if (Number.isFinite(n)) out[def.key] = n;
      } else if (Array.isArray(value)) out[def.key] = value;
      else out[def.key] = typeof value === 'string' ? value.trim() : (value as JsonValue);
    }
    return Object.keys(out).length ? out : null;
  }
}

function isBlank(value: FieldValue, def: PortalFieldDefDto): boolean {
  if (def.fieldType.toLowerCase() === 'checkbox') return value !== true;
  if (value === null) return true;
  if (Array.isArray(value)) return value.length === 0;
  return typeof value === 'string' && value.trim() === '';
}
