import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ContractDetailDto,
  ContractFormMetaDto,
  ContractStatus,
  ContractType,
  ContractsApi,
  CustomersApi,
  Guid,
  JsonValue,
  ProductDto,
  ProductsApi,
  SaveContractRequest,
  saveErrorMessage,
  validationMessages,
} from '@sd/core';
import { SdButton, SdCheckbox, SdCombobox, SdDateInput, SdDialog, SdFormField, SdInput, SdOption, SdOptionSource } from '@sd/ui';
import { map } from 'rxjs';
import { addMonthsIso, humanize, todayIso } from '../customers/shared/customer-ui';

export interface ContractFormData {
  contract?: ContractDetailDto | null;
  customerId?: Guid | null;
  customerName?: string | null;
}

/** Create / edit a support contract (type, period, SLA, support hours, prepaid hours, billing, renewal, coverage). */
@Component({
  selector: 'desk-contract-form-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdCombobox, SdDateInput, SdCheckbox],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="c ? 'Edit contract ' + c.summary.number : 'New contract'" description="Contracts decide support eligibility, the SLA policy and support hours of tickets.">
      <form id="contract-form" class="form" (ngSubmit)="save()" novalidate>
        <sd-form-field label="Customer" required [error]="errors()['customerId']">
          <sd-combobox [search]="searchCustomers" [options]="customerOptions()" [value]="customerId()" (valueChange)="setCustomer($event)" placeholder="Choose a customer" [disabled]="!!c" />
        </sd-form-field>
        <div class="two">
          <sd-form-field label="Name" required [error]="errors()['name']">
            <input sdInput name="name" [(ngModel)]="name" required maxlength="250" />
          </sd-form-field>
          <sd-form-field label="Number" optional [error]="errors()['number']" hint="Generated when empty (e.g. AMC-2026-0007).">
            <input sdInput name="number" [(ngModel)]="number" maxlength="60" />
          </sd-form-field>
        </div>
        <div class="three">
          <sd-form-field label="Type">
            <select sdInput name="type" [(ngModel)]="type">
              @for (t of meta()?.types ?? []; track t) {
                <option [value]="t">{{ humanize(t) }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Status">
            <select sdInput name="status" [(ngModel)]="status">
              @for (s of meta()?.statuses ?? []; track s) {
                <option [value]="s">{{ s }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="SLA policy" optional [error]="errors()['slaPolicyId']">
            <select sdInput name="sla" [(ngModel)]="slaPolicyId">
              <option [ngValue]="null">Default selection</option>
              @for (p of meta()?.slaPolicies ?? []; track p.id) {
                <option [ngValue]="p.id">{{ p.name }}</option>
              }
            </select>
          </sd-form-field>
        </div>
        <div class="three">
          <sd-form-field label="Start date" required>
            <sd-date-input [(value)]="startDate" />
          </sd-form-field>
          <sd-form-field label="End date" required [error]="errors()['endDate']">
            <sd-date-input [(value)]="endDate" />
          </sd-form-field>
          <sd-form-field label="Support hours" optional [error]="errors()['calendarId']">
            <select sdInput name="calendar" [(ngModel)]="calendarId">
              <option [ngValue]="null">Policy / department hours</option>
              @for (cal of meta()?.calendars ?? []; track cal.id) {
                <option [ngValue]="cal.id">{{ cal.name }}</option>
              }
            </select>
          </sd-form-field>
        </div>
        <div class="three">
          <sd-form-field label="Contract value" optional [error]="errors()['contractValue']">
            <input sdInput type="number" min="0" step="1" name="value" [(ngModel)]="value" />
          </sd-form-field>
          <sd-form-field label="Currency">
            <input sdInput name="currency" [(ngModel)]="currency" maxlength="3" />
          </sd-form-field>
          <sd-form-field label="Prepaid hours" [optional]="type !== 'PrepaidHours'" [error]="errors()['prepaidHours']">
            <input sdInput type="number" min="0" step="0.5" name="hours" [(ngModel)]="prepaidHours" />
          </sd-form-field>
        </div>
        <div class="three">
          <sd-form-field label="Hourly rate" optional [error]="errors()['hourlyRate']">
            <input sdInput type="number" min="0" step="1" name="rate" [(ngModel)]="hourlyRate" />
          </sd-form-field>
          <sd-form-field label="Renewal date" optional>
            <sd-date-input [(value)]="renewalDate" />
          </sd-form-field>
          <sd-form-field label="ERP reference" optional>
            <input sdInput name="erp" [(ngModel)]="erpReference" maxlength="100" />
          </sd-form-field>
        </div>
        <div class="checks">
          <sd-checkbox [(value)]="billable">Billable (time is charged)</sd-checkbox>
          <sd-checkbox [(value)]="autoRenew">Auto-renew</sd-checkbox>
          <sd-checkbox [(value)]="onsite">On-site visits covered</sd-checkbox>
        </div>
        <sd-form-field label="Covered channels" optional>
          <sd-combobox multiple [options]="channelOptions" [(value)]="channels" placeholder="All channels" />
        </sd-form-field>
        @if (!c) {
          <sd-form-field label="Covered products" optional hint="Creates a support entitlement per product; leave empty for customer-wide coverage.">
            <sd-combobox multiple [options]="productOptions()" [(value)]="productIds" placeholder="Customer-wide" />
          </sd-form-field>
        }
        <sd-form-field label="Notes" optional>
          <textarea sdInput name="notes" rows="2" [(ngModel)]="notes" maxlength="8000"></textarea>
        </sd-form-field>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="contract-form" [loading]="saving()">{{ c ? 'Save changes' : 'Create contract' }}</button>
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
export class ContractFormDialog {
  protected readonly data = inject<ContractFormData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<ContractDetailDto>>(DialogRef);
  private readonly api = inject(ContractsApi);
  private readonly customers = inject(CustomersApi);
  private readonly products = inject(ProductsApi);
  protected readonly humanize = humanize;

  protected readonly c = this.data.contract ?? null;
  private readonly s = this.c?.summary;
  protected readonly meta = signal<ContractFormMetaDto | null>(null);
  protected readonly productList = signal<ProductDto[]>([]);
  protected readonly customerId = signal<string | null>(this.s?.customerId ?? this.data.customerId ?? null);
  private readonly customerName = this.s?.customerName ?? this.data.customerName ?? null;
  protected name = this.s?.name ?? 'Annual Maintenance Contract';
  protected number = this.s?.number ?? '';
  protected type: ContractType = this.s?.type ?? 'Amc';
  protected status: ContractStatus = this.s?.status ?? 'Active';
  protected slaPolicyId: Guid | null = this.s?.slaPolicyId ?? null;
  protected calendarId: Guid | null = this.c?.calendarId ?? null;
  protected readonly startDate = signal<string | null>(this.s?.startDate ?? todayIso());
  protected readonly endDate = signal<string | null>(this.s?.endDate ?? addMonthsIso(todayIso(), 12, -1));
  protected readonly renewalDate = signal<string | null>(this.s?.renewalDate ?? null);
  protected value: number | null = this.s?.contractValue ?? null;
  protected currency = this.s?.currency ?? 'INR';
  protected prepaidHours: number | null = this.s?.prepaidHours ?? null;
  protected hourlyRate: number | null = this.c?.hourlyRate ?? null;
  protected erpReference = this.s?.erpReference ?? '';
  protected notes = this.c?.notes ?? '';
  protected readonly billable = signal(this.s?.isBillable ?? false);
  protected readonly autoRenew = signal(this.s?.autoRenew ?? false);
  protected readonly onsite = signal(this.c?.coverage?.['onsite'] === true);
  protected readonly channels = signal<string[]>(Array.isArray(this.c?.coverage?.['channels']) ? (this.c!.coverage['channels'] as string[]) : []);
  protected readonly productIds = signal<string[]>([]);
  protected readonly channelOptions: SdOption[] = ['Email', 'Phone', 'Portal', 'WhatsApp', 'Chat'].map((c) => ({ value: c, label: c }));
  protected readonly productOptions = computed<SdOption[]>(() => this.productList().filter((p) => p.isActive).map((p) => ({ value: p.id, label: p.name, description: p.code })));

  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly customerOptions = computed<SdOption[]>(() =>
    this.customerId() && this.customerName ? [{ value: this.customerId()!, label: this.customerName }] : [],
  );
  protected readonly searchCustomers: SdOptionSource = (query) =>
    this.customers.list({ q: query, limit: 20 }).pipe(map((p) => p.items.map((x) => ({ value: x.id, label: x.name, description: x.code }))));

  constructor() {
    this.api.formMeta().subscribe((m) => this.meta.set(m));
    if (!this.c) this.products.list(false).subscribe((p) => this.productList.set(p));
  }

  protected setCustomer(value: string | string[] | null): void {
    this.customerId.set(Array.isArray(value) ? (value[0] ?? null) : value);
  }

  protected save(): void {
    const customerId = this.customerId();
    if (!customerId) {
      this.errors.set({ customerId: 'Choose a customer.' });
      return;
    }
    const coverage: Record<string, JsonValue> = { ...(this.c?.coverage ?? {}), onsite: this.onsite(), channels: this.channels() };
    const request: SaveContractRequest = {
      customerId,
      number: this.number.trim() || null,
      name: this.name.trim(),
      type: this.type,
      status: this.status,
      startDate: this.startDate() ?? todayIso(),
      endDate: this.endDate() ?? todayIso(),
      slaPolicyId: this.slaPolicyId,
      calendarId: this.calendarId,
      prepaidHours: this.prepaidHours === null || (this.prepaidHours as unknown) === '' ? null : Number(this.prepaidHours),
      isBillable: this.billable(),
      hourlyRate: this.hourlyRate === null || (this.hourlyRate as unknown) === '' ? null : Number(this.hourlyRate),
      contractValue: this.value === null || (this.value as unknown) === '' ? null : Number(this.value),
      currency: this.currency || 'INR',
      autoRenew: this.autoRenew(),
      renewalDate: this.renewalDate(),
      coverage,
      erpReference: this.erpReference || null,
      notes: this.notes || null,
      entitlements: this.c
        ? null
        : this.productIds().map((id) => ({
            name: `${this.productList().find((p) => p.id === id)?.name ?? 'Product'} support`,
            type: 'Support' as const,
            productId: id,
            isActive: true,
          })),
      rowVersion: this.c?.rowVersion ?? null,
    };
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    this.api.save(this.c?.summary.id ?? null, request).subscribe({
      next: (saved) => this.ref.close(saved),
      error: (err: unknown) => {
        this.saving.set(false);
        this.errors.set(validationMessages(err));
        this.formError.set(saveErrorMessage(err));
      },
    });
  }
}
