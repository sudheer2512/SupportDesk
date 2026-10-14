import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import {
  DEPLOYMENT_MODES,
  INSTALLATION_ENVIRONMENTS,
  InstallationDetailDto,
  InstallationListItemDto,
  InstallationsApi,
  MeStore,
  ProductDto,
  ProductsApi,
  cursorList,
  saveErrorMessage,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCellDef,
  SdColumn,
  SdDataTable,
  SdDialogService,
  SdDrawer,
  SdIcon,
  SdInput,
  SdPageHeader,
  SdSkeleton,
  SdToggle,
  ToastService,
} from '@sd/ui';
import { DateOnlyPipe, HumanizePipe, humanize, statusTone } from '../customers/shared/customer-ui';
import { ModuleTabsComponent, PRODUCT_TABS } from '../customers/shared/module-tabs.component';
import { SECTION_STYLES, TABLE_STYLES } from '../customers/shared/styles';
import { InstallationFormData, InstallationFormDialog } from './installation-form.dialog';

/** /products/installations — installations browser with environment details (drawer), filters by product/version/environment. */
@Component({
  selector: 'desk-installations-page',
  imports: [
    FormsModule,
    RouterLink,
    SdPageHeader,
    SdButton,
    SdDataTable,
    SdCellDef,
    SdBadge,
    SdIcon,
    SdInput,
    SdToggle,
    SdDrawer,
    SdSkeleton,
    DateOnlyPipe,
    HumanizePipe,
    ModuleTabsComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Installations" subtitle="Where each product runs: version, environment, deployment and infrastructure." icon="server">
        @if (canManage) {
          <button sdButton sdPageActions variant="primary" icon="plus" (click)="edit(null)">New installation</button>
        }
      </sd-page-header>
      <desk-module-tabs [tabs]="tabs" ariaLabel="Products & Assets sections" />
      <div class="desk-page__body">
        <div class="toolbar">
          <label class="search">
            <sd-icon name="search" [size]="16" />
            <input sdInput type="search" placeholder="Search installation, code, customer, OS or database" aria-label="Search installations" [ngModel]="q()" (ngModelChange)="onSearch($event)" />
          </label>
          <select sdInput size="sm" class="f" aria-label="Product" [ngModel]="product()" (ngModelChange)="product.set($event); version.set('')">
            <option value="">All products</option>
            @for (p of products(); track p.id) {
              <option [value]="p.id">{{ p.name }}</option>
            }
          </select>
          <select sdInput size="sm" class="f" aria-label="Version" [ngModel]="version()" (ngModelChange)="version.set($event)" [disabled]="!product()">
            <option value="">All versions</option>
            @for (v of versions(); track v.id) {
              <option [value]="v.id">{{ v.version }}</option>
            }
          </select>
          <select sdInput size="sm" class="f" aria-label="Environment" [ngModel]="environment()" (ngModelChange)="environment.set($event)">
            <option value="">All environments</option>
            @for (e of environments; track e) {
              <option [value]="e">{{ e }}</option>
            }
          </select>
          <select sdInput size="sm" class="f" aria-label="Deployment" [ngModel]="mode()" (ngModelChange)="mode.set($event)">
            <option value="">All deployments</option>
            @for (m of modes; track m) {
              <option [value]="m">{{ humanize(m) }}</option>
            }
          </select>
          <sd-toggle [value]="eos()" (valueChange)="eos.set($event)">End of support only</sd-toggle>
        </div>
        <div class="table-host">
          <sd-data-table
            [columns]="columns"
            [rows]="list.items()"
            storageKey="installations.list"
            [loading]="list.loading()"
            [loadingMore]="list.loadingMore()"
            [hasMore]="list.hasMore()"
            [total]="list.total()"
            [error]="list.error()?.userMessage ?? null"
            [activeKey]="openId()"
            emptyTitle="No installations match"
            emptyIcon="server"
            ariaLabel="Installations"
            (loadMore)="list.loadMore()"
            (rowClick)="open($event.id)"
          >
            <ng-template sdCell="name" [sdCellRows]="list.items()" let-row>
              <span class="sd-truncate"><strong>{{ row.name }}</strong>&ngsp;&ngsp;<span class="sd-xs sd-muted">{{ row.installationCode }}</span></span>
            </ng-template>
            <ng-template sdCell="version" [sdCellRows]="list.items()" let-row>
              <span>{{ row.productName }} {{ row.release ?? row.version ?? '' }}</span>
              @if (row.versionEndOfSupport) {
                <sd-badge tone="danger" size="sm">EOS</sd-badge>
              }
            </ng-template>
            <ng-template sdCell="environment" [sdCellRows]="list.items()" let-row>
              <sd-badge [tone]="row.environment === 'Production' ? 'primary' : 'neutral'" size="sm">{{ row.environment }}</sd-badge>
            </ng-template>
            <ng-template sdCell="deploymentMode" [sdCellRows]="list.items()" let-row>{{ row.deploymentMode | deskHumanize }}</ng-template>
            <ng-template sdCell="status" [sdCellRows]="list.items()" let-row>
              <sd-badge [tone]="statusTone(row.status)" size="sm" dot>{{ row.status }}</sd-badge>
            </ng-template>
            <ng-template sdCell="goLiveOn" [sdCellRows]="list.items()" let-row>{{ row.goLiveOn | deskDate }}</ng-template>
          </sd-data-table>
        </div>
      </div>
    </div>

    <sd-drawer [(open)]="drawerOpen" [title]="detail()?.summary?.name ?? 'Installation'" width="560px">
      @if (!detail()) {
        <sd-skeleton [lines]="8" />
      } @else {
        @let d = detail()!;
        <div class="drawer-body">
          <div class="badges">
            <sd-badge [tone]="statusTone(d.summary.status)" dot>{{ d.summary.status }}</sd-badge>
            <sd-badge [tone]="d.summary.environment === 'Production' ? 'primary' : 'neutral'">{{ d.summary.environment }}</sd-badge>
            <sd-badge variant="outline">{{ d.summary.deploymentMode | deskHumanize }}</sd-badge>
            @if (d.summary.versionEndOfSupport) {
              <sd-badge tone="danger">Version past end of support</sd-badge>
            }
            @if (d.erpInstallationId) {
              <sd-badge tone="info" variant="outline" icon="plug">ERP</sd-badge>
            }
          </div>
          <dl class="meta">
            <dt>Customer</dt>
            <dd><a [routerLink]="['/customers', d.summary.customerId]">{{ d.summary.customerName }}</a>@if (d.summary.locationName) { · {{ d.summary.locationName }} }</dd>
            <dt>Product</dt>
            <dd>{{ d.summary.productName }} {{ d.summary.version ?? '' }}@if (d.summary.release) { (release {{ d.summary.release }}) }</dd>
            <dt>Code</dt>
            <dd class="sd-mono">{{ d.summary.installationCode ?? '—' }}</dd>
            <dt>Operating system</dt>
            <dd>{{ d.operatingSystem ?? '—' }}</dd>
            <dt>Database</dt>
            <dd>{{ d.databaseVersion ?? '—' }}</dd>
            <dt>Server</dt>
            <dd>{{ d.serverConfiguration ?? '—' }}</dd>
            <dt>Users</dt>
            <dd>{{ d.summary.userCount ?? '—' }}</dd>
            <dt>Installed / go-live</dt>
            <dd>{{ d.installedOn | deskDate }} / {{ d.summary.goLiveOn | deskDate }}</dd>
            <dt>Tickets</dt>
            <dd>{{ d.summary.openTickets }} open · {{ d.ticketsLast90Days }} in 90 days</dd>
          </dl>
          <h3 class="sub">Enabled modules</h3>
          <p class="sd-small">{{ moduleNames(d) }}</p>
          <h3 class="sub">Integrations</h3>
          @if (d.integrations.length === 0) {
            <p class="sd-small sd-muted">None recorded.</p>
          } @else {
            <ul class="ints">
              @for (i of d.integrations; track $index) {
                <li class="sd-small"><sd-icon name="plug" [size]="12" /> {{ i.system }} <span class="sd-muted">{{ i.direction }}</span></li>
              }
            </ul>
          }
          <h3 class="sub">Licences</h3>
          @if (d.licences.length === 0) {
            <p class="sd-small sd-muted">No licences.</p>
          } @else {
            <div class="dt-wrap">
              <table class="dt">
                <tbody>
                  @for (l of d.licences; track l.id) {
                    <tr>
                      <td class="sd-mono">{{ l.keyMasked }}</td>
                      <td>{{ l.edition }} · {{ l.seats }} seats</td>
                      <td class="nowrap">{{ l.expiresOn | deskDate }}</td>
                      <td><sd-badge [tone]="statusTone(l.status)" size="sm">{{ l.status }}</sd-badge></td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
          @if (d.assets.length) {
            <h3 class="sub">Assets</h3>
            <ul class="ints">
              @for (a of d.assets; track a.id) {
                <li class="sd-small">{{ a.name }} <span class="sd-mono sd-muted">{{ a.serialNumber }}</span></li>
              }
            </ul>
          }
          @if (d.notes) {
            <h3 class="sub">Notes</h3>
            <p class="sd-small">{{ d.notes }}</p>
          }
        </div>
      }
      <div sdDrawerFooter class="foot">
        @if (canManage && detail()) {
          <button sdButton icon="pencil" (click)="editOpen()">Edit</button>
          @if (detail()!.summary.status !== 'Decommissioned') {
            <button sdButton variant="ghost" icon="trash" (click)="removeOpen()">Delete</button>
          }
        }
        @if (detail(); as dd) {
          <a sdButton variant="ghost" icon="building" [routerLink]="['/customers', dd.summary.customerId]">Customer 360</a>
        }
      </div>
    </sd-drawer>
  `,
  styles: [
    TABLE_STYLES,
    SECTION_STYLES,
    `
      .toolbar {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sd-space-2);
        margin-bottom: var(--sd-space-3);
      }
      .search {
        position: relative;
        flex: 1 1 260px;
        max-width: 360px;
      }
      .search sd-icon {
        position: absolute;
        left: 10px;
        top: 50%;
        transform: translateY(-50%);
        color: var(--sd-text-3);
      }
      .search input {
        padding-left: 32px;
      }
      .f {
        width: 160px;
      }
      .table-host {
        height: max(420px, calc(100dvh - 330px));
        border: 1px solid var(--sd-border);
        border-radius: var(--sd-radius-lg);
        overflow: hidden;
        background: var(--sd-surface);
      }
      .drawer-body {
        display: grid;
        gap: var(--sd-space-3);
      }
      .badges {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
      }
      .sub {
        font-size: var(--sd-text-sm);
        font-weight: var(--sd-weight-semibold);
        margin-top: var(--sd-space-2);
      }
      .ints {
        display: grid;
        gap: 4px;
        list-style: none;
        padding: 0;
      }
      .meta a {
        color: var(--sd-primary);
      }
      .foot {
        display: flex;
        gap: var(--sd-space-2);
      }
    `,
  ],
})
export class InstallationsPage {
  /** Deep links: ?id= opens an installation, ?productId= / ?customerId= pre-filter. */
  readonly id = input<string | null>(null);
  readonly productId = input<string | null>(null);
  readonly versionId = input<string | null>(null);
  readonly customerId = input<string | null>(null);

  private readonly api = inject(InstallationsApi);
  private readonly productsApi = inject(ProductsApi);
  private readonly dialog = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  protected readonly canManage = inject(MeStore).can('products.manage');
  protected readonly tabs = PRODUCT_TABS;
  protected readonly humanize = humanize;
  protected readonly statusTone = statusTone;
  protected readonly environments = INSTALLATION_ENVIRONMENTS;
  protected readonly modes = DEPLOYMENT_MODES;

  protected readonly q = signal('');
  protected readonly product = signal('');
  protected readonly version = signal('');
  protected readonly environment = signal('');
  protected readonly mode = signal('');
  protected readonly eos = signal(false);
  protected readonly products = signal<ProductDto[]>([]);
  protected readonly versions = computed(() => this.products().find((p) => p.id === this.product())?.versions ?? []);
  protected readonly drawerOpen = signal(false);
  protected readonly openId = signal<string | null>(null);
  protected readonly detail = signal<InstallationDetailDto | null>(null);
  private timer: ReturnType<typeof setTimeout> | null = null;

  protected readonly columns: SdColumn<InstallationListItemDto>[] = [
    { key: 'name', header: 'Installation', flex: 2, minWidth: 220, locked: true },
    { key: 'customerName', header: 'Customer', flex: 2, minWidth: 160 },
    { key: 'version', header: 'Product / version', flex: 1, minWidth: 160 },
    { key: 'environment', header: 'Environment', width: 120 },
    { key: 'deploymentMode', header: 'Deployment', width: 120 },
    { key: 'status', header: 'Status', width: 120 },
    { key: 'userCount', header: 'Users', width: 80, align: 'end' },
    { key: 'openTickets', header: 'Open', width: 70, align: 'end' },
    { key: 'goLiveOn', header: 'Go-live', width: 120, hiddenByDefault: true },
    { key: 'locationName', header: 'Location', flex: 1, minWidth: 120, hiddenByDefault: true },
  ];

  private readonly query = computed(() => ({
    q: this.q() || null,
    productId: this.product() || null,
    versionId: this.version() || null,
    environment: this.environment() || null,
    deploymentMode: this.mode() || null,
    endOfSupport: this.eos() || null,
    customerId: this.customerId(),
  }));

  protected readonly list = cursorList<InstallationListItemDto>((cursor) => this.api.list({ ...this.query(), cursor, limit: 50, includeTotal: !cursor }));

  constructor() {
    this.productsApi.list(true).subscribe((p) => this.products.set(p));
    effect(() => {
      const pid = this.productId();
      const vid = this.versionId();
      untracked(() => {
        if (pid) this.product.set(pid);
        if (vid) this.version.set(vid);
      });
    });
    effect(() => {
      this.query();
      untracked(() => this.list.load());
    });
    effect(() => {
      const id = this.id();
      untracked(() => id && this.open(id));
    });
    effect(() => {
      if (!this.drawerOpen() && untracked(() => this.id())) void this.router.navigate([], { queryParams: { id: null }, queryParamsHandling: 'merge', replaceUrl: true });
    });
  }

  protected onSearch(value: string): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => this.q.set(value), 250);
  }

  protected open(id: string): void {
    this.openId.set(id);
    this.detail.set(null);
    this.drawerOpen.set(true);
    this.api.get(id).subscribe((d) => this.detail.set(d));
  }

  protected moduleNames(d: InstallationDetailDto): string {
    return d.enabledModules.map((m) => m.name).join(', ') || 'None recorded';
  }

  protected edit(installation: InstallationDetailDto | null): void {
    this.dialog
      .open<InstallationDetailDto, InstallationFormData>(InstallationFormDialog, { data: { installation }, size: 'lg', disableClose: true })
      .closed.subscribe((saved) => {
        if (!saved) return;
        this.toast.success('Installation saved', saved.summary.name);
        this.list.load();
        if (this.drawerOpen()) this.detail.set(saved);
      });
  }

  protected editOpen(): void {
    const d = this.detail();
    if (d) this.edit(d);
  }

  /** DELETE /installations/{id}: installations referenced by tickets or entitlements are decommissioned instead. */
  protected async removeOpen(): Promise<void> {
    const d = this.detail();
    if (!d) return;
    const referenced = d.summary.openTickets > 0 || d.ticketsLast90Days > 0;
    const ok = await this.dialog.confirm({
      title: `Delete ${d.summary.name}?`,
      message: referenced
        ? 'Tickets reference this installation, so it will be marked Decommissioned instead of deleted (history stays intact).'
        : 'If tickets or contract entitlements reference it, it is marked Decommissioned instead; otherwise it is deleted and its assets are unlinked.',
      confirmLabel: referenced ? 'Decommission' : 'Delete installation',
      danger: true,
    });
    if (!ok) return;
    this.api.delete(d.summary.id).subscribe({
      next: () => {
        this.list.load();
        this.api.get(d.summary.id).subscribe({
          next: (still) => {
            this.detail.set(still);
            this.toast.info('Installation decommissioned', `${d.summary.name} is still referenced, so it was kept for history.`);
          },
          error: () => {
            this.drawerOpen.set(false);
            this.toast.success('Installation deleted', d.summary.name);
          },
        });
      },
      error: (err: unknown) => this.toast.error('Could not delete the installation', saveErrorMessage(err)),
    });
  }
}
