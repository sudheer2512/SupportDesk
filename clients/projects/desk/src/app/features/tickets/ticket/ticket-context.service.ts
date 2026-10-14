import { Injectable, computed, inject, signal } from '@angular/core';
import {
  ApiError,
  ChangeStatusRequest,
  Guid,
  MeStore,
  MetaApi,
  TicketDetailDto,
  TicketsApi,
  TimelineItemDto,
  UpdateTicketRequest,
  WorkbenchApi,
} from '@sd/core';
import { SdDialogService, ToastService } from '@sd/ui';
import { Observable, firstValueFrom } from 'rxjs';
import { toastApiError } from '../shared/ticket-ui';
import { TicketWorkbenchStore } from '../workspace/workbench.store';
import { ConflictDialog, ConflictDialogData } from './dialogs/conflict.dialog';

/** Field changes the agent made, in UpdateTicketRequest shape, plus readable labels for the conflict dialog. */
export interface FieldEdit {
  changes: Omit<UpdateTicketRequest, 'rowVersion'>;
  /** field key → label of the agent's value (for "your change" in the conflict dialog). */
  labels: Record<string, string>;
}

/**
 * State and actions of the ticket open in the main panel (provided by TicketViewComponent): the detail
 * (with its rowVersion), the timeline, and every mutation with consistent error handling — 409 conflicts show
 * the server's current values and let the agent re-apply, other failures toast their problem title.
 */
@Injectable()
export class TicketContext {
  private readonly tickets = inject(TicketsApi);
  private readonly workbench = inject(WorkbenchApi);
  private readonly store = inject(TicketWorkbenchStore);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(SdDialogService);
  private readonly me = inject(MeStore);
  readonly meta = inject(MetaApi);

  readonly ticket = signal<TicketDetailDto | null>(null);
  readonly loading = signal(false);
  readonly error = signal<ApiError | null>(null);
  readonly timeline = signal<TimelineItemDto[]>([]);
  readonly timelineLoading = signal(false);
  readonly timelineError = signal<ApiError | null>(null);
  /** An action is in flight (disables quick editors). */
  readonly busy = signal(false);

  readonly id = computed(() => this.ticket()?.id ?? null);
  readonly can = computed(() => this.ticket()?.can ?? null);
  readonly isClosed = computed(() => {
    const c = this.ticket()?.statusCategory;
    return c === 'Resolved' || c === 'Closed';
  });
  readonly lockedByOther = computed(() => {
    const t = this.ticket();
    return !!t?.lock && t.lock.userId !== this.me.user()?.id && new Date(t.lock.until).getTime() > Date.now();
  });
  readonly isWatching = computed(() => !!this.ticket()?.watchers.some((w) => w.userId === this.me.user()?.id));

  private loadSeq = 0;

  load(id: Guid): void {
    const seq = ++this.loadSeq;
    this.loading.set(true);
    this.error.set(null);
    this.tickets.get(id).subscribe({
      next: (t) => {
        if (seq !== this.loadSeq) return;
        this.loading.set(false);
        this.apply(t);
      },
      error: (err: unknown) => {
        if (seq !== this.loadSeq) return;
        this.loading.set(false);
        this.error.set(ApiError.from(err));
      },
    });
    this.loadTimeline(id);
  }

  /** Refetches the detail quietly (realtime changes, after actions that return something else). */
  refresh(): void {
    const id = this.id();
    if (!id) return;
    this.tickets.get(id).subscribe({ next: (t) => this.apply(t), error: () => undefined });
  }

  loadTimeline(id: Guid | null = this.id()): void {
    if (!id) return;
    this.timelineLoading.set(true);
    this.tickets.timeline(id).subscribe({
      next: (items) => {
        this.timeline.set(items);
        this.timelineLoading.set(false);
        this.timelineError.set(null);
      },
      error: (err: unknown) => {
        this.timelineLoading.set(false);
        this.timelineError.set(ApiError.from(err));
      },
    });
  }

  /** Sets the detail and mirrors it into the workspace (context panel, tab title). */
  apply(t: TicketDetailDto): void {
    this.ticket.set(t);
    this.error.set(null);
    if (this.store.activeTicketId() === t.id) this.store.activeTicket.set(t);
    this.store.openTab({ id: t.id, displayNumber: t.displayNumber, subject: t.subject });
  }

  /** Runs an action returning the new detail; toasts failures. Resolves true on success. */
  async run(action: Observable<TicketDetailDto>, success?: string, fallbackError = 'The action failed'): Promise<boolean> {
    this.busy.set(true);
    try {
      const t = await firstValueFrom(action);
      this.apply(t);
      if (success) this.toast.success(success);
      this.loadTimeline();
      this.store.refreshCounts();
      return true;
    } catch (err) {
      const e = ApiError.from(err);
      if (e.is('concurrency_conflict') && e.currentTicket) this.apply(e.currentTicket);
      toastApiError(this.toast, e, fallbackError);
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  /** PATCH with optimistic concurrency; on 409 shows the server's values and offers to re-apply. */
  async update(edit: FieldEdit, success?: string): Promise<boolean> {
    const t = this.ticket();
    if (!t) return false;
    this.busy.set(true);
    try {
      const updated = await firstValueFrom(this.tickets.update(t.id, { ...edit.changes, rowVersion: t.rowVersion }));
      this.apply(updated);
      if (success) this.toast.success(success);
      this.loadTimeline();
      return true;
    } catch (err) {
      const e = ApiError.from(err);
      if (e.is('concurrency_conflict')) return this.resolveConflict(e, () => this.update(edit, success), edit);
      if (e.is('validation_failed') || e.is('missing_required_fields') || e.is('invalid_transition')) {
        toastApiError(this.toast, e, 'Some values were not accepted');
      } else {
        toastApiError(this.toast, e, 'Could not save the ticket');
      }
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  async changeStatus(statusId: Guid, extra: Partial<ChangeStatusRequest> = {}): Promise<boolean> {
    const t = this.ticket();
    if (!t || statusId === t.statusId) return false;
    const label = this.meta.statusById().get(statusId)?.name ?? 'new status';
    this.busy.set(true);
    try {
      const updated = await firstValueFrom(this.tickets.changeStatus(t.id, { statusId, rowVersion: t.rowVersion, ...extra }));
      this.apply(updated);
      this.toast.success(`Status changed to ${label}`);
      this.loadTimeline();
      this.store.refreshCounts();
      return true;
    } catch (err) {
      const e = ApiError.from(err);
      if (e.is('concurrency_conflict')) {
        return this.resolveConflict(e, () => this.changeStatus(statusId, extra), { changes: {}, labels: { status: label } });
      }
      toastApiError(this.toast, e, 'Could not change the status');
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  assign(assigneeId: Guid | null, teamId: Guid | null, note: string | null = null): Promise<boolean> {
    const t = this.ticket();
    if (!t) return Promise.resolve(false);
    const who = assigneeId ? (this.meta.agentById().get(assigneeId)?.name ?? 'agent') : null;
    return this.run(this.tickets.assign(t.id, { assigneeId, teamId, note }), who ? `Assigned to ${who}` : 'Ticket unassigned', 'Could not assign');
  }

  assignToMe(): Promise<boolean> {
    const t = this.ticket();
    const uid = this.me.user()?.id ?? null;
    if (!t || !uid) return Promise.resolve(false);
    if (t.assigneeId === uid) {
      this.toast.info('Already assigned to you');
      return Promise.resolve(false);
    }
    return this.assign(uid, t.teamId);
  }

  /** Transfer; a department that needs approval creates a request instead (202). */
  async transfer(departmentId: Guid, teamId: Guid | null, assigneeId: Guid | null, reason: string | null): Promise<boolean> {
    const t = this.ticket();
    if (!t) return false;
    this.busy.set(true);
    try {
      const result = await firstValueFrom(this.workbench.transfer(t.id, { departmentId, teamId, assigneeId, reason }));
      if (result.kind === 'approval_required') {
        this.toast.show({ kind: 'info', title: 'Approval requested', message: result.message, durationMs: 8000 });
        this.loadTimeline();
        return true;
      }
      this.apply(result.ticket);
      const dept = this.meta.ticketForm()?.departments.find((d) => d.id === departmentId)?.name ?? 'the department';
      this.toast.success(`Transferred to ${dept}`);
      this.loadTimeline();
      this.store.refreshCounts();
      return true;
    } catch (err) {
      toastApiError(this.toast, err, 'Could not transfer');
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  setTags(tags: string[]): Promise<boolean> {
    const t = this.ticket();
    if (!t) return Promise.resolve(false);
    return this.run(this.tickets.setTags(t.id, tags), undefined, 'Could not update tags');
  }

  async toggleWatch(): Promise<void> {
    const t = this.ticket();
    const uid = this.me.user()?.id;
    if (!t || !uid) return;
    try {
      if (this.isWatching()) {
        await firstValueFrom(this.tickets.removeWatcher(t.id, uid));
        this.toast.success('You stopped watching this ticket');
      } else {
        await firstValueFrom(this.tickets.addWatcher(t.id, { userId: uid, kind: 'Watcher' }));
        this.toast.success('You are watching this ticket');
      }
      this.refresh();
      this.store.refreshCounts();
    } catch (err) {
      toastApiError(this.toast, err, 'Could not update watchers');
    }
  }

  async lock(): Promise<void> {
    const t = this.ticket();
    if (!t) return;
    try {
      const info = await firstValueFrom(this.tickets.lock(t.id));
      this.toast.success('Ticket locked', `Others see that you are working on it until ${new Date(info.until).toLocaleTimeString()}.`);
      this.refresh();
    } catch (err) {
      toastApiError(this.toast, err, 'Could not lock the ticket');
    }
  }

  async unlock(): Promise<void> {
    const t = this.ticket();
    if (!t) return;
    try {
      await firstValueFrom(this.tickets.unlock(t.id));
      this.toast.success('Ticket unlocked');
      this.refresh();
    } catch (err) {
      toastApiError(this.toast, err, 'Could not unlock the ticket');
    }
  }

  private async resolveConflict(e: ApiError, retry: () => Promise<boolean>, edit: FieldEdit): Promise<boolean> {
    const current = e.currentTicket;
    if (current) this.apply(current);
    else this.refresh();
    this.loadTimeline();
    const ref = this.dialogs.open<'reapply' | 'discard', ConflictDialogData>(ConflictDialog, {
      data: { current: current ?? this.ticket(), labels: edit.labels, message: e.title },
      size: 'md',
    });
    const choice = await firstValueFrom(ref.closed);
    if (choice === 'reapply') return retry();
    this.toast.info('Kept the latest saved values');
    return false;
  }
}
