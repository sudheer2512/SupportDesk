import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MeStore, ProductDto, ProductFamilyDto, ProductReleaseDto, ProductVersionDto, ProductsApi, saveErrorMessage } from '@sd/core';
import { SdBadge, SdButton, SdCard, SdDialogService, SdEmptyState, SdPageHeader, SdSkeleton, ToastService } from '@sd/ui';
import { DateOnlyPipe, HumanizePipe, statusTone } from '../customers/shared/customer-ui';
import { ModuleTabsComponent, PRODUCT_TABS } from '../customers/shared/module-tabs.component';
import { SECTION_STYLES, TABLE_STYLES } from '../customers/shared/styles';
import { TextPromptData, TextPromptDialog } from '../customers/shared/text-prompt.dialog';
import { ProductFormData, ProductFormDialog, ReleaseFormData, ReleaseFormDialog, VersionFormData, VersionFormDialog } from './catalog-forms.dialog';

/** /products — product catalog: families, products, modules, versions (status, end of support) and releases. */
@Component({
  selector: 'desk-catalog-page',
  imports: [RouterLink, SdPageHeader, SdButton, SdBadge, SdCard, SdSkeleton, SdEmptyState, DateOnlyPipe, HumanizePipe, ModuleTabsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Products & Assets" subtitle="Product catalog, versions and releases used to classify and route tickets." icon="package">
        @if (canManage) {
          <button sdButton sdPageActions variant="primary" icon="plus" (click)="editProduct(null)">New product</button>
        }
      </sd-page-header>
      <desk-module-tabs [tabs]="tabs" ariaLabel="Products & Assets sections" />
      <div class="desk-page__body">
        @if (loading()) {
          <sd-skeleton [lines]="8" />
        } @else if (products().length === 0) {
          <sd-empty-state icon="package" title="No products yet" message="Add the products you support to classify tickets and track installations." />
        } @else {
          <div class="layout">
            <nav class="plist" aria-label="Products">
              @for (group of grouped(); track group.family) {
                <h2 class="fam">{{ group.family }}</h2>
                @for (p of group.products; track p.id) {
                  <button type="button" class="pitem" [class.is-active]="p.id === selected()?.id" [attr.aria-current]="p.id === selected()?.id ? 'true' : null" (click)="selectedId.set(p.id)">
                    <span class="pname">{{ p.name }} <span class="sd-xs sd-muted sd-mono">{{ p.code }}</span></span>
                    <span class="sd-xs sd-muted">{{ p.installationCount }} installations · {{ p.customerCount }} customers</span>
                    @if (!p.isActive) {
                      <sd-badge size="sm">Retired</sd-badge>
                    }
                  </button>
                }
              }
            </nav>
            @if (selected(); as p) {
              <section class="detail" [attr.aria-label]="p.name">
                <sd-card [title]="p.name" [subtitle]="p.description">
                  <div sdCardActions>
                    @if (canManage) {
                      <button sdButton size="sm" icon="pencil" (click)="editProduct(p)">Edit</button>
                    }
                  </div>
                  <dl class="meta">
                    <dt>Code</dt>
                    <dd class="sd-mono">{{ p.code }}</dd>
                    <dt>Family</dt>
                    <dd>{{ p.familyName ?? '—' }}</dd>
                    <dt>Routing</dt>
                    <dd>{{ p.defaultDepartmentName ?? 'Brand / default department' }}@if (p.defaultTeamName) { · {{ p.defaultTeamName }} }</dd>
                    <dt>Install base</dt>
                    <dd>
                      <a [routerLink]="['/products/installations']" [queryParams]="{ productId: p.id }">{{ p.installationCount }} installations</a>
                      at {{ p.customerCount }} customers · {{ p.openTickets }} open tickets
                    </dd>
                  </dl>
                </sd-card>

                <div class="section-head">
                  <h2>Modules</h2>
                  <span class="sd-spacer"></span>
                  @if (canManage) {
                    <button sdButton size="sm" icon="plus" (click)="addModule(p)">Add module</button>
                  }
                </div>
                @if (p.modules.length === 0) {
                  <p class="empty-note">No modules defined.</p>
                } @else {
                  <ul class="chips">
                    @for (m of p.modules; track m.id) {
                      <li [class.off]="!m.isActive">
                        {{ m.name }}
                        @if (canManage) {
                          <button sdButton size="sm" variant="ghost" icon="x" iconOnly [attr.aria-label]="'Remove ' + m.name" (click)="removeModule(p, m.id, m.name)"></button>
                        }
                      </li>
                    }
                  </ul>
                }

                <div class="section-head">
                  <h2>Versions &amp; releases</h2>
                  <span class="sd-spacer"></span>
                  @if (canManage) {
                    <button sdButton size="sm" icon="plus" (click)="editVersion(p, null)">Add version</button>
                  }
                </div>
                @if (p.versions.length === 0) {
                  <p class="empty-note">No versions defined.</p>
                } @else {
                  <div class="dt-wrap">
                    <table class="dt">
                      <thead>
                        <tr>
                          <th scope="col">Version</th>
                          <th scope="col">Status</th>
                          <th scope="col">Released</th>
                          <th scope="col">End of support</th>
                          <th scope="col" class="num">Installations</th>
                          <th scope="col">Releases</th>
                          <th scope="col"><span class="sd-visually-hidden">Actions</span></th>
                        </tr>
                      </thead>
                      <tbody>
                        @for (v of p.versions; track v.id) {
                          <tr>
                            <td><strong>{{ v.version }}</strong></td>
                            <td>
                              <sd-badge [tone]="statusTone(v.status)" size="sm" dot>{{ v.status | deskHumanize }}</sd-badge>
                              @if (v.isEndOfSupport && v.status !== 'EndOfLife') {
                                <sd-badge tone="danger" size="sm">Past end of support</sd-badge>
                              }
                            </td>
                            <td class="nowrap">{{ v.releaseDate | deskDate }}</td>
                            <td class="nowrap">{{ v.endOfSupportDate | deskDate }}</td>
                            <td class="num">{{ v.installationCount }}</td>
                            <td>
                              <ul class="rels">
                                @for (r of v.releases; track r.id) {
                                  <li>
                                    <button type="button" class="rel" (click)="editRelease(p, v, r)" [title]="r.releaseNotes ?? ''">
                                      {{ r.name }}@if (r.buildNumber) { <span class="sd-xs sd-muted"> b{{ r.buildNumber }}</span> }
                                    </button>
                                    <span class="sd-xs sd-muted">{{ r.type }} · {{ r.installationCount }} inst.</span>
                                  </li>
                                }
                              </ul>
                            </td>
                            <td class="row-actions">
                              @if (canManage) {
                                <button sdButton size="sm" variant="ghost" icon="plus" iconOnly [attr.aria-label]="'Add release to ' + v.version" (click)="editRelease(p, v, null)"></button>
                                <button sdButton size="sm" variant="ghost" icon="pencil" iconOnly [attr.aria-label]="'Edit ' + v.version" (click)="editVersion(p, v)"></button>
                              }
                            </td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  </div>
                }
              </section>
            }
          </div>
        }
      </div>
    </div>
  `,
  styles: [
    TABLE_STYLES,
    SECTION_STYLES,
    `
      .layout {
        display: grid;
        gap: var(--sd-space-5);
        grid-template-columns: 280px minmax(0, 1fr);
      }
      @media (max-width: 900px) {
        .layout {
          grid-template-columns: 1fr;
        }
      }
      .plist {
        display: grid;
        gap: 4px;
        align-content: start;
      }
      .fam {
        font-size: var(--sd-text-xs);
        text-transform: uppercase;
        letter-spacing: 0.04em;
        color: var(--sd-text-3);
        margin: var(--sd-space-2) 0 2px;
      }
      .pitem {
        display: grid;
        gap: 2px;
        text-align: left;
        padding: 10px 12px;
        border: 1px solid var(--sd-border);
        border-radius: var(--sd-radius-md);
        background: var(--sd-surface);
        color: var(--sd-text);
        cursor: pointer;
        font: inherit;
      }
      .pitem:hover {
        background: var(--sd-row-hover);
      }
      .pitem.is-active {
        border-color: var(--sd-primary);
        box-shadow: inset 3px 0 0 var(--sd-primary);
      }
      .pitem:focus-visible {
        outline: none;
        box-shadow: var(--sd-focus-ring);
      }
      .pname {
        font-weight: var(--sd-weight-medium);
      }
      .chips {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
        list-style: none;
        padding: 0;
      }
      .chips li {
        display: inline-flex;
        align-items: center;
        gap: 2px;
        padding: 2px 4px 2px 10px;
        border: 1px solid var(--sd-border);
        border-radius: var(--sd-radius-pill);
        background: var(--sd-surface-2);
        font-size: var(--sd-text-sm);
      }
      .chips li.off {
        opacity: 0.6;
      }
      .rels {
        display: grid;
        gap: 2px;
        list-style: none;
        padding: 0;
      }
      .rel {
        border: 0;
        background: none;
        padding: 0;
        color: var(--sd-primary);
        cursor: pointer;
        font: inherit;
      }
      .detail sd-card {
        display: flex;
      }
      .meta a {
        color: var(--sd-primary);
      }
    `,
  ],
})
export class CatalogPage {
  private readonly api = inject(ProductsApi);
  private readonly dialog = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  protected readonly canManage = inject(MeStore).can('products.manage');
  protected readonly tabs = PRODUCT_TABS;
  protected readonly statusTone = statusTone;

  protected readonly products = signal<ProductDto[]>([]);
  protected readonly families = signal<ProductFamilyDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly selectedId = signal<string | null>(null);
  protected readonly selected = computed<ProductDto | null>(() => this.products().find((p) => p.id === this.selectedId()) ?? this.products()[0] ?? null);
  protected readonly grouped = computed(() => {
    const map = new Map<string, ProductDto[]>();
    for (const p of this.products()) {
      const key = p.familyName ?? 'Other products';
      map.set(key, [...(map.get(key) ?? []), p]);
    }
    return [...map.entries()].map(([family, products]) => ({ family, products }));
  });

  constructor() {
    this.load();
    this.api.families().subscribe((f) => this.families.set(f));
  }

  private load(): void {
    this.api.list(true).subscribe({
      next: (p) => {
        this.products.set(p);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  protected editProduct(product: ProductDto | null): void {
    this.dialog
      .open<ProductDto, ProductFormData>(ProductFormDialog, { data: { product, families: this.families() }, size: 'md' })
      .closed.subscribe((saved) => {
        if (!saved) return;
        this.toast.success('Product saved', saved.name);
        this.selectedId.set(saved.id);
        this.load();
      });
  }

  protected addModule(p: ProductDto): void {
    this.dialog
      .open<string, TextPromptData>(TextPromptDialog, { data: { title: `New module for ${p.name}`, label: 'Module name' }, size: 'sm' })
      .closed.subscribe((name) => {
        if (!name) return;
        this.api.saveModule(p.id, null, { name, isActive: true }).subscribe({
          next: () => this.load(),
          error: (err: unknown) => this.toast.error('Could not add the module', saveErrorMessage(err)),
        });
      });
  }

  protected async removeModule(p: ProductDto, id: string, name: string): Promise<void> {
    const ok = await this.dialog.confirm({ title: `Remove ${name}?`, message: 'Modules used by tickets are deactivated instead.', danger: true, confirmLabel: 'Remove' });
    if (ok) this.api.deleteModule(p.id, id).subscribe(() => this.load());
  }

  protected editVersion(p: ProductDto, version: ProductVersionDto | null): void {
    this.dialog
      .open<ProductVersionDto, VersionFormData>(VersionFormDialog, { data: { productId: p.id, version }, size: 'md' })
      .closed.subscribe((saved) => saved && this.load());
  }

  protected editRelease(p: ProductDto, v: ProductVersionDto, release: ProductReleaseDto | null): void {
    if (!this.canManage && !release) return;
    this.dialog
      .open<ProductReleaseDto, ReleaseFormData>(ReleaseFormDialog, { data: { productId: p.id, versionId: v.id, release }, size: 'md' })
      .closed.subscribe((saved) => saved && this.load());
  }
}
