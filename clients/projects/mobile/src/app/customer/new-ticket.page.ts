import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import {
  IonButton,
  IonCheckbox,
  IonContent,
  IonHeader,
  IonInput,
  IonItem,
  IonLabel,
  IonList,
  IonListHeader,
  IonNote,
  IonSelect,
  IonSelectOption,
  IonSpinner,
  IonText,
  IonTextarea,
  IonTitle,
  IonToolbar,
} from '@ionic/angular/standalone';
import {
  ApiError,
  JsonValue,
  PortalArticleSummaryDto,
  PortalCategoryOptionDto,
  PortalContentApi,
  PortalFieldDefDto,
  PortalTicketFormDto,
  PortalTicketsApi,
} from '@sd/core';
import { Subject, debounceTime, distinctUntilChanged, filter, switchMap, catchError, of } from 'rxjs';
import { cachedResource } from '../core/cached-resource';
import { DraftStore } from '../core/drafts.service';
import { ModeService } from '../core/mobile-session';
import { NetworkService } from '../core/network.service';
import { OutboxService } from '../core/outbox.service';
import { MobileToasts } from '../core/ui-services';
import { AttachmentPicker } from '../shared/attachment-picker.component';
import { OfflineBanner } from '../shared/offline-banner.component';
import { StateView } from '../shared/state-view.component';
import { textToHtml } from '../shared/format';

type FieldValue = string | string[] | boolean | null;

interface NewTicketDraft {
  subject: string;
  description: string;
  departmentId: string | null;
  categoryId: string | null;
  subcategoryId: string | null;
  productId: string | null;
  installationId: string | null;
  priority: string | null;
  cf: Record<string, FieldValue>;
}

const STANDARD_KEYS = ['departmentId', 'categoryId', 'productId', 'installationId', 'priority'];
const DRAFT_KEY = 'portal:new-ticket';

/**
 * New customer request from the portal form API (departments, categories, products, installations, priorities and the
 * brand's custom fields / layout). Suggests knowledge-base articles while typing. Saved as a draft on the device; when
 * offline it is queued in the outbox and submitted (idempotently) once the connection is back.
 */
@Component({
  selector: 'm-new-ticket',
  imports: [
    RouterLink,
    IonHeader,
    IonToolbar,
    IonTitle,
    IonContent,
    IonList,
    IonListHeader,
    IonItem,
    IonLabel,
    IonInput,
    IonTextarea,
    IonSelect,
    IonSelectOption,
    IonCheckbox,
    IonButton,
    IonNote,
    IonText,
    IonSpinner,
    AttachmentPicker,
    OfflineBanner,
    StateView,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ion-header>
      <ion-toolbar><ion-title>New request</ion-title></ion-toolbar>
      <m-offline-banner [staleSince]="formRes.staleSince()" />
    </ion-header>
    <ion-content>
      @if (form(); as f) {
        <form (submit)="$event.preventDefault(); submit()" novalidate>
          @if (error()) {
            <ion-text color="danger"><p class="error" role="alert">{{ error() }}</p></ion-text>
          }
          <ion-list lines="full">
            <ion-item>
              <ion-input label="Subject" labelPlacement="stacked" [value]="d().subject" (ionInput)="patch({ subject: $any($event).detail.value ?? '' })"
                [maxlength]="250" [required]="true" helperText="A short summary of the problem" [errorText]="errors()['subject'] ?? ''"
                [class.ion-invalid]="!!errors()['subject']" [class.ion-touched]="!!errors()['subject']" />
            </ion-item>
            @if (suggestions().length) {
              <div class="suggest" aria-live="polite">
                <p>These articles might already answer it:</p>
                @for (a of suggestions(); track a.id) {
                  <a [routerLink]="['/customer/help/articles', a.slug]">{{ a.title }}</a>
                }
              </div>
            }
            <ion-item>
              <ion-textarea label="Description" labelPlacement="stacked" [autoGrow]="true" [rows]="4" [value]="d().description"
                (ionInput)="patch({ description: $any($event).detail.value ?? '' })" placeholder="What happened? What did you expect? Steps, error messages…"
                [errorText]="errors()['description'] ?? ''" [class.ion-invalid]="!!errors()['description']" [class.ion-touched]="!!errors()['description']" />
            </ion-item>
          </ion-list>

          @for (section of sections(); track $index) {
            @if (visibleKeys(section.fields).length) {
              <ion-list lines="full">
                <ion-list-header><ion-label>{{ section.title }}</ion-label></ion-list-header>
                @for (key of visibleKeys(section.fields); track key) {
                  @switch (key) {
                    @case ('departmentId') {
                      <ion-item>
                        <ion-select label="Department" labelPlacement="stacked" interface="action-sheet" [value]="d().departmentId" (ionChange)="setDepartment($any($event).detail.value)">
                          @for (o of f.departments; track o.id) {
                            <ion-select-option [value]="o.id">{{ o.name }}</ion-select-option>
                          }
                        </ion-select>
                      </ion-item>
                    }
                    @case ('categoryId') {
                      <ion-item>
                        <ion-select label="Category" labelPlacement="stacked" interface="action-sheet" [value]="d().categoryId" (ionChange)="setCategory($any($event).detail.value)">
                          @for (o of topCategories(); track o.id) {
                            <ion-select-option [value]="o.id">{{ o.name }}</ion-select-option>
                          }
                        </ion-select>
                      </ion-item>
                      @if (subcategories().length) {
                        <ion-item>
                          <ion-select label="Sub-category" labelPlacement="stacked" interface="action-sheet" [value]="d().subcategoryId" (ionChange)="patch({ subcategoryId: $any($event).detail.value })">
                            @for (o of subcategories(); track o.id) {
                              <ion-select-option [value]="o.id">{{ o.name }}</ion-select-option>
                            }
                          </ion-select>
                        </ion-item>
                      }
                    }
                    @case ('productId') {
                      <ion-item>
                        <ion-select label="Product" labelPlacement="stacked" interface="action-sheet" [value]="d().productId" (ionChange)="setProduct($any($event).detail.value)">
                          @for (o of f.products; track o.id) {
                            <ion-select-option [value]="o.id">{{ o.name }}</ion-select-option>
                          }
                        </ion-select>
                      </ion-item>
                    }
                    @case ('installationId') {
                      <ion-item>
                        <ion-select label="Installation" labelPlacement="stacked" interface="action-sheet" [value]="d().installationId" (ionChange)="setInstallation($any($event).detail.value)">
                          @for (o of installations(); track o.id) {
                            <ion-select-option [value]="o.id">{{ o.name }} · {{ o.productName }}{{ o.locationName ? ' · ' + o.locationName : '' }}</ion-select-option>
                          }
                        </ion-select>
                      </ion-item>
                    }
                    @case ('priority') {
                      <ion-item>
                        <ion-select label="Priority" labelPlacement="stacked" interface="action-sheet" [value]="d().priority" (ionChange)="patch({ priority: $any($event).detail.value })">
                          @for (p of f.priorities; track p) {
                            <ion-select-option [value]="p">{{ p }}</ion-select-option>
                          }
                        </ion-select>
                      </ion-item>
                    }
                    @default {
                      @if (fieldDef(key); as def) {
                        <ion-item>
                          @switch (kind(def)) {
                            @case ('checkbox') {
                              <ion-checkbox [checked]="d().cf[def.key] === true" (ionChange)="setCustom(def.key, $any($event).detail.checked)" labelPlacement="end">
                                {{ def.label }}{{ def.required ? ' *' : '' }}
                              </ion-checkbox>
                            }
                            @case ('select') {
                              <ion-select [label]="def.label + (def.required ? ' *' : '')" labelPlacement="stacked" interface="action-sheet" [value]="d().cf[def.key]"
                                (ionChange)="setCustom(def.key, $any($event).detail.value)">
                                @for (o of def.options; track o.value) {
                                  <ion-select-option [value]="o.value">{{ o.label }}</ion-select-option>
                                }
                              </ion-select>
                            }
                            @case ('multi') {
                              <ion-select [label]="def.label + (def.required ? ' *' : '')" labelPlacement="stacked" [multiple]="true" [value]="d().cf[def.key]"
                                (ionChange)="setCustom(def.key, $any($event).detail.value)">
                                @for (o of def.options; track o.value) {
                                  <ion-select-option [value]="o.value">{{ o.label }}</ion-select-option>
                                }
                              </ion-select>
                            }
                            @case ('textarea') {
                              <ion-textarea [label]="def.label + (def.required ? ' *' : '')" labelPlacement="stacked" [autoGrow]="true" [value]="$any(d().cf[def.key]) ?? ''"
                                (ionInput)="setCustom(def.key, $any($event).detail.value)" [helperText]="def.helpText ?? ''" [errorText]="errors()[def.key] ?? ''"
                                [class.ion-invalid]="!!errors()[def.key]" [class.ion-touched]="!!errors()[def.key]" />
                            }
                            @default {
                              <ion-input [label]="def.label + (def.required ? ' *' : '')" labelPlacement="stacked" [type]="inputType(def)" [value]="$any(d().cf[def.key]) ?? ''"
                                [inputmode]="def.fieldType === 'Number' || def.fieldType === 'Decimal' || def.fieldType === 'Currency' ? 'decimal' : undefined"
                                (ionInput)="setCustom(def.key, $any($event).detail.value)" [helperText]="def.helpText ?? ''" [errorText]="errors()[def.key] ?? ''"
                                [class.ion-invalid]="!!errors()[def.key]" [class.ion-touched]="!!errors()[def.key]" />
                            }
                          }
                        </ion-item>
                        @if (errors()[def.key] && kind(def) !== 'textarea' && kind(def) !== 'input') {
                          <ion-note color="danger" class="field-error">{{ errors()[def.key] }}</ion-note>
                        }
                      }
                    }
                  }
                }
              </ion-list>
            }
          }

          <div class="attach">
            <m-attachment-picker [(files)]="files" [maxMb]="f.maxAttachmentMb || 25" />
          </div>
          <div class="submit">
            <ion-button expand="block" type="submit" [disabled]="busy()">
              @if (busy()) {
                <ion-spinner name="crescent" aria-label="Submitting" />
              } @else {
                {{ online() ? 'Submit request' : 'Save — submit when online' }}
              }
            </ion-button>
            <ion-button expand="block" fill="clear" color="medium" type="button" (click)="clear()">Clear form</ion-button>
          </div>
        </form>
      } @else if (formRes.loading()) {
        <m-state kind="loading" title="Loading the form" />
      } @else if (formRes.error()) {
        <m-state [kind]="online() ? 'error' : 'offline'" [message]="online() ? formRes.error()!.userMessage : 'Open this screen once while online to use it offline.'"
          actionLabel="Try again" (action)="formRes.load()" />
      }
    </ion-content>
  `,
  styles: `
    .error {
      margin: 12px 16px 0;
    }
    .suggest {
      padding: 8px 16px 12px;
      font-size: 0.85rem;
      background: var(--sd-info-bg);
    }
    .suggest p {
      margin: 0 0 4px;
      color: var(--sd-text-2);
    }
    .suggest a {
      display: block;
      padding: 6px 0;
      color: var(--ion-color-primary);
    }
    .field-error {
      display: block;
      padding: 0 16px 8px;
      font-size: 0.8rem;
    }
    .attach {
      padding: 8px;
    }
    .submit {
      padding: 8px 16px calc(24px + var(--ion-safe-area-bottom, 0px));
    }
  `,
})
export class NewTicketPage implements OnInit {
  private readonly api = inject(PortalTicketsApi);
  private readonly content = inject(PortalContentApi);
  private readonly mode = inject(ModeService);
  private readonly outbox = inject(OutboxService);
  private readonly drafts = inject(DraftStore);
  private readonly network = inject(NetworkService);
  private readonly router = inject(Router);
  private readonly toasts = inject(MobileToasts);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly formRes = cachedResource<PortalTicketFormDto>({
    key: () => 'portal:form',
    kind: 'meta',
    fetch: () => this.api.form(this.mode.brand()),
  });
  protected readonly form = this.formRes.value;
  protected readonly online = this.network.online;
  protected readonly d = signal<NewTicketDraft>(emptyDraft());
  protected readonly files = signal<File[]>([]);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly errors = signal<Record<string, string | null>>({});
  protected readonly suggestions = signal<PortalArticleSummaryDto[]>([]);
  private readonly subjectChanges = new Subject<string>();
  private readonly lastSent = new Map<string, { ticketId: string; displayNumber: string | null }>();

  protected readonly sections = computed(() => {
    const f = this.form();
    if (!f) return [];
    const configured = f.sections.filter((s) => s.fields.length).map((s) => ({ title: s.title || 'Details', fields: s.fields }));
    const sections = configured.length ? configured : [{ title: 'Details', fields: [...STANDARD_KEYS, ...f.fields.map((x) => `cf:${x.key}`)] }];
    const placed = new Set(sections.flatMap((s) => s.fields));
    const missing = f.fields.filter((x) => !placed.has(`cf:${x.key}`)).map((x) => `cf:${x.key}`);
    return missing.length ? [...sections, { title: 'Additional information', fields: missing }] : sections;
  });
  protected readonly topCategories = computed<PortalCategoryOptionDto[]>(() => {
    const dept = this.d().departmentId;
    return (this.form()?.categories ?? []).filter((c) => !c.parentId && (!c.departmentId || !dept || c.departmentId === dept));
  });
  protected readonly subcategories = computed<PortalCategoryOptionDto[]>(() => {
    const parent = this.d().categoryId;
    return parent ? (this.form()?.categories ?? []).filter((c) => c.parentId === parent) : [];
  });
  protected readonly installations = computed(() => {
    const product = this.d().productId;
    return (this.form()?.installations ?? []).filter((i) => !product || i.productId === product);
  });
  private readonly fieldMap = computed(() => new Map((this.form()?.fields ?? []).map((x) => [x.key, x] as const)));

  constructor() {
    effect(() => {
      const f = this.form();
      if (f) untracked(() => this.applyDefaults(f));
    });
  }

  ngOnInit(): void {
    void this.formRes.load();
    void this.drafts.get<NewTicketDraft>(DRAFT_KEY).then((draft) => {
      // Restore unless the user already started typing.
      if (draft && !this.d().subject && !this.d().description) this.d.set({ ...emptyDraft(), ...draft, cf: { ...draft.cf } });
    });
    const sub = this.subjectChanges
      .pipe(
        debounceTime(500),
        distinctUntilChanged(),
        filter(() => this.online()),
        switchMap((q) => (q.length >= 4 ? this.content.suggest(this.mode.brand(), q, 3).pipe(catchError(() => of([]))) : of([]))),
      )
      .subscribe((list) => this.suggestions.set(list));
    const sent = this.outbox.sent$.subscribe((e) => {
      if (e.entry.kind === 'customer-ticket') this.lastSent.set(e.entry.id, { ticketId: e.ticketId, displayNumber: e.displayNumber });
    });
    this.destroyRef.onDestroy(() => {
      sub.unsubscribe();
      sent.unsubscribe();
    });
  }

  protected patch(changes: Partial<NewTicketDraft>): void {
    this.d.update((v) => ({ ...v, ...changes }));
    if (changes.subject !== undefined) this.subjectChanges.next(changes.subject.trim());
    this.drafts.save(DRAFT_KEY, this.d(), (v) => !v.subject.trim() && !v.description.trim());
  }

  protected setDepartment(id: string | null): void {
    const cat = this.form()?.categories.find((c) => c.id === this.d().categoryId);
    const clearCategory = !!cat?.departmentId && cat.departmentId !== id;
    this.patch({ departmentId: id, ...(clearCategory ? { categoryId: null, subcategoryId: null } : {}) });
  }

  protected setCategory(id: string | null): void {
    this.patch({ categoryId: id, subcategoryId: null });
  }

  protected setProduct(id: string | null): void {
    const inst = this.form()?.installations.find((i) => i.id === this.d().installationId);
    this.patch({ productId: id, ...(inst && id && inst.productId !== id ? { installationId: null } : {}) });
  }

  protected setInstallation(id: string | null): void {
    const inst = this.form()?.installations.find((i) => i.id === id);
    this.patch({ installationId: id, ...(inst ? { productId: inst.productId } : {}) });
  }

  protected setCustom(key: string, value: FieldValue): void {
    this.patch({ cf: { ...this.d().cf, [key]: value } });
  }

  protected fieldDef(key: string): PortalFieldDefDto | null {
    return key.startsWith('cf:') ? (this.fieldMap().get(key.slice(3)) ?? null) : null;
  }

  protected visibleKeys(keys: string[]): string[] {
    const f = this.form();
    if (!f) return [];
    return keys.filter((key) => {
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
          return !!def && def.editable !== false && this.applies(def);
        }
      }
    });
  }

  protected kind(def: PortalFieldDefDto): 'checkbox' | 'select' | 'multi' | 'textarea' | 'input' {
    switch (def.fieldType.toLowerCase()) {
      case 'checkbox':
        return 'checkbox';
      case 'picklist':
        return 'select';
      case 'multipicklist':
        return 'multi';
      case 'textarea':
        return 'textarea';
      default:
        return 'input';
    }
  }

  protected inputType(def: PortalFieldDefDto): 'text' | 'number' | 'date' | 'datetime-local' | 'url' | 'email' | 'tel' {
    switch (def.fieldType.toLowerCase()) {
      case 'number':
      case 'decimal':
      case 'currency':
        return 'number';
      case 'date':
        return 'date';
      case 'datetime':
        return 'datetime-local';
      case 'url':
        return 'url';
      case 'email':
        return 'email';
      case 'phone':
        return 'tel';
      default:
        return 'text';
    }
  }

  protected async clear(): Promise<void> {
    this.d.set(emptyDraft());
    this.files.set([]);
    this.errors.set({});
    this.error.set(null);
    this.suggestions.set([]);
    await this.drafts.remove(DRAFT_KEY);
    const f = this.form();
    if (f) this.applyDefaults(f);
  }

  protected async submit(): Promise<void> {
    const f = this.form();
    if (!f || this.busy()) return;
    const errors = this.validate(f);
    this.errors.set(errors);
    if (Object.values(errors).some(Boolean)) {
      this.error.set('Please check the highlighted fields.');
      return;
    }
    this.error.set(null);
    this.busy.set(true);
    const v = this.d();
    try {
      const entry = await this.outbox.enqueue({
        kind: 'customer-ticket',
        brand: this.mode.brand(),
        label: v.subject.trim(),
        bodyHtml: textToHtml(v.description),
        bodyText: v.description,
        files: this.files(),
        ticket: {
          subject: v.subject.trim(),
          descriptionHtml: textToHtml(v.description),
          departmentId: v.departmentId,
          categoryId: v.categoryId,
          subcategoryId: v.subcategoryId,
          productId: v.productId,
          installationId: v.installationId,
          priority: v.priority,
          customFields: this.customFieldPayload(f),
        },
      });
      const outcome = await this.outbox.settle(entry.id);
      if (outcome?.status === 'failed') {
        // Rejected by the server (validation): give it back to the form so it can be corrected.
        await this.outbox.takeBack(entry.id);
        this.error.set(outcome.lastError ?? 'Your request could not be submitted.');
        return;
      }
      const sent = this.sentTicket(entry.id, outcome === null);
      await this.clear();
      if (sent) {
        void this.toasts.success(`Request ${sent.displayNumber ?? ''} created — we will be in touch.`);
        await this.router.navigate(['/customer/tickets', sent.ticketId], { replaceUrl: true });
      } else {
        void this.toasts.show('Saved on this device — it will be submitted when you are back online.');
        await this.router.navigate(['/customer/tickets'], { replaceUrl: true });
      }
    } catch (e) {
      this.error.set(ApiError.from(e).userMessage);
    } finally {
      this.busy.set(false);
    }
  }

  /** The created ticket of a delivered entry (captured from the outbox `sent$` stream during settle). */
  private sentTicket(id: string, delivered: boolean): { ticketId: string; displayNumber: string | null } | null {
    return delivered ? (this.lastSent.get(id) ?? null) : null;
  }

  private applies(def: PortalFieldDefDto): boolean {
    const dept = this.d().departmentId;
    return !def.departmentIds.length || (!!dept && def.departmentIds.includes(dept));
  }

  private applyDefaults(f: PortalTicketFormDto): void {
    const v = this.d();
    const changes: Partial<NewTicketDraft> = {};
    if (f.departments.length === 1 && !v.departmentId) changes.departmentId = f.departments[0].id;
    if (!v.priority && f.priorities.includes('Medium')) changes.priority = 'Medium';
    const cf: Record<string, FieldValue> = {};
    for (const def of f.fields) {
      if (def.defaultValue === null || def.defaultValue === undefined || def.defaultValue === '' || v.cf[def.key] !== undefined) continue;
      const type = def.fieldType.toLowerCase();
      cf[def.key] = type === 'checkbox' ? def.defaultValue === 'true' : type === 'multipicklist' ? def.defaultValue.split(',').map((x) => x.trim()) : def.defaultValue;
    }
    if (Object.keys(cf).length) changes.cf = { ...cf, ...v.cf };
    if (Object.keys(changes).length) this.d.update((x) => ({ ...x, ...changes }));
  }

  private validate(f: PortalTicketFormDto): Record<string, string | null> {
    const v = this.d();
    const errors: Record<string, string | null> = {};
    const subject = v.subject.trim();
    if (!subject) errors['subject'] = 'Enter a subject.';
    else if (subject.length < 5) errors['subject'] = 'Use at least 5 characters.';
    if (!v.description.trim()) errors['description'] = 'Describe the problem so we can help.';
    for (const def of f.fields) {
      if (!def.required || def.editable === false || !this.applies(def)) continue;
      if (isBlank(v.cf[def.key] ?? null, def)) errors[def.key] = `${def.label} is required.`;
    }
    return errors;
  }

  private customFieldPayload(f: PortalTicketFormDto): Record<string, JsonValue> | null {
    const out: Record<string, JsonValue> = {};
    for (const def of f.fields) {
      if (def.editable === false || !this.applies(def)) continue;
      const value = this.d().cf[def.key] ?? null;
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

function emptyDraft(): NewTicketDraft {
  return { subject: '', description: '', departmentId: null, categoryId: null, subcategoryId: null, productId: null, installationId: null, priority: null, cf: {} };
}

function isBlank(value: FieldValue, def: PortalFieldDefDto): boolean {
  if (def.fieldType.toLowerCase() === 'checkbox') return value !== true;
  if (value === null || value === undefined) return true;
  if (Array.isArray(value)) return value.length === 0;
  return typeof value === 'string' && value.trim() === '';
}
