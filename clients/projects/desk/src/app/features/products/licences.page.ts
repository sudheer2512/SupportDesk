import { ChangeDetectionStrategy, Component, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AssetDto, InstallationsApi, LICENCE_STATUSES, LicenceDto, MeStore } from '@sd/core';
import { SdBadge, SdButton, SdDialogService, SdEmptyState, SdIcon, SdInput, SdPageHeader, SdSkeleton, ToastService } from '@sd/ui';
import { DateOnlyPipe, HumanizePipe, statusTone, urgencyTone } from '../customers/shared/customer-ui';
import { ModuleTabsComponent, PRODUCT_TABS } from '../customers/shared/module-tabs.component';
import { SECTION_STYLES, TABLE_STYLES } from '../customers/shared/styles';
import { AssetFormData, AssetFormDialog, LicenceFormData, LicenceFormDialog } from './licence-asset-forms.dialog';

/** /products/licences — licences (masked keys, seats, expiry) with an "expiring soon" filter. */
@Component({
  selector: 'desk-licences-page',
  imports: [FormsModule, RouterLink, SdPageHeader, SdButton, SdBadge, SdInput, SdSkeleton, SdEmptyState, DateOnlyPipe, ModuleTabsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Licences" subtitle="Licence keys (masked), seats and expiry per installation." icon="key">
        @if (canManage) {
          <button sdButton sdPageActions variant="primary" icon="plus" (click)="edit(null)">New licence</button>
        }
      </sd-page-header>
      <desk-module-tabs [tabs]="tabs" ariaLabel="Products & Assets sections" />
      <div class="desk-page__body">
        <div class="toolbar">
          <select sdInput size="sm" class="f" aria-label="Status" [ngModel]="status()" (ngModelChange)="status.set($event)">
            <option value="">All statuses</option>
            @for (s of statuses; track s) {
              <option [value]="s">{{ s }}</option>
            }
          </select>
          <select sdInput size="sm" class="f" aria-label="Expiry" [ngModel]="expiring()" (ngModelChange)="expiring.set($event)">
            <option [ngValue]="null">Any expiry</option>
            <option [ngValue]="30">Expiring within 30 days</option>
            <option [ngValue]="90">Expiring within 90 days</option>
            <option [ngValue]="365">Expiring within a year</option>
          </select>
        </div>
        @if (loading()) {
          <sd-skeleton [lines]="6" />
        } @else if (rows().length === 0) {
          <sd-empty-state icon="key" title="No licences match" />
        } @else {
          <div class="dt-wrap">
            <table class="dt">
              <thead>
                <tr>
                  <th scope="col">Key</th>
                  <th scope="col">Customer</th>
                  <th scope="col">Installation</th>
                  <th scope="col">Edition</th>
                  <th scope="col" class="num">Seats</th>
                  <th scope="col">Expires</th>
                  <th scope="col">Status</th>
                  <th scope="col"><span class="sd-visually-hidden">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                @for (l of rows(); track l.id) {
                  <tr>
                    <td class="sd-mono nowrap">{{ l.keyMasked }}</td>
                    <td><a [routerLink]="['/customers', l.customerId]">{{ l.customerName }}</a></td>
                    <td>{{ l.installationName }} <span class="sd-xs sd-muted">{{ l.productName }}</span></td>
                    <td>{{ l.edition ?? '—' }}</td>
                    <td class="num">{{ l.seats }}</td>
                    <td class="nowrap">
                      {{ l.expiresOn | deskDate }}
                      @if (l.daysToExpiry !== null && l.daysToExpiry <= 90 && l.status === 'Active') {
                        <sd-badge [tone]="urgencyTone(l.daysToExpiry)" size="sm">{{ l.daysToExpiry }} d</sd-badge>
                      }
                    </td>
                    <td><sd-badge [tone]="statusTone(l.status)" size="sm" dot>{{ l.status }}</sd-badge></td>
                    <td class="row-actions">
                      @if (canManage) {
                        <button sdButton size="sm" variant="ghost" icon="pencil" iconOnly aria-label="Edit licence" (click)="edit(l)"></button>
                        <button sdButton size="sm" variant="ghost" icon="trash" iconOnly aria-label="Delete licence" (click)="remove(l)"></button>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </div>
    </div>
  `,
  styles: [TABLE_STYLES, SECTION_STYLES, `.toolbar { display: flex; gap: var(--sd-space-2); flex-wrap: wrap; margin-bottom: var(--sd-space-3); } .f { width: 210px; }`],
})
export class LicencesPage {
  private readonly api = inject(InstallationsApi);
  private readonly dialog = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  protected readonly canManage = inject(MeStore).can('products.manage');
  protected readonly tabs = PRODUCT_TABS;
  protected readonly statuses = LICENCE_STATUSES;
  protected readonly statusTone = statusTone;
  protected readonly urgencyTone = urgencyTone;
  protected readonly status = signal('');
  protected readonly expiring = signal<number | null>(null);
  protected readonly rows = signal<LicenceDto[]>([]);
  protected readonly loading = signal(true);

  constructor() {
    effect(() => {
      const status = this.status();
      const days = this.expiring();
      untracked(() => this.load(status, days));
    });
  }

  private load(status: string, days: number | null): void {
    this.loading.set(true);
    this.api.licences({ status: status || null, expiringWithinDays: days, limit: 500 }).subscribe({
      next: (r) => {
        this.rows.set(r);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  protected edit(licence: LicenceDto | null): void {
    this.dialog
      .open<LicenceDto, LicenceFormData>(LicenceFormDialog, { data: { licence }, size: 'md' })
      .closed.subscribe((saved) => {
        if (!saved) return;
        this.toast.success('Licence saved', saved.keyMasked);
        this.load(this.status(), this.expiring());
      });
  }

  protected async remove(l: LicenceDto): Promise<void> {
    if (!(await this.dialog.confirm({ title: 'Delete this licence?', message: l.keyMasked, danger: true, confirmLabel: 'Delete' }))) return;
    this.api.deleteLicence(l.id).subscribe(() => this.load(this.status(), this.expiring()));
  }
}

/** /products/assets — serialised customer assets with warranty status. */
@Component({
  selector: 'desk-assets-page',
  imports: [FormsModule, RouterLink, SdPageHeader, SdButton, SdBadge, SdIcon, SdInput, SdSkeleton, SdEmptyState, DateOnlyPipe, HumanizePipe, ModuleTabsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Assets" subtitle="Hardware and other serialised items at customer sites, with warranties." icon="devices">
        @if (canManage) {
          <button sdButton sdPageActions variant="primary" icon="plus" (click)="edit(null)">New asset</button>
        }
      </sd-page-header>
      <desk-module-tabs [tabs]="tabs" ariaLabel="Products & Assets sections" />
      <div class="desk-page__body">
        <div class="toolbar">
          <label class="search">
            <sd-icon name="search" [size]="16" />
            <input sdInput type="search" placeholder="Search name, serial or tag" aria-label="Search assets" [ngModel]="q()" (ngModelChange)="onSearch($event)" />
          </label>
          <select sdInput size="sm" class="f" aria-label="Warranty" [ngModel]="warranty()" (ngModelChange)="warranty.set($event)">
            <option value="">Any warranty</option>
            <option value="active">Under warranty</option>
            <option value="expired">Warranty expired</option>
          </select>
        </div>
        @if (loading()) {
          <sd-skeleton [lines]="6" />
        } @else if (rows().length === 0) {
          <sd-empty-state icon="devices" title="No assets match" />
        } @else {
          <div class="dt-wrap">
            <table class="dt">
              <thead>
                <tr>
                  <th scope="col">Asset</th>
                  <th scope="col">Serial / tag</th>
                  <th scope="col">Customer</th>
                  <th scope="col">Installation</th>
                  <th scope="col">Purchased</th>
                  <th scope="col">Warranty</th>
                  <th scope="col">Status</th>
                  <th scope="col"><span class="sd-visually-hidden">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                @for (a of rows(); track a.id) {
                  <tr>
                    <td>{{ a.name }}<div class="sd-xs sd-muted">{{ a.productName }}</div></td>
                    <td class="sd-mono sd-small">{{ a.serialNumber ?? '—' }}@if (a.assetTag) { <div class="sd-xs sd-muted">{{ a.assetTag }}</div> }</td>
                    <td><a [routerLink]="['/customers', a.customerId]">{{ a.customerName }}</a></td>
                    <td>{{ a.installationName ?? '—' }}</td>
                    <td class="nowrap">{{ a.purchaseDate | deskDate }}</td>
                    <td class="nowrap">
                      <sd-badge [tone]="a.underWarranty ? 'success' : a.warrantyEndsOn ? 'danger' : 'neutral'" size="sm">
                        {{ a.warrantyEndsOn ? (a.underWarranty ? 'Until ' : 'Ended ') + (a.warrantyEndsOn | deskDate) : 'None' }}
                      </sd-badge>
                    </td>
                    <td>{{ a.status | deskHumanize }}</td>
                    <td class="row-actions">
                      @if (canManage) {
                        <button sdButton size="sm" variant="ghost" icon="pencil" iconOnly aria-label="Edit asset" (click)="edit(a)"></button>
                        <button sdButton size="sm" variant="ghost" icon="trash" iconOnly aria-label="Delete asset" (click)="remove(a)"></button>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </div>
    </div>
  `,
  styles: [
    TABLE_STYLES,
    SECTION_STYLES,
    `
      .toolbar { display: flex; gap: var(--sd-space-2); flex-wrap: wrap; margin-bottom: var(--sd-space-3); }
      .f { width: 200px; }
      .search { position: relative; flex: 1 1 240px; max-width: 360px; }
      .search sd-icon { position: absolute; left: 10px; top: 50%; transform: translateY(-50%); color: var(--sd-text-3); }
      .search input { padding-left: 32px; }
    `,
  ],
})
export class AssetsPage {
  private readonly api = inject(InstallationsApi);
  private readonly dialog = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  protected readonly canManage = inject(MeStore).can('products.manage');
  protected readonly tabs = PRODUCT_TABS;
  protected readonly q = signal('');
  protected readonly warranty = signal('');
  protected readonly rows = signal<AssetDto[]>([]);
  protected readonly loading = signal(true);
  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    effect(() => {
      const q = this.q();
      const w = this.warranty();
      untracked(() => this.load(q, w));
    });
  }

  private load(q: string, warranty: string): void {
    this.loading.set(true);
    this.api.assets({ q: q || null, warrantyExpired: warranty === '' ? null : warranty === 'expired', limit: 500 }).subscribe({
      next: (r) => {
        this.rows.set(r);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  protected onSearch(value: string): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => this.q.set(value), 250);
  }

  protected edit(asset: AssetDto | null): void {
    this.dialog
      .open<AssetDto, AssetFormData>(AssetFormDialog, { data: { asset }, size: 'md' })
      .closed.subscribe((saved) => {
        if (!saved) return;
        this.toast.success('Asset saved', saved.name);
        this.load(this.q(), this.warranty());
      });
  }

  protected async remove(a: AssetDto): Promise<void> {
    if (!(await this.dialog.confirm({ title: `Delete ${a.name}?`, message: 'Assets referenced by tickets or entitlements are retired instead.', danger: true, confirmLabel: 'Delete' }))) return;
    this.api.deleteAsset(a.id).subscribe(() => this.load(this.q(), this.warranty()));
  }
}
