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

/** Tasks, subtasks and checklist items of the ticket (GET/POST /tickets/{id}/tasks, PATCH/DELETE /tasks/{id}). */
@Component({
  selector: 'desk-tasks-panel',
  imports: [FormsModule, SdButton, SdCheckbox, SdCombobox, SdDateInput, SdEmptyState, SdIcon, SdRelativeTimePipe, SdSkeleton],
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
            <div class="task" [class.is-done]="t.status === 'Done'">
              <sd-checkbox [value]="t.status === 'Done'" (valueChange)="toggle(t, $event)" [ariaLabel]="'Done: ' + t.title" />
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
              <button type="button" class="icon" [attr.aria-label]="'Add subtask to ' + t.title" (click)="startSubtask(t.id)"><sd-icon name="plus" [size]="14" /></button>
              <button type="button" class="icon danger" [attr.aria-label]="'Delete ' + t.title" (click)="remove(t)"><sd-icon name="trash" [size]="14" /></button>
            </div>
            @if (t.subtasks.length || subtaskFor() === t.id) {
              <ul class="tasks sub">
                @for (s of t.subtasks; track s.id) {
                  <li class="task" [class.is-done]="s.status === 'Done'">
                    <sd-checkbox [value]="s.status === 'Done'" (valueChange)="toggle(s, $event)" [ariaLabel]="'Done: ' + s.title" />
                    <span class="body title">{{ s.title }}</span>
                    <button type="button" class="icon danger" [attr.aria-label]="'Delete ' + s.title" (click)="remove(s)"><sd-icon name="trash" [size]="14" /></button>
                  </li>
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
          <sd-empty-state icon="list-checks" title="No tasks yet" message="Break the work down into tasks or a checklist." compact />
        }
      </ul>

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
    .icon.danger:hover {
      color: var(--sd-danger);
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
  protected readonly agents = computed(() => agentOptions(this.ctx.meta.ticketForm(), null, true));
  private readonly flat = computed(() => this.tasks().flatMap((t) => [t, ...t.subtasks]));
  protected readonly total = computed(() => this.flat().filter((t) => t.status !== 'Cancelled').length);
  protected readonly done = computed(() => this.flat().filter((t) => t.status === 'Done').length);

  constructor() {
    effect(() => {
      this.ticketId();
      untracked(() => this.load());
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
    this.tickets.deleteTask(t.id).subscribe({
      next: () => this.load(),
      error: (err: unknown) => toastApiError(this.toast, err, 'Could not delete the task'),
    });
  }
}
