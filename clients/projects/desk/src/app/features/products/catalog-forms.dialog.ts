import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  Guid,
  MetaApi,
  ProductDto,
  ProductFamilyDto,
  ProductReleaseDto,
  ProductVersionDto,
  ProductsApi,
  RELEASE_TYPES,
  ReleaseType,
  VERSION_STATUSES,
  VersionStatus,
  saveErrorMessage,
  validationMessages,
} from '@sd/core';
import { SdButton, SdCheckbox, SdDateInput, SdDialog, SdFormField, SdInput } from '@sd/ui';
import { humanize } from '../customers/shared/customer-ui';

const FORM_STYLES = `
  .form { display: grid; gap: var(--sd-space-3); }
  .two { display: grid; gap: var(--sd-space-3); grid-template-columns: repeat(2, minmax(0, 1fr)); }
  @media (max-width: 640px) { .two { grid-template-columns: 1fr; } }
  .error { color: var(--sd-danger); }
`;

export interface ProductFormData {
  product?: ProductDto | null;
  families: ProductFamilyDto[];
}

/** Create / edit a product: family, code, default department/team used to route its tickets. */
@Component({
  selector: 'desk-product-form-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdCheckbox],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="p ? 'Edit ' + p.name : 'New product'">
      <form id="product-form" class="form" (ngSubmit)="save()" novalidate>
        <div class="two">
          <sd-form-field label="Name" required [error]="errors()['name']">
            <input sdInput name="name" [(ngModel)]="name" required maxlength="200" />
          </sd-form-field>
          <sd-form-field label="Code" required [error]="errors()['code']" hint="Short unique code, also used by the ERP (e.g. WERP).">
            <input sdInput name="code" [(ngModel)]="code" required maxlength="40" />
          </sd-form-field>
        </div>
        <sd-form-field label="Family" optional [error]="errors()['familyId']">
          <select sdInput name="family" [(ngModel)]="familyId">
            <option [ngValue]="null">—</option>
            @for (f of data.families; track f.id) {
              <option [ngValue]="f.id">{{ f.name }}</option>
            }
          </select>
        </sd-form-field>
        <div class="two">
          <sd-form-field label="Default department" optional hint="New tickets for this product go here." [error]="errors()['defaultDepartmentId']">
            <select sdInput name="dept" [(ngModel)]="departmentId" (ngModelChange)="teamId = null">
              <option [ngValue]="null">—</option>
              @for (d of departments(); track d.id) {
                <option [ngValue]="d.id">{{ d.name }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Default team" optional [error]="errors()['defaultTeamId']">
            <select sdInput name="team" [(ngModel)]="teamId">
              <option [ngValue]="null">—</option>
              @for (t of teams(); track t.id) {
                <option [ngValue]="t.id">{{ t.name }}</option>
              }
            </select>
          </sd-form-field>
        </div>
        <sd-form-field label="Description" optional>
          <textarea sdInput name="desc" rows="3" [(ngModel)]="description" maxlength="4000"></textarea>
        </sd-form-field>
        <sd-checkbox [(value)]="active">Active (selectable on tickets)</sd-checkbox>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="product-form" [loading]="saving()">Save</button>
      </div>
    </sd-dialog>
  `,
  styles: FORM_STYLES,
})
export class ProductFormDialog {
  protected readonly data = inject<ProductFormData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<ProductDto>>(DialogRef);
  private readonly api = inject(ProductsApi);
  private readonly meta = inject(MetaApi);
  protected readonly p = this.data.product ?? null;
  protected name = this.p?.name ?? '';
  protected code = this.p?.code ?? '';
  protected familyId: Guid | null = this.p?.familyId ?? null;
  protected departmentId: Guid | null = this.p?.defaultDepartmentId ?? null;
  protected teamId: Guid | null = this.p?.defaultTeamId ?? null;
  protected description = this.p?.description ?? '';
  protected readonly active = signal(this.p?.isActive ?? true);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly departments = computed(() => this.meta.ticketForm()?.departments ?? []);
  private readonly allTeams = computed(() => this.meta.ticketForm()?.teams ?? []);

  protected teams() {
    return this.allTeams().filter((t) => !this.departmentId || t.departmentId === this.departmentId);
  }

  constructor() {
    this.meta.ticketForm$().subscribe();
  }

  protected save(): void {
    this.saving.set(true);
    this.api
      .save(this.p?.id ?? null, {
        name: this.name.trim(),
        code: this.code.trim(),
        familyId: this.familyId,
        defaultDepartmentId: this.departmentId,
        defaultTeamId: this.teamId,
        description: this.description || null,
        isActive: this.active(),
        erpProductId: this.p?.erpProductId ?? null,
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

export interface VersionFormData {
  productId: Guid;
  version?: ProductVersionDto | null;
}

/** Create / edit a product version line (status, release and end-of-support dates). */
@Component({
  selector: 'desk-version-form-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdDateInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="v ? 'Edit version ' + v.version : 'New version'">
      <form id="version-form" class="form" (ngSubmit)="save()" novalidate>
        <div class="two">
          <sd-form-field label="Version" required [error]="errors()['version']">
            <input sdInput name="version" [(ngModel)]="version" required maxlength="60" placeholder="8.1" />
          </sd-form-field>
          <sd-form-field label="Status">
            <select sdInput name="status" [(ngModel)]="status">
              @for (s of statuses; track s) {
                <option [value]="s">{{ humanize(s) }}</option>
              }
            </select>
          </sd-form-field>
        </div>
        <div class="two">
          <sd-form-field label="Release date" optional>
            <sd-date-input [(value)]="releaseDate" />
          </sd-form-field>
          <sd-form-field label="End of support" optional [error]="errors()['endOfSupportDate']">
            <sd-date-input [(value)]="endOfSupport" />
          </sd-form-field>
        </div>
        <sd-form-field label="Notes" optional>
          <textarea sdInput name="notes" rows="2" [(ngModel)]="notes" maxlength="4000"></textarea>
        </sd-form-field>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="version-form" [loading]="saving()">Save</button>
      </div>
    </sd-dialog>
  `,
  styles: FORM_STYLES,
})
export class VersionFormDialog {
  protected readonly data = inject<VersionFormData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<ProductVersionDto>>(DialogRef);
  private readonly api = inject(ProductsApi);
  protected readonly humanize = humanize;
  protected readonly statuses = VERSION_STATUSES;
  protected readonly v = this.data.version ?? null;
  protected version = this.v?.version ?? '';
  protected status: VersionStatus = this.v?.status ?? 'Released';
  protected readonly releaseDate = signal<string | null>(this.v?.releaseDate ?? null);
  protected readonly endOfSupport = signal<string | null>(this.v?.endOfSupportDate ?? null);
  protected notes = this.v?.notes ?? '';
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  protected save(): void {
    this.saving.set(true);
    this.api
      .saveVersion(this.data.productId, this.v?.id ?? null, {
        version: this.version.trim(),
        status: this.status,
        releaseDate: this.releaseDate(),
        endOfSupportDate: this.endOfSupport(),
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

export interface ReleaseFormData {
  productId: Guid;
  versionId: Guid;
  release?: ProductReleaseDto | null;
}

/** Create / edit a release (build) of a version, with release notes and the engineering release reference. */
@Component({
  selector: 'desk-release-form-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdDateInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="r ? 'Edit release ' + r.name : 'New release'">
      <form id="release-form" class="form" (ngSubmit)="save()" novalidate>
        <div class="two">
          <sd-form-field label="Release" required [error]="errors()['name']">
            <input sdInput name="name" [(ngModel)]="name" required maxlength="100" placeholder="8.0.2" />
          </sd-form-field>
          <sd-form-field label="Build" optional>
            <input sdInput name="build" [(ngModel)]="build" maxlength="60" />
          </sd-form-field>
        </div>
        <div class="two">
          <sd-form-field label="Type">
            <select sdInput name="type" [(ngModel)]="type">
              @for (t of types; track t) {
                <option [value]="t">{{ t }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Release date" optional>
            <sd-date-input [(value)]="releaseDate" />
          </sd-form-field>
        </div>
        <sd-form-field label="Engineering release" optional hint="Azure DevOps / GitHub release or tag reference.">
          <input sdInput name="eng" [(ngModel)]="engineeringRef" maxlength="200" />
        </sd-form-field>
        <sd-form-field label="Release notes" optional>
          <textarea sdInput name="notes" rows="5" [(ngModel)]="notes"></textarea>
        </sd-form-field>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="release-form" [loading]="saving()">Save</button>
      </div>
    </sd-dialog>
  `,
  styles: FORM_STYLES,
})
export class ReleaseFormDialog {
  protected readonly data = inject<ReleaseFormData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<ProductReleaseDto>>(DialogRef);
  private readonly api = inject(ProductsApi);
  protected readonly types = RELEASE_TYPES;
  protected readonly r = this.data.release ?? null;
  protected name = this.r?.name ?? '';
  protected build = this.r?.buildNumber ?? '';
  protected type: ReleaseType = this.r?.type ?? 'Patch';
  protected readonly releaseDate = signal<string | null>(this.r?.releaseDate ?? null);
  protected engineeringRef = this.r?.engineeringReleaseRef ?? '';
  protected notes = this.r?.releaseNotes ?? '';
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  protected save(): void {
    this.saving.set(true);
    this.api
      .saveRelease(this.data.productId, this.data.versionId, this.r?.id ?? null, {
        name: this.name.trim(),
        buildNumber: this.build || null,
        type: this.type,
        releaseDate: this.releaseDate(),
        engineeringReleaseRef: this.engineeringRef || null,
        releaseNotes: this.notes || null,
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
