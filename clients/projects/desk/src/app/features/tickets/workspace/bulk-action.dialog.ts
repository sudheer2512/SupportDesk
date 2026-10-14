import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ApiError, BulkAction, BulkResultDto, Guid, JsonValue, MetaApi, PRIORITIES, TicketsApi } from '@sd/core';
import { SdButton, SdChipsInput, SdCombobox, SdDialog, SdFormField, SdIcon, SdOption } from '@sd/ui';
import { agentOptions, departmentOptions, statusOptions, teamOptions } from '../shared/ticket-ui';

export interface BulkDialogData {
  action: BulkAction;
  ticketIds: Guid[];
  /** id → display number, for the failure list. */
  labels: Partial<Record<Guid, string>>;
}

export interface BulkDialogResult {
  result: BulkResultDto;
}

const TITLES: Partial<Record<BulkAction, string>> = {
  assign: 'Assign tickets',
  status: 'Change status',
  priority: 'Change priority',
  tags_add: 'Add tags',
  tags_remove: 'Remove tags',
  transfer: 'Transfer to department',
  close: 'Close tickets',
  spam: 'Mark as spam',
  watch: 'Watch tickets',
  delete: 'Delete tickets',
};

/** Parameters + confirmation for one bulk action (POST /tickets/bulk), then the per-ticket outcome. */
@Component({
  selector: 'desk-bulk-action-dialog',
  imports: [SdDialog, SdButton, SdCombobox, SdChipsInput, SdFormField, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="title()" [description]="description()">
      @if (result(); as r) {
        <div class="outcome" role="status">
          <p class="summary">
            <sd-icon [name]="r.failures.length ? 'triangle-alert' : 'circle-check'" [size]="18" />
            <strong>{{ r.succeeded }} of {{ r.requested }}</strong> tickets updated.
          </p>
          @if (r.failures.length) {
            <h3>Not updated ({{ r.failures.length }})</h3>
            <ul class="failures">
              @for (f of r.failures; track f.id) {
                <li>
                  <strong>{{ data.labels[f.id] ?? f.id }}</strong>
                  <span class="sd-muted">{{ f.message }}</span>
                </li>
              }
            </ul>
          }
        </div>
      } @else {
        @switch (data.action) {
          @case ('assign') {
            <sd-form-field label="Team" optional>
              <sd-combobox [options]="teams()" [value]="teamId()" (valueChange)="teamId.set($any($event))" placeholder="Keep team" />
            </sd-form-field>
            <sd-form-field label="Agent" hint="Leave empty to unassign (team stays).">
              <sd-combobox [options]="agents()" [value]="assigneeId()" (valueChange)="assigneeId.set($any($event))" placeholder="Unassigned" />
            </sd-form-field>
          }
          @case ('status') {
            <sd-form-field label="New status" required>
              <sd-combobox [options]="statuses()" [value]="statusId()" (valueChange)="statusId.set($any($event))" placeholder="Choose a status" />
            </sd-form-field>
          }
          @case ('priority') {
            <sd-form-field label="New priority" required>
              <sd-combobox [options]="priorities" [value]="priority()" (valueChange)="priority.set($any($event))" [searchable]="false" />
            </sd-form-field>
          }
          @case ('transfer') {
            <sd-form-field label="Department" required>
              <sd-combobox [options]="departments()" [value]="departmentId()" (valueChange)="departmentId.set($any($event))" />
            </sd-form-field>
            <sd-form-field label="Team" optional>
              <sd-combobox [options]="transferTeams()" [value]="teamId()" (valueChange)="teamId.set($any($event))" />
            </sd-form-field>
          }
          @case ('tags_add') {
            <sd-form-field label="Tags to add" required>
              <sd-chips-input [value]="tags()" (valueChange)="tags.set($event)" [suggestions]="tagSuggestions()" lowercase placeholder="Type a tag and press Enter" />
            </sd-form-field>
          }
          @case ('tags_remove') {
            <sd-form-field label="Tags to remove" required>
              <sd-chips-input [value]="tags()" (valueChange)="tags.set($event)" [suggestions]="tagSuggestions()" lowercase [allowNew]="false" placeholder="Pick tags" />
            </sd-form-field>
          }
          @case ('delete') {
            <p class="confirm">{{ confirmText() }}</p>
            <label class="typed">
              <span>Type <strong>{{ confirmWord() }}</strong> to confirm</span>
              <input
                class="sd-input"
                name="typed"
                autocomplete="off"
                [attr.aria-describedby]="'bulk-delete-hint'"
                [value]="typed()"
                (input)="typed.set($any($event.target).value)"
              />
            </label>
            <p id="bulk-delete-hint" class="sd-small sd-muted">Deleted tickets leave every queue. Agents with the delete permission can restore them from Deleted tickets.</p>
          }
          @default {
            <p class="confirm">{{ confirmText() }}</p>
          }
        }
        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
      }
      <div sdDialogFooter>
        @if (result()) {
          <button sdButton variant="primary" type="button" (click)="ref.close({ result: result()! })">Done</button>
        } @else {
          <button sdButton type="button" (click)="ref.close()">Cancel</button>
          <button
            sdButton
            type="button"
            [variant]="danger() ? 'danger' : 'primary'"
            [disabled]="!valid()"
            [loading]="busy()"
            (click)="apply()"
          >
            {{ applyLabel() }}
          </button>
        }
      </div>
    </sd-dialog>
  `,
  styles: `
    sd-form-field {
      display: block;
      margin-bottom: var(--sd-space-3);
    }
    .confirm {
      color: var(--sd-text-2);
    }
    .typed {
      display: grid;
      gap: 6px;
      margin-top: var(--sd-space-3);
      font-size: var(--sd-text-sm);
    }
    .error {
      margin-top: var(--sd-space-2);
      color: var(--sd-danger);
    }
    .summary {
      display: flex;
      align-items: center;
      gap: 8px;
    }
    h3 {
      margin: var(--sd-space-4) 0 var(--sd-space-2);
      font-size: var(--sd-text-sm);
      color: var(--sd-text-2);
    }
    .failures {
      display: grid;
      gap: 6px;
      margin: 0;
      padding: 0;
      list-style: none;
      max-height: 260px;
      overflow: auto;
    }
    .failures li {
      display: grid;
      gap: 2px;
      padding: 8px 10px;
      border: 1px solid var(--sd-danger-border);
      border-radius: var(--sd-radius-sm);
      background: var(--sd-danger-bg);
    }
  `,
})
export class BulkActionDialog {
  protected readonly data = inject<BulkDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<BulkDialogResult>>(DialogRef);
  private readonly tickets = inject(TicketsApi);
  private readonly meta = inject(MetaApi);

  protected readonly assigneeId = signal<string | null>(null);
  protected readonly teamId = signal<string | null>(null);
  protected readonly statusId = signal<string | null>(null);
  protected readonly priority = signal<string | null>('High');
  protected readonly departmentId = signal<string | null>(null);
  protected readonly tags = signal<string[]>([]);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly result = signal<BulkResultDto | null>(null);
  /** Typed confirmation for bulk delete ("DELETE 3"). */
  protected readonly typed = signal('');

  protected readonly priorities: SdOption[] = PRIORITIES.map((p) => ({ value: p, label: p }));
  protected readonly agents = computed(() => agentOptions(this.meta.ticketForm()));
  protected readonly teams = computed(() => teamOptions(this.meta.ticketForm(), null));
  protected readonly transferTeams = computed(() => teamOptions(this.meta.ticketForm(), this.departmentId()));
  protected readonly statuses = computed(() => statusOptions((this.meta.ticketForm()?.statuses ?? []).filter((s) => s.isActive)));
  protected readonly departments = computed(() => departmentOptions(this.meta.ticketForm()));
  protected readonly tagSuggestions = computed(() => this.meta.ticketForm()?.tags ?? []);

  protected readonly count = this.data.ticketIds.length;
  protected readonly confirmWord = computed(() => `DELETE ${this.count}`);
  protected readonly title = computed(() => TITLES[this.data.action] ?? 'Bulk update');
  protected readonly description = computed(() => (this.result() ? null : `${this.count} selected ticket${this.count === 1 ? '' : 's'}`));
  protected readonly danger = computed(() => ['spam', 'delete', 'close'].includes(this.data.action));
  protected readonly confirmText = computed(() => {
    switch (this.data.action) {
      case 'close':
        return `Close ${this.count} ticket(s)? Customers are not asked to confirm.`;
      case 'spam':
        return `Mark ${this.count} ticket(s) as spam? They are removed from every queue.`;
      case 'delete':
        return `Delete ${this.count} ticket(s)? They move to the recycle bin and can be restored.`;
      case 'watch':
        return `Add yourself as a watcher on ${this.count} ticket(s)?`;
      default:
        return `Apply to ${this.count} ticket(s)?`;
    }
  });
  protected readonly applyLabel = computed(() => {
    switch (this.data.action) {
      case 'close':
        return 'Close tickets';
      case 'spam':
        return 'Mark as spam';
      case 'delete':
        return 'Delete';
      case 'watch':
        return 'Watch';
      default:
        return 'Apply';
    }
  });
  protected readonly valid = computed(() => {
    switch (this.data.action) {
      case 'status':
        return !!this.statusId();
      case 'priority':
        return !!this.priority();
      case 'transfer':
        return !!this.departmentId();
      case 'tags_add':
      case 'tags_remove':
        return this.tags().length > 0;
      case 'delete':
        return this.typed().trim().toUpperCase() === this.confirmWord();
      default:
        return true;
    }
  });

  constructor() {
    this.meta.load();
  }

  protected apply(): void {
    const parameters: Record<string, JsonValue> = {};
    switch (this.data.action) {
      case 'assign':
        parameters['assigneeId'] = this.assigneeId();
        parameters['teamId'] = this.teamId();
        break;
      case 'status':
        parameters['statusId'] = this.statusId();
        break;
      case 'priority':
        parameters['priority'] = this.priority();
        break;
      case 'transfer':
        parameters['departmentId'] = this.departmentId();
        parameters['teamId'] = this.teamId();
        break;
      case 'tags_add':
      case 'tags_remove':
        parameters['tags'] = this.tags();
        break;
    }
    this.busy.set(true);
    this.error.set(null);
    this.tickets.bulk({ ticketIds: this.data.ticketIds, action: this.data.action, parameters }).subscribe({
      next: (r) => {
        this.busy.set(false);
        this.result.set(r);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(ApiError.from(err).userMessage);
      },
    });
  }
}
