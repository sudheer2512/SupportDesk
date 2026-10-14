import { Injectable, computed, inject } from '@angular/core';
import { Router } from '@angular/router';
import { MeStore, TicketDetailDto, TicketsApi, WorkbenchApi } from '@sd/core';
import { SdDialogService, SdMenuItem, ToastService } from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { toastApiError } from '../shared/ticket-ui';
import { TicketWorkbenchStore } from '../workspace/workbench.store';
import { AssignDialog, TicketDialogData, TransferDialog } from './dialogs/assign-transfer.dialogs';
import { EscalateDialog, OverrideDialogData, OverrideEntitlementDialog, ReopenDialog, ResolveDialog, StatusDialog } from './dialogs/lifecycle.dialogs';
import { LinkDialog, MergeDialog, SplitDialog } from './dialogs/merge-split-link.dialogs';
import { TicketContext } from './ticket-context.service';

/**
 * Ticket actions that need a dialog or navigation (actions menu, shortcuts, palette). Every action respects the
 * server-computed `ticket.can` flags; the API enforces them again.
 */
@Injectable()
export class TicketActions {
  private readonly ctx = inject(TicketContext);
  private readonly dialogs = inject(SdDialogService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly tickets = inject(TicketsApi);
  private readonly workbench = inject(WorkbenchApi);
  private readonly store = inject(TicketWorkbenchStore);
  private readonly me = inject(MeStore);

  private readonly data = (): TicketDialogData => ({ ctx: this.ctx });

  /** Items of the "More actions" menu for the current ticket. */
  readonly menu = computed<SdMenuItem[]>(() => {
    const t = this.ctx.ticket();
    if (!t) return [];
    const c = t.can;
    const closed = t.statusCategory === 'Resolved' || t.statusCategory === 'Closed';
    const items: SdMenuItem[] = [
      { id: 'assign', label: 'Assign…', icon: 'user-check', hint: 'a = me', disabled: !c.assign, run: () => this.assign() },
      { id: 'transfer', label: 'Transfer…', icon: 'share', disabled: !c.transfer, run: () => this.transfer() },
      { id: 'escalate', label: t.isEscalated ? 'Escalate further…' : 'Escalate…', icon: 'arrow-up-right', hint: 'e', disabled: !c.escalate, run: () => this.escalate() },
      { id: 'status', label: 'Change status…', icon: 'circle-dot', hint: 's', disabled: !c.edit, run: () => this.status() },
      { id: 'd1', divider: true },
      closed
        ? { id: 'reopen', label: 'Reopen…', icon: 'history', disabled: !c.reopen, run: () => this.reopen() }
        : { id: 'resolve', label: 'Resolve…', icon: 'circle-check', disabled: !c.resolve, run: () => this.resolve() },
      { id: 'close', label: 'Close', icon: 'archive', disabled: !c.resolve || t.statusCategory === 'Closed', run: () => void this.close() },
      { id: 'd2', divider: true },
      { id: 'merge', label: 'Merge tickets into this…', icon: 'merge', disabled: !c.merge, run: () => this.merge() },
      { id: 'split', label: 'Split messages…', icon: 'split', disabled: !c.split, run: () => this.split() },
      { id: 'clone', label: 'Clone', icon: 'copy', disabled: !this.me.can('tickets.create'), run: () => void this.clone() },
      { id: 'link', label: 'Link to ticket…', icon: 'link', disabled: !c.edit, run: () => this.link() },
      { id: 'd3', divider: true },
      { id: 'watch', label: this.ctx.isWatching() ? 'Stop watching' : 'Watch', icon: 'eye', run: () => void this.ctx.toggleWatch() },
      t.lock && t.lock.userId === this.me.user()?.id
        ? { id: 'unlock', label: 'Unlock', icon: 'lock-open', run: () => void this.ctx.unlock() }
        : t.lock
          ? { id: 'unlock', label: `Unlock (${t.lock.userName})`, icon: 'lock-open', disabled: !this.me.can('supervisor.monitor'), run: () => void this.ctx.unlock() }
          : { id: 'lock', label: 'Lock while I work on it', icon: 'lock', disabled: !c.edit, run: () => void this.ctx.lock() },
      { id: 'override', label: 'Override entitlement…', icon: 'scale', disabled: !c.overrideEntitlement, run: () => void this.overrideEntitlement() },
      { id: 'd4', divider: true },
      { id: 'spam', label: 'Mark as spam…', icon: 'shield-off', danger: true, disabled: !c.delete, run: () => void this.remove(true) },
      { id: 'delete', label: 'Delete…', icon: 'trash', danger: true, disabled: !c.delete, run: () => void this.remove(false) },
    ];
    return items;
  });

  assign(): void {
    if (this.ctx.can()?.assign) this.dialogs.open(AssignDialog, { data: this.data(), size: 'sm' });
  }

  transfer(): void {
    if (this.ctx.can()?.transfer) this.dialogs.open(TransferDialog, { data: this.data(), size: 'sm' });
  }

  escalate(): void {
    if (this.ctx.can()?.escalate) this.dialogs.open(EscalateDialog, { data: this.data(), size: 'sm' });
    else this.toast.info('You cannot escalate this ticket');
  }

  status(): void {
    if (this.ctx.can()?.edit) this.dialogs.open(StatusDialog, { data: this.data(), size: 'sm' });
  }

  resolve(): void {
    if (this.ctx.can()?.resolve) this.dialogs.open(ResolveDialog, { data: this.data(), size: 'md' });
  }

  reopen(): void {
    if (this.ctx.can()?.reopen) this.dialogs.open(ReopenDialog, { data: this.data(), size: 'sm' });
  }

  merge(): void {
    if (this.ctx.can()?.merge) this.dialogs.open(MergeDialog, { data: this.data(), size: 'md' });
  }

  link(): void {
    this.dialogs.open(LinkDialog, { data: this.data(), size: 'sm' });
  }

  split(): void {
    if (!this.ctx.can()?.split) return;
    const ref = this.dialogs.open<TicketDetailDto | null, TicketDialogData>(SplitDialog, { data: this.data(), size: 'md' });
    ref.closed.subscribe((created) => {
      if (created) this.openTicket(created);
    });
  }

  async close(): Promise<void> {
    const t = this.ctx.ticket();
    if (!t) return;
    const ok = await this.dialogs.confirm({ title: `Close ${t.displayNumber}?`, message: 'Closed tickets can still be reopened.', confirmLabel: 'Close ticket' });
    if (ok) await this.ctx.run(this.tickets.close(t.id), 'Ticket closed', 'Could not close');
  }

  async clone(): Promise<void> {
    const t = this.ctx.ticket();
    if (!t) return;
    const ok = await this.dialogs.confirm({ title: `Clone ${t.displayNumber}?`, message: 'Creates a new ticket with the same customer, fields and description.', confirmLabel: 'Clone' });
    if (!ok) return;
    try {
      const created = await firstValueFrom(this.tickets.clone(t.id));
      this.toast.success(`Created ${created.displayNumber}`);
      this.openTicket(created);
    } catch (err) {
      toastApiError(this.toast, err, 'Could not clone');
    }
  }

  async overrideEntitlement(): Promise<void> {
    const t = this.ctx.ticket();
    if (!t?.can.overrideEntitlement) return;
    let contracts: OverrideDialogData['contracts'] = [];
    try {
      contracts = (await firstValueFrom(this.tickets.context(t.id))).contracts;
    } catch {
      // Without the contract list the override can still be recorded without a contract.
    }
    this.dialogs.open(OverrideEntitlementDialog, { data: { ctx: this.ctx, contracts } satisfies OverrideDialogData, size: 'sm' });
  }

  async remove(spam: boolean): Promise<void> {
    const t = this.ctx.ticket();
    if (!t) return;
    const ok = await this.dialogs.confirm({
      title: spam ? `Mark ${t.displayNumber} as spam?` : `Delete ${t.displayNumber}?`,
      message: spam
        ? 'The ticket leaves every queue and future messages from this sender are treated as spam.'
        : 'The ticket is moved to the recycle bin; an administrator can restore it.',
      confirmLabel: spam ? 'Mark as spam' : 'Delete',
      danger: true,
      typeToConfirm: spam ? undefined : t.displayNumber,
    });
    if (!ok) return;
    try {
      await firstValueFrom(this.workbench.deleteTicket(t.id, spam));
      this.toast.success(spam ? 'Marked as spam' : 'Ticket deleted', t.displayNumber);
      const next = this.store.closeTab(t.id);
      this.store.refreshCounts();
      this.store.listReload.next();
      void this.router.navigate(next ? ['/tickets', next] : ['/tickets']);
    } catch (err) {
      toastApiError(this.toast, err, spam ? 'Could not mark as spam' : 'Could not delete');
    }
  }

  private openTicket(t: TicketDetailDto): void {
    this.store.openTab({ id: t.id, displayNumber: t.displayNumber, subject: t.subject });
    this.store.listReload.next();
    void this.router.navigate(['/tickets', t.id]);
  }
}
