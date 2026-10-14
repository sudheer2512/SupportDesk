import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError, Guid, SaveTaskRequest, TaskDto, TicketsApi } from '@sd/core';
import { SdButton, SdCheckbox, SdCombobox, SdDateInput, SdEmptyState, SdIcon, SdRelativeTimePipe, SdSkeleton, ToastService } from '@sd/ui';
import { agentOptions, toastApiError } from '../../shared/ticket-ui';
import { TicketContext } from '../ticket-context.service';

function toRequest(t: TaskDto, changes: Partial<SaveTaskRequest> = {}): SaveTaskRequest {
  return {
    title: t.title,
    description: t.description,
    isChecklistItem: t.isChecklistItem,
    parentTaskId: t.parentTaskId,
    assigneeId: t.assigneeId,
    dueAt: t.dueAt,
    status: t.status,
    sortOrder: t.sortOrder,
    ...changes,
  };
}

/** Values of the inline task editor. */
interface TaskDraft {
  title: string;
  assigneeId: Guid | null;
  dueAt: string | null;
  isChecklistItem: boolean;
}

/**
 * Tasks, subtasks and checklist items of the ticket (GET/POST /tickets/{id}/tasks, PATCH/DELETE /tasks/{id}).
 * Adding, editing and ticking tasks needs tickets.edit or tickets.note (`can.edit` / `can.note`); deleting needs
 * tickets.edit (`can.edit`) — the API enforces both.
 */
@Component({
  selector: 'desk-tasks-panel',
  imports: [NgTemplateOutlet, FormsModule, SdButton, SdCheckbox, SdCombobox, SdDateInput, SdEmptyState, SdIcon, SdRelativeTimePipe, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading() && tasks().length === 0) {
      <sd-skeleton [lines]="4" />
    } @else if (error()) {
      <p class="error" role="alert">{{ error() }} <button sdButton variant="link" size="sm" type="button" (click)="load()">Retry</button></p>
    } @else {
      @if (total() > 0) {
        <div class="progress" role="progressbar" [attr.aria-valuenow]="done()" aria-valuemin="0" [attr.aria-valuemax]="total()" [attr.aria-label]="done() + ' of ' + total() + ' done'">
          <span [style.width.%]="(done() / total()) * 100"></span>
        </div>
        <p class="sd-small sd-muted">{{ done() }} of {{ total() }} done</p>
      }
      <ul class="tasks">
        @for (t of tasks(); track t.id) {
          <li>
            @if (editingId() === t.id) {
              <ng-container [ngTemplateOutlet]="editor" [ngTemplateOutletContext]="{ $implicit: t }" />
            } @else {
              <div class="task" [class.is-done]="t.status === 'Done'">
                <sd-checkbox [value]="t.status === 'Done'" (valueChange)="toggle(t, $event)" [disabled]="!canSave()" [ariaLabel]="'Done: ' + t.title" />
                <div class="body">
                  <span class="title">
                    @if (t.isChecklistItem) {
                      <sd-icon name="list-checks" [size]="13" />
                    }
                    {{ t.title }}
                  </span>
                  <span class="meta sd-xs sd-muted">
                    {{ t.assigneeName ?? 'Unassigned' }}
                    @if (t.dueAt) {
                      · due {{ t.dueAt | sdRelativeTime }}
                    }
                  </span>
                </div>
                @if (canSave()) {
                  <button type="button" class="icon" [attr.aria-label]="'Edit ' + t.title" (click)="startEdit(t)"><sd-icon name="pencil" [size]="14" /></button>
                  <button type="button" class="icon" [attr.aria-label]="'Add subtask to ' + t.title" (click)="startSubtask(t.id)"><sd-icon name="plus" [size]="14" /></button>
                }
                @if (canDelete()) {
                  <button type="button" class="icon danger" [attr.aria-label]="'Delete ' + t.title" (click)="remove(t)"><sd-icon name="trash" [size]="14" /></button>
                }
              </div>
            }
            @if (t.subtasks.length || subtaskFor() === t.id) {
              <ul class="tasks sub">
                @for (s of t.subtasks; track s.id) {
                  @if (editingId() === s.id) {
                    <li><ng-container [ngTemplateOutlet]="editor" [ngTemplateOutletContext]="{ $implicit: s }" /></li>
                  } @else {
                    <li class="task" [class.is-done]="s.status === 'Done'">
                      <sd-checkbox [value]="s.status === 'Done'" (valueChange)="toggle(s, $event)" [disabled]="!canSave()" [ariaLabel]="'Done: ' + s.title" />
                      <span class="body">
                        <span class="title">{{ s.title }}</span>
                        @if (s.assigneeName || s.dueAt) {
                          <span class="meta sd-xs sd-muted">
                            {{ s.assigneeName ?? 'Unassigned' }}
                            @if (s.dueAt) {
                              · due {{ s.dueAt | sdRelativeTime }}
                            }
                          </span>
                        }
                      </span>
                      @if (canSave()) {
                        <button type="button" class="icon" [attr.aria-label]="'Edit ' + s.title" (click)="startEdit(s)"><sd-icon name="pencil" [size]="14" /></button>
                      }
                      @if (canDelete()) {
                        <button type="button" class="icon danger" [attr.aria-label]="'Delete ' + s.title" (click)="remove(s)"><sd-icon name="trash" [size]="14" /></button>
                      }
                    </li>
                  }
                }
                @if (subtaskFor() === t.id) {
                  <li>
                    <form class="inline" (ngSubmit)="addSubtask(t.id)">
                      <input class="sd-input sd-input-sm" name="sub" placeholder="Subtask title" aria-label="Subtask title" [ngModel]="subTitle()" (ngModelChange)="subTitle.set($event)" (keydown.escape)="subtaskFor.set(null)" />
                      <button sdButton size="sm" type="submit" [disabled]="!subTitle().trim()">Add</button>
                    </form>
                  </li>
                }
              </ul>
            }
          </li>
        } @empty {
          <sd-empty-state icon="list-checks" title="No tasks yet" [message]="canSave() ? 'Break the work down into tasks or a checklist.' : 'Nobody has added tasks to this ticket.'" compact />
        }
      </ul>

      @if (canSave()) {
        <form class="add" (ngSubmit)="add()">
          <input class="sd-input" name="title" placeholder="New task or checklist item" aria-label="New task title" [ngModel]="title()" (ngModelChange)="title.set($event)" maxlength="250" />
          <div class="add-row">
            <sd-combobox size="sm" ariaLabel="Task assignee" placeholder="Assignee" [options]="agents()" [value]="assigneeId()" (valueChange)="assigneeId.set($any($event))" />
            <sd-date-input size="sm" type="datetime" ariaLabel="Due" [value]="dueAt()" (valueChange)="dueAt.set($event)" />
          </div>
          <div class="add-row">
            <sd-checkbox [value]="checklist()" (valueChange)="checklist.set($event)">Checklist item</sd-checkbox>
            <span class="sd-spacer"></span>
            <button sdButton size="sm" variant="primary" icon="plus" type="submit" [disabled]="!title().trim()" [loading]="saving()">Add</button>
          </div>
        </form>
      }
    }

    <ng-template #editor let-t>
      <form class="edit" (ngSubmit)="saveEdit(t)" [attr.aria-label]="'Edit task ' + t.title" (keydown.escape)="cancelEdit()">
        <input
          class="sd-input sd-input-sm"
          name="editTitle"
          aria-label="Task title"
          maxlength="250"
          [ngModel]="draft().title"
          (ngModelChange)="patchDraft({ title: $event })"
        />
        <div class="add-row">
          <sd-combobox size="sm" ariaLabel="Task assignee" placeholder="Unassigned" [options]="agents()" [value]="draft().assigneeId" (valueChange)="patchDraft({ assigneeId: $any($event) })" />
          <sd-date-input size="sm" type="datetime" ariaLabel="Due" [value]="draft().dueAt" (valueChange)="patchDraft({ dueAt: $event })" />
        </div>
        <div class="add-row">
          <sd-checkbox [value]="draft().isChecklistItem" (valueChange)="patchDraft({ isChecklistItem: $event })">Checklist item</sd-checkbox>
          <span class="sd-spacer"></span>
          <button sdButton size="sm" variant="ghost" type="button" (click)="cancelEdit()">Cancel</button>
          <button sdButton size="sm" variant="primary" icon="save" type="submit" [disabled]="!draft().title.trim()" [loading]="saving()">Save</button>
        </div>
      </form>
    </ng-template>
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--sd-space-3);
    }
    .progress {
      height: 6px;
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface-3);
      overflow: hidden;
    }
    .progress span {
      display: block;
      height: 100%;
      background: var(--sd-success);
    }
    .tasks {
      display: grid;
      gap: 2px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .tasks.sub {
      margin-left: 26px;
    }
    .task {
      display: flex;
      align-items: flex-start;
      gap: 8px;
      padding: 6px 4px;
      border-radius: var(--sd-radius-sm);
    }
    .task:hover {
      background: var(--sd-surface-2);
    }
    .body {
      display: grid;
      flex: 1;
      min-width: 0;
    }
    .title {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      overflow-wrap: anywhere;
    }
    .is-done .title {
      text-decoration: line-through;
      color: var(--sd-text-3);
    }
    .icon {
      display: inline-flex;
      padding: 4px;
      border: 0;
      border-radius: var(--sd-radius-xs);
      background: transparent;
      color: var(--sd-text-3);
      cursor: pointer;
    }
    .icon:hover {
      background: var(--sd-surface-3);
      color: var(--sd-text);
    }
    .icon:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .icon.danger:hover {
      color: var(--sd-danger);
    }
    .edit {
      display: grid;
      gap: 6px;
      padding: 8px;
      border: 1px solid var(--sd-primary-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-primary-soft);
    }
    .add {
      display: grid;
      gap: 6px;
      padding-top: var(--sd-space-3);
      border-top: 1px solid var(--sd-divider);
    }
    .add-row,
    .inline {
      display: flex;
      align-items: center;
      gap: 6px;
      flex-wrap: wrap;
    }
    .add-row > * {
      flex: 1 1 140px;
      min-width: 0;
    }
    .add-row > .sd-spacer,
    .add-row > button,
    .add-row > sd-checkbox {
      flex: none;
    }
    .inline input {
      flex: 1;
      min-width: 0;
    }
    .error {
      color: var(--sd-danger);
    }
  `,
})
export class TasksPanelComponent {
  readonly ticketId = input.required<Guid>();
  private readonly ctx = inject(TicketContext);
  private readonly tickets = inject(TicketsApi);
  private readonly toast = inject(ToastService);

  protected readonly tasks = signal<TaskDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly title = signal('');
  protected readonly assigneeId = signal<string | null>(null);
  protected readonly dueAt = signal<string | null>(null);
  protected readonly checklist = signal(false);
  protected readonly subtaskFor = signal<Guid | null>(null);
  protected readonly subTitle = signal('');
  protected readonly editingId = signal<Guid | null>(null);
  protected readonly draft = signal<TaskDraft>({ title: '', assigneeId: null, dueAt: null, isChecklistItem: false });
  protected readonly agents = computed(() => agentOptions(this.ctx.meta.ticketForm(), null, true));
  /** Create / edit / tick (API: tickets.edit or tickets.note). */
  protected readonly canSave = computed(() => !!this.ctx.can()?.edit || !!this.ctx.can()?.note);
  /** Delete (API: tickets.edit). */
  protected readonly canDelete = computed(() => !!this.ctx.can()?.edit);
  private readonly flat = computed(() => this.tasks().flatMap((t) => [t, ...t.subtasks]));
  protected readonly total = computed(() => this.flat().filter((t) => t.status !== 'Cancelled').length);
  protected readonly done = computed(() => this.flat().filter((t) => t.status === 'Done').length);

  constructor() {
    effect(() => {
      this.ticketId();
      untracked(() => {
        this.editingId.set(null);
        this.load();
      });
    });
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.tickets.tasks(this.ticketId()).subscribe({
      next: (list) => {
        this.tasks.set(list);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.error.set(ApiError.from(err).userMessage);
      },
    });
  }

  protected add(): void {
    const title = this.title().trim();
    if (!title) return;
    this.saving.set(true);
    this.tickets
      .createTask(this.ticketId(), { title, isChecklistItem: this.checklist(), assigneeId: this.assigneeId(), dueAt: this.dueAt(), sortOrder: this.tasks().length })
      .subscribe({
        next: () => {
          this.saving.set(false);
          this.title.set('');
          this.dueAt.set(null);
          this.load();
        },
        error: (err: unknown) => {
          this.saving.set(false);
          toastApiError(this.toast, err, 'Could not add the task');
        },
      });
  }

  protected startSubtask(id: Guid): void {
    this.subTitle.set('');
    this.subtaskFor.set(id);
  }

  protected addSubtask(parentId: Guid): void {
    const title = this.subTitle().trim();
    if (!title) return;
    this.tickets.createTask(this.ticketId(), { title, isChecklistItem: true, parentTaskId: parentId }).subscribe({
      next: () => {
        this.subtaskFor.set(null);
        this.load();
      },
      error: (err: unknown) => toastApiError(this.toast, err, 'Could not add the subtask'),
    });
  }

  protected startEdit(t: TaskDto): void {
    this.draft.set({ title: t.title, assigneeId: t.assigneeId, dueAt: t.dueAt, isChecklistItem: t.isChecklistItem });
    this.editingId.set(t.id);
  }

  protected cancelEdit(): void {
    this.editingId.set(null);
  }

  protected patchDraft(changes: Partial<TaskDraft>): void {
    this.draft.update((d) => ({ ...d, ...changes }));
  }

  protected saveEdit(t: TaskDto): void {
    const d = this.draft();
    const title = d.title.trim();
    if (!title || this.saving()) return;
    this.saving.set(true);
    this.tickets.updateTask(t.id, toRequest(t, { title, assigneeId: d.assigneeId, dueAt: d.dueAt, isChecklistItem: d.isChecklistItem })).subscribe({
      next: () => {
        this.saving.set(false);
        this.editingId.set(null);
        this.toast.success('Task updated', title);
        this.load();
      },
      error: (err: unknown) => {
        this.saving.set(false);
        toastApiError(this.toast, err, 'Could not update the task');
      },
    });
  }

  protected toggle(t: TaskDto, done: boolean): void {
    this.tickets.updateTask(t.id, toRequest(t, { status: done ? 'Done' : 'Open' })).subscribe({
      next: () => this.load(),
      error: (err: unknown) => {
        toastApiError(this.toast, err, 'Could not update the task');
        this.load();
      },
    });
  }

  protected remove(t: TaskDto): void {
    if (!this.canDelete()) return;
    this.tickets.deleteTask(t.id).subscribe({
      next: () => this.load(),
      error: (err: unknown) => toastApiError(this.toast, err, 'Could not delete the task'),
    });
  }
}
