import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ApiError,
  ContactDetailDto,
  ContactsApi,
  CustomerDepartmentDto,
  CustomersApi,
  Guid,
  JsonValue,
  LocationDto,
  MetaApi,
  PORTAL_ACCESS_SCOPES,
  PortalAccessScope,
  SaveContactRequest,
  saveErrorMessage,
  validationMessages,
} from '@sd/core';
import { SdButton, SdCheckbox, SdCombobox, SdDialog, SdFormField, SdInput, SdOption, SdOptionSource, SdLabelPipe } from '@sd/ui';
import { map } from 'rxjs';
import { CustomFieldsEditorComponent } from './shared/custom-fields-editor.component';

export interface ContactFormData {
  contact?: ContactDetailDto | null;
  customerId?: Guid | null;
  customerName?: string | null;
}

/** Create / edit a contact (identity fields, customer/location/department, preferences, portal access, custom fields). */
@Component({
  selector: 'desk-contact-form-dialog',
  imports: [SdLabelPipe, FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdCombobox, SdCheckbox, CustomFieldsEditorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="c ? 'Edit ' + c.fullName : 'New contact'">
      <form id="contact-form" class="form" (ngSubmit)="save()" novalidate>
        <div class="three">
          <sd-form-field label="First name" optional>
            <input sdInput name="first" [(ngModel)]="firstName" maxlength="120" autocomplete="given-name" />
          </sd-form-field>
          <sd-form-field label="Last name" optional>
            <input sdInput name="last" [(ngModel)]="lastName" maxlength="120" autocomplete="family-name" />
          </sd-form-field>
          <sd-form-field label="Display name" [error]="errors()['fullName']" hint="Defaults to first + last name.">
            <input sdInput name="full" [(ngModel)]="fullName" maxlength="250" />
          </sd-form-field>
        </div>
        <div class="two">
          <sd-form-field label="E-mail" optional [error]="errors()['email']">
            <input sdInput type="email" name="email" [(ngModel)]="email" maxlength="300" autocomplete="email" />
          </sd-form-field>
          <sd-form-field label="Job title" optional>
            <input sdInput name="title" [(ngModel)]="jobTitle" maxlength="200" />
          </sd-form-field>
        </div>
        <div class="three">
          <sd-form-field label="Phone" optional [error]="errors()['phone']">
            <input sdInput type="tel" name="phone" [(ngModel)]="phone" maxlength="50" />
          </sd-form-field>
          <sd-form-field label="Mobile" optional>
            <input sdInput type="tel" name="mobile" [(ngModel)]="mobile" maxlength="50" />
          </sd-form-field>
          <sd-form-field label="WhatsApp" optional>
            <input sdInput type="tel" name="wa" [(ngModel)]="whatsApp" maxlength="50" />
          </sd-form-field>
        </div>
        <sd-form-field label="Customer" [optional]="true" [error]="errors()['customerId']" hint="Left empty, the customer is matched by the e-mail domain.">
          <sd-combobox [search]="searchCustomers" [options]="customerOptions()" [value]="customerId()" (valueChange)="setCustomer($event)" placeholder="No customer" />
        </sd-form-field>
        <div class="two">
          <sd-form-field label="Location" optional [error]="errors()['locationId']">
            <select sdInput name="loc" [(ngModel)]="locationId" [disabled]="!customerId()">
              <option [ngValue]="null">—</option>
              @for (l of locations(); track l.id) {
                <option [ngValue]="l.id">{{ l.name }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Department" optional [error]="errors()['customerDepartmentId']">
            <select sdInput name="dep" [(ngModel)]="departmentId" [disabled]="!customerId()">
              <option [ngValue]="null">—</option>
              @for (d of departments(); track d.id) {
                <option [ngValue]="d.id">{{ d.name }}</option>
              }
            </select>
          </sd-form-field>
        </div>
        <div class="three">
          <sd-form-field label="Preferred channel" optional>
            <select sdInput name="channel" [(ngModel)]="preferredChannel">
              <option [ngValue]="null">No preference</option>
              @for (ch of channels; track ch) {
                <option [ngValue]="ch">{{ ch | sdLabel }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Language">
            <input sdInput name="lang" [(ngModel)]="language" maxlength="20" />
          </sd-form-field>
          <sd-form-field label="Portal access">
            <select sdInput name="portal" [(ngModel)]="portalAccess">
              @for (p of portalScopes; track p) {
                <option [value]="p">{{ p === 'Own' ? 'Own tickets' : p === 'Location' ? 'Location tickets' : p === 'Organisation' ? 'Whole organisation' : 'No access' }}</option>
              }
            </select>
          </sd-form-field>
        </div>
        <div class="checks">
          <sd-checkbox [(value)]="vip">VIP</sd-checkbox>
          <sd-checkbox [(value)]="primary">Primary contact</sd-checkbox>
          @if (c) {
            <sd-checkbox [(value)]="inactive">Inactive</sd-checkbox>
          }
        </div>
        @if (contactFields().length) {
          <desk-custom-fields-editor [fields]="contactFields()" [(values)]="customFields" [errors]="errors()" />
        }
        @if (formError()) {
          <p class="error" role="alert">
            {{ formError() }}
            @if (duplicateHint()) {
              <span> Use the duplicate finder to merge the two contacts.</span>
            }
          </p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="contact-form" [loading]="saving()">{{ c ? 'Save changes' : 'Create contact' }}</button>
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
    .checks {
      display: flex;
      flex-wrap: wrap;
      gap: var(--sd-space-5);
    }
    .error {
      color: var(--sd-danger);
    }
  `,
})
export class ContactFormDialog {
  protected readonly data = inject<ContactFormData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<ContactDetailDto>>(DialogRef);
  private readonly api = inject(ContactsApi);
  private readonly customers = inject(CustomersApi);
  private readonly meta = inject(MetaApi);

  protected readonly c = this.data.contact ?? null;
  protected readonly channels = ['Email', 'Phone', 'WhatsApp', 'Sms', 'Portal'];
  protected readonly portalScopes = PORTAL_ACCESS_SCOPES;

  protected firstName = this.c?.firstName ?? '';
  protected lastName = this.c?.lastName ?? '';
  protected fullName = this.c?.fullName ?? '';
  protected email = this.c?.email ?? '';
  protected jobTitle = this.c?.jobTitle ?? '';
  protected phone = this.c?.phone ?? '';
  protected mobile = this.c?.mobile ?? '';
  protected whatsApp = this.c?.whatsAppNumber ?? '';
  protected readonly customerId = signal<string | null>(this.c?.customerId ?? this.data.customerId ?? null);
  private readonly customerName = this.c?.customerName ?? this.data.customerName ?? null;
  protected locationId: Guid | null = this.c?.locationId ?? null;
  protected departmentId: Guid | null = this.c?.customerDepartmentId ?? null;
  protected preferredChannel: string | null = this.c?.preferredChannel ?? null;
  protected language = this.c?.preferredLanguage ?? 'en';
  protected portalAccess: PortalAccessScope = this.c?.portalAccess ?? 'Own';
  protected readonly vip = signal(this.c?.isVip ?? false);
  protected readonly primary = signal(this.c?.isPrimary ?? false);
  protected readonly inactive = signal(this.c?.status === 'Inactive');
  protected readonly customFields = signal<Record<string, JsonValue>>({ ...(this.c?.customFields ?? {}) });

  protected readonly locations = signal<LocationDto[]>([]);
  protected readonly departments = signal<CustomerDepartmentDto[]>([]);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly duplicateHint = signal(false);

  protected readonly contactFields = computed(() => (this.meta.ticketForm()?.customFields ?? []).filter((f) => f.entity === 'Contact'));
  protected readonly customerOptions = computed<SdOption[]>(() =>
    this.customerId() && this.customerName ? [{ value: this.customerId()!, label: this.customerName }] : [],
  );
  protected readonly searchCustomers: SdOptionSource = (query) =>
    this.customers.list({ q: query, limit: 20 }).pipe(map((p) => p.items.map((x) => ({ value: x.id, label: x.name, description: x.code }))));

  constructor() {
    this.meta.ticketForm$().subscribe();
    if (this.customerId()) this.loadCustomer(this.customerId()!);
  }

  protected setCustomer(value: string | string[] | null): void {
    const id = Array.isArray(value) ? (value[0] ?? null) : value;
    this.customerId.set(id);
    this.locationId = null;
    this.departmentId = null;
    this.locations.set([]);
    this.departments.set([]);
    if (id) this.loadCustomer(id);
  }

  private loadCustomer(id: Guid): void {
    this.customers.locations(id).subscribe((l) => this.locations.set(l));
    this.customers.departments(id).subscribe((d) => this.departments.set(d));
  }

  protected save(): void {
    const request: SaveContactRequest = {
      firstName: this.firstName || null,
      lastName: this.lastName || null,
      fullName: this.fullName || null,
      email: this.email || null,
      jobTitle: this.jobTitle || null,
      phone: this.phone || null,
      mobile: this.mobile || null,
      whatsAppNumber: this.whatsApp || null,
      customerId: this.customerId(),
      locationId: this.locationId,
      customerDepartmentId: this.departmentId,
      preferredChannel: this.preferredChannel,
      preferredLanguage: this.language || 'en',
      portalAccess: this.portalAccess,
      isVip: this.vip(),
      isPrimary: this.primary(),
      status: this.c ? (this.inactive() ? 'Inactive' : 'Active') : null,
      customFields: this.customFields(),
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
        this.duplicateHint.set(ApiError.from(err).code === 'duplicate_contact');
      },
    });
  }
}
