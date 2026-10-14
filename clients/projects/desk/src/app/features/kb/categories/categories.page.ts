import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { KbCategoryDto, KnowledgeApi, ToastBridge } from '@sd/core';
import { SdBadge, SdButton, SdDialogService, SdEmptyState, SdIcon, SdInput, SdSkeleton, hasIcon } from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { KbVisibilityBadge } from '../components/kb-badges.component';
import { CategoryNode, KB_BASE, KB_STYLES, errorMessage, toastedGlobally } from '../kb-common';
import { KbStore } from '../kb.store';
import { CategoryDialog, CategoryDialogData } from './category.dialog';

/** /kb/categories — category tree with article counts, create/edit dialog and delete. */
@Component({
  selector: 'desk-kb-categories',
  imports: [FormsModule, RouterLink, SdButton, SdBadge, SdIcon, SdInput, SdSkeleton, SdEmptyState, KbVisibilityBadge],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section aria-labelledby="kb-cat-title">
      <header class="kb-head">
        <div class="kb-head__titles">
          <h2 id="kb-cat-title">Categories</h2>
          <p class="kb-head__sub">Organise articles into a tree; visibility and brands also apply to the help center.</p>
        </div>
        <div class="kb-head__actions">
          <button sdButton variant="ghost" icon="refresh" type="button" [loading]="store.categoriesLoading()" (click)="refresh()">Refresh</button>
          <button sdButton variant="primary" icon="plus" type="button" (click)="openDialog(null)">New category</button>
        </div>
      </header>

      <div class="kb-toolbar">
        <div class="kb-filter kb-filter--grow">
          <label for="kb-cat-filter">Filter</label>
          <div class="kb-search">
            <sd-icon name="search" [size]="15" />
            <input sdInput id="kb-cat-filter" type="search" placeholder="Category name or slug" [ngModel]="filter()" (ngModelChange)="filter.set($event)" />
          </div>
        </div>
        <p class="sd-small sd-muted count" aria-live="polite">{{ summary() }}</p>
      </div>

      @if (store.categoriesError(); as err) {
        <div class="kb-error" role="alert">
          <sd-icon name="circle-alert" [size]="16" />
          <p>Could not load categories. {{ err }}</p>
          <button sdButton size="sm" type="button" icon="refresh" (click)="refresh()">Retry</button>
        </div>
      } @else if (!store.categoriesLoaded()) {
        <div class="skeleton" aria-busy="true" aria-label="Loading categories">
          @for (n of [1, 2, 3, 4, 5]; track n) {
            <sd-skeleton height="48px" radius="var(--sd-radius-sm)" />
          }
        </div>
      } @else if (store.categories().length === 0) {
        <sd-empty-state icon="folder-tree" title="No categories yet" message="Create top-level categories such as “Getting started” or “Billing”, then nest sub-categories under them.">
          <button sdButton variant="primary" icon="plus" type="button" (click)="openDialog(null)">New category</button>
        </sd-empty-state>
      } @else if (visible().length === 0) {
        <sd-empty-state icon="search-x" title="No categories match" [message]="'Nothing matches “' + filter() + '”.'" compact>
          <button sdButton type="button" (click)="filter.set('')">Clear filter</button>
        </sd-empty-state>
      } @else {
        <div class="kb-table-wrap">
          <table class="kb-table">
            <caption class="sd-visually-hidden">Knowledge base categories in tree order</caption>
            <thead>
              <tr>
                <th scope="col">Category</th>
                <th scope="col">Visibility</th>
                <th scope="col">Brands</th>
                <th scope="col" class="num">Articles</th>
                <th scope="col" class="num">Order</th>
                <th scope="col"><span class="sd-visually-hidden">Actions</span></th>
              </tr>
            </thead>
            <tbody>
              @for (n of visible(); track n.category.id) {
                <tr>
                  <td>
                    <div class="name-cell" [style.padding-left.px]="n.depth * 22">
                      @if (n.depth > 0) {
                        <span class="branch" aria-hidden="true"></span>
                      }
                      <span class="icon" aria-hidden="true">
                        <sd-icon [name]="iconOf(n.category)" [size]="16" />
                      </span>
                      <div class="name-text">
                        <span class="name">
                          @if (n.depth > 0) {
                            <span class="sd-visually-hidden">Sub-category of {{ parentPath(n) }}: </span>
                          }
                          {{ n.category.name }}
                        </span>
                        <span class="slug sd-mono">/{{ n.category.slug }}</span>
                        @if (n.category.description) {
                          <span class="desc">{{ n.category.description }}</span>
                        }
                      </div>
                    </div>
                  </td>
                  <td><desk-kb-visibility [visibility]="n.category.visibility" /></td>
                  <td>
                    @if (n.category.brandIds.length) {
                      <span class="kb-chips">
                        @for (b of brandNames(n.category); track b) {
                          <sd-badge size="sm" variant="outline">{{ b }}</sd-badge>
                        }
                      </span>
                    } @else {
                      <span class="sd-muted sd-small">All brands</span>
                    }
                  </td>
                  <td class="num">
                    <a [routerLink]="base" [queryParams]="{ category: n.category.id }" [attr.aria-label]="n.category.articleCount + ' articles in ' + n.category.name">
                      {{ n.category.articleCount }}
                    </a>
                  </td>
                  <td class="num">{{ n.category.sortOrder }}</td>
                  <td class="actions">
                    <button sdButton size="sm" variant="ghost" icon="plus" iconOnly type="button" [attr.aria-label]="'Add a sub-category to ' + n.category.name" (click)="openDialog(null, n.category.id)"></button>
                    <button sdButton size="sm" variant="ghost" icon="pencil" iconOnly type="button" [attr.aria-label]="'Edit ' + n.category.name" (click)="openDialog(n.category)"></button>
                    <button
                      sdButton
                      size="sm"
                      variant="ghost"
                      icon="trash"
                      iconOnly
                      type="button"
                      [loading]="deleting() === n.category.id"
                      [attr.aria-label]="'Delete ' + n.category.name"
                      (click)="remove(n)"
                    ></button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </section>
  `,
  styles: [
    KB_STYLES,
    `
      .count {
        align-self: center;
        padding-top: 16px;
      }
      .skeleton {
        display: grid;
        gap: 6px;
      }
      .name-cell {
        position: relative;
        display: flex;
        align-items: flex-start;
        gap: 10px;
        min-width: 260px;
      }
      .branch {
        position: absolute;
        top: -9px;
        bottom: 50%;
        width: 12px;
        margin-left: -16px;
        border-left: 1px solid var(--sd-border-strong);
        border-bottom: 1px solid var(--sd-border-strong);
        border-bottom-left-radius: 4px;
      }
      .icon {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        flex: none;
        width: 30px;
        height: 30px;
        border-radius: var(--sd-radius-sm);
        background: var(--sd-primary-soft);
        color: var(--sd-primary);
      }
      .name-text {
        display: grid;
        gap: 1px;
        min-width: 0;
      }
      .name {
        font-weight: var(--sd-weight-semibold);
      }
      .slug {
        color: var(--sd-text-3);
        font-size: var(--sd-text-xs);
      }
      .desc {
        color: var(--sd-text-2);
        font-size: var(--sd-text-xs);
        max-width: 520px;
      }
      .actions {
        white-space: nowrap;
        text-align: right;
      }
    `,
  ],
})
export class CategoriesPage {
  protected readonly store = inject(KbStore);
  private readonly api = inject(KnowledgeApi);
  private readonly dialogs = inject(SdDialogService);
  private readonly toasts = inject(ToastBridge);

  protected readonly base = KB_BASE;
  protected readonly filter = signal('');
  protected readonly deleting = signal<string | null>(null);

  private readonly brandsById = computed(() => new Map(this.store.options().brands.map((b) => [b.id, b.name])));
  private readonly byId = computed(() => new Map(this.store.categoryTree().map((n) => [n.category.id, n])));

  /** Matches plus their ancestors, so the tree context stays visible while filtering. */
  protected readonly visible = computed<CategoryNode[]>(() => {
    const tree = this.store.categoryTree();
    const q = this.filter().trim().toLowerCase();
    if (!q) return tree;
    const keep = new Set<string>();
    const byId = this.byId();
    for (const n of tree) {
      if (n.category.name.toLowerCase().includes(q) || n.category.slug.toLowerCase().includes(q)) {
        let cur: CategoryNode | undefined = n;
        while (cur && !keep.has(cur.category.id)) {
          keep.add(cur.category.id);
          cur = cur.category.parentId ? byId.get(cur.category.parentId) : undefined;
        }
      }
    }
    return tree.filter((n) => keep.has(n.category.id));
  });

  protected readonly summary = computed(() => {
    const all = this.store.categories();
    if (!this.store.categoriesLoaded()) return '';
    const articles = all.reduce((sum, c) => sum + c.articleCount, 0);
    return `${all.length} categor${all.length === 1 ? 'y' : 'ies'} · ${articles} article${articles === 1 ? '' : 's'}`;
  });

  constructor() {
    this.store.ensureOptions();
    this.refresh();
  }

  protected refresh(): void {
    this.store.refreshCategories().subscribe({ error: () => undefined });
  }

  protected iconOf(c: KbCategoryDto): string {
    return c.icon && hasIcon(c.icon) ? c.icon : 'folder';
  }

  protected parentPath(n: CategoryNode): string {
    const index = n.path.lastIndexOf(' › ');
    return index > 0 ? n.path.slice(0, index) : '';
  }

  protected brandNames(c: KbCategoryDto): string[] {
    const map = this.brandsById();
    return c.brandIds.map((id) => map.get(id) ?? 'Unknown brand');
  }

  protected async openDialog(category: KbCategoryDto | null, parentId: string | null = null): Promise<void> {
    const ref = this.dialogs.open<KbCategoryDto, CategoryDialogData, CategoryDialog>(CategoryDialog, {
      data: { category, parentId, categories: this.store.categories(), brands: this.store.options().brands },
      size: 'md',
      disableClose: false,
    });
    const saved = await firstValueFrom(ref.closed);
    if (!saved) return;
    this.store.upsertCategory(saved);
    this.toasts.success(category ? 'Category updated' : 'Category created', saved.name);
    this.refresh();
  }

  protected async remove(n: CategoryNode): Promise<void> {
    const c = n.category;
    const blocked = n.childCount > 0 || c.articleCount > 0;
    const ok = await this.dialogs.confirm({
      title: `Delete “${c.name}”?`,
      message: blocked
        ? `It still contains ${n.childCount ? `${n.childCount} sub-categor${n.childCount === 1 ? 'y' : 'ies'}` : ''}${n.childCount && c.articleCount ? ' and ' : ''}${c.articleCount ? `${c.articleCount} article${c.articleCount === 1 ? '' : 's'}` : ''}. Move them first — the server refuses to delete a category that is not empty.`
        : 'This cannot be undone.',
      confirmLabel: 'Delete category',
      danger: true,
    });
    if (!ok) return;
    this.deleting.set(c.id);
    try {
      await firstValueFrom(this.api.deleteCategory(c.id));
      this.store.removeCategory(c.id);
      this.toasts.success('Category deleted', c.name);
    } catch (err) {
      if (!toastedGlobally(err)) this.toasts.error(`Could not delete “${c.name}”`, errorMessage(err));
    } finally {
      this.deleting.set(null);
    }
  }
}
