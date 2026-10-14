import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  CustomersApi,
  Guid,
  InstallationListItemDto,
  InstallationsApi,
  LocationDto,
  MeStore,
  MetaApi,
  SERVICE_ACTIVITY_STATUSES,
  SERVICE_ACTIVITY_TYPES,
  ServiceActivitiesApi,
  ServiceActivityDto,
  ServiceActivityStatus,
  ServiceActivityType,
  saveErrorMessage,
  validationMessages,
} from '@sd/core';
import { SdButton, SdCheckbox, SdCombobox, SdDateInput, SdDialog, SdDialogService, SdFormField, SdInput, SdOption, SdOptionSource } from '@sd/ui';
import { map } from 'rxjs';
import { humanize } from './shared/customer-ui';

export interface ActivityFormData {
  customerId?: Guid | null;
  customerName?: string | null;
  activity?: ServiceActivityDto | null;
  /** Pre-filled start (e.g. the day clicked in a calendar). */
  start?: Date | null;
}

/** Schedule / update a service visit or activity (on-site visit, maintenance, training, upgrade…). Returns the saved activity, or `null` after a delete. */
@Component({
  selector: 'desk-activity-form-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdCombobox, SdDateInput, SdCheckbox],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="a ? 'Service activity' : 'Schedule activity'">
      <form id="activity-form" class="form" (ngSubmit)="save()" novalidate>
        <sd-form-field label="Customer" required [error]="errors()['customerId']">
          <sd-combobox [search]="searchCustomers" [options]="customerOptions()" [value]="customerId()" (valueChange)="setCustomer($event)" placeholder="Choose a customer" [disabled]="!canEdit" />
        </sd-form-field>
        <div class="two">
          <sd-form-field label="Type">
            <select sdInput name="type" [(ngModel)]="type" [disabled]="!canEdit">
              @for (t of types; track t) {
                <option [value]="t">{{ humanize(t) }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Status">
            <select sdInput name="status" [(ngModel)]="status" [disabled]="!canEdit">
              @for (s of statuses; track s) {
                <option [value]="s">{{ humanize(s) }}</option>
              }
            </select>
          </sd-form-field>
        </div>
        <sd-form-field label="Title" required [error]="errors()['title']">
          <input sdInput name="title" [(ngModel)]="title" required maxlength="300" [disabled]="!canEdit" />
        </sd-form-field>
        <div class="two">
          <sd-form-field label="Starts" required [error]="errors()['scheduledStart']">
            <sd-date-input type="datetime" [(value)]="start" [disabled]="!canEdit" />
          </sd-form-field>
          <sd-form-field label="Ends" required [error]="errors()['scheduledEnd']">
            <sd-date-input type="datetime" [(value)]="end" [disabled]="!canEdit" />
          </sd-form-field>
        </div>
        <div class="two">
          <sd-form-field label="Assigned to" optional [error]="errors()['assignedUserId']">
            <sd-combobox [options]="agentOptions()" [(value)]="assignee" placeholder="Unassigned" [disabled]="!canEdit" />
          </sd-form-field>
          <sd-form-field label="Location" optional [error]="errors()['locationId']">
            <select sdInput name="location" [(ngModel)]="locationId" [disabled]="!canEdit">
              <option [ngValue]="null">—</option>
              @for (l of locations(); track l.id) {
                <option [ngValue]="l.id">{{ l.name }}</option>
              }
            </select>
          </sd-form-field>
        </div>
        <sd-form-field label="Installation" optional [error]="errors()['installationId']">
          <select sdInput name="installation" [(ngModel)]="installationId" [disabled]="!canEdit">
            <option [ngValue]="null">—</option>
            @for (i of installations(); track i.id) {
              <option [ngValue]="i.id">{{ i.name }} ({{ i.productName }} {{ i.version }})</option>
            }
          </select>
        </sd-form-field>
        <sd-form-field label="Description" optional>
          <textarea sdInput name="description" rows="3" [(ngModel)]="description" maxlength="8000" [disabled]="!canEdit"></textarea>
        </sd-form-field>
        <sd-form-field label="Outcome" optional hint="What happened — shown on the customer's history.">
          <textarea sdInput name="outcome" rows="2" [(ngModel)]="outcome" maxlength="8000" [disabled]="!canEdit"></textarea>
        </sd-form-field>
        <sd-checkbox [(value)]="visible" [disabled]="!canEdit">Visible to the customer in the portal</sd-checkbox>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        @if (a && canEdit) {
          <button sdButton type="button" variant="danger" icon="trash" (click)="remove()">Delete</button>
          <span class="sd-spacer"></span>
        }
        <button sdButton type="button" (click)="ref.close()">{{ canEdit ? 'Cancel' : 'Close' }}</button>
        @if (canEdit) {
          <button sdButton variant="primary" type="submit" form="activity-form" [loading]="saving()">Save</button>
        }
      </div>
    </sd-dialog>
  `,
  styles: `
    .form {
      display: grid;
      gap: var(--sd-space-3);
    }
    .two {
      display: grid;
      gap: var(--sd-space-3);
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }
    @media (max-width: 640px) {
      .two {
        grid-template-columns: 1fr;
      }
    }
    .error {
      color: var(--sd-danger);
    }
  `,
})
export class ActivityFormDialog {
  protected readonly data = inject<ActivityFormData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<ServiceActivityDto | null>>(DialogRef);
  private readonly api = inject(ServiceActivitiesApi);
  private readonly customers = inject(CustomersApi);
  private readonly installationsApi = inject(InstallationsApi);
  private readonly meta = inject(MetaApi);
  private readonly dialogs = inject(SdDialogService);
  private readonly me = inject(MeStore);

  protected readonly a = this.data.activity ?? null;
  protected readonly canEdit = this.me.can('customers.manage');
  protected readonly humanize = humanize;
  protected readonly types = SERVICE_ACTIVITY_TYPES;
  protected readonly statuses = SERVICE_ACTIVITY_STATUSES;

  protected readonly customerId = signal<string | null>(this.a?.customerId ?? this.data.customerId ?? null);
  private readonly customerName = this.a?.customerName ?? this.data.customerName ?? null;
  protected type: ServiceActivityType = this.a?.type ?? 'OnsiteVisit';
  protected status: ServiceActivityStatus = this.a?.status ?? 'Scheduled';
  protected title = this.a?.title ?? '';
  protected readonly start = signal<string | null>(this.a?.scheduledStart ?? defaultStart(this.data.start));
  protected readonly end = signal<string | null>(this.a?.scheduledEnd ?? new Date(new Date(defaultStart(this.data.start)).getTime() + 2 * 3600_000).toISOString());
  protected readonly assignee = signal<string | null>(this.a?.assignedUserId ?? this.me.user()?.id ?? null);
  protected locationId: Guid | null = this.a?.locationId ?? null;
  protected installationId: Guid | null = this.a?.installationId ?? null;
  protected description = this.a?.description ?? '';
  protected outcome = this.a?.outcome ?? '';
  protected readonly visible = signal(this.a?.visibleToCustomer ?? true);

  protected readonly locations = signal<LocationDto[]>([]);
  protected readonly installations = signal<InstallationListItemDto[]>([]);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  protected readonly agentOptions = computed<SdOption[]>(() =>
    (this.meta.ticketForm()?.agents ?? []).map((x) => ({ value: x.userId, label: x.name, avatar: x.name })),
  );
  protected readonly customerOptions = computed<SdOption[]>(() =>
    this.customerId() && this.customerName ? [{ value: this.customerId()!, label: this.customerName }] : [],
  );
  protected readonly searchCustomers: SdOptionSource = (query) =>
    this.customers.list({ q: query, limit: 20 }).pipe(map((p) => p.items.map((c) => ({ value: c.id, label: c.name, description: c.code }))));

  constructor() {
    this.meta.ticketForm$().subscribe();
    if (this.customerId()) this.loadCustomerData(this.customerId()!);
  }

  protected setCustomer(value: string | string[] | null): void {
    const id = Array.isArray(value) ? (value[0] ?? null) : value;
    this.customerId.set(id);
    this.locationId = null;
    this.installationId = null;
    if (id) this.loadCustomerData(id);
  }

  private loadCustomerData(id: Guid): void {
    this.customers.locations(id).subscribe((l) => this.locations.set(l));
    if (this.me.can('products.view'))
      this.installationsApi.list({ customerId: id, limit: 100 }).subscribe((p) => this.installations.set(p.items));
  }

  protected save(): void {
    const customerId = this.customerId();
    if (!customerId) {
      this.errors.set({ customerId: 'Choose a customer.' });
      return;
    }
    this.saving.set(true);
    this.api
      .save(this.a?.id ?? null, {
        customerId,
        type: this.type,
        status: this.status,
        title: this.title.trim(),
        scheduledStart: this.start() ?? '',
        scheduledEnd: this.end() ?? '',
        assignedUserId: this.assignee(),
        locationId: this.locationId,
        installationId: this.installationId,
        contractId: this.a?.contractId ?? null,
        ticketId: this.a?.ticketId ?? null,
        description: this.description || null,
        outcome: this.outcome || null,
        visibleToCustomer: this.visible(),
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

  protected async remove(): Promise<void> {
    if (!this.a) return;
    if (!(await this.dialogs.confirm({ title: 'Delete this activity?', message: this.a.title, danger: true, confirmLabel: 'Delete' }))) return;
    this.api.delete(this.a.id).subscribe(() => this.ref.close(null));
  }
}

function defaultStart(day?: Date | null): string {
  const d = day ? new Date(day) : new Date();
  if (day) d.setHours(10, 0, 0, 0);
  else {
    d.setDate(d.getDate() + 1);
    d.setHours(10, 0, 0, 0);
  }
  return d.toISOString();
}
