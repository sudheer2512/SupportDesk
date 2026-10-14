import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  CustomersApi,
  DEPLOYMENT_MODES,
  DeploymentMode,
  Guid,
  INSTALLATION_ENVIRONMENTS,
  INSTALLATION_STATUSES,
  InstallationDetailDto,
  InstallationEnvironment,
  InstallationIntegrationDto,
  InstallationStatus,
  InstallationsApi,
  LocationDto,
  ProductDto,
  ProductsApi,
  saveErrorMessage,
  validationMessages,
} from '@sd/core';
import { SdButton, SdCombobox, SdDateInput, SdDialog, SdFormField, SdInput, SdOption, SdOptionSource } from '@sd/ui';
import { map } from 'rxjs';
import { humanize } from '../customers/shared/customer-ui';

export interface InstallationFormData {
  installation?: InstallationDetailDto | null;
  customerId?: Guid | null;
  customerName?: string | null;
}

/** Create / edit an installation with its environment details (OS, database, server, integrations, enabled modules). */
@Component({
  selector: 'desk-installation-form-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdCombobox, SdDateInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="i ? 'Edit ' + i.summary.name : 'New installation'">
      <form id="installation-form" class="form" (ngSubmit)="save()" novalidate>
        <sd-form-field label="Customer" required [error]="errors()['customerId']">
          <sd-combobox [search]="searchCustomers" [options]="customerOptions()" [value]="customerId()" (valueChange)="setCustomer($event)" placeholder="Choose a customer" />
        </sd-form-field>
        <div class="two">
          <sd-form-field label="Name" required [error]="errors()['name']">
            <input sdInput name="name" [(ngModel)]="name" required maxlength="250" placeholder="ABC ERP – Production" />
          </sd-form-field>
          <sd-form-field label="Installation code" optional [error]="errors()['installationCode']">
            <input sdInput name="code" [(ngModel)]="code" maxlength="60" />
          </sd-form-field>
        </div>
        <div class="three">
          <sd-form-field label="Product" required [error]="errors()['productId']">
            <select sdInput name="product" [ngModel]="productId()" (ngModelChange)="setProduct($event)">
              <option [ngValue]="null">Choose…</option>
              @for (p of products(); track p.id) {
                <option [ngValue]="p.id">{{ p.name }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Version" optional [error]="errors()['versionId']">
            <select sdInput name="version" [ngModel]="versionId()" (ngModelChange)="versionId.set($event); releaseId = null">
              <option [ngValue]="null">—</option>
              @for (v of product()?.versions ?? []; track v.id) {
                <option [ngValue]="v.id">{{ v.version }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Release" optional [error]="errors()['releaseId']">
            <select sdInput name="release" [(ngModel)]="releaseId">
              <option [ngValue]="null">—</option>
              @for (r of releases(); track r.id) {
                <option [ngValue]="r.id">{{ r.name }}</option>
              }
            </select>
          </sd-form-field>
        </div>
        <div class="three">
          <sd-form-field label="Environment">
            <select sdInput name="env" [(ngModel)]="environment">
              @for (e of environments; track e) {
                <option [value]="e">{{ e }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Deployment">
            <select sdInput name="mode" [(ngModel)]="mode">
              @for (m of modes; track m) {
                <option [value]="m">{{ humanize(m) }}</option>
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
        </div>
        <div class="three">
          <sd-form-field label="Location" optional [error]="errors()['locationId']">
            <select sdInput name="loc" [(ngModel)]="locationId">
              <option [ngValue]="null">—</option>
              @for (l of locations(); track l.id) {
                <option [ngValue]="l.id">{{ l.name }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Users" optional [error]="errors()['userCount']">
            <input sdInput type="number" min="0" name="users" [(ngModel)]="userCount" />
          </sd-form-field>
          <sd-form-field label="Go-live" optional [error]="errors()['goLiveOn']">
            <sd-date-input [(value)]="goLive" />
          </sd-form-field>
        </div>
        <div class="two">
          <sd-form-field label="Operating system" optional>
            <input sdInput name="os" [(ngModel)]="os" maxlength="200" placeholder="Windows Server 2022" />
          </sd-form-field>
          <sd-form-field label="Database" optional>
            <input sdInput name="db" [(ngModel)]="dbVersion" maxlength="200" placeholder="SQL Server 2022 Standard" />
          </sd-form-field>
        </div>
        <sd-form-field label="Server configuration" optional>
          <textarea sdInput name="server" rows="2" [(ngModel)]="server" maxlength="4000"></textarea>
        </sd-form-field>
        <sd-form-field label="Enabled modules" optional [error]="errors()['enabledModuleIds']">
          <sd-combobox multiple [options]="moduleOptions()" [(value)]="moduleIds" placeholder="None" />
        </sd-form-field>
        <fieldset class="ints">
          <legend>Integrations</legend>
          @for (it of integrations(); track $index; let idx = $index) {
            <div class="int-row">
              <input sdInput size="sm" [attr.aria-label]="'Integration ' + (idx + 1) + ' system'" placeholder="System (e.g. GST portal)" [ngModel]="it.system" (ngModelChange)="updateIntegration(idx, { system: $event })" [name]="'is' + idx" />
              <select sdInput size="sm" [attr.aria-label]="'Integration ' + (idx + 1) + ' direction'" [ngModel]="it.direction" (ngModelChange)="updateIntegration(idx, { direction: $event })" [name]="'id' + idx">
                <option [ngValue]="null">Direction</option>
                <option value="inbound">Inbound</option>
                <option value="outbound">Outbound</option>
                <option value="bidirectional">Two-way</option>
              </select>
              <button sdButton size="sm" type="button" variant="ghost" icon="x" iconOnly aria-label="Remove integration" (click)="removeIntegration(idx)"></button>
            </div>
          }
          <button sdButton size="sm" type="button" icon="plus" (click)="addIntegration()">Add integration</button>
        </fieldset>
        <sd-form-field label="Notes" optional>
          <textarea sdInput name="notes" rows="2" [(ngModel)]="notes" maxlength="8000"></textarea>
        </sd-form-field>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="installation-form" [loading]="saving()">Save</button>
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
    .ints {
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      padding: var(--sd-space-3);
      display: grid;
      gap: var(--sd-space-2);
    }
    legend {
      padding: 0 6px;
      color: var(--sd-text-2);
    }
    .int-row {
      display: grid;
      grid-template-columns: 1fr 150px auto;
      gap: 6px;
    }
    .error {
      color: var(--sd-danger);
    }
  `,
})
export class InstallationFormDialog {
  protected readonly data = inject<InstallationFormData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<InstallationDetailDto>>(DialogRef);
  private readonly api = inject(InstallationsApi);
  private readonly productsApi = inject(ProductsApi);
  private readonly customers = inject(CustomersApi);
  protected readonly humanize = humanize;
  protected readonly environments = INSTALLATION_ENVIRONMENTS;
  protected readonly modes = DEPLOYMENT_MODES;
  protected readonly statuses = INSTALLATION_STATUSES;

  protected readonly i = this.data.installation ?? null;
  private readonly s = this.i?.summary;
  protected readonly customerId = signal<string | null>(this.s?.customerId ?? this.data.customerId ?? null);
  private readonly customerName = this.s?.customerName ?? this.data.customerName ?? null;
  protected name = this.s?.name ?? '';
  protected code = this.s?.installationCode ?? '';
  protected readonly productId = signal<Guid | null>(this.s?.productId ?? null);
  protected readonly versionId = signal<Guid | null>(this.s?.versionId ?? null);
  protected releaseId: Guid | null = this.s?.releaseId ?? null;
  protected environment: InstallationEnvironment = this.s?.environment ?? 'Production';
  protected mode: DeploymentMode = this.s?.deploymentMode ?? 'OnPremise';
  protected status: InstallationStatus = this.s?.status ?? 'Active';
  protected locationId: Guid | null = this.s?.locationId ?? null;
  protected userCount: number | null = this.s?.userCount ?? null;
  protected readonly goLive = signal<string | null>(this.s?.goLiveOn ?? null);
  protected os = this.i?.operatingSystem ?? '';
  protected dbVersion = this.i?.databaseVersion ?? '';
  protected server = this.i?.serverConfiguration ?? '';
  protected notes = this.i?.notes ?? '';
  protected readonly moduleIds = signal<string[]>(this.i?.enabledModules.map((m) => m.id) ?? []);
  protected readonly integrations = signal<InstallationIntegrationDto[]>(this.i?.integrations ?? []);

  protected readonly products = signal<ProductDto[]>([]);
  protected readonly locations = signal<LocationDto[]>([]);
  protected readonly product = computed(() => this.products().find((p) => p.id === this.productId()) ?? null);
  protected readonly releases = computed(() => this.product()?.versions.find((v) => v.id === this.versionId())?.releases ?? []);
  protected readonly moduleOptions = computed<SdOption[]>(() => (this.product()?.modules ?? []).map((m) => ({ value: m.id, label: m.name })));
  protected readonly customerOptions = computed<SdOption[]>(() =>
    this.customerId() && this.customerName ? [{ value: this.customerId()!, label: this.customerName }] : [],
  );
  protected readonly searchCustomers: SdOptionSource = (query) =>
    this.customers.list({ q: query, limit: 20 }).pipe(map((p) => p.items.map((x) => ({ value: x.id, label: x.name, description: x.code }))));

  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  constructor() {
    this.productsApi.list(false).subscribe((p) => this.products.set(p));
    if (this.customerId()) this.customers.locations(this.customerId()!).subscribe((l) => this.locations.set(l));
  }

  protected setCustomer(value: string | string[] | null): void {
    const id = Array.isArray(value) ? (value[0] ?? null) : value;
    this.customerId.set(id);
    this.locationId = null;
    this.locations.set([]);
    if (id) this.customers.locations(id).subscribe((l) => this.locations.set(l));
  }

  protected setProduct(id: Guid | null): void {
    this.productId.set(id);
    this.versionId.set(null);
    this.releaseId = null;
    this.moduleIds.set([]);
  }

  protected addIntegration(): void {
    this.integrations.update((l) => [...l, { system: '', direction: null, notes: null }]);
  }

  protected updateIntegration(index: number, change: Partial<InstallationIntegrationDto>): void {
    this.integrations.update((l) => l.map((x, i) => (i === index ? { ...x, ...change } : x)));
  }

  protected removeIntegration(index: number): void {
    this.integrations.update((l) => l.filter((_, i) => i !== index));
  }

  protected save(): void {
    const customerId = this.customerId();
    const productId = this.productId();
    if (!customerId || !productId) {
      this.errors.set({ ...(customerId ? {} : { customerId: 'Choose a customer.' }), ...(productId ? {} : { productId: 'Choose a product.' }) });
      return;
    }
    this.saving.set(true);
    this.api
      .save(this.s?.id ?? null, {
        customerId,
        productId,
        versionId: this.versionId(),
        releaseId: this.releaseId,
        locationId: this.locationId,
        name: this.name.trim(),
        installationCode: this.code || null,
        environment: this.environment,
        deploymentMode: this.mode,
        status: this.status,
        userCount: this.userCount === null || (this.userCount as unknown) === '' ? null : Number(this.userCount),
        goLiveOn: this.goLive(),
        installedOn: this.i?.installedOn ?? null,
        operatingSystem: this.os || null,
        databaseVersion: this.dbVersion || null,
        serverConfiguration: this.server || null,
        enabledModuleIds: this.moduleIds(),
        integrations: this.integrations().filter((x) => x.system.trim()),
        notes: this.notes || null,
        rowVersion: this.i?.rowVersion ?? null,
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
