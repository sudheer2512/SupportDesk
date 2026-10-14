import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { CustomerDepartmentDto, CustomersApi, Guid, LocationDto, MeStore, saveErrorMessage } from '@sd/core';
import { SdBadge, SdButton, SdDialogService, SdEmptyState, ToastService } from '@sd/ui';
import { LocationFormData, LocationFormDialog } from './location-form.dialog';
import { SECTION_STYLES, TABLE_STYLES } from './shared/styles';
import { TextPromptData, TextPromptDialog } from './shared/text-prompt.dialog';

/** Customer locations (branches/sites) and customer departments — Customer 360 tab. */
@Component({
  selector: 'desk-c360-locations',
  imports: [SdButton, SdBadge, SdEmptyState],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="section-head first">
      <h2>Locations</h2>
      <span class="sd-spacer"></span>
      @if (canManage) {
        <button sdButton size="sm" icon="plus" (click)="editLocation(null)">Add location</button>
      }
    </div>
    @if (locations().length === 0) {
      <sd-empty-state icon="map-pin" title="No locations" compact />
    } @else {
      <div class="dt-wrap">
        <table class="dt">
          <thead>
            <tr>
              <th scope="col">Name</th>
              <th scope="col">Address</th>
              <th scope="col">Region</th>
              <th scope="col" class="num">Contacts</th>
              <th scope="col" class="num">Installations</th>
              <th scope="col"><span class="sd-visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (l of locations(); track l.id) {
              <tr>
                <td>
                  <strong>{{ l.name }}</strong>
                  @if (l.code) {
                    <span class="sd-muted sd-xs"> · {{ l.code }}</span>
                  }
                  @if (l.isHeadOffice) {
                    <sd-badge tone="primary" size="sm">Head office</sd-badge>
                  }
                  @if (!l.isActive) {
                    <sd-badge size="sm">Inactive</sd-badge>
                  }
                  @if (l.erpLocationId) {
                    <sd-badge tone="info" variant="outline" size="sm">ERP</sd-badge>
                  }
                </td>
                <td class="sd-small">{{ address(l) }}</td>
                <td>{{ l.serviceRegion ?? '—' }}</td>
                <td class="num">{{ l.contactCount }}</td>
                <td class="num">{{ l.installationCount }}</td>
                <td class="row-actions">
                  @if (canManage) {
                    <button sdButton size="sm" variant="ghost" icon="pencil" iconOnly [attr.aria-label]="'Edit ' + l.name" (click)="editLocation(l)"></button>
                    <button sdButton size="sm" variant="ghost" icon="trash" iconOnly [attr.aria-label]="'Delete ' + l.name" (click)="deleteLocation(l)"></button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }

    <div class="section-head">
      <h2>Customer departments</h2>
      <span class="sd-spacer"></span>
      @if (canManage) {
        <button sdButton size="sm" icon="plus" (click)="editDepartment(null)">Add department</button>
      }
    </div>
    @if (departments().length === 0) {
      <p class="empty-note">No departments. Departments group the customer's contacts (Finance, IT, Operations…).</p>
    } @else {
      <ul class="deps">
        @for (d of departments(); track d.id) {
          <li>
            <span>{{ d.name }}</span>
            <span class="sd-xs sd-muted">{{ d.contactCount }} contacts</span>
            @if (canManage) {
              <button sdButton size="sm" variant="ghost" icon="pencil" iconOnly [attr.aria-label]="'Rename ' + d.name" (click)="editDepartment(d)"></button>
              <button sdButton size="sm" variant="ghost" icon="trash" iconOnly [attr.aria-label]="'Delete ' + d.name" (click)="deleteDepartment(d)"></button>
            }
          </li>
        }
      </ul>
    }
  `,
  styles: [
    TABLE_STYLES,
    SECTION_STYLES,
    `
      .first {
        margin-top: 0;
      }
      sd-badge {
        margin-left: 6px;
      }
      .deps {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sd-space-2);
        list-style: none;
        padding: 0;
      }
      .deps li {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        padding: 4px 4px 4px 10px;
        border: 1px solid var(--sd-border);
        border-radius: var(--sd-radius-pill);
        background: var(--sd-surface);
      }
    `,
  ],
})
export class C360LocationsComponent {
  private readonly api = inject(CustomersApi);
  private readonly dialog = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  protected readonly canManage = inject(MeStore).can('customers.manage');

  readonly customerId = input.required<Guid>();
  protected readonly locations = signal<LocationDto[]>([]);
  protected readonly departments = signal<CustomerDepartmentDto[]>([]);

  constructor() {
    effect(() => {
      const id = this.customerId();
      untracked(() => this.load(id));
    });
  }

  private load(id: Guid): void {
    this.api.locations(id).subscribe((l) => this.locations.set(l));
    this.api.departments(id).subscribe((d) => this.departments.set(d));
  }

  protected address(l: LocationDto): string {
    return [l.addressLine1, l.addressLine2, l.city, l.state, l.postalCode, l.country].filter(Boolean).join(', ') || '—';
  }

  protected editLocation(location: LocationDto | null): void {
    this.dialog
      .open<LocationDto, LocationFormData>(LocationFormDialog, { data: { customerId: this.customerId(), location }, size: 'md' })
      .closed.subscribe((saved) => saved && this.load(this.customerId()));
  }

  protected async deleteLocation(l: LocationDto): Promise<void> {
    const ok = await this.dialog.confirm({
      title: `Delete ${l.name}?`,
      message: 'Locations referenced by contacts, installations or tickets are deactivated instead of deleted.',
      danger: true,
      confirmLabel: 'Delete',
    });
    if (!ok) return;
    this.api.deleteLocation(this.customerId(), l.id).subscribe(() => this.load(this.customerId()));
  }

  protected editDepartment(d: CustomerDepartmentDto | null): void {
    this.dialog
      .open<string, TextPromptData>(TextPromptDialog, { data: { title: d ? 'Rename department' : 'New department', label: 'Department name', value: d?.name }, size: 'sm' })
      .closed.subscribe((name) => {
        if (!name) return;
        this.api.saveDepartment(this.customerId(), d?.id ?? null, name).subscribe({
          next: () => this.load(this.customerId()),
          error: (err: unknown) => this.toast.error('Could not save the department', saveErrorMessage(err)),
        });
      });
  }

  protected async deleteDepartment(d: CustomerDepartmentDto): Promise<void> {
    if (!(await this.dialog.confirm({ title: `Delete ${d.name}?`, message: 'Its contacts stay with the customer.', danger: true, confirmLabel: 'Delete' }))) return;
    this.api.deleteDepartment(this.customerId(), d.id).subscribe(() => this.load(this.customerId()));
  }
}
