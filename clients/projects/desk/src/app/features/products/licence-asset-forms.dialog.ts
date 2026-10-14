import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ASSET_STATUSES,
  AssetDto,
  CustomersApi,
  Guid,
  InstallationListItemDto,
  InstallationsApi,
  LICENCE_STATUSES,
  LicenceDto,
  ProductDto,
  ProductsApi,
  saveErrorMessage,
  validationMessages,
} from '@sd/core';
import { SdButton, SdCombobox, SdDateInput, SdDialog, SdFormField, SdInput, SdOption, SdOptionSource } from '@sd/ui';
import { map } from 'rxjs';
import { humanize, todayIso } from '../customers/shared/customer-ui';

const FORM_STYLES = `
  .form { display: grid; gap: var(--sd-space-3); }
  .two { display: grid; gap: var(--sd-space-3); grid-template-columns: repeat(2, minmax(0, 1fr)); }
  @media (max-width: 640px) { .two { grid-template-columns: 1fr; } }
  .error { color: var(--sd-danger); }
`;

function customerSearch(api: CustomersApi): SdOptionSource {
  return (query) => api.list({ q: query, limit: 20 }).pipe(map((p) => p.items.map((x) => ({ value: x.id, label: x.name, description: x.code }))));
}

export interface LicenceFormData {
  licence?: LicenceDto | null;
}

/** Record / edit a licence for an installation. Keys are masked on save (only the last characters are kept). */
@Component({
  selector: 'desk-licence-form-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdCombobox, SdDateInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="l ? 'Edit licence' : 'New licence'" description="Only a masked key is stored; full keys stay in the licensing system.">
      <form id="licence-form" class="form" (ngSubmit)="save()" novalidate>
        <sd-form-field label="Customer" required>
          <sd-combobox [search]="search" [options]="customerOptions()" [value]="customerId()" (valueChange)="setCustomer($event)" placeholder="Choose a customer" />
        </sd-form-field>
        <sd-form-field label="Installation" required [error]="errors()['installationId']">
          <select sdInput name="inst" [(ngModel)]="installationId">
            <option [ngValue]="null">Choose…</option>
            @for (i of installations(); track i.id) {
              <option [ngValue]="i.id">{{ i.name }} ({{ i.productName }})</option>
            }
          </select>
        </sd-form-field>
        <sd-form-field label="Licence key" required [error]="errors()['licenceKey']" hint="Paste the key — it is masked before saving.">
          <input sdInput name="key" [(ngModel)]="key" required autocomplete="off" />
        </sd-form-field>
        <div class="two">
          <sd-form-field label="Edition" optional><input sdInput name="edition" [(ngModel)]="edition" maxlength="100" /></sd-form-field>
          <sd-form-field label="Seats" [error]="errors()['seats']"><input sdInput type="number" min="0" name="seats" [(ngModel)]="seats" /></sd-form-field>
        </div>
        <div class="two">
          <sd-form-field label="Issued on" required><sd-date-input [(value)]="issuedOn" /></sd-form-field>
          <sd-form-field label="Expires on" optional [error]="errors()['expiresOn']"><sd-date-input [(value)]="expiresOn" /></sd-form-field>
        </div>
        <sd-form-field label="Status">
          <select sdInput name="status" [(ngModel)]="status">
            @for (s of statuses; track s) {
              <option [value]="s">{{ s }}</option>
            }
          </select>
        </sd-form-field>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="licence-form" [loading]="saving()">Save</button>
      </div>
    </sd-dialog>
  `,
  styles: FORM_STYLES,
})
export class LicenceFormDialog {
  protected readonly data = inject<LicenceFormData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<LicenceDto>>(DialogRef);
  private readonly api = inject(InstallationsApi);
  private readonly customers = inject(CustomersApi);
  protected readonly statuses = LICENCE_STATUSES;
  protected readonly l = this.data.licence ?? null;
  protected readonly customerId = signal<string | null>(this.l?.customerId ?? null);
  protected installationId: Guid | null = this.l?.installationId ?? null;
  protected key = this.l?.keyMasked ?? '';
  protected edition = this.l?.edition ?? '';
  protected seats = this.l?.seats ?? 10;
  protected readonly issuedOn = signal<string | null>(this.l?.issuedOn ?? todayIso());
  protected readonly expiresOn = signal<string | null>(this.l?.expiresOn ?? null);
  protected status = this.l?.status ?? 'Active';
  protected readonly installations = signal<InstallationListItemDto[]>([]);
  protected readonly search = customerSearch(this.customers);
  protected readonly customerOptions = computed<SdOption[]>(() => (this.l ? [{ value: this.l.customerId, label: this.l.customerName }] : []));
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  constructor() {
    if (this.customerId()) this.loadInstallations(this.customerId()!);
  }

  protected setCustomer(value: string | string[] | null): void {
    const id = Array.isArray(value) ? (value[0] ?? null) : value;
    this.customerId.set(id);
    this.installationId = null;
    this.installations.set([]);
    if (id) this.loadInstallations(id);
  }

  private loadInstallations(id: Guid): void {
    this.api.list({ customerId: id, limit: 200 }).subscribe((p) => this.installations.set(p.items));
  }

  protected save(): void {
    if (!this.installationId) {
      this.errors.set({ installationId: 'Choose an installation.' });
      return;
    }
    this.saving.set(true);
    this.api
      .saveLicence(this.l?.id ?? null, {
        installationId: this.installationId,
        licenceKey: this.key.trim(),
        edition: this.edition || null,
        seats: Number(this.seats) || 0,
        issuedOn: this.issuedOn() ?? todayIso(),
        expiresOn: this.expiresOn(),
        status: this.status,
      })
      .subscribe({
        next: (saved) => this.ref.close(saved),
        error: (err: unknown) => {
          this.saving.set(false);
          this.errors.set(validationMessages(err));
          this.formError.set(saveErrorMessage(err));
        },
      });
  }
}

export interface AssetFormData {
  asset?: AssetDto | null;
}

/** Register / edit a customer asset (hardware or other serialised item) with its warranty. */
@Component({
  selector: 'desk-asset-form-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdCombobox, SdDateInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="a ? 'Edit asset' : 'New asset'">
      <form id="asset-form" class="form" (ngSubmit)="save()" novalidate>
        <sd-form-field label="Customer" required [error]="errors()['customerId']">
          <sd-combobox [search]="search" [options]="customerOptions()" [value]="customerId()" (valueChange)="setCustomer($event)" placeholder="Choose a customer" />
        </sd-form-field>
        <sd-form-field label="Name" required [error]="errors()['name']">
          <input sdInput name="name" [(ngModel)]="name" required maxlength="250" placeholder="POS terminal – Counter 3" />
        </sd-form-field>
        <div class="two">
          <sd-form-field label="Serial number" optional [error]="errors()['serialNumber']"><input sdInput name="serial" [(ngModel)]="serial" maxlength="120" /></sd-form-field>
          <sd-form-field label="Asset tag" optional><input sdInput name="tag" [(ngModel)]="tag" maxlength="120" /></sd-form-field>
        </div>
        <div class="two">
          <sd-form-field label="Product" optional>
            <select sdInput name="product" [(ngModel)]="productId">
              <option [ngValue]="null">—</option>
              @for (p of products(); track p.id) {
                <option [ngValue]="p.id">{{ p.name }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Installation" optional [error]="errors()['installationId']">
            <select sdInput name="inst" [(ngModel)]="installationId">
              <option [ngValue]="null">—</option>
              @for (i of installations(); track i.id) {
                <option [ngValue]="i.id">{{ i.name }}</option>
              }
            </select>
          </sd-form-field>
        </div>
        <div class="two">
          <sd-form-field label="Purchased" optional><sd-date-input [(value)]="purchased" /></sd-form-field>
          <sd-form-field label="Warranty ends" optional [error]="errors()['warrantyEndsOn']"><sd-date-input [(value)]="warranty" /></sd-form-field>
        </div>
        <sd-form-field label="Status">
          <select sdInput name="status" [(ngModel)]="status">
            @for (s of statuses; track s) {
              <option [value]="s">{{ humanize(s) }}</option>
            }
          </select>
        </sd-form-field>
        <sd-form-field label="Notes" optional><textarea sdInput name="notes" rows="2" [(ngModel)]="notes"></textarea></sd-form-field>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="asset-form" [loading]="saving()">Save</button>
      </div>
    </sd-dialog>
  `,
  styles: FORM_STYLES,
})
export class AssetFormDialog {
  protected readonly data = inject<AssetFormData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<AssetDto>>(DialogRef);
  private readonly api = inject(InstallationsApi);
  private readonly customers = inject(CustomersApi);
  private readonly productsApi = inject(ProductsApi);
  protected readonly humanize = humanize;
  protected readonly statuses = ASSET_STATUSES;
  protected readonly a = this.data.asset ?? null;
  protected readonly customerId = signal<string | null>(this.a?.customerId ?? null);
  protected name = this.a?.name ?? '';
  protected serial = this.a?.serialNumber ?? '';
  protected tag = this.a?.assetTag ?? '';
  protected productId: Guid | null = this.a?.productId ?? null;
  protected installationId: Guid | null = this.a?.installationId ?? null;
  protected readonly purchased = signal<string | null>(this.a?.purchaseDate ?? null);
  protected readonly warranty = signal<string | null>(this.a?.warrantyEndsOn ?? null);
  protected status = this.a?.status ?? 'InService';
  protected notes = this.a?.notes ?? '';
  protected readonly products = signal<ProductDto[]>([]);
  protected readonly installations = signal<InstallationListItemDto[]>([]);
  protected readonly search = customerSearch(this.customers);
  protected readonly customerOptions = computed<SdOption[]>(() => (this.a ? [{ value: this.a.customerId, label: this.a.customerName }] : []));
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  constructor() {
    this.productsApi.list(false).subscribe((p) => this.products.set(p));
    if (this.customerId()) this.api.list({ customerId: this.customerId(), limit: 200 }).subscribe((p) => this.installations.set(p.items));
  }

  protected setCustomer(value: string | string[] | null): void {
    const id = Array.isArray(value) ? (value[0] ?? null) : value;
    this.customerId.set(id);
    this.installationId = null;
    this.installations.set([]);
    if (id) this.api.list({ customerId: id, limit: 200 }).subscribe((p) => this.installations.set(p.items));
  }

  protected save(): void {
    const customerId = this.customerId();
    if (!customerId) {
      this.errors.set({ customerId: 'Choose a customer.' });
      return;
    }
    this.saving.set(true);
    this.api
      .saveAsset(this.a?.id ?? null, {
        customerId,
        name: this.name.trim(),
        serialNumber: this.serial || null,
        assetTag: this.tag || null,
        productId: this.productId,
        installationId: this.installationId,
        purchaseDate: this.purchased(),
        warrantyEndsOn: this.warranty(),
        status: this.status,
        notes: this.notes || null,
      })
      .subscribe({
        next: (saved) => this.ref.close(saved),
        error: (err: unknown) => {
          this.saving.set(false);
          this.errors.set(validationMessages(err));
          this.formError.set(saveErrorMessage(err));
        },
      });
  }
}
