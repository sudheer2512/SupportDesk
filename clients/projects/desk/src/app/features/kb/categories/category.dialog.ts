import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError, IdName, KbCategoryDto, KbVisibility, KnowledgeApi, SaveKbCategoryRequest } from '@sd/core';
import { SdButton, SdCombobox, SdDialog, SdFormField, SdIcon, SdInput, SdOption, hasIcon } from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { ALL_VISIBILITIES, VISIBILITY_META, categorySubtree, comboMany, comboOne, flattenCategories } from '../kb-common';

export interface CategoryDialogData {
  /** null = create. */
  category: KbCategoryDto | null;
  /** Pre-selected parent when creating a sub-category. */
  parentId?: string | null;
  categories: readonly KbCategoryDto[];
  brands: readonly IdName[];
}

type CategoryErrors = Partial<Record<'name' | 'slug' | 'parentId' | 'description' | 'icon' | 'visibility' | 'brandIds' | 'sortOrder', string | null>>;

function slugify(text: string): string {
  return text
    .normalize('NFKD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 80);
}

/** Create / edit a knowledge-base category; saves through the API and closes with the saved category. */
@Component({
  selector: 'desk-kb-category-dialog',
  imports: [FormsModule, SdDialog, SdButton, SdFormField, SdInput, SdCombobox, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="data.category ? 'Edit category' : 'New category'" [description]="data.category ? data.category.name : 'Group related articles for agents and the help center.'">
      <form id="kb-category-form" class="form" (ngSubmit)="save()" novalidate>
        <sd-form-field label="Name" required [error]="errors().name">
          <input sdInput name="name" maxlength="120" [ngModel]="name()" (ngModelChange)="setName($event)" />
        </sd-form-field>
        <div class="two">
          <sd-form-field label="Slug" hint="Used in help-center URLs." [error]="errors().slug">
            <input sdInput name="slug" maxlength="80" [ngModel]="slug()" (ngModelChange)="setSlug($event)" />
          </sd-form-field>
          <sd-form-field label="Parent category" [error]="errors().parentId">
            <sd-combobox placeholder="Top level" [options]="parentOptions()" [value]="parentId()" (valueChange)="parentId.set(comboOne($event))" />
          </sd-form-field>
        </div>
        <sd-form-field label="Description" optional [error]="errors().description">
          <textarea sdInput name="description" rows="2" maxlength="500" [ngModel]="description()" (ngModelChange)="description.set($event)"></textarea>
        </sd-form-field>
        <div class="two">
          <sd-form-field label="Visibility" [hint]="visibilityHint()" [error]="errors().visibility">
            <select sdInput name="visibility" [ngModel]="visibility()" (ngModelChange)="visibility.set($event)">
              @for (v of visibilities; track v) {
                <option [value]="v">{{ visibilityMeta[v].label }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Icon" optional hint="An icon name, e.g. book-open, package, key." [error]="errors().icon">
            <div class="icon-row">
              <input sdInput name="icon" maxlength="40" [ngModel]="icon()" (ngModelChange)="icon.set($event)" />
              <span class="icon-preview" aria-hidden="true">
                @if (icon() && iconKnown()) {
                  <sd-icon [name]="icon()" [size]="18" />
                }
              </span>
            </div>
          </sd-form-field>
        </div>
        <div class="two">
          <sd-form-field label="Brands" hint="Empty = every brand portal." [error]="errors().brandIds">
            <sd-combobox multiple placeholder="All brands" [options]="brandOptions" [value]="brandIds()" (valueChange)="brandIds.set(comboMany($event))" />
          </sd-form-field>
          <sd-form-field label="Sort order" hint="Lower numbers come first." [error]="errors().sortOrder">
            <input sdInput name="sortOrder" type="number" step="1" [ngModel]="sortOrder()" (ngModelChange)="setSortOrder($event)" />
          </sd-form-field>
        </div>
        @if (formError(); as err) {
          <p class="form-error" role="alert"><sd-icon name="circle-alert" [size]="14" /> {{ err }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()" [disabled]="saving()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="kb-category-form" [loading]="saving()">{{ data.category ? 'Save changes' : 'Create category' }}</button>
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
      grid-template-columns: 1fr 1fr;
      gap: var(--sd-space-3);
    }
    @media (max-width: 560px) {
      .two {
        grid-template-columns: 1fr;
      }
    }
    .icon-row {
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .icon-preview {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      flex: none;
      width: 32px;
      height: 32px;
      border-radius: var(--sd-radius-sm);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    .form-error {
      display: flex;
      align-items: center;
      gap: 6px;
      color: var(--sd-danger);
      font-size: var(--sd-text-sm);
    }
  `,
})
export class CategoryDialog {
  protected readonly data = inject<CategoryDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<KbCategoryDto>>(DialogRef);
  private readonly api = inject(KnowledgeApi);

  protected readonly visibilities = ALL_VISIBILITIES;
  protected readonly visibilityMeta = VISIBILITY_META;
  protected readonly comboOne = comboOne;
  protected readonly comboMany = comboMany;

  private readonly c = this.data.category;
  protected readonly name = signal(this.c?.name ?? '');
  protected readonly slug = signal(this.c?.slug ?? '');
  private slugTouched = !!this.c;
  protected readonly parentId = signal<string | null>(this.c ? this.c.parentId : (this.data.parentId ?? null));
  protected readonly description = signal(this.c?.description ?? '');
  protected readonly icon = signal(this.c?.icon ?? '');
  protected readonly visibility = signal<KbVisibility>(this.c?.visibility ?? 'Public');
  protected readonly brandIds = signal<string[]>(this.c ? [...this.c.brandIds] : []);
  protected readonly sortOrder = signal<number>(this.c?.sortOrder ?? 0);
  protected readonly errors = signal<CategoryErrors>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);

  protected readonly brandOptions: SdOption[] = this.data.brands.map((b) => ({ value: b.id, label: b.name }));
  /** A category cannot be moved under itself or one of its descendants. */
  protected readonly parentOptions = computed<SdOption[]>(() => {
    const blocked = this.c ? categorySubtree(this.data.categories, this.c.id) : new Set<string>();
    return flattenCategories(this.data.categories)
      .filter((n) => !blocked.has(n.category.id))
      .map((n) => ({ value: n.category.id, label: n.path }));
  });
  protected readonly visibilityHint = computed(() => VISIBILITY_META[this.visibility()]?.description ?? null);
  protected readonly iconKnown = computed(() => hasIcon(this.icon().trim()));

  protected setName(value: string): void {
    this.name.set(value);
    if (!this.slugTouched) this.slug.set(slugify(value));
  }

  protected setSortOrder(value: unknown): void {
    const n = Number(value);
    this.sortOrder.set(Number.isFinite(n) ? n : 0);
  }

  protected setSlug(value: string): void {
    this.slugTouched = true;
    this.slug.set(value);
  }

  protected async save(): Promise<void> {
    if (!this.name().trim()) {
      this.errors.set({ name: 'Enter a name.' });
      return;
    }
    const body: SaveKbCategoryRequest = {
      parentId: this.parentId(),
      name: this.name().trim(),
      slug: this.slug().trim() || null,
      description: this.description().trim() || null,
      icon: this.icon().trim() || null,
      visibility: this.visibility(),
      brandIds: this.brandIds(),
      sortOrder: Number.isFinite(this.sortOrder()) ? Math.round(this.sortOrder()) : 0,
    };
    this.errors.set({});
    this.formError.set(null);
    this.saving.set(true);
    try {
      const saved = await firstValueFrom(this.c ? this.api.updateCategory(this.c.id, body) : this.api.createCategory(body));
      this.ref.close(saved);
    } catch (err) {
      const e = ApiError.from(err);
      const fields: CategoryErrors = {
        name: e.fieldError('name'),
        slug: e.fieldError('slug'),
        parentId: e.fieldError('parentId'),
        description: e.fieldError('description'),
        icon: e.fieldError('icon'),
        visibility: e.fieldError('visibility'),
        brandIds: e.fieldError('brandIds'),
        sortOrder: e.fieldError('sortOrder'),
      };
      this.errors.set(fields);
      if (!Object.values(fields).some(Boolean)) this.formError.set(e.userMessage);
    } finally {
      this.saving.set(false);
    }
  }
}
