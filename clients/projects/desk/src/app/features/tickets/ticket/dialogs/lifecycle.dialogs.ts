import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ContractSummaryDto, TicketsApi } from '@sd/core';
import { SdButton, SdCheckbox, SdCombobox, SdDialog, SdFormField, SdInput, SdOption, SdRichTextEditor } from '@sd/ui';
import { agentOptions, allowedStatuses, lookupOptions, statusOptions } from '../../shared/ticket-ui';
import { TicketDialogData } from './assign-transfer.dialogs';

/** Escalate: reason, optional escalation owner, optionally raise the priority. */
@Component({
  selector: 'desk-escalate-dialog',
  imports: [FormsModule, SdDialog, SdButton, SdCombobox, SdFormField, SdInput, SdCheckbox],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Escalate ticket" description="Flags the ticket, notifies the escalation owner and raises its escalation level.">
      <form id="esc-form" class="form" (ngSubmit)="save()">
        <sd-form-field label="Reason" required [error]="error()">
          <textarea sdInput name="reason" rows="3" [ngModel]="reason()" (ngModelChange)="reason.set($event)" maxlength="1000"></textarea>
        </sd-form-field>
        <sd-form-field label="Escalate to" optional>
          <sd-combobox [options]="agents()" [value]="to()" (valueChange)="to.set($any($event))" placeholder="Department escalation owner" />
        </sd-form-field>
        <sd-checkbox [value]="raise()" (valueChange)="raise.set($event)">Raise the priority one level</sd-checkbox>
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close(false)">Cancel</button>
        <button sdButton variant="danger" type="submit" form="esc-form" [loading]="data.ctx.busy()">Escalate</button>
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
export class EscalateDialog {
  protected readonly data = inject<TicketDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  private readonly tickets = inject(TicketsApi);
  protected readonly reason = signal('');
  protected readonly to = signal<string | null>(null);
  protected readonly raise = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly agents = computed(() => agentOptions(this.data.ctx.meta.ticketForm()));

  protected async save(): Promise<void> {
    if (!this.reason().trim()) {
      this.error.set('Say why the ticket needs escalating.');
      return;
    }
    const id = this.data.ctx.id();
    if (!id) return;
    const ok = await this.data.ctx.run(
      this.tickets.escalate(id, { reason: this.reason().trim(), escalateToUserId: this.to(), raisePriority: this.raise() }),
      'Ticket escalated',
      'Could not escalate',
    );
    if (ok) this.ref.close(true);
  }
}

/** Resolve: resolution code, root cause, notes; optionally ask the customer to confirm. */
@Component({
  selector: 'desk-resolve-dialog',
  imports: [FormsModule, SdDialog, SdButton, SdCombobox, SdFormField, SdInput, SdCheckbox, SdRichTextEditor],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Resolve ticket" [description]="data.ctx.ticket()?.displayNumber + ' · ' + data.ctx.ticket()?.subject">
      <form id="resolve-form" class="form" (ngSubmit)="save()">
        <div class="row">
          <sd-form-field label="Resolution code" [required]="codes().length > 0">
            <sd-combobox [options]="codes()" [value]="code()" (valueChange)="code.set($any($event))" placeholder="How was it solved?" />
          </sd-form-field>
          <sd-form-field label="Root cause" optional>
            @if (causes().length) {
              <sd-combobox [options]="causes()" [value]="rootCause()" (valueChange)="rootCause.set($any($event))" />
            } @else {
              <input sdInput name="rootCause" [ngModel]="rootCause()" (ngModelChange)="rootCause.set($event)" maxlength="200" />
            }
          </sd-form-field>
        </div>
        <sd-form-field label="Resolution notes" optional hint="Shared with the customer when they are notified.">
          <sd-rich-text-editor toolbar="compact" minHeight="110px" [value]="notes()" (valueChange)="notes.set($event)" ariaLabel="Resolution notes" />
        </sd-form-field>
        <sd-checkbox [value]="notify()" (valueChange)="notify.set($event)">Notify the customer</sd-checkbox>
        <sd-checkbox [value]="confirm()" (valueChange)="confirm.set($event)">Ask the customer to confirm the fix (keeps it open until they reply)</sd-checkbox>
        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close(false)">Cancel</button>
        <button sdButton variant="primary" icon="circle-check" type="submit" form="resolve-form" [loading]="data.ctx.busy()">Resolve</button>
      </div>
    </sd-dialog>
  `,
  styles: `
    .form {
      display: grid;
      gap: var(--sd-space-3);
    }
    .row {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: var(--sd-space-3);
    }
    @media (max-width: 560px) {
      .row {
        grid-template-columns: 1fr;
      }
    }
    .error {
      color: var(--sd-danger);
    }
  `,
})
export class ResolveDialog {
  protected readonly data = inject<TicketDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  private readonly tickets = inject(TicketsApi);
  protected readonly codes = computed(() => lookupOptions(this.data.ctx.meta.ticketForm(), 'resolution_code'));
  protected readonly causes = computed(() => lookupOptions(this.data.ctx.meta.ticketForm(), 'root_cause'));
  protected readonly code = signal<string | null>(this.data.ctx.ticket()?.resolutionCode ?? null);
  protected readonly rootCause = signal<string | null>(this.data.ctx.ticket()?.rootCause ?? null);
  protected readonly notes = signal(this.data.ctx.ticket()?.resolutionHtml ?? '');
  protected readonly notify = signal(true);
  protected readonly confirm = signal(false);
  protected readonly error = signal<string | null>(null);

  protected async save(): Promise<void> {
    if (this.codes().length > 0 && !this.code()) {
      this.error.set('Pick a resolution code.');
      return;
    }
    const id = this.data.ctx.id();
    if (!id) return;
    const ok = await this.data.ctx.run(
      this.tickets.resolve(id, {
        resolutionCode: this.code(),
        rootCause: this.rootCause()?.trim() || null,
        resolutionHtml: this.notes() || null,
        notifyCustomer: this.notify(),
        requestConfirmation: this.confirm(),
      }),
      this.confirm() ? 'Resolved — waiting for the customer to confirm' : 'Ticket resolved',
      'Could not resolve',
    );
    if (ok) this.ref.close(true);
  }
}

/** Reopen with a reason. */
@Component({
  selector: 'desk-reopen-dialog',
  imports: [FormsModule, SdDialog, SdButton, SdFormField, SdInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Reopen ticket" description="The SLA clocks restart and the reopen count goes up.">
      <form id="reopen-form" (ngSubmit)="save()">
        <sd-form-field label="Reason" required [error]="error()">
          <textarea sdInput name="reason" rows="3" [ngModel]="reason()" (ngModelChange)="reason.set($event)" maxlength="1000"></textarea>
        </sd-form-field>
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close(false)">Cancel</button>
        <button sdButton variant="primary" type="submit" form="reopen-form" [loading]="data.ctx.busy()">Reopen</button>
      </div>
    </sd-dialog>
  `,
})
export class ReopenDialog {
  protected readonly data = inject<TicketDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  private readonly tickets = inject(TicketsApi);
  protected readonly reason = signal('');
  protected readonly error = signal<string | null>(null);

  protected async save(): Promise<void> {
    if (!this.reason().trim()) {
      this.error.set('Say why the ticket is reopened.');
      return;
    }
    const id = this.data.ctx.id();
    if (!id) return;
    const ok = await this.data.ctx.run(this.tickets.reopen(id, { reason: this.reason().trim() }), 'Ticket reopened', 'Could not reopen');
    if (ok) this.ref.close(true);
  }
}

/** Change status (shortcut "s" / palette): allowed next statuses, optional sub-status and comment. */
@Component({
  selector: 'desk-status-dialog',
  imports: [FormsModule, SdDialog, SdButton, SdCombobox, SdFormField, SdInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Change status" [description]="'Currently ' + (data.ctx.ticket()?.status ?? '')">
      <form id="status-form" class="form" (ngSubmit)="save()">
        <sd-form-field label="Status" required>
          <sd-combobox [options]="options()" [value]="statusId()" (valueChange)="statusId.set($any($event))" placeholder="Choose a status" />
        </sd-form-field>
        @if (substatuses().length) {
          <sd-form-field label="Sub-status" optional>
            <select sdInput name="sub" [ngModel]="substatus()" (ngModelChange)="substatus.set($event)">
              <option value="">—</option>
              @for (s of substatuses(); track s) {
                <option [value]="s">{{ s }}</option>
              }
            </select>
          </sd-form-field>
        }
        <sd-form-field label="Comment" optional>
          <input sdInput name="comment" [ngModel]="comment()" (ngModelChange)="comment.set($event)" maxlength="500" />
        </sd-form-field>
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close(false)">Cancel</button>
        <button sdButton variant="primary" type="submit" form="status-form" [disabled]="!statusId()" [loading]="data.ctx.busy()">Change status</button>
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
export class StatusDialog {
  protected readonly data = inject<TicketDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  protected readonly statusId = signal<string | null>(null);
  protected readonly substatus = signal('');
  protected readonly comment = signal('');
  protected readonly options = computed(() =>
    statusOptions(allowedStatuses(this.data.ctx.meta.ticketForm(), this.data.ctx.ticket()?.statusId ?? null).filter((s) => s.id !== this.data.ctx.ticket()?.statusId)),
  );
  protected readonly substatuses = computed(() => this.data.ctx.meta.statusById().get(this.statusId() ?? '')?.substatuses ?? []);

  protected async save(): Promise<void> {
    const id = this.statusId();
    if (!id) return;
    const ok = await this.data.ctx.changeStatus(id, { substatus: this.substatus() || null, comment: this.comment().trim() || null });
    if (ok) this.ref.close(true);
  }
}

export interface OverrideDialogData extends TicketDialogData {
  contracts: ContractSummaryDto[];
}

/** Override entitlement (support outside contract) with a mandatory reason; audited server-side. */
@Component({
  selector: 'desk-override-entitlement-dialog',
  imports: [FormsModule, SdDialog, SdButton, SdCombobox, SdFormField, SdInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Override entitlement" description="Lets the team work this ticket although the customer is not entitled. The reason is audited.">
      <form id="override-form" class="form" (ngSubmit)="save()">
        <sd-form-field label="Bill against contract" optional>
          <sd-combobox [options]="contracts" [value]="contractId()" (valueChange)="contractId.set($any($event))" placeholder="No contract" />
        </sd-form-field>
        <sd-form-field label="Reason" required [error]="error()">
          <textarea sdInput name="reason" rows="3" [ngModel]="reason()" (ngModelChange)="reason.set($event)" maxlength="1000"></textarea>
        </sd-form-field>
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close(false)">Cancel</button>
        <button sdButton variant="primary" type="submit" form="override-form" [loading]="data.ctx.busy()">Override</button>
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
export class OverrideEntitlementDialog {
  protected readonly data = inject<OverrideDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  private readonly tickets = inject(TicketsApi);
  protected readonly contracts: SdOption[] = this.data.contracts.map((c) => ({
    value: c.id,
    label: `${c.number} · ${c.name}`,
    description: `${c.type} · ${c.status}${c.coversTicket ? ' · covers this ticket' : ''}`,
  }));
  protected readonly contractId = signal<string | null>(null);
  protected readonly reason = signal('');
  protected readonly error = signal<string | null>(null);

  protected async save(): Promise<void> {
    if (!this.reason().trim()) {
      this.error.set('A reason is required for the audit trail.');
      return;
    }
    const id = this.data.ctx.id();
    if (!id) return;
    const ok = await this.data.ctx.run(
      this.tickets.overrideEntitlement(id, { reason: this.reason().trim(), contractId: this.contractId() }),
      'Entitlement overridden',
      'Could not override the entitlement',
    );
    if (ok) this.ref.close(true);
  }
}
