import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { forkJoin } from 'rxjs';
import { AdminCategoryDto, AdminIssueTypeDto, ApiError, MetaApi, TicketConfigApi } from '@sd/core';
import { SdBadge, SdButton, SdDialogService, SdDrawer, SdEmptyState, SdFormField, SdIcon, SdInput, SdSkeleton, SdToggle, ToastService } from '@sd/ui';
import { ADMIN_EDITOR_STYLES, AdminLeaveGuarded, sameValue, saveErrors } from '../admin-shared';

interface CategoryForm {
  id: string | null;
  name: string;
  parentId: string;
  departmentId: string;
  visibleInPortal: boolean;
  isActive: boolean;
}

interface IssueTypeForm {
  id: string | null;
  name: string;
  description: string;
  isActive: boolean;
}

/** Categories (two levels) and issue types used to classify tickets; per-department categories and portal visibility. */
@Component({
  selector: 'desk-admin-categories',
  imports: [FormsModule, SdButton, SdBadge, SdIcon, SdDrawer, SdFormField, SdInput, SdToggle, SdEmptyState, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading()) {
      <sd-skeleton [lines]="8" height="40px" />
    } @else if (error()) {
      <sd-empty-state icon="circle-alert" title="Classification could not be loaded" [message]="error()">
        <button sdButton icon="refresh" (click)="load()">Try again</button>
      </sd-empty-state>
    } @else {
      <div class="cols">
        <section class="card" aria-labelledby="cat-h">
          <div class="head">
            <div>
              <h2 id="cat-h">Categories</h2>
              <p class="sd-muted small">Two levels: categories and subcategories. Limit a category to one department or show it to everyone.</p>
            </div>
            <button sdButton size="sm" variant="primary" icon="plus" (click)="editCategory(null, null)">Category</button>
          </div>
          @if (tree().length === 0) {
            <sd-empty-state compact icon="folder" title="No categories yet" message="Add categories so agents and customers can classify requests." />
          } @else {
            <ul class="rows">
              @for (node of tree(); track node.cat.id; let i = $index; let lastTop = $last) {
                <li [class.inactive]="!node.cat.isActive">
                  <sd-icon name="folder" [size]="15" />
                  <div class="grow">
                    <button type="button" class="link" (click)="editCategory(node.cat, null)">{{ node.cat.name }}</button>
                    <span class="badges">
                      @if (node.cat.departmentId) {
                        <sd-badge size="sm" variant="outline">{{ departmentName(node.cat.departmentId) }}</sd-badge>
                      }
                      @if (!node.cat.visibleInPortal) {
                        <sd-badge size="sm" icon="eye-off">Agents only</sd-badge>
                      }
                      @if (!node.cat.isActive) {
                        <sd-badge size="sm">Inactive</sd-badge>
                      }
                    </span>
                  </div>
                  <span class="sd-muted small">{{ node.cat.ticketCount }}</span>
                  <button sdButton size="sm" variant="ghost" iconOnly icon="arrow-up" [disabled]="i === 0" [attr.aria-label]="'Move ' + node.cat.name + ' up'" (click)="moveCategory(node.cat, -1)"></button>
                  <button sdButton size="sm" variant="ghost" iconOnly icon="arrow-down" [disabled]="lastTop" [attr.aria-label]="'Move ' + node.cat.name + ' down'" (click)="moveCategory(node.cat, 1)"></button>
                  <button sdButton size="sm" variant="ghost" icon="plus" [attr.aria-label]="'Add a subcategory to ' + node.cat.name" (click)="editCategory(null, node.cat.id)">Sub</button>
                </li>
                @for (sub of node.children; track sub.id; let j = $index; let lastSub = $last) {
                  <li class="sub" [class.inactive]="!sub.isActive">
                    <sd-icon name="chevron-right" [size]="13" />
                    <div class="grow">
                      <button type="button" class="link" (click)="editCategory(sub, node.cat.id)">{{ sub.name }}</button>
                      <span class="badges">
                        @if (!sub.visibleInPortal) {
                          <sd-badge size="sm" icon="eye-off">Agents only</sd-badge>
                        }
                        @if (!sub.isActive) {
                          <sd-badge size="sm">Inactive</sd-badge>
                        }
                      </span>
                    </div>
                    <span class="sd-muted small">{{ sub.ticketCount }}</span>
                    <button sdButton size="sm" variant="ghost" iconOnly icon="arrow-up" [disabled]="j === 0" [attr.aria-label]="'Move ' + sub.name + ' up'" (click)="moveCategory(sub, -1)"></button>
                    <button sdButton size="sm" variant="ghost" iconOnly icon="arrow-down" [disabled]="lastSub" [attr.aria-label]="'Move ' + sub.name + ' down'" (click)="moveCategory(sub, 1)"></button>
                  </li>
                }
              }
            </ul>
          }
        </section>

        <section class="card" aria-labelledby="it-h">
          <div class="head">
            <div>
              <h2 id="it-h">Issue types</h2>
              <p class="sd-muted small">The nature of the request, e.g. question, incident, service request, defect.</p>
            </div>
            <button sdButton size="sm" variant="primary" icon="plus" (click)="editIssueType(null)">Issue type</button>
          </div>
          @if (issueTypes().length === 0) {
            <sd-empty-state compact icon="tag" title="No issue types yet" message="Add issue types to report on the nature of requests." />
          } @else {
            <ul class="rows">
              @for (it of issueTypes(); track it.id; let i = $index; let last = $last) {
                <li [class.inactive]="!it.isActive">
                  <sd-icon name="tag" [size]="15" />
                  <div class="grow">
                    <button type="button" class="link" (click)="editIssueType(it)">{{ it.name }}</button>
                    @if (!it.isActive) {
                      <sd-badge size="sm">Inactive</sd-badge>
                    }
                    @if (it.description) {
                      <span class="sd-muted small sd-truncate desc">{{ it.description }}</span>
                    }
                  </div>
                  <span class="sd-muted small">{{ it.ticketCount }}</span>
                  <button sdButton size="sm" variant="ghost" iconOnly icon="arrow-up" [disabled]="i === 0" [attr.aria-label]="'Move ' + it.name + ' up'" (click)="moveIssueType(i, -1)"></button>
                  <button sdButton size="sm" variant="ghost" iconOnly icon="arrow-down" [disabled]="last" [attr.aria-label]="'Move ' + it.name + ' down'" (click)="moveIssueType(i, 1)"></button>
                </li>
              }
            </ul>
          }
        </section>
      </div>
    }

    <sd-drawer [(open)]="catOpen" [title]="catForm().id ? 'Edit category' : catForm().parentId ? 'New subcategory' : 'New category'" width="480px">
      <form class="form" id="cat-form" (ngSubmit)="saveCategory()">
        @if (formError()) {
          <div class="callout danger" role="alert"><sd-icon name="circle-alert" [size]="16" /><div>{{ formError() }}</div></div>
        }
        <sd-form-field label="Name" required [error]="errors()['name']">
          <input sdInput name="name" [ngModel]="catForm().name" (ngModelChange)="patchCat({ name: $event })" maxlength="100" />
        </sd-form-field>
        <sd-form-field label="Parent category" [error]="errors()['parentId']">
          <select sdInput name="parent" [ngModel]="catForm().parentId" (ngModelChange)="patchCat({ parentId: $event })">
            <option value="">None — top-level category</option>
            @for (p of parentOptions(); track p.id) {
              <option [value]="p.id">{{ p.name }}</option>
            }
          </select>
        </sd-form-field>
        <sd-form-field label="Department" [error]="errors()['departmentId']" hint="Only tickets of this department offer the category.">
          <select sdInput name="department" [ngModel]="catForm().departmentId" (ngModelChange)="patchCat({ departmentId: $event })">
            <option value="">All departments</option>
            @for (d of departments(); track d.id) {
              <option [value]="d.id">{{ d.name }}</option>
            }
          </select>
        </sd-form-field>
        <sd-toggle [value]="catForm().visibleInPortal" (valueChange)="patchCat({ visibleInPortal: $event })">Customers can choose it in the portal</sd-toggle>
        <sd-toggle [value]="catForm().isActive" (valueChange)="patchCat({ isActive: $event })">Active</sd-toggle>
      </form>
      <div sdDrawerFooter class="footer">
        @if (catForm().id) {
          <button sdButton type="button" variant="ghost" icon="trash" class="del" (click)="deleteCategory()">Delete</button>
        }
        <span class="sd-spacer"></span>
        <button sdButton type="button" variant="ghost" (click)="catOpen.set(false)">Cancel</button>
        <button sdButton type="submit" form="cat-form" variant="primary" [loading]="saving()">Save</button>
      </div>
    </sd-drawer>

    <sd-drawer [(open)]="itOpen" [title]="itForm().id ? 'Edit issue type' : 'New issue type'" width="440px">
      <form class="form" id="it-form" (ngSubmit)="saveIssueType()">
        @if (formError()) {
          <div class="callout danger" role="alert"><sd-icon name="circle-alert" [size]="16" /><div>{{ formError() }}</div></div>
        }
        <sd-form-field label="Name" required [error]="errors()['name']">
          <input sdInput name="name" [ngModel]="itForm().name" (ngModelChange)="patchIt({ name: $event })" maxlength="100" />
        </sd-form-field>
        <sd-form-field label="Description" optional>
          <textarea sdInput name="description" rows="3" [ngModel]="itForm().description" (ngModelChange)="patchIt({ description: $event })" maxlength="500"></textarea>
        </sd-form-field>
        <sd-toggle [value]="itForm().isActive" (valueChange)="patchIt({ isActive: $event })">Active</sd-toggle>
      </form>
      <div sdDrawerFooter class="footer">
        @if (itForm().id) {
          <button sdButton type="button" variant="ghost" icon="trash" class="del" (click)="deleteIssueType()">Delete</button>
        }
        <span class="sd-spacer"></span>
        <button sdButton type="button" variant="ghost" (click)="itOpen.set(false)">Cancel</button>
        <button sdButton type="submit" form="it-form" variant="primary" [loading]="saving()">Save</button>
      </div>
    </sd-drawer>
  `,
  styles: `
    ${ADMIN_EDITOR_STYLES}
    .cols { display: grid; grid-template-columns: minmax(0, 3fr) minmax(0, 2fr); gap: var(--sd-space-4); align-items: start; }
    .cols .card + .card { margin-top: 0; }
    .head { display: flex; align-items: flex-start; gap: var(--sd-space-3); justify-content: space-between; margin-bottom: var(--sd-space-3); }
    .head p { margin: 2px 0 0; }
    .rows > li { padding: 6px 8px; gap: var(--sd-space-2); }
    .rows > li.sub { padding-left: 32px; background: var(--sd-surface-2); }
    .rows > li.inactive .link { color: var(--sd-text-3); }
    .grow { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
    .badges { display: inline-flex; gap: 4px; flex-wrap: wrap; }
    .desc { flex-basis: 100%; }
    .link { padding: 0; border: 0; background: none; color: var(--sd-text); font: inherit; font-weight: var(--sd-weight-medium); cursor: pointer; text-align: left; }
    .link:hover, .link:focus-visible { color: var(--sd-primary); text-decoration: underline; outline: none; }
    .del { color: var(--sd-danger); }
    .footer { align-items: center; }
    @media (max-width: 1000px) { .cols { grid-template-columns: minmax(0, 1fr); } }
  `,
})
export class CategoriesPage implements OnInit, AdminLeaveGuarded {
  private readonly api = inject(TicketConfigApi);
  private readonly meta = inject(MetaApi);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(SdDialogService);

  protected readonly categories = signal<AdminCategoryDto[]>([]);
  protected readonly issueTypes = signal<AdminIssueTypeDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  protected readonly catOpen = signal(false);
  protected readonly catForm = signal<CategoryForm>({ id: null, name: '', parentId: '', departmentId: '', visibleInPortal: true, isActive: true });
  private readonly catInitial = signal<CategoryForm | null>(null);
  protected readonly itOpen = signal(false);
  protected readonly itForm = signal<IssueTypeForm>({ id: null, name: '', description: '', isActive: true });
  private readonly itInitial = signal<IssueTypeForm | null>(null);

  protected readonly departments = computed(() => this.meta.ticketForm()?.departments ?? []);
  protected readonly tree = computed(() => {
    const all = this.categories();
    return all.filter((c) => !c.parentId).map((cat) => ({ cat, children: all.filter((c) => c.parentId === cat.id) }));
  });
  protected readonly parentOptions = computed(() => this.categories().filter((c) => !c.parentId && c.id !== this.catForm().id));

  ngOnInit(): void {
    this.meta.load();
    this.load();
  }

  hasUnsavedChanges(): boolean {
    return (this.catOpen() && !sameValue(this.catForm(), this.catInitial())) || (this.itOpen() && !sameValue(this.itForm(), this.itInitial()));
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    forkJoin({ categories: this.api.categories(), issueTypes: this.api.issueTypes() }).subscribe({
      next: ({ categories, issueTypes }) => {
        this.categories.set(categories);
        this.issueTypes.set(issueTypes);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.error.set(ApiError.from(err).userMessage);
        this.loading.set(false);
      },
    });
  }

  protected departmentName(id: string): string {
    return this.departments().find((d) => d.id === id)?.name ?? 'Department';
  }

  protected editCategory(c: AdminCategoryDto | null, parentId: string | null): void {
    const f: CategoryForm = c
      ? { id: c.id, name: c.name, parentId: c.parentId ?? '', departmentId: c.departmentId ?? '', visibleInPortal: c.visibleInPortal, isActive: c.isActive }
      : { id: null, name: '', parentId: parentId ?? '', departmentId: '', visibleInPortal: true, isActive: true };
    this.catForm.set(f);
    this.catInitial.set({ ...f });
    this.errors.set({});
    this.formError.set(null);
    this.catOpen.set(true);
  }

  protected patchCat(change: Partial<CategoryForm>): void {
    this.catForm.update((f) => ({ ...f, ...change }));
  }

  protected saveCategory(): void {
    const f = this.catForm();
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    this.api
      .saveCategory(f.id, { name: f.name.trim(), parentId: f.parentId || null, departmentId: f.departmentId || null, visibleInPortal: f.visibleInPortal, isActive: f.isActive })
      .subscribe({
        next: (saved) => {
          this.saving.set(false);
          this.catOpen.set(false);
          this.toast.success('Category saved', saved.name);
          this.changed();
        },
        error: (err: unknown) => this.fail(err, 'The category could not be saved.'),
      });
  }

  protected async deleteCategory(): Promise<void> {
    const f = this.catForm();
    if (!f.id || !(await this.dialogs.confirm({ title: `Delete “${f.name}”?`, danger: true, confirmLabel: 'Delete' }))) return;
    this.api.deleteCategory(f.id).subscribe({
      next: () => {
        this.catOpen.set(false);
        this.toast.success('Category deleted', f.name);
        this.changed();
      },
      error: (err: unknown) => this.formError.set(ApiError.from(err).userMessage),
    });
  }

  protected moveCategory(c: AdminCategoryDto, delta: number): void {
    const siblings = this.categories().filter((x) => x.parentId === c.parentId);
    const i = siblings.findIndex((x) => x.id === c.id);
    const j = i + delta;
    if (j < 0 || j >= siblings.length) return;
    const ids = siblings.map((x) => x.id);
    [ids[i], ids[j]] = [ids[j], ids[i]];
    this.api.reorderCategories(ids).subscribe({
      next: (list) => {
        this.categories.set(list);
        this.meta.refresh();
      },
      error: (err: unknown) => this.toast.error('Could not reorder', ApiError.from(err).userMessage),
    });
  }

  protected editIssueType(it: AdminIssueTypeDto | null): void {
    const f: IssueTypeForm = it ? { id: it.id, name: it.name, description: it.description ?? '', isActive: it.isActive } : { id: null, name: '', description: '', isActive: true };
    this.itForm.set(f);
    this.itInitial.set({ ...f });
    this.errors.set({});
    this.formError.set(null);
    this.itOpen.set(true);
  }

  protected patchIt(change: Partial<IssueTypeForm>): void {
    this.itForm.update((f) => ({ ...f, ...change }));
  }

  protected saveIssueType(): void {
    const f = this.itForm();
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    this.api.saveIssueType(f.id, { name: f.name.trim(), description: f.description.trim() || null, isActive: f.isActive }).subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.itOpen.set(false);
        this.toast.success('Issue type saved', saved.name);
        this.changed();
      },
      error: (err: unknown) => this.fail(err, 'The issue type could not be saved.'),
    });
  }

  protected async deleteIssueType(): Promise<void> {
    const f = this.itForm();
    if (!f.id || !(await this.dialogs.confirm({ title: `Delete “${f.name}”?`, danger: true, confirmLabel: 'Delete' }))) return;
    this.api.deleteIssueType(f.id).subscribe({
      next: () => {
        this.itOpen.set(false);
        this.toast.success('Issue type deleted', f.name);
        this.changed();
      },
      error: (err: unknown) => this.formError.set(ApiError.from(err).userMessage),
    });
  }

  protected moveIssueType(index: number, delta: number): void {
    const ids = this.issueTypes().map((x) => x.id);
    const j = index + delta;
    if (j < 0 || j >= ids.length) return;
    [ids[index], ids[j]] = [ids[j], ids[index]];
    this.api.reorderIssueTypes(ids).subscribe({
      next: (list) => {
        this.issueTypes.set(list);
        this.meta.refresh();
      },
      error: (err: unknown) => this.toast.error('Could not reorder', ApiError.from(err).userMessage),
    });
  }

  private fail(err: unknown, fallback: string): void {
    this.saving.set(false);
    const { fields, message } = saveErrors(err, fallback);
    this.errors.set(fields);
    this.formError.set(message);
  }

  private changed(): void {
    this.load();
    this.meta.refresh();
  }
}
