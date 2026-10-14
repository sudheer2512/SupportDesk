import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiError, MeStore, ProductDto, ProductFamilyDto, ProductsApi, saveErrorMessage } from '@sd/core';
import { SdButton, SdDialogService, SdEmptyState, SdPageHeader, SdSkeleton, ToastService } from '@sd/ui';
import { forkJoin } from 'rxjs';
import { ModuleTabsComponent, PRODUCT_TABS } from '../customers/shared/module-tabs.component';
import { NameDescriptionData, NameDescriptionDialog } from '../customers/shared/name-description.dialog';
import { TABLE_STYLES } from '../customers/shared/styles';

/**
 * /products/families — product families (GET/POST/PUT/DELETE /product-families) with their products. products.view to
 * see, products.manage to create, rename and delete; a family that still has products cannot be deleted (move the
 * products to another family from the product form first).
 */
@Component({
  selector: 'desk-product-families-page',
  imports: [RouterLink, SdPageHeader, SdButton, SdSkeleton, SdEmptyState, ModuleTabsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Product families" subtitle="Group related products for reporting, routing and the portal." icon="folder-tree">
        @if (canManage) {
          <button sdButton sdPageActions variant="primary" icon="plus" (click)="edit(null)">New family</button>
        }
      </sd-page-header>
      <desk-module-tabs [tabs]="tabs" ariaLabel="Products & Assets sections" />
      <div class="desk-page__body">
        @if (error()) {
          <sd-empty-state icon="triangle-alert" title="Product families could not be loaded" [message]="error()">
            <button sdButton icon="refresh" (click)="load()">Try again</button>
          </sd-empty-state>
        } @else if (loading()) {
          <sd-skeleton [lines]="5" />
        } @else if (families().length === 0) {
          <sd-empty-state icon="folder-tree" title="No product families yet" message="Families group related products, for example by product line.">
            @if (canManage) {
              <button sdButton variant="primary" icon="plus" (click)="edit(null)">New family</button>
            }
          </sd-empty-state>
        } @else {
          <div class="dt-wrap">
            <table class="dt">
              <caption class="sd-visually-hidden">Product families</caption>
              <thead>
                <tr>
                  <th scope="col">Family</th>
                  <th scope="col" class="hide-sm">Products</th>
                  <th scope="col" class="num">Count</th>
                  @if (canManage) {
                    <th scope="col"><span class="sd-visually-hidden">Actions</span></th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (f of families(); track f.id) {
                  <tr>
                    <td>
                      <strong>{{ f.name }}</strong>
                      @if (f.description) {
                        <span class="sd-xs sd-muted block">{{ f.description }}</span>
                      }
                    </td>
                    <td class="hide-sm">
                      @for (p of productsOf(f.id); track p.id; let last = $last) {
                        <a routerLink="/products" class="plink">{{ p.name }}</a>{{ last ? '' : ', ' }}
                      } @empty {
                        <span class="sd-muted">—</span>
                      }
                    </td>
                    <td class="num">{{ f.productCount }}</td>
                    @if (canManage) {
                      <td class="row-actions">
                        <button sdButton size="sm" variant="ghost" icon="pencil" iconOnly [attr.aria-label]="'Edit ' + f.name" (click)="edit(f)"></button>
                        <button sdButton size="sm" variant="ghost" icon="trash" iconOnly [attr.aria-label]="'Delete ' + f.name" (click)="remove(f)"></button>
                      </td>
                    }
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
    `
      .block {
        display: block;
      }
      .plink {
        color: var(--sd-primary);
      }
      @media (max-width: 640px) {
        .hide-sm {
          display: none;
        }
      }
    `,
  ],
})
export class ProductFamiliesPage {
  private readonly api = inject(ProductsApi);
  private readonly dialog = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  protected readonly canManage = inject(MeStore).can('products.manage');
  protected readonly tabs = PRODUCT_TABS;

  protected readonly families = signal<ProductFamilyDto[]>([]);
  private readonly products = signal<ProductDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  private readonly byFamily = computed(() => {
    const map = new Map<string, ProductDto[]>();
    for (const p of this.products()) if (p.familyId) map.set(p.familyId, [...(map.get(p.familyId) ?? []), p]);
    return map;
  });

  constructor() {
    this.load();
  }

  protected productsOf(familyId: string): ProductDto[] {
    return this.byFamily().get(familyId) ?? [];
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    forkJoin({ families: this.api.families(), products: this.api.list(true) }).subscribe({
      next: ({ families, products }) => {
        this.families.set(families);
        this.products.set(products);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.error.set(ApiError.from(err).userMessage);
      },
    });
  }

  protected edit(family: ProductFamilyDto | null): void {
    if (!this.canManage) return;
    this.dialog
      .open<ProductFamilyDto, NameDescriptionData<ProductFamilyDto>>(NameDescriptionDialog, {
        data: {
          title: family ? `Edit ${family.name}` : 'New product family',
          name: family?.name,
          text: family?.description,
          nameLabel: 'Family name',
          confirmLabel: family ? 'Save' : 'Create family',
          save: (name, description) => this.api.saveFamily(family?.id ?? null, name, description),
        },
        size: 'sm',
      })
      .closed.subscribe((saved) => {
        if (!saved) return;
        this.toast.success(family ? 'Family saved' : 'Family created', saved.name);
        this.load();
      });
  }

  protected async remove(family: ProductFamilyDto): Promise<void> {
    if (family.productCount > 0) {
      this.toast.warning(
        `${family.name} still has ${family.productCount} product${family.productCount === 1 ? '' : 's'}`,
        'Move them to another family (edit the product) before deleting the family.',
      );
      return;
    }
    const ok = await this.dialog.confirm({ title: `Delete ${family.name}?`, message: 'The family has no products.', confirmLabel: 'Delete family', danger: true });
    if (!ok) return;
    this.api.deleteFamily(family.id).subscribe({
      next: () => {
        this.toast.success('Family deleted', family.name);
        this.load();
      },
      error: (err: unknown) => this.toast.error('Could not delete the family', saveErrorMessage(err)),
    });
  }
}
