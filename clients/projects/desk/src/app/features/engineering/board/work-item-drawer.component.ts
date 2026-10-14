import { ChangeDetectionStrategy, Component, computed, effect, inject, input, model, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import {
  ApiError,
  EngineeringAffectedTicketsDto,
  EngineeringApi,
  EngineeringBoardDto,
  EngineeringWorkItemDto,
  Guid,
  LOCAL_WORK_ITEM_STATES,
  LocalWorkItemState,
  saveErrorMessage,
  validationMessages,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdChipsInput,
  SdDateTimePipe,
  SdDialogService,
  SdDrawer,
  SdFormField,
  SdInput,
  SdRichTextEditor,
  SdSkeleton,
  ToastService,
} from '@sd/ui';

interface WorkItemForm {
  title: string;
  type: string;
  state: LocalWorkItemState;
  priority: string;
  severity: string;
  assigneeUserId: Guid | null;
  releaseId: Guid | null;
  productId: Guid | null;
  areaPath: string;
  iterationPath: string;
  tags: string[];
  descriptionHtml: string;
}

const EMPTY: WorkItemForm = {
  title: '',
  type: 'Bug',
  state: 'New',
  priority: 'Medium',
  severity: '',
  assigneeUserId: null,
  releaseId: null,
  productId: null,
  areaPath: '',
  iterationPath: '',
  tags: [],
  descriptionHtml: '',
};

/** Work item details (create / edit / delete) with the tickets linked to it. Read-only without engineering.manage. */
@Component({
  selector: 'desk-work-item-drawer',
  imports: [FormsModule, RouterLink, SdDrawer, SdFormField, SdInput, SdButton, SdBadge, SdChipsInput, SdRichTextEditor, SdSkeleton, SdDateTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-drawer [(open)]="open" [title]="title()" width="620px">
      @if (loading()) {
        <sd-skeleton [lines]="8" height="20px" />
      } @else if (loadError()) {
        <p class="error" role="alert">{{ loadError() }}</p>
      } @else {
        <form id="work-item-form" class="form" (ngSubmit)="save()">
          <sd-form-field label="Title" required [error]="errors()['title']">
            <input sdInput name="title" maxlength="500" [readonly]="!canManage()" [ngModel]="form().title" (ngModelChange)="patch({ title: $event })" />
          </sd-form-field>
          <div class="grid">
            <sd-form-field label="Type" [error]="errors()['type']">
              <select sdInput name="type" [disabled]="!canManage()" [ngModel]="form().type" (ngModelChange)="patch({ type: $event })">
                @for (t of types; track t) {
                  <option [value]="t">{{ t }}</option>
                }
              </select>
            </sd-form-field>
            <sd-form-field label="State">
              <select sdInput name="state" [disabled]="!canManage()" [ngModel]="form().state" (ngModelChange)="patch({ state: $event })">
                @for (s of states; track s) {
                  <option [value]="s">{{ s }}</option>
                }
              </select>
            </sd-form-field>
            <sd-form-field label="Priority">
              <select sdInput name="priority" [disabled]="!canManage()" [ngModel]="form().priority" (ngModelChange)="patch({ priority: $event })">
                @for (p of priorities; track p) {
                  <option [value]="p">{{ p }}</option>
                }
              </select>
            </sd-form-field>
            <sd-form-field label="Severity" optional>
              <select sdInput name="severity" [disabled]="!canManage()" [ngModel]="form().severity" (ngModelChange)="patch({ severity: $event })">
                <option value="">—</option>
                @for (s of severities; track s) {
                  <option [value]="s">{{ s }}</option>
                }
              </select>
            </sd-form-field>
            <sd-form-field label="Assignee" optional [error]="errors()['assigneeUserId']">
              <select sdInput name="assignee" [disabled]="!canManage()" [ngModel]="form().assigneeUserId" (ngModelChange)="patch({ assigneeUserId: $event })">
                <option [ngValue]="null">Unassigned</option>
                @for (a of board().assignees; track a.id) {
                  <option [ngValue]="a.id">{{ a.name }}{{ a.title ? ' — ' + a.title : '' }}</option>
                }
              </select>
            </sd-form-field>
            <sd-form-field label="Release (fix version)" optional [error]="errors()['releaseId']">
              <select sdInput name="release" [disabled]="!canManage()" [ngModel]="form().releaseId" (ngModelChange)="patch({ releaseId: $event })">
                <option [ngValue]="null">Not planned</option>
                @for (r of board().releases; track r.id) {
                  <option [ngValue]="r.id">{{ r.name }}{{ r.releasedAt ? ' (released)' : '' }}</option>
                }
              </select>
            </sd-form-field>
            <sd-form-field label="Product" optional [error]="errors()['productId']">
              <select sdInput name="product" [disabled]="!canManage()" [ngModel]="form().productId" (ngModelChange)="patch({ productId: $event })">
                <option [ngValue]="null">—</option>
                @for (p of board().products; track p.id) {
                  <option [ngValue]="p.id">{{ p.name }}</option>
                }
              </select>
            </sd-form-field>
            <sd-form-field label="Iteration" optional>
              <input sdInput name="iteration" maxlength="300" [readonly]="!canManage()" [ngModel]="form().iterationPath" (ngModelChange)="patch({ iterationPath: $event })" />
            </sd-form-field>
          </div>
          <sd-form-field label="Area" optional>
            <input sdInput name="area" maxlength="300" [readonly]="!canManage()" [ngModel]="form().areaPath" (ngModelChange)="patch({ areaPath: $event })" />
          </sd-form-field>
          <sd-form-field label="Tags" optional [error]="errors()['tags']">
            <sd-chips-input ariaLabel="Tags" [value]="form().tags" (valueChange)="patch({ tags: $event })" />
          </sd-form-field>
          <sd-form-field label="Description / repro steps" optional>
            @if (canManage()) {
              <sd-rich-text-editor toolbar="compact" minHeight="140px" ariaLabel="Description and repro steps" [value]="form().descriptionHtml" (valueChange)="patch({ descriptionHtml: $event })" />
            } @else {
              <div class="sd-prose readonly" [innerHTML]="form().descriptionHtml || '—'"></div>
            }
          </sd-form-field>
          @if (formError()) {
            <p class="error" role="alert">{{ formError() }}</p>
          }
        </form>

        @if (item(); as w) {
          <section class="linked" aria-label="Linked tickets">
            <h3>Linked tickets</h3>
            @if (tickets(); as t) {
              @if (t.total === 0) {
                <p class="sd-muted small">No tickets are linked. Agents link tickets from the ticket's Engineering panel ({{ w.key }}).</p>
              } @else {
                <ul>
                  @for (x of t.tickets; track x.id) {
                    <li>
                      <a [routerLink]="['/tickets', x.id]">{{ x.displayNumber }}</a>
                      <span class="sd-truncate">{{ x.subject }}</span>
                      <sd-badge size="sm" variant="outline">{{ x.status }}</sd-badge>
                    </li>
                  }
                </ul>
                @if (t.hidden > 0) {
                  <p class="sd-muted small">+{{ t.hidden }} outside your ticket access.</p>
                }
              }
            }
            <p class="sd-muted small">Created {{ w.createdAt | sdDateTime }} · updated {{ w.updatedAt | sdDateTime }}{{ w.customerImpactCount ? ' · ' + w.customerImpactCount + ' customer(s) at escalation' : '' }}</p>
          </section>
        }
      }
      <div sdDrawerFooter class="footer">
        @if (item() && canManage()) {
          <button sdButton type="button" variant="danger" icon="trash" (click)="remove()">Delete</button>
        }
        <span class="sd-spacer"></span>
        <button sdButton type="button" variant="ghost" (click)="open.set(false)">{{ canManage() ? 'Cancel' : 'Close' }}</button>
        @if (canManage()) {
          <button sdButton type="submit" form="work-item-form" variant="primary" [loading]="saving()" [disabled]="loading()">{{ item() ? 'Save' : 'Create' }}</button>
        }
      </div>
    </sd-drawer>
  `,
  styles: `
    .form {
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
    .grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 10px 12px;
    }
    @media (max-width: 520px) {
      .grid {
        grid-template-columns: 1fr;
      }
    }
    .readonly {
      padding: 8px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-sm);
      background: var(--sd-surface-sunken);
    }
    .error {
      margin: 0;
      color: var(--sd-danger);
      font-size: var(--sd-text-sm);
    }
    .linked {
      margin-top: 16px;
      padding-top: 12px;
      border-top: 1px solid var(--sd-divider);
    }
    h3 {
      margin: 0 0 8px;
      font-size: var(--sd-text-sm);
    }
    .linked ul {
      display: flex;
      flex-direction: column;
      gap: 6px;
      margin: 0 0 8px;
      padding: 0;
      list-style: none;
    }
    .linked li {
      display: flex;
      align-items: center;
      gap: 8px;
      min-width: 0;
      font-size: var(--sd-text-sm);
    }
    .small {
      font-size: var(--sd-text-xs);
    }
    .footer {
      display: flex;
      align-items: center;
      gap: 8px;
    }
  `,
})
export class WorkItemDrawerComponent {
  readonly open = model(false);
  readonly workItemId = input<Guid | null>(null);
  readonly board = input.required<EngineeringBoardDto>();
  readonly canManage = input(false);
  readonly saved = output<EngineeringWorkItemDto>();
  readonly deleted = output<Guid>();

  private readonly api = inject(EngineeringApi);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(SdDialogService);

  protected readonly types = ['Bug', 'Feature', 'Task'];
  protected readonly states = LOCAL_WORK_ITEM_STATES;
  protected readonly priorities = ['Low', 'Medium', 'High', 'Critical'];
  protected readonly severities = ['Cosmetic', 'Minor', 'Major', 'Critical'];

  protected readonly item = signal<EngineeringWorkItemDto | null>(null);
  protected readonly tickets = signal<EngineeringAffectedTicketsDto | null>(null);
  protected readonly form = signal<WorkItemForm>(EMPTY);
  protected readonly loading = signal(false);
  protected readonly loadError = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly title = computed(() => {
    const w = this.item();
    return w ? `${w.key} · ${w.type}` : 'New work item';
  });

  constructor() {
    effect(() => {
      const open = this.open();
      const id = this.workItemId();
      if (!open) return;
      untracked(() => this.load(id));
    });
  }

  private load(id: Guid | null): void {
    this.errors.set({});
    this.formError.set(null);
    this.loadError.set(null);
    this.tickets.set(null);
    if (!id) {
      this.item.set(null);
      this.form.set({ ...EMPTY });
      return;
    }
    this.loading.set(true);
    this.api.workItem(id).subscribe({
      next: (d) => {
        this.item.set(d.item);
        this.tickets.set(d.tickets);
        const w = d.item;
        this.form.set({
          title: w.title,
          type: w.type,
          state: w.state,
          priority: w.priority,
          severity: w.severity ?? '',
          assigneeUserId: w.assigneeUserId,
          releaseId: w.releaseId,
          productId: w.productId,
          areaPath: w.areaPath ?? '',
          iterationPath: w.iterationPath ?? '',
          tags: [...w.tags],
          descriptionHtml: w.descriptionHtml ?? '',
        });
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.loadError.set(err instanceof ApiError ? err.userMessage : 'The work item could not be loaded.');
      },
    });
  }

  protected patch(change: Partial<WorkItemForm>): void {
    this.form.update((f) => ({ ...f, ...change }));
  }

  protected save(): void {
    if (!this.canManage()) return;
    const f = this.form();
    const existing = this.item();
    const request = {
      trackerId: this.board().trackerId,
      title: f.title.trim(),
      type: f.type,
      priority: f.priority,
      severity: f.severity || null,
      assigneeUserId: f.assigneeUserId,
      releaseId: f.releaseId,
      productId: f.productId,
      areaPath: f.areaPath.trim() || null,
      iterationPath: f.iterationPath.trim() || null,
      tags: f.tags,
      descriptionHtml: f.descriptionHtml || null,
      rowVersion: existing?.rowVersion ?? null,
    };
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    const call = existing ? this.api.updateWorkItem(existing.id, request) : this.api.createWorkItem(request);
    call.subscribe({
      next: (w) => {
        if (w.state !== f.state) {
          this.api.moveWorkItem(w.id, { state: f.state, rowVersion: w.rowVersion }).subscribe({
            next: (moved) => this.finish(moved, !existing),
            error: (err: unknown) => this.fail(err),
          });
        } else this.finish(w, !existing);
      },
      error: (err: unknown) => this.fail(err),
    });
  }

  private finish(w: EngineeringWorkItemDto, created: boolean): void {
    this.saving.set(false);
    this.saved.emit(w);
    this.open.set(false);
    this.toast.success(created ? 'Work item created' : 'Work item saved', `${w.key} · ${w.title}`);
  }

  private fail(err: unknown): void {
    this.saving.set(false);
    const fields = validationMessages(err);
    this.errors.set(fields);
    this.formError.set(Object.keys(fields).length > 0 ? null : saveErrorMessage(err));
  }

  protected async remove(): Promise<void> {
    const w = this.item();
    if (!w) return;
    const linked = this.tickets()?.total ?? 0;
    const ok = await this.dialogs.confirm({
      title: `Delete ${w.key}?`,
      message: linked > 0 ? `${linked} linked ticket(s) keep its last known state and are marked as orphaned.` : 'The work item is removed from the board.',
      confirmLabel: 'Delete',
      danger: true,
    });
    if (!ok) return;
    this.api.deleteWorkItem(w.id).subscribe({
      next: () => {
        this.deleted.emit(w.id);
        this.open.set(false);
        this.toast.success('Work item deleted', w.key);
      },
      error: (err: unknown) => this.toast.error('Could not delete', err instanceof ApiError ? err.userMessage : undefined),
    });
  }
}
