import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import {
  ApiError,
  Channel,
  ContactLookupDto,
  CreateTicketRequest,
  CustomerLookupDto,
  FilesApi,
  Guid,
  InstallationLookupDto,
  JsonValue,
  MeStore,
  MetaApi,
  PRIORITIES,
  Priority,
  SEVERITIES,
  Severity,
  TicketDetailDto,
  TicketsApi,
  WorkbenchApi,
  newClientRequestId,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCard,
  SdCheckbox,
  SdChipsInput,
  SdCombobox,
  SdFormField,
  SdIcon,
  SdInput,
  SdOption,
  SdOptionSource,
  SdPageHeader,
  SdRichTextEditor,
  SdSlaCountdown,
  ToastService,
} from '@sd/ui';
import { Subscription, map } from 'rxjs';
import { CustomFieldsComponent, applicableCustomFields, validateCustomFields } from '../shared/custom-fields.component';
import { AGENT_CHANNELS, agentOptions, channelLabel, departmentOptions, entitlementLabel, entitlementTone, teamOptions, toastApiError } from '../shared/ticket-ui';
import { TicketWorkbenchStore } from '../workspace/workbench.store';

interface Upload {
  key: number;
  name: string;
  percent: number | null;
  fileId: Guid | null;
  error: string | null;
  sub: Subscription | null;
}

/**
 * /tickets/new — log a ticket for a customer (phone, walk-in, e-mail…): customer & contact pickers with
 * create-on-the-fly, routing (department/team/assignee), product/installation, classification, priority and
 * severity, the department's custom fields (validated), tags, attachments and the description. After creation
 * it shows the customer's support eligibility (entitlement, contract, SLA).
 */
@Component({
  selector: 'desk-new-ticket-page',
  imports: [
    FormsModule,
    RouterLink,
    SdPageHeader,
    SdCard,
    SdFormField,
    SdInput,
    SdCombobox,
    SdChipsInput,
    SdCheckbox,
    SdButton,
    SdIcon,
    SdBadge,
    SdRichTextEditor,
    SdSlaCountdown,
    CustomFieldsComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="New ticket" icon="file-plus" [breadcrumbs]="[{ label: 'Tickets', link: '/tickets' }, { label: 'New ticket' }]" />
      <div class="desk-page__body">
        @if (created(); as t) {
          <sd-card class="done">
            <div class="done-head">
              <sd-icon name="circle-check" [size]="28" />
              <div>
                <h2>{{ t.displayNumber }} created</h2>
                <p class="sd-muted">{{ t.subject }}</p>
              </div>
            </div>
            <h3>Support eligibility</h3>
            <dl class="elig">
              <dt>Entitlement</dt>
              <dd><sd-badge [tone]="entitlementTone(t.entitlementStatus)" icon="scale">{{ entitlementLabel(t.entitlementStatus) }}</sd-badge></dd>
              <dt>Contract</dt>
              <dd>{{ t.contractName ?? 'No covering contract' }}</dd>
              @if (t.entitlementNote) {
                <dt>Note</dt>
                <dd>{{ t.entitlementNote }}</dd>
              }
              <dt>SLA policy</dt>
              <dd>{{ t.slaPolicyName ?? 'None' }}</dd>
              @if (t.firstResponseDueAt) {
                <dt>First response</dt>
                <dd><sd-sla-countdown [dueAt]="t.firstResponseDueAt" [state]="t.slaState" /></dd>
              }
              @if (t.dueAt) {
                <dt>Resolution</dt>
                <dd><sd-sla-countdown [dueAt]="t.dueAt" [state]="t.slaState" /></dd>
              }
              <dt>Routed to</dt>
              <dd>{{ t.departmentName }}{{ t.teamName ? ' › ' + t.teamName : '' }} · {{ t.assigneeName ?? 'unassigned' }}</dd>
            </dl>
            @if (t.entitlementStatus === 'NotEntitled' || t.entitlementStatus === 'Expired') {
              <p class="warn" role="alert">
                <sd-icon name="triangle-alert" [size]="15" /> This customer is not entitled to support for this request. A supervisor can override the
                entitlement from the ticket's actions menu.
              </p>
            }
            <div class="done-actions">
              <button sdButton type="button" icon="plus" (click)="reset()">Create another</button>
              <button sdButton variant="primary" type="button" iconRight="arrow-right" (click)="openCreated()">Open ticket</button>
            </div>
          </sd-card>
        } @else {
          <form class="form" (ngSubmit)="submit()" novalidate>
            <sd-card class="section" title="Requester">
              <div class="grid">
                <sd-form-field label="Customer" hint="Search by name, code or e-mail">
                  <sd-combobox [search]="customerSource" [options]="customerKnown()" [value]="customerId()" (valueChange)="setCustomer($any($event))" placeholder="Search customers…" />
                </sd-form-field>
                @if (!newContact()) {
                  <sd-form-field label="Contact" [error]="errors()['contactId']">
                    <sd-combobox [search]="contactSource" [options]="contactKnown()" [value]="contactId()" (valueChange)="setContact($any($event))" placeholder="Search contacts…" />
                  </sd-form-field>
                }
              </div>
              <sd-checkbox [value]="newContact()" (valueChange)="toggleNewContact($event)">The requester is a new contact</sd-checkbox>
              @if (newContact()) {
                <div class="grid">
                  <sd-form-field label="Full name" required [error]="errors()['newContact.fullName']">
                    <input sdInput name="ncName" autocomplete="off" [ngModel]="ncName()" (ngModelChange)="ncName.set($event)" maxlength="200" />
                  </sd-form-field>
                  <sd-form-field label="E-mail" [error]="errors()['newContact.email']">
                    <input sdInput type="email" name="ncEmail" autocomplete="off" [ngModel]="ncEmail()" (ngModelChange)="ncEmail.set($event)" maxlength="250" />
                  </sd-form-field>
                  <sd-form-field label="Phone" [error]="errors()['newContact.phone']">
                    <input sdInput type="tel" name="ncPhone" autocomplete="off" [ngModel]="ncPhone()" (ngModelChange)="ncPhone.set($event)" maxlength="40" />
                  </sd-form-field>
                </div>
                <p class="sd-small sd-muted">An existing contact with the same e-mail or phone is reused instead of creating a duplicate.</p>
              }
              <sd-form-field label="Channel">
                <div class="channels" role="radiogroup" aria-label="Channel">
                  @for (c of channels; track c) {
                    <button type="button" role="radio" class="chip" [attr.aria-checked]="channel() === c" (click)="channel.set(c)">{{ channelLabel(c) }}</button>
                  }
                </div>
              </sd-form-field>
            </sd-card>

            <sd-card class="section" title="Request">
              <sd-form-field label="Subject" required [error]="errors()['subject']">
                <input sdInput name="subject" [ngModel]="subject()" (ngModelChange)="subject.set($event)" maxlength="250" />
              </sd-form-field>
              <sd-form-field label="Description" [error]="errors()['descriptionHtml']">
                <sd-rich-text-editor [value]="description()" (valueChange)="description.set($event)" minHeight="150px" ariaLabel="Description" placeholder="What does the customer need? Steps, error messages, impact…" />
              </sd-form-field>
              <div class="attach">
                <input #fileInput type="file" multiple hidden (change)="onFiles($event)" />
                <button sdButton size="sm" variant="ghost" icon="paperclip" type="button" (click)="fileInput.click()">Attach files</button>
                @for (u of uploads(); track u.key) {
                  <span class="upload" [class.is-error]="!!u.error">
                    <span class="sd-truncate">{{ u.name }}</span>
                    @if (u.error) {
                      <span class="err sd-xs">{{ u.error }}</span>
                    } @else if (!u.fileId) {
                      <progress max="100" [value]="u.percent ?? 0" [attr.aria-label]="'Uploading ' + u.name"></progress>
                    }
                    <button type="button" class="x" [attr.aria-label]="'Remove ' + u.name" (click)="removeUpload(u.key)"><sd-icon name="x" [size]="12" /></button>
                  </span>
                }
              </div>
              <div class="grid">
                <sd-form-field label="Priority">
                  <sd-combobox [options]="priorities" [searchable]="false" [clearable]="false" [value]="priority()" (valueChange)="priority.set($any($event))" />
                </sd-form-field>
                <sd-form-field label="Severity" optional>
                  <sd-combobox [options]="severities" [searchable]="false" [value]="severity()" (valueChange)="severity.set($any($event))" />
                </sd-form-field>
                <sd-form-field label="Category" optional>
                  <sd-combobox [options]="categories()" [value]="categoryId()" (valueChange)="setCategory($any($event))" />
                </sd-form-field>
                <sd-form-field label="Subcategory" optional>
                  <sd-combobox [options]="subcategories()" [value]="subcategoryId()" (valueChange)="subcategoryId.set($any($event))" [disabled]="!categoryId()" />
                </sd-form-field>
                <sd-form-field label="Issue type" optional>
                  <sd-combobox [options]="issueTypes()" [value]="issueTypeId()" (valueChange)="issueTypeId.set($any($event))" />
                </sd-form-field>
              </div>
              <sd-form-field label="Tags" optional>
                <sd-chips-input [value]="tags()" (valueChange)="tags.set($event)" [suggestions]="tagSuggestions()" lowercase placeholder="Add tag" />
              </sd-form-field>
            </sd-card>

            <sd-card class="section" title="Product">
              <div class="grid">
                <sd-form-field label="Product" optional>
                  <sd-combobox [options]="products()" [value]="productId()" (valueChange)="setProduct($any($event))" />
                </sd-form-field>
                <sd-form-field label="Module" optional>
                  <sd-combobox [options]="modules()" [value]="moduleId()" (valueChange)="moduleId.set($any($event))" [disabled]="!productId()" />
                </sd-form-field>
                <sd-form-field label="Version" optional>
                  <sd-combobox [options]="versions()" [value]="versionId()" (valueChange)="versionId.set($any($event))" [disabled]="!productId()" />
                </sd-form-field>
                <sd-form-field label="Installation" optional [hint]="customerId() ? null : 'Choose a customer first'">
                  <sd-combobox [options]="installationOptions()" [value]="installationId()" (valueChange)="setInstallation($any($event))" [disabled]="!customerId()" />
                </sd-form-field>
              </div>
            </sd-card>

            <sd-card class="section" title="Routing">
              <div class="grid">
                <sd-form-field label="Department" optional hint="Leave empty to let routing rules decide" [error]="errors()['departmentId']">
                  <sd-combobox [options]="departments()" [value]="departmentId()" (valueChange)="setDepartment($any($event))" />
                </sd-form-field>
                <sd-form-field label="Team" optional>
                  <sd-combobox [options]="teams()" [value]="teamId()" (valueChange)="teamId.set($any($event))" />
                </sd-form-field>
                <sd-form-field label="Assignee" optional>
                  <sd-combobox [options]="agents()" [value]="assigneeId()" (valueChange)="assigneeId.set($any($event))" />
                </sd-form-field>
              </div>
              @if (assigneeId()) {
                <sd-checkbox [value]="skipRouting()" (valueChange)="skipRouting.set($event)">Keep my choice (skip automatic assignment rules)</sd-checkbox>
              }
            </sd-card>

            @if (customFields().length) {
              <sd-card class="section" title="Additional fields">
                <desk-custom-fields [fields]="customFields()" [values]="customValues()" (valuesChange)="customValues.set($event)" [errors]="cfErrors()" />
              </sd-card>
            }

            @if (formError()) {
              <p class="form-error" role="alert"><sd-icon name="circle-alert" [size]="15" /> {{ formError() }}</p>
            }
            <div class="submit-bar">
              <a sdButton routerLink="/tickets">Cancel</a>
              <button sdButton variant="primary" type="submit" icon="check" [loading]="saving()" [disabled]="uploading()">Create ticket</button>
            </div>
          </form>
        }
      </div>
    </div>
  `,
  styles: `
    .form {
      display: grid;
      gap: var(--sd-space-4);
      max-width: 1000px;
    }
    .section ::ng-deep .sd-card__body,
    .section {
      display: block;
    }
    .grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 220px), 1fr));
      gap: var(--sd-space-3);
      margin-bottom: var(--sd-space-3);
    }
    sd-form-field {
      display: block;
      margin-bottom: var(--sd-space-3);
    }
    .grid sd-form-field {
      margin-bottom: 0;
    }
    .channels {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    .chip {
      height: 30px;
      padding: 0 12px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface);
      color: var(--sd-text-2);
      font: inherit;
      font-size: var(--sd-text-sm);
      cursor: pointer;
    }
    .chip[aria-checked='true'] {
      border-color: var(--sd-primary);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
      font-weight: var(--sd-weight-medium);
    }
    .attach {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
      margin-bottom: var(--sd-space-3);
    }
    .upload {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      max-width: 260px;
      padding: 3px 6px 3px 8px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface-2);
      font-size: var(--sd-text-sm);
    }
    .upload.is-error {
      border-color: var(--sd-danger-border);
      background: var(--sd-danger-bg);
    }
    progress {
      width: 60px;
      height: 6px;
    }
    .x {
      display: inline-flex;
      padding: 2px;
      border: 0;
      background: transparent;
      color: var(--sd-text-3);
      cursor: pointer;
    }
    .err,
    .form-error {
      color: var(--sd-danger);
    }
    .form-error {
      display: flex;
      align-items: center;
      gap: 6px;
    }
    .submit-bar {
      position: sticky;
      bottom: 0;
      display: flex;
      justify-content: flex-end;
      gap: 8px;
      padding: 12px 0;
      background: var(--sd-bg);
      border-top: 1px solid var(--sd-divider);
    }
    .done {
      max-width: 720px;
    }
    .done-head {
      display: flex;
      align-items: center;
      gap: 12px;
      color: var(--sd-success);
    }
    .done-head h2 {
      color: var(--sd-text);
      font-size: var(--sd-text-xl);
    }
    h3 {
      margin: var(--sd-space-5) 0 var(--sd-space-2);
      font-size: var(--sd-text-sm);
      color: var(--sd-text-2);
    }
    .elig {
      display: grid;
      grid-template-columns: max-content 1fr;
      gap: 8px 16px;
      margin: 0;
    }
    .elig dt {
      color: var(--sd-text-3);
    }
    .elig dd {
      margin: 0;
    }
    .warn {
      display: flex;
      gap: 6px;
      margin-top: var(--sd-space-4);
      padding: 8px 10px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-warning-bg);
      color: var(--sd-warning);
    }
    .done-actions {
      display: flex;
      justify-content: flex-end;
      gap: 8px;
      margin-top: var(--sd-space-5);
    }
  `,
})
export class NewTicketPage {
  private readonly tickets = inject(TicketsApi);
  private readonly workbench = inject(WorkbenchApi);
  private readonly filesApi = inject(FilesApi);
  private readonly meta = inject(MetaApi);
  private readonly me = inject(MeStore);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly store = inject(TicketWorkbenchStore);

  protected readonly channelLabel = channelLabel;
  protected readonly entitlementTone = entitlementTone;
  protected readonly entitlementLabel = entitlementLabel;
  protected readonly channels = AGENT_CHANNELS;
  protected readonly priorities: SdOption[] = PRIORITIES.map((p) => ({ value: p, label: p }));
  protected readonly severities: SdOption[] = SEVERITIES.map((s) => ({ value: s, label: s }));

  protected readonly subject = signal('');
  protected readonly description = signal('');
  protected readonly channel = signal<Channel>('Phone');
  protected readonly customerId = signal<Guid | null>(null);
  protected readonly contactId = signal<Guid | null>(null);
  protected readonly newContact = signal(false);
  protected readonly ncName = signal('');
  protected readonly ncEmail = signal('');
  protected readonly ncPhone = signal('');
  protected readonly departmentId = signal<Guid | null>(null);
  protected readonly teamId = signal<Guid | null>(null);
  protected readonly assigneeId = signal<Guid | null>(null);
  protected readonly skipRouting = signal(true);
  protected readonly productId = signal<Guid | null>(null);
  protected readonly moduleId = signal<Guid | null>(null);
  protected readonly versionId = signal<Guid | null>(null);
  protected readonly installationId = signal<Guid | null>(null);
  protected readonly categoryId = signal<Guid | null>(null);
  protected readonly subcategoryId = signal<Guid | null>(null);
  protected readonly issueTypeId = signal<Guid | null>(null);
  protected readonly priority = signal<string>('Medium');
  protected readonly severity = signal<string | null>(null);
  protected readonly tags = signal<string[]>([]);
  protected readonly customValues = signal<Record<string, JsonValue>>({});
  protected readonly uploads = signal<Upload[]>([]);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly cfErrors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly created = signal<TicketDetailDto | null>(null);
  private readonly installations = signal<InstallationLookupDto[]>([]);
  private readonly customerKnownMap = signal<Map<string, CustomerLookupDto>>(new Map());
  private readonly contactKnownMap = signal<Map<string, ContactLookupDto>>(new Map());
  private clientRequestId = newClientRequestId();
  private uploadKey = 0;

  protected readonly uploading = computed(() => this.uploads().some((u) => !u.fileId && !u.error));
  protected readonly customerKnown = computed<SdOption[]>(() => [...this.customerKnownMap().values()].map((c) => this.customerOption(c)));
  protected readonly contactKnown = computed<SdOption[]>(() => [...this.contactKnownMap().values()].map((c) => this.contactOption(c)));
  protected readonly departments = computed(() => departmentOptions(this.meta.ticketForm()));
  protected readonly teams = computed(() => teamOptions(this.meta.ticketForm(), this.departmentId()));
  protected readonly agents = computed(() => agentOptions(this.meta.ticketForm(), this.departmentId()));
  protected readonly tagSuggestions = computed(() => this.meta.ticketForm()?.tags ?? []);
  protected readonly categories = computed(() =>
    (this.meta.ticketForm()?.categories ?? [])
      .filter((c) => !c.parentId && (!c.departmentId || !this.departmentId() || c.departmentId === this.departmentId()))
      .map((c) => ({ value: c.id, label: c.name })),
  );
  protected readonly subcategories = computed(() =>
    (this.meta.ticketForm()?.categories ?? []).filter((c) => c.parentId && c.parentId === this.categoryId()).map((c) => ({ value: c.id, label: c.name })),
  );
  protected readonly issueTypes = computed(() => (this.meta.ticketForm()?.issueTypes ?? []).map((i) => ({ value: i.id, label: i.name })));
  protected readonly products = computed(() => (this.meta.ticketForm()?.products ?? []).map((p) => ({ value: p.id, label: p.name, description: p.code })));
  private readonly product = computed(() => this.meta.ticketForm()?.products.find((p) => p.id === this.productId()) ?? null);
  protected readonly modules = computed(() => (this.product()?.modules ?? []).map((m) => ({ value: m.id, label: m.name })));
  protected readonly versions = computed(() => (this.product()?.versions ?? []).map((v) => ({ value: v.id, label: v.name })));
  protected readonly installationOptions = computed<SdOption[]>(() =>
    this.installations()
      .filter((i) => !this.productId() || i.productId === this.productId())
      .map((i) => ({ value: i.id, label: i.name, description: `${i.productName}${i.version ? ' ' + i.version : ''} · ${i.environment}` })),
  );
  protected readonly customFields = computed(() => applicableCustomFields(this.meta.ticketForm(), this.departmentId()));

  protected readonly customerSource: SdOptionSource = (q) =>
    this.workbench.customers(q, 15).pipe(
      map((list) => {
        this.customerKnownMap.update((m) => new Map([...m, ...list.map((c) => [c.id, c] as const)]));
        return list.map((c) => this.customerOption(c));
      }),
    );

  protected readonly contactSource: SdOptionSource = (q) =>
    this.workbench.contacts(q, this.customerId(), 15).pipe(
      map((list) => {
        this.contactKnownMap.update((m) => new Map([...m, ...list.map((c) => [c.id, c] as const)]));
        return list.map((c) => this.contactOption(c));
      }),
    );

  constructor() {
    this.meta.load();
    effect(() => {
      const customerId = this.customerId();
      untracked(() => {
        this.installations.set([]);
        if (customerId) {
          this.workbench.installations(customerId).subscribe({
            next: (list) => this.installations.set(list),
            error: (err: unknown) => toastApiError(this.toast, err, 'Could not load installations'),
          });
        }
      });
    });
    inject(DestroyRef).onDestroy(() => this.uploads().forEach((u) => u.sub?.unsubscribe()));
  }

  protected setCustomer(id: Guid | null): void {
    this.customerId.set(id);
    const contact = this.contactKnownMap().get(this.contactId() ?? '');
    if (contact && contact.customerId !== id) this.contactId.set(null);
    this.installationId.set(null);
  }

  protected setContact(id: Guid | null): void {
    this.contactId.set(id);
    const contact = id ? this.contactKnownMap().get(id) : null;
    if (contact?.customerId && contact.customerId !== this.customerId()) {
      this.customerKnownMap.update(
        (m) =>
          new Map(m).set(contact.customerId!, {
            id: contact.customerId!,
            name: contact.customerName ?? 'Customer',
            code: null,
            tier: '',
            industry: null,
            status: 'Active',
            openTickets: 0,
          }),
      );
      this.customerId.set(contact.customerId);
    }
  }

  protected toggleNewContact(on: boolean): void {
    this.newContact.set(on);
    if (on) this.contactId.set(null);
  }

  protected setDepartment(id: Guid | null): void {
    this.departmentId.set(id);
    this.teamId.set(null);
    if (this.assigneeId() && id && !this.meta.agentById().get(this.assigneeId()!)?.departmentIds.includes(id)) this.assigneeId.set(null);
  }

  protected setCategory(id: Guid | null): void {
    this.categoryId.set(id);
    this.subcategoryId.set(null);
  }

  protected setProduct(id: Guid | null): void {
    this.productId.set(id);
    this.moduleId.set(null);
    this.versionId.set(null);
    const inst = this.installations().find((i) => i.id === this.installationId());
    if (inst && inst.productId !== id) this.installationId.set(null);
  }

  protected setInstallation(id: Guid | null): void {
    this.installationId.set(id);
    const inst = this.installations().find((i) => i.id === id);
    if (inst) {
      if (this.productId() !== inst.productId) {
        this.productId.set(inst.productId);
        this.moduleId.set(null);
      }
      if (inst.versionId) this.versionId.set(inst.versionId);
    }
  }

  protected onFiles(event: Event): void {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    input.value = '';
    for (const file of files) {
      const key = ++this.uploadKey;
      const update = (changes: Partial<Upload>) => this.uploads.update((list) => list.map((u) => (u.key === key ? { ...u, ...changes } : u)));
      this.uploads.update((list) => [...list, { key, name: file.name, percent: 0, fileId: null, error: null, sub: null }]);
      const sub = this.filesApi.upload(file).subscribe({
        next: (e) => {
          if (e.kind === 'progress') update({ percent: e.percent });
          else update({ fileId: e.file.fileId, percent: 100, error: e.file.scanStatus === 'Infected' ? 'Blocked by the virus scanner' : null });
        },
        error: (err: unknown) => update({ error: ApiError.from(err).userMessage }),
      });
      update({ sub });
    }
  }

  protected removeUpload(key: number): void {
    this.uploads.update((list) => {
      list.find((u) => u.key === key)?.sub?.unsubscribe();
      return list.filter((u) => u.key !== key);
    });
  }

  protected submit(): void {
    const errors: Record<string, string> = {};
    if (!this.subject().trim()) errors['subject'] = 'Enter a subject.';
    if (this.newContact()) {
      if (!this.ncName().trim()) errors['newContact.fullName'] = 'Enter the contact’s name.';
      if (this.ncEmail() && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(this.ncEmail())) errors['newContact.email'] = 'Enter a valid e-mail address.';
      if (!this.ncEmail().trim() && !this.ncPhone().trim()) errors['newContact.email'] = 'Give an e-mail address or a phone number.';
    }
    const cfErrors = validateCustomFields(this.customFields(), this.customValues());
    this.errors.set(errors);
    this.cfErrors.set(cfErrors);
    if (Object.keys(errors).length || Object.keys(cfErrors).length) {
      this.formError.set('Please fix the highlighted fields.');
      return;
    }
    const applicable = new Set(this.customFields().map((f) => f.key));
    const customFields: Record<string, JsonValue> = {};
    for (const [k, v] of Object.entries(this.customValues())) if (applicable.has(k) && v !== null && v !== '') customFields[k] = v;

    const request: CreateTicketRequest = {
      subject: this.subject().trim(),
      descriptionHtml: this.description() || null,
      channel: this.channel(),
      customerId: this.customerId(),
      contactId: this.newContact() ? null : this.contactId(),
      newContact: this.newContact()
        ? { fullName: this.ncName().trim(), email: this.ncEmail().trim() || null, phone: this.ncPhone().trim() || null, customerId: this.customerId() }
        : null,
      departmentId: this.departmentId(),
      teamId: this.teamId(),
      assigneeId: this.assigneeId(),
      productId: this.productId(),
      moduleId: this.moduleId(),
      versionId: this.versionId(),
      installationId: this.installationId(),
      categoryId: this.categoryId(),
      subcategoryId: this.subcategoryId(),
      issueTypeId: this.issueTypeId(),
      priority: this.priority() as Priority,
      severity: (this.severity() as Severity | null) ?? null,
      tags: this.tags().length ? this.tags() : null,
      customFields: Object.keys(customFields).length ? customFields : null,
      attachmentFileIds: this.uploads()
        .filter((u) => u.fileId)
        .map((u) => u.fileId!),
      skipRouting: !!this.assigneeId() && this.skipRouting(),
      clientRequestId: this.clientRequestId,
    };
    this.saving.set(true);
    this.formError.set(null);
    this.tickets.create(request).subscribe({
      next: (t) => {
        this.saving.set(false);
        this.created.set(t);
        this.store.openTab({ id: t.id, displayNumber: t.displayNumber, subject: t.subject });
        this.store.refreshCounts();
        this.store.listReload.next();
        this.toast.success(`${t.displayNumber} created`, t.subject);
      },
      error: (err: unknown) => {
        this.saving.set(false);
        const e = ApiError.from(err);
        const fieldErrors: Record<string, string> = {};
        for (const [key, messages] of Object.entries(e.errors)) if (messages[0]) fieldErrors[key] = messages[0];
        this.errors.set(fieldErrors);
        const cf: Record<string, string> = {};
        for (const [key, msg] of Object.entries(fieldErrors)) if (key.startsWith('customFields.')) cf[key.slice('customFields.'.length)] = msg;
        this.cfErrors.set(cf);
        this.formError.set(e.userMessage);
        if (!e.is('validation_failed')) toastApiError(this.toast, e, 'The ticket was not created');
      },
    });
  }

  protected openCreated(): void {
    const t = this.created();
    if (t) void this.router.navigate(['/tickets', t.id]);
  }

  protected reset(): void {
    this.created.set(null);
    this.clientRequestId = newClientRequestId();
    this.subject.set('');
    this.description.set('');
    this.contactId.set(null);
    this.newContact.set(false);
    this.ncName.set('');
    this.ncEmail.set('');
    this.ncPhone.set('');
    this.tags.set([]);
    this.customValues.set({});
    this.uploads.set([]);
    this.errors.set({});
    this.cfErrors.set({});
    this.formError.set(null);
  }

  private customerOption(c: CustomerLookupDto): SdOption {
    return { value: c.id, label: c.name, description: [c.code, c.tier, c.openTickets ? `${c.openTickets} open` : null].filter((x) => !!x).join(' · ') };
  }

  private contactOption(c: ContactLookupDto): SdOption {
    return {
      value: c.id,
      label: c.fullName + (c.isVip ? ' ★' : ''),
      avatar: c.fullName,
      description: [c.email, c.phone, c.customerName].filter((x) => !!x).join(' · '),
    };
  }
}
