import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MeStore } from '@sd/core';
import { SdButton, SdCombobox, SdDialog, SdFormField, SdInput } from '@sd/ui';
import { agentOptions, departmentOptions, teamOptions } from '../../shared/ticket-ui';
import { TicketContext } from '../ticket-context.service';

export interface TicketDialogData {
  ctx: TicketContext;
}

/** Assign to an agent and/or team (POST /tickets/{id}/assign). */
@Component({
  selector: 'desk-assign-dialog',
  imports: [FormsModule, SdDialog, SdButton, SdCombobox, SdFormField, SdInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Assign ticket" [description]="ticket()?.displayNumber + ' · ' + ticket()?.subject">
      <form id="assign-form" class="form" (ngSubmit)="save()">
        <sd-form-field label="Team" optional>
          <sd-combobox [options]="teams()" [value]="teamId()" (valueChange)="teamId.set($any($event))" placeholder="No team" />
        </sd-form-field>
        <sd-form-field label="Agent" hint="Agents of the ticket's department. Leave empty to unassign.">
          <sd-combobox [options]="agents()" [value]="assigneeId()" (valueChange)="assigneeId.set($any($event))" placeholder="Unassigned" />
        </sd-form-field>
        <button sdButton variant="link" size="sm" type="button" (click)="assigneeId.set(me.user()?.id ?? null)">Assign to me</button>
        <sd-form-field label="Note" optional hint="Added to the ticket history.">
          <input sdInput name="note" [ngModel]="note()" (ngModelChange)="note.set($event)" maxlength="500" />
        </sd-form-field>
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close(false)">Cancel</button>
        <button sdButton variant="primary" type="submit" form="assign-form" [loading]="data.ctx.busy()">Assign</button>
      </div>
    </sd-dialog>
  `,
  styles: `
    .form {
      display: grid;
      gap: var(--sd-space-3);
      justify-items: stretch;
    }
    .form > button {
      justify-self: start;
    }
  `,
})
export class AssignDialog {
  protected readonly data = inject<TicketDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  protected readonly me = inject(MeStore);
  protected readonly ticket = this.data.ctx.ticket;
  private readonly meta = this.data.ctx.meta;

  protected readonly teamId = signal<string | null>(this.ticket()?.teamId ?? null);
  protected readonly assigneeId = signal<string | null>(this.ticket()?.assigneeId ?? null);
  protected readonly note = signal('');
  protected readonly teams = computed(() => teamOptions(this.meta.ticketForm(), this.ticket()?.departmentId ?? null));
  protected readonly agents = computed(() => agentOptions(this.meta.ticketForm(), this.ticket()?.departmentId ?? null));

  protected async save(): Promise<void> {
    const ok = await this.data.ctx.assign(this.assigneeId(), this.teamId(), this.note().trim() || null);
    if (ok) this.ref.close(true);
  }
}

/** Transfer to another department (approval may be required — the API then creates a request). */
@Component({
  selector: 'desk-transfer-dialog',
  imports: [FormsModule, SdDialog, SdButton, SdCombobox, SdFormField, SdInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Transfer ticket" description="Moves the ticket to another department's queue. Some departments need a supervisor's approval first.">
      <form id="transfer-form" class="form" (ngSubmit)="save()">
        <sd-form-field label="Department" required [error]="error()">
          <sd-combobox [options]="departments()" [value]="departmentId()" (valueChange)="setDepartment($any($event))" placeholder="Choose a department" />
        </sd-form-field>
        <sd-form-field label="Team" optional>
          <sd-combobox [options]="teams()" [value]="teamId()" (valueChange)="teamId.set($any($event))" [disabled]="!departmentId()" />
        </sd-form-field>
        <sd-form-field label="Agent" optional hint="Leave empty to let routing pick an agent.">
          <sd-combobox [options]="agents()" [value]="assigneeId()" (valueChange)="assigneeId.set($any($event))" [disabled]="!departmentId()" />
        </sd-form-field>
        <sd-form-field label="Reason" optional>
          <textarea sdInput name="reason" rows="3" [ngModel]="reason()" (ngModelChange)="reason.set($event)" maxlength="1000"></textarea>
        </sd-form-field>
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close(false)">Cancel</button>
        <button sdButton variant="primary" type="submit" form="transfer-form" [loading]="data.ctx.busy()">Transfer</button>
      </div>
    </sd-dialog>
  `,
  styles: `
    .form {
      display: grid;
      gap: var(--sd-space-3);
    }
  `,
})
export class TransferDialog {
  protected readonly data = inject<TicketDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  private readonly meta = this.data.ctx.meta;
  private readonly current = this.data.ctx.ticket()?.departmentId ?? null;

  protected readonly departmentId = signal<string | null>(null);
  protected readonly teamId = signal<string | null>(null);
  protected readonly assigneeId = signal<string | null>(null);
  protected readonly reason = signal('');
  protected readonly error = signal<string | null>(null);
  protected readonly departments = computed(() => departmentOptions(this.meta.ticketForm()).filter((d) => d.value !== this.current));
  protected readonly teams = computed(() => teamOptions(this.meta.ticketForm(), this.departmentId()));
  protected readonly agents = computed(() => agentOptions(this.meta.ticketForm(), this.departmentId()));

  protected setDepartment(id: string | null): void {
    this.departmentId.set(id);
    this.teamId.set(null);
    this.assigneeId.set(null);
    this.error.set(null);
  }

  protected async save(): Promise<void> {
    const dept = this.departmentId();
    if (!dept) {
      this.error.set('Choose the department to transfer to.');
      return;
    }
    const ok = await this.data.ctx.transfer(dept, this.teamId(), this.assigneeId(), this.reason().trim() || null);
    if (ok) this.ref.close(true);
  }
}
