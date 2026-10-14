import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  CommunicationPreferencesDto,
  CustomerDetailDto,
  CustomerGroupDto,
  CustomersApi,
  Guid,
  JsonValue,
  MeStore,
  MetaApi,
  SaveCustomerRequest,
  saveErrorMessage,
  validationMessages,
} from '@sd/core';
import { SdButton, SdChipsInput, SdCombobox, SdDialog, SdFormField, SdInput, SdOption, SdOptionSource, SdToggle } from '@sd/ui';
import { map } from 'rxjs';
import { CustomFieldsEditorComponent } from './shared/custom-fields-editor.component';

export interface CustomerFormData {
  customer?: CustomerDetailDto | null;
  parent?: { id: Guid; name: string } | null;
}

const TIERS = ['Platinum', 'Gold', 'Silver', 'Bronze', 'Standard'];
const CHANNELS = ['Email', 'Phone', 'WhatsApp', 'Sms', 'Portal'];
const LANGUAGES = [
  { value: 'en', label: 'English' },
  { value: 'hi', label: 'Hindi' },
  { value: 'ta', label: 'Tamil' },
  { value: 'ml', label: 'Malayalam' },
  { value: 'kn', label: 'Kannada' },
  { value: 'te', label: 'Telugu' },
  { value: 'mr', label: 'Marathi' },
  { value: 'gu', label: 'Gujarati' },
  { value: 'bn', label: 'Bengali' },
];

/** Create / edit a customer organisation (hierarchy, segmentation, account manager, domains, preferences, custom fields). */
@Component({
  selector: 'desk-customer-form-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdCombobox, SdChipsInput, SdToggle, CustomFieldsEditorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="isEdit ? 'Edit ' + (data.customer?.name ?? 'customer') : 'New customer'" description="Organisation details used for routing, SLA selection and contact auto-linking.">
      <form id="customer-form" class="form" (ngSubmit)="save()" novalidate>
        <div class="two">
          <sd-form-field label="Name" required [error]="errors()['name']">
            <input sdInput name="name" [(ngModel)]="name" required maxlength="300" autocomplete="organization" />
          </sd-form-field>
          <sd-form-field label="Customer code" optional [error]="errors()['code']" hint="Unique, e.g. the ERP customer code.">
            <input sdInput name="code" [(ngModel)]="code" maxlength="40" />
          </sd-form-field>
        </div>
        <div class="three">
          <sd-form-field label="Tier" hint="Drives SLA policy selection.">
            <select sdInput name="tier" [(ngModel)]="tier">
              @for (t of tiers; track t) {
                <option [value]="t">{{ t }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Status">
            <select sdInput name="status" [(ngModel)]="status">
              @for (s of statuses; track s) {
                <option [value]="s">{{ s }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Group" optional [error]="errors()['customerGroupId']">
            <select sdInput name="group" [(ngModel)]="groupId">
              <option [ngValue]="null">—</option>
              @for (g of groups(); track g.id) {
                <option [ngValue]="g.id">{{ g.name }}</option>
              }
            </select>
          </sd-form-field>
        </div>
        <div class="three">
          <sd-form-field label="Industry" optional>
            <input sdInput name="industry" [(ngModel)]="industry" maxlength="100" />
          </sd-form-field>
          <sd-form-field label="Category" optional>
            <input sdInput name="category" [(ngModel)]="category" maxlength="100" />
          </sd-form-field>
          <sd-form-field label="Service region" optional>
            <input sdInput name="region" [(ngModel)]="region" maxlength="100" />
          </sd-form-field>
        </div>
        <div class="two">
          <sd-form-field label="Parent organisation" optional [error]="errors()['parentCustomerId']">
            <sd-combobox [search]="searchCustomers" [options]="parentOptions()" [(value)]="parentId" placeholder="None (top level)" />
          </sd-form-field>
          <sd-form-field label="Account manager" optional [error]="errors()['accountManagerUserId']">
            <sd-combobox [options]="agentOptions()" [(value)]="accountManagerId" placeholder="Unassigned" />
          </sd-form-field>
        </div>
        <div class="three">
          <sd-form-field label="E-mail" optional [error]="errors()['email']">
            <input sdInput type="email" name="email" [(ngModel)]="email" maxlength="300" />
          </sd-form-field>
          <sd-form-field label="Phone" optional>
            <input sdInput type="tel" name="phone" [(ngModel)]="phone" maxlength="50" />
          </sd-form-field>
          <sd-form-field label="Website" optional [error]="errors()['website']">
            <input sdInput name="website" [(ngModel)]="website" maxlength="300" />
          </sd-form-field>
        </div>
        <sd-form-field label="E-mail domains" hint="New contacts from these domains are linked to this customer automatically." [error]="errors()['domains']">
          <sd-chips-input [(value)]="domains" placeholder="example.com" [lowercase]="true" ariaLabel="E-mail domains" />
        </sd-form-field>
        <div class="two">
          <sd-form-field label="Preferred language">
            <select sdInput name="lang" [(ngModel)]="language">
              @for (l of languages; track l.value) {
                <option [value]="l.value">{{ l.label }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Time zone" optional>
            <input sdInput name="tz" [(ngModel)]="timeZone" placeholder="Asia/Kolkata" maxlength="100" />
          </sd-form-field>
        </div>
        <fieldset class="prefs">
          <legend>Communication preferences</legend>
          <div class="toggles">
            <sd-toggle [(value)]="prefEmail">E-mail</sd-toggle>
            <sd-toggle [(value)]="prefPhone">Phone</sd-toggle>
            <sd-toggle [(value)]="prefSms">SMS</sd-toggle>
            <sd-toggle [(value)]="prefWhatsApp">WhatsApp</sd-toggle>
          </div>
          <sd-form-field label="Preferred channel" optional>
            <select sdInput name="prefChannel" [(ngModel)]="preferredChannel">
              <option [ngValue]="null">No preference</option>
              @for (c of channels; track c) {
                <option [ngValue]="c">{{ c }}</option>
              }
            </select>
          </sd-form-field>
        </fieldset>
        @if (customerFields().length) {
          <fieldset class="prefs">
            <legend>Custom fields</legend>
            <desk-custom-fields-editor [fields]="customerFields()" [(values)]="customFields" [errors]="errors()" />
          </fieldset>
        }
        <sd-form-field label="Notes" optional>
          <textarea sdInput name="notes" rows="3" [(ngModel)]="notes" maxlength="8000"></textarea>
        </sd-form-field>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="customer-form" [loading]="saving()">{{ isEdit ? 'Save changes' : 'Create customer' }}</button>
      </div>
    </sd-dialog>
  `,
  styles: `
    .form {
      display: grid;
      gap: var(--sd-space-3);
    }
    .two,
    .three {
      display: grid;
      gap: var(--sd-space-3);
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }
    .three {
      grid-template-columns: repeat(3, minmax(0, 1fr));
    }
    @media (max-width: 640px) {
      .two,
      .three {
        grid-template-columns: 1fr;
      }
    }
    .prefs {
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      padding: var(--sd-space-3);
      display: grid;
      gap: var(--sd-space-3);
    }
    legend {
      padding: 0 6px;
      font-weight: var(--sd-weight-medium);
      color: var(--sd-text-2);
    }
    .toggles {
      display: flex;
      flex-wrap: wrap;
      gap: var(--sd-space-4);
    }
    .error {
      color: var(--sd-danger);
    }
  `,
})
export class CustomerFormDialog {
  protected readonly data = inject<CustomerFormData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<CustomerDetailDto>>(DialogRef);
  private readonly api = inject(CustomersApi);
  private readonly meta = inject(MetaApi);
  private readonly me = inject(MeStore);

  protected readonly isEdit = !!this.data.customer;
  protected readonly tiers = TIERS;
  protected readonly statuses = ['Active', 'Inactive', 'Prospect', 'OnHold'];
  protected readonly channels = CHANNELS;
  protected readonly languages = LANGUAGES;

  private readonly c = this.data.customer;
  protected name = this.c?.name ?? '';
  protected code = this.c?.code ?? '';
  protected tier = this.c?.tier ?? 'Standard';
  protected status = this.c?.status ?? 'Active';
  protected groupId: Guid | null = this.c?.customerGroupId ?? null;
  protected industry = this.c?.industry ?? '';
  protected category = this.c?.category ?? '';
  protected region = this.c?.serviceRegion ?? '';
  protected readonly parentId = signal<string | null>(this.c?.parentCustomerId ?? this.data.parent?.id ?? null);
  protected readonly accountManagerId = signal<string | null>(this.c?.accountManagerUserId ?? (this.c ? null : this.me.user()?.id ?? null));
  protected email = this.c?.email ?? '';
  protected phone = this.c?.phone ?? '';
  protected website = this.c?.website ?? '';
  protected readonly domains = signal<string[]>(this.c?.domains ?? []);
  protected language = this.c?.preferredLanguage ?? 'en';
  protected timeZone = this.c?.timeZone ?? 'Asia/Kolkata';
  protected readonly prefEmail = signal(this.c?.communicationPreferences.email ?? true);
  protected readonly prefPhone = signal(this.c?.communicationPreferences.phone ?? true);
  protected readonly prefSms = signal(this.c?.communicationPreferences.sms ?? false);
  protected readonly prefWhatsApp = signal(this.c?.communicationPreferences.whatsApp ?? false);
  protected preferredChannel: string | null = this.c?.communicationPreferences.preferredChannel ?? null;
  protected readonly customFields = signal<Record<string, JsonValue>>({ ...(this.c?.customFields ?? {}) });
  protected notes = this.c?.notes ?? '';

  protected readonly groups = signal<CustomerGroupDto[]>([]);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  protected readonly agentOptions = computed<SdOption[]>(() =>
    (this.meta.ticketForm()?.agents ?? []).filter((a) => !a.isLightAgent).map((a) => ({ value: a.userId, label: a.name, avatar: a.name, description: a.email })),
  );
  protected readonly customerFields = computed(() => (this.meta.ticketForm()?.customFields ?? []).filter((f) => f.entity === 'Customer'));
  protected readonly parentOptions = computed<SdOption[]>(() => {
    const parent = this.c?.parentCustomerId ? { id: this.c.parentCustomerId, name: this.c.parentName ?? '' } : this.data.parent;
    return parent ? [{ value: parent.id, label: parent.name }] : [];
  });

  protected readonly searchCustomers: SdOptionSource = (query) =>
    this.api.list({ q: query, limit: 20 }).pipe(
      map((page) => page.items.filter((x) => x.id !== this.c?.id).map((x) => ({ value: x.id, label: x.name, description: [x.code, x.tier].filter(Boolean).join(' · ') }))),
    );

  constructor() {
    this.meta.ticketForm$().subscribe();
    this.api.groups().subscribe((g) => this.groups.set(g));
  }

  protected save(): void {
    if (this.saving()) return;
    const prefs: CommunicationPreferencesDto = {
      email: this.prefEmail(),
      phone: this.prefPhone(),
      sms: this.prefSms(),
      whatsApp: this.prefWhatsApp(),
      preferredChannel: this.preferredChannel,
      notes: null,
    };
    const request: SaveCustomerRequest = {
      name: this.name.trim(),
      code: this.code.trim() || null,
      tier: this.tier,
      status: this.status as SaveCustomerRequest['status'],
      customerGroupId: this.groupId,
      industry: this.industry || null,
      category: this.category || null,
      serviceRegion: this.region || null,
      parentCustomerId: this.parentId(),
      accountManagerUserId: this.accountManagerId(),
      email: this.email || null,
      phone: this.phone || null,
      website: this.website || null,
      domains: this.domains(),
      preferredLanguage: this.language,
      timeZone: this.timeZone || null,
      communicationPreferences: prefs,
      customFields: this.customFields(),
      notes: this.notes || null,
      rowVersion: this.c?.rowVersion ?? null,
    };
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    const call = this.c ? this.api.update(this.c.id, request) : this.api.create(request);
    call.subscribe({
      next: (saved) => this.ref.close(saved),
      error: (err: unknown) => {
        this.saving.set(false);
        this.errors.set(validationMessages(err));
        this.formError.set(saveErrorMessage(err));
      },
    });
  }
}
