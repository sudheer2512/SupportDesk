import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ApiError, ConversationDetailDto, ConversationsApi, MeStore, MetaApi, TicketsApi } from '@sd/core';
import { SdButton, SdCombobox, SdDialog, SdFormField, SdInput, SdOption, SdRadio, SdRadioGroup, ToastService } from '@sd/ui';

export interface ChatTransferData {
  conversation: ConversationDetailDto;
}

type Target = 'agent' | 'department';

/**
 * Transfer a live chat (POST /conversations/{id}/transfer): to another agent of the ticket's department, or — with
 * tickets.transfer — to another department (optionally straight to one of its agents). The visitor sees
 * "Transferred to …", the receiving agent is notified and the chat's ticket follows. Closes with the updated
 * conversation, or null when cancelled / waiting for a transfer approval.
 */
@Component({
  selector: 'desk-chat-transfer-dialog',
  imports: [SdDialog, SdButton, SdCombobox, SdFormField, SdInput, SdRadio, SdRadioGroup],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Transfer chat" [description]="'Chat with ' + visitor">
      <form id="chat-transfer-form" class="form" (submit)="$event.preventDefault(); save()" novalidate>
        @if (canTransferDepartment) {
          <sd-form-field label="Transfer to">
            <sd-radio-group [value]="target()" (valueChange)="setTarget($any($event))" direction="row">
              <sd-radio value="agent">Another agent</sd-radio>
              <sd-radio value="department">Another department</sd-radio>
            </sd-radio-group>
          </sd-form-field>
        }
        @if (target() === 'department') {
          <sd-form-field label="Department" required [error]="errors()['departmentId']">
            <sd-combobox [options]="departments()" [value]="departmentId()" (valueChange)="setDepartment($any($event))" placeholder="Choose a department" />
          </sd-form-field>
          <sd-form-field label="Agent" optional hint="Leave empty to put the chat in the department's waiting queue." [error]="errors()['agentId']">
            <sd-combobox [options]="agents()" [value]="agentId()" (valueChange)="agentId.set($any($event))" placeholder="Next available agent" />
          </sd-form-field>
        } @else {
          <sd-form-field
            label="Agent"
            required
            [hint]="ticketDepartment() ? 'Agents of ' + ticketDepartment() + ' who can take chats.' : null"
            [error]="errors()['agentId']"
          >
            <sd-combobox [options]="agents()" [value]="agentId()" (valueChange)="agentId.set($any($event))" placeholder="Choose an agent" />
          </sd-form-field>
        }
        <sd-form-field label="Note for the next agent" optional>
          <textarea sdInput name="note" rows="3" maxlength="500" [value]="note()" (input)="note.set($any($event.target).value)"></textarea>
        </sd-form-field>
        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close(null)">Cancel</button>
        <button sdButton variant="primary" icon="share" type="submit" form="chat-transfer-form" [disabled]="!valid()" [loading]="busy()">Transfer</button>
      </div>
    </sd-dialog>
  `,
  styles: `
    .form {
      display: grid;
      gap: var(--sd-space-3);
    }
    .error {
      color: var(--sd-danger);
    }
  `,
})
export class ChatTransferDialog {
  private readonly data = inject<ChatTransferData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<ConversationDetailDto | null>>(DialogRef);
  private readonly api = inject(ConversationsApi);
  private readonly tickets = inject(TicketsApi);
  private readonly meta = inject(MetaApi);
  private readonly me = inject(MeStore);
  private readonly toast = inject(ToastService);

  protected readonly visitor = this.data.conversation.summary.visitorName || this.data.conversation.summary.contactName || 'Visitor';
  protected readonly canTransferDepartment = this.me.can('tickets.transfer');
  protected readonly target = signal<Target>('agent');
  protected readonly agentId = signal<string | null>(null);
  protected readonly departmentId = signal<string | null>(null);
  protected readonly note = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly errors = signal<Record<string, string>>({});
  /** Department of the chat's ticket (agents of that department can take the chat directly). */
  private readonly ticketDepartmentId = signal<string | null>(null);

  protected readonly ticketDepartment = computed(
    () => this.meta.ticketForm()?.departments.find((d) => d.id === this.ticketDepartmentId())?.name ?? null,
  );
  protected readonly departments = computed<SdOption[]>(() =>
    (this.meta.ticketForm()?.departments ?? []).filter((d) => d.id !== this.ticketDepartmentId()).map((d) => ({ value: d.id, label: d.name })),
  );
  protected readonly agents = computed<SdOption[]>(() => {
    const dept = this.target() === 'department' ? this.departmentId() : this.ticketDepartmentId();
    const current = this.data.conversation.summary.assignedUserId;
    return (this.meta.ticketForm()?.agents ?? [])
      .filter((a) => !a.isLightAgent && a.userId !== current && (!dept || a.departmentIds.includes(dept)))
      .map((a) => ({ value: a.userId, label: a.name, avatar: a.name, description: `${a.availability}${a.userId === this.me.user()?.id ? ' · you' : ''}` }));
  });
  protected readonly valid = computed(() => (this.target() === 'agent' ? !!this.agentId() : !!this.departmentId()));

  constructor() {
    this.meta.load();
    const ticketId = this.data.conversation.summary.ticketId;
    if (ticketId) this.tickets.get(ticketId).subscribe({ next: (t) => this.ticketDepartmentId.set(t.departmentId), error: () => undefined });
  }

  protected setTarget(target: Target): void {
    this.target.set(target);
    this.agentId.set(null);
    this.errors.set({});
    this.error.set(null);
  }

  protected setDepartment(id: string | null): void {
    this.departmentId.set(id);
    this.agentId.set(null);
  }

  protected save(): void {
    if (!this.valid() || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.errors.set({});
    const department = this.target() === 'department';
    this.api
      .transfer(this.data.conversation.summary.id, {
        agentId: this.agentId(),
        departmentId: department ? this.departmentId() : null,
        note: this.note().trim() || null,
      })
      .subscribe({
        next: (result) => {
          this.busy.set(false);
          if (result.kind === 'approval_required') {
            this.toast.info('Transfer sent for approval', result.message);
            this.ref.close(null);
            return;
          }
          this.toast.success('Chat transferred', result.detail.summary.assignedUserName ?? undefined);
          this.ref.close(result.detail);
        },
        error: (err: unknown) => {
          this.busy.set(false);
          const e = ApiError.from(err);
          this.errors.set({ agentId: e.fieldError('agentId') ?? '', departmentId: e.fieldError('departmentId') ?? '' });
          this.error.set(e.fieldError('agentId') || e.fieldError('departmentId') ? null : e.userMessage);
        },
      });
  }
}
