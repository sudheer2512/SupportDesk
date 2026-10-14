import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDropList, moveItemInArray } from '@angular/cdk/drag-drop';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminStatusCategory, AdminStatusDto, ApiError, MetaApi, SaveStatusRequest, TicketConfigApi } from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCheckbox,
  SdChipsInput,
  SdDialogService,
  SdDrawer,
  SdEmptyState,
  SdFormField,
  SdIcon,
  SdInput,
  SdSkeleton,
  SdToggle,
  ToastService,
} from '@sd/ui';
import { ADMIN_EDITOR_STYLES, AdminLeaveGuarded, sameValue, saveErrors } from '../admin-shared';

interface StatusForm {
  id: string | null;
  name: string;
  category: AdminStatusCategory;
  color: string;
  isDefault: boolean;
  pausesSla: boolean;
  isActive: boolean;
  customerLabel: string;
  anyTransition: boolean;
  allowedNext: string[];
  substatuses: string[];
  isSystem: boolean;
  rowVersion: string | null;
}

const CATEGORIES: { value: AdminStatusCategory; label: string }[] = [
  { value: 'Open', label: 'Open' },
  { value: 'Assigned', label: 'Assigned' },
  { value: 'InProgress', label: 'In progress' },
  { value: 'Waiting', label: 'Waiting' },
  { value: 'Resolved', label: 'Resolved' },
  { value: 'Closed', label: 'Closed' },
];

/**
 * Ticket statuses: drag (or move with the arrow buttons) to reorder; each status maps to a lifecycle category, has a
 * colour, an optional customer-facing label, a "default for its category" flag, SLA pause, allowed next statuses
 * (empty = any) and substatuses.
 */
@Component({
  selector: 'desk-admin-statuses',
  imports: [FormsModule, CdkDropList, CdkDrag, CdkDragHandle, SdButton, SdBadge, SdIcon, SdDrawer, SdFormField, SdInput, SdToggle, SdCheckbox, SdChipsInput, SdEmptyState, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="toolbar">
      <p class="sd-muted intro">Agents move tickets between these statuses. Restrict transitions to enforce a process; blueprints override them for tickets that follow one.</p>
      <span class="sd-spacer"></span>
      <button sdButton variant="primary" icon="plus" (click)="edit(null)">New status</button>
    </div>
    @if (loading()) {
      <sd-skeleton [lines]="7" height="48px" />
    } @else if (error()) {
      <sd-empty-state icon="circle-alert" title="Statuses could not be loaded" [message]="error()">
        <button sdButton icon="refresh" (click)="load()">Try again</button>
      </sd-empty-state>
    } @else {
      <ol class="rows list" cdkDropList (cdkDropListDropped)="drop($event)" aria-label="Statuses in display order">
        @for (s of statuses(); track s.id; let i = $index; let last = $last) {
          <li cdkDrag cdkDragLockAxis="y" [class.inactive]="!s.isActive">
            <button type="button" class="grip" cdkDragHandle [attr.aria-label]="'Drag to reorder ' + s.name"><sd-icon name="grip" [size]="16" /></button>
            <span class="swatch" [style.background]="s.color" aria-hidden="true"></span>
            <div class="grow">
              <div class="title">
                <button type="button" class="link" (click)="edit(s)">{{ s.name }}</button>
                <sd-badge size="sm" variant="outline">{{ categoryLabel(s.category) }}</sd-badge>
                @if (s.isDefault) {
                  <sd-badge size="sm" tone="primary">Default</sd-badge>
                }
                @if (s.pausesSla) {
                  <sd-badge size="sm" tone="warning" icon="pause">Pauses SLA</sd-badge>
                }
                @if (!s.isActive) {
                  <sd-badge size="sm">Inactive</sd-badge>
                }
                @if (s.isSystem) {
                  <sd-badge size="sm" icon="lock">Built-in</sd-badge>
                }
              </div>
              <span class="sd-muted small">
                Next: {{ transitionsText(s) }}
                @if (s.customerLabel) {
                  · customers see “{{ s.customerLabel }}”
                }
                @if (s.substatuses.length) {
                  · substatuses: {{ s.substatuses.join(', ') }}
                }
              </span>
            </div>
            <span class="count small sd-muted">{{ s.ticketCount }} tickets</span>
            <span class="moves">
              <button sdButton size="sm" variant="ghost" iconOnly icon="arrow-up" [disabled]="i === 0 || busy()" [attr.aria-label]="'Move ' + s.name + ' up'" (click)="move(i, -1)"></button>
              <button sdButton size="sm" variant="ghost" iconOnly icon="arrow-down" [disabled]="last || busy()" [attr.aria-label]="'Move ' + s.name + ' down'" (click)="move(i, 1)"></button>
            </span>
            <button sdButton size="sm" icon="pencil" [attr.aria-label]="'Edit ' + s.name" (click)="edit(s)">Edit</button>
          </li>
        }
      </ol>
    }

    <sd-drawer [(open)]="editorOpen" [title]="form().id ? 'Edit status' : 'New status'" width="560px">
      <form class="form" id="status-form" (ngSubmit)="save()">
        @if (formError()) {
          <div class="callout danger" role="alert"><sd-icon name="circle-alert" [size]="16" /><div>{{ formError() }}</div></div>
        }
        <div class="grid2">
          <sd-form-field label="Name" required [error]="errors()['name']">
            <input sdInput name="name" [ngModel]="form().name" (ngModelChange)="patch({ name: $event })" maxlength="100" />
          </sd-form-field>
          <sd-form-field label="Category" required [error]="errors()['category']" [hint]="form().isSystem ? 'Built-in statuses keep their category.' : null">
            <select sdInput name="category" [ngModel]="form().category" (ngModelChange)="patch({ category: $event })" [disabled]="form().isSystem">
              @for (c of categories; track c.value) {
                <option [value]="c.value">{{ c.label }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Colour" required [error]="errors()['color']">
            <span class="color">
              <input type="color" name="colorPicker" [ngModel]="form().color" (ngModelChange)="patch({ color: $event })" aria-label="Pick a colour" />
              <input sdInput name="color" [ngModel]="form().color" (ngModelChange)="patch({ color: $event })" maxlength="7" />
            </span>
          </sd-form-field>
          <sd-form-field label="Label shown to customers" optional hint="Keeps internal names private in the portal." [error]="errors()['customerLabel']">
            <input sdInput name="customerLabel" [ngModel]="form().customerLabel" (ngModelChange)="patch({ customerLabel: $event })" maxlength="100" />
          </sd-form-field>
        </div>
        <div class="toggles">
          <sd-toggle [value]="form().isDefault" (valueChange)="patch({ isDefault: $event })">Default status of its category</sd-toggle>
          <sd-toggle [value]="form().pausesSla" (valueChange)="patch({ pausesSla: $event })">Pause SLA resolution clocks while in this status</sd-toggle>
          <sd-toggle [value]="form().isActive" (valueChange)="patch({ isActive: $event })" [disabled]="form().isSystem">Active</sd-toggle>
          @if (errors()['isActive'] || errors()['isDefault']) {
            <p class="err">{{ errors()['isActive'] || errors()['isDefault'] }}</p>
          }
        </div>
        <fieldset>
          <legend>Allowed next statuses</legend>
          <sd-toggle [value]="form().anyTransition" (valueChange)="patch({ anyTransition: $event })">Any status (no restriction)</sd-toggle>
          @if (!form().anyTransition) {
            <div class="targets">
              @for (t of otherStatuses(); track t.id) {
                <sd-checkbox [value]="form().allowedNext.includes(t.id)" (valueChange)="toggleNext(t.id, $event)">
                  <span class="swatch" [style.background]="t.color"></span> {{ t.name }}
                </sd-checkbox>
              }
            </div>
            @if (form().allowedNext.length === 0) {
              <p class="err">Choose at least one status, or allow any.</p>
            }
          }
          @if (errors()['allowedNext']) {
            <p class="err">{{ errors()['allowedNext'] }}</p>
          }
        </fieldset>
        <sd-form-field label="Substatuses" optional hint="Finer reasons within the status, e.g. “Engineering” or “Vendor”. Press Enter to add." [error]="errors()['substatuses']">
          <sd-chips-input [value]="form().substatuses" (valueChange)="patch({ substatuses: $event })" placeholder="Add substatus" ariaLabel="Substatuses" />
        </sd-form-field>
      </form>
      <div sdDrawerFooter class="footer">
        @if (form().id && !form().isSystem) {
          <button sdButton type="button" variant="ghost" icon="trash" class="del" (click)="remove()">Delete</button>
        }
        <span class="sd-spacer"></span>
        <button sdButton type="button" variant="ghost" (click)="close()">Cancel</button>
        <button sdButton type="submit" form="status-form" variant="primary" [loading]="saving()" [disabled]="!form().anyTransition && form().allowedNext.length === 0">
          {{ form().id ? 'Save status' : 'Create status' }}
        </button>
      </div>
    </sd-drawer>
  `,
  styles: `
    ${ADMIN_EDITOR_STYLES}
    .intro { margin: 0; max-width: 720px; font-size: var(--sd-text-sm); }
    .list li { background: var(--sd-surface); }
    .list li.inactive .title button { color: var(--sd-text-3); }
    .grip { display: grid; place-items: center; width: 24px; height: 28px; border: 0; background: none; color: var(--sd-text-3); cursor: grab; border-radius: var(--sd-radius-xs); }
    .grip:focus-visible { outline: none; box-shadow: var(--sd-focus-ring); }
    .title { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
    .link { padding: 0; border: 0; background: none; color: var(--sd-text); font: inherit; font-weight: var(--sd-weight-semibold); cursor: pointer; }
    .link:hover, .link:focus-visible { color: var(--sd-primary); text-decoration: underline; outline: none; }
    .count { white-space: nowrap; }
    .moves { display: inline-flex; }
    .cdk-drag-preview { box-shadow: var(--sd-shadow-lg); border-radius: var(--sd-radius-sm); background: var(--sd-surface); list-style: none; display: flex; align-items: center; gap: 12px; padding: 10px 12px; }
    .cdk-drag-placeholder { opacity: 0.35; }
    .color { display: flex; gap: 6px; align-items: center; }
    .color input[type='color'] { width: 40px; height: var(--sd-control-h); padding: 2px; border: 1px solid var(--sd-border); border-radius: var(--sd-radius-sm); background: var(--sd-surface); }
    .toggles { display: flex; flex-direction: column; gap: 8px; }
    .targets { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 6px; margin-top: 8px; }
    .del { color: var(--sd-danger); }
    .footer { align-items: center; }
    @media (max-width: 760px) { .moves { display: none; } .targets { grid-template-columns: minmax(0, 1fr); } }
  `,
})
export class StatusesPage implements OnInit, AdminLeaveGuarded {
  private readonly api = inject(TicketConfigApi);
  private readonly meta = inject(MetaApi);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(SdDialogService);

  protected readonly categories = CATEGORIES;
  protected readonly statuses = signal<AdminStatusDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);

  protected readonly editorOpen = signal(false);
  protected readonly form = signal<StatusForm>(this.blank());
  private readonly initial = signal<StatusForm>(this.blank());
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);

  protected readonly otherStatuses = computed(() => this.statuses().filter((s) => s.id !== this.form().id));
  private readonly dirty = computed(() => this.editorOpen() && !sameValue(this.form(), this.initial()));

  ngOnInit(): void {
    this.load();
  }

  hasUnsavedChanges(): boolean {
    return this.dirty();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.statuses().subscribe({
      next: (list) => {
        this.statuses.set(list);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.error.set(ApiError.from(err).userMessage);
        this.loading.set(false);
      },
    });
  }

  protected categoryLabel(c: string): string {
    return CATEGORIES.find((x) => x.value === c)?.label ?? c;
  }

  protected transitionsText(s: AdminStatusDto): string {
    if (s.allowedNext.length === 0) return 'any status';
    const names = new Map(this.statuses().map((x) => [x.id, x.name]));
    return s.allowedNext.map((id) => names.get(id) ?? 'unknown').join(', ');
  }

  protected edit(s: AdminStatusDto | null): void {
    const f: StatusForm = s
      ? {
          id: s.id,
          name: s.name,
          category: s.category,
          color: s.color,
          isDefault: s.isDefault,
          pausesSla: s.pausesSla,
          isActive: s.isActive,
          customerLabel: s.customerLabel ?? '',
          anyTransition: s.allowedNext.length === 0,
          allowedNext: [...s.allowedNext],
          substatuses: [...s.substatuses],
          isSystem: s.isSystem,
          rowVersion: s.rowVersion,
        }
      : this.blank();
    this.form.set(f);
    this.initial.set({ ...f, allowedNext: [...f.allowedNext], substatuses: [...f.substatuses] });
    this.errors.set({});
    this.formError.set(null);
    this.editorOpen.set(true);
  }

  protected patch(change: Partial<StatusForm>): void {
    this.form.update((f) => ({ ...f, ...change }));
  }

  protected toggleNext(id: string, on: boolean): void {
    const next = this.form().allowedNext.filter((x) => x !== id);
    this.patch({ allowedNext: on ? [...next, id] : next });
  }

  protected async close(): Promise<void> {
    if (this.dirty() && !(await this.dialogs.confirm({ title: 'Discard changes to this status?', confirmLabel: 'Discard', danger: true }))) return;
    this.editorOpen.set(false);
  }

  protected save(): void {
    const f = this.form();
    const request: SaveStatusRequest = {
      name: f.name.trim(),
      category: f.category,
      color: f.color.trim(),
      isDefault: f.isDefault,
      pausesSla: f.pausesSla,
      isActive: f.isActive,
      customerLabel: f.customerLabel.trim() || null,
      allowedNext: f.anyTransition ? [] : f.allowedNext,
      substatuses: f.substatuses,
      rowVersion: f.rowVersion,
    };
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    (f.id ? this.api.updateStatus(f.id, request) : this.api.createStatus(request)).subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.editorOpen.set(false);
        this.toast.success(f.id ? 'Status saved' : 'Status created', saved.name);
        this.changed();
      },
      error: (err: unknown) => {
        this.saving.set(false);
        const e = ApiError.from(err);
        if (e.is('concurrency_conflict')) {
          this.formError.set('Someone else changed this status. Close the editor and open it again to see the latest version.');
          return;
        }
        const { fields, message } = saveErrors(err, 'The status could not be saved.');
        this.errors.set(fields);
        this.formError.set(message);
      },
    });
  }

  protected async remove(): Promise<void> {
    const f = this.form();
    if (!f.id) return;
    const ok = await this.dialogs.confirm({ title: `Delete “${f.name}”?`, message: 'Transitions pointing to it are removed too.', danger: true, confirmLabel: 'Delete' });
    if (!ok) return;
    this.api.deleteStatus(f.id).subscribe({
      next: () => {
        this.editorOpen.set(false);
        this.toast.success('Status deleted', f.name);
        this.changed();
      },
      error: (err: unknown) => this.formError.set(ApiError.from(err).userMessage),
    });
  }

  protected drop(event: CdkDragDrop<unknown>): void {
    if (event.previousIndex === event.currentIndex) return;
    const list = [...this.statuses()];
    moveItemInArray(list, event.previousIndex, event.currentIndex);
    this.persistOrder(list);
  }

  protected move(index: number, delta: number): void {
    const list = [...this.statuses()];
    moveItemInArray(list, index, index + delta);
    this.persistOrder(list);
  }

  private persistOrder(list: AdminStatusDto[]): void {
    const previous = this.statuses();
    this.statuses.set(list);
    this.busy.set(true);
    this.api.reorderStatuses(list.map((s) => s.id)).subscribe({
      next: (saved) => {
        this.busy.set(false);
        this.statuses.set(saved);
        this.meta.refresh();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.statuses.set(previous);
        this.toast.error('Could not reorder statuses', ApiError.from(err).userMessage);
      },
    });
  }

  private changed(): void {
    this.load();
    this.meta.refresh();
  }

  private blank(): StatusForm {
    return {
      id: null,
      name: '',
      category: 'InProgress',
      color: '#0891b2',
      isDefault: false,
      pausesSla: false,
      isActive: true,
      customerLabel: '',
      anyTransition: true,
      allowedNext: [],
      substatuses: [],
      isSystem: false,
      rowVersion: null,
    };
  }
}
