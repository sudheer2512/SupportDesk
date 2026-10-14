import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CustomersApi, Guid, LocationDto, saveErrorMessage, validationMessages } from '@sd/core';
import { SdButton, SdCheckbox, SdDialog, SdFormField, SdInput } from '@sd/ui';

export interface LocationFormData {
  customerId: Guid;
  location?: LocationDto | null;
}

/** Create / edit a customer location (branch, site). */
@Component({
  selector: 'desk-location-form-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdCheckbox],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="l ? 'Edit location' : 'New location'">
      <form id="location-form" class="form" (ngSubmit)="save()" novalidate>
        <div class="two">
          <sd-form-field label="Name" required [error]="errors()['name']">
            <input sdInput name="name" [(ngModel)]="name" required maxlength="200" />
          </sd-form-field>
          <sd-form-field label="Code" optional>
            <input sdInput name="code" [(ngModel)]="code" maxlength="40" />
          </sd-form-field>
        </div>
        <sd-form-field label="Address" optional>
          <input sdInput name="a1" [(ngModel)]="address1" maxlength="300" autocomplete="address-line1" />
        </sd-form-field>
        <sd-form-field label="Address line 2" optional>
          <input sdInput name="a2" [(ngModel)]="address2" maxlength="300" autocomplete="address-line2" />
        </sd-form-field>
        <div class="three">
          <sd-form-field label="City" optional><input sdInput name="city" [(ngModel)]="city" maxlength="120" /></sd-form-field>
          <sd-form-field label="State" optional><input sdInput name="state" [(ngModel)]="state" maxlength="120" /></sd-form-field>
          <sd-form-field label="Postal code" optional><input sdInput name="pin" [(ngModel)]="postal" maxlength="20" /></sd-form-field>
        </div>
        <div class="three">
          <sd-form-field label="Country" optional><input sdInput name="country" [(ngModel)]="country" maxlength="120" /></sd-form-field>
          <sd-form-field label="Phone" optional><input sdInput type="tel" name="phone" [(ngModel)]="phone" maxlength="50" /></sd-form-field>
          <sd-form-field label="Service region" optional><input sdInput name="region" [(ngModel)]="region" maxlength="100" /></sd-form-field>
        </div>
        <sd-form-field label="Time zone" optional><input sdInput name="tz" [(ngModel)]="timeZone" placeholder="Asia/Kolkata" maxlength="100" /></sd-form-field>
        <div class="checks">
          <sd-checkbox [(value)]="headOffice">Head office</sd-checkbox>
          <sd-checkbox [(value)]="active">Active</sd-checkbox>
        </div>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="location-form" [loading]="saving()">Save</button>
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
      gap: var(--sd-space-5);
    }
    .error {
      color: var(--sd-danger);
    }
  `,
})
export class LocationFormDialog {
  protected readonly data = inject<LocationFormData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<LocationDto>>(DialogRef);
  private readonly api = inject(CustomersApi);
  protected readonly l = this.data.location ?? null;

  protected name = this.l?.name ?? '';
  protected code = this.l?.code ?? '';
  protected address1 = this.l?.addressLine1 ?? '';
  protected address2 = this.l?.addressLine2 ?? '';
  protected city = this.l?.city ?? '';
  protected state = this.l?.state ?? '';
  protected postal = this.l?.postalCode ?? '';
  protected country = this.l?.country ?? 'India';
  protected phone = this.l?.phone ?? '';
  protected region = this.l?.serviceRegion ?? '';
  protected timeZone = this.l?.timeZone ?? '';
  protected readonly headOffice = signal(this.l?.isHeadOffice ?? false);
  protected readonly active = signal(this.l?.isActive ?? true);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  protected save(): void {
    this.saving.set(true);
    this.api
      .saveLocation(this.data.customerId, this.l?.id ?? null, {
        name: this.name.trim(),
        code: this.code || null,
        addressLine1: this.address1 || null,
        addressLine2: this.address2 || null,
        city: this.city || null,
        state: this.state || null,
        postalCode: this.postal || null,
        country: this.country || null,
        phone: this.phone || null,
        serviceRegion: this.region || null,
        timeZone: this.timeZone || null,
        isHeadOffice: this.headOffice(),
        isActive: this.active(),
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
