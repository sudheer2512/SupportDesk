import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, signal, untracked } from '@angular/core';
import { Router } from '@angular/router';
import { DeletedTicketDto, WorkbenchApi, cursorList } from '@sd/core';
import { SdBadge, SdButton, SdDialogService, SdEmptyState, SdIcon, SdRelativeTimePipe, SdSkeleton, SdTooltip, ToastService } from '@sd/ui';
import { Subject, debounceTime } from 'rxjs';
import { toastApiError } from '../shared/ticket-ui';
import { TicketWorkbenchStore } from './workbench.store';

/**
 * Recycle bin of the workspace ("Deleted tickets" queue, tickets.delete): deleted and spam tickets in the agent's scope,
 * newest deletion first (GET /tickets/deleted), with Restore (POST /tickets/{id}/restore). Tickets under an active
 * legal hold are flagged — they are kept for the hold and can still be restored.
 */
@Component({
  selector: 'desk-recycle-bin',
  imports: [SdBadge, SdButton, SdEmptyState, SdIcon, SdRelativeTimePipe, SdSkeleton, SdTooltip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="toolbar">
      <h2>Deleted tickets</h2>
      <span class="sd-spacer"></span>
      <label class="search">
        <sd-icon name="search" [size]="14" />
        <span class="sd-visually-hidden">Filter deleted tickets</span>
        <input type="search" placeholder="Filter by # or subject…" [value]="query()" (input)="onQuery($any($event.target).value)" />
      </label>
      <button sdButton size="sm" variant="ghost" iconOnly icon="refresh" type="button" aria-label="Refresh deleted tickets" sdTooltip="Refresh" (click)="list.load()"></button>
    </div>
    <p class="hint sd-small">
      <sd-icon name="info" [size]="14" />
      Deleted and spam tickets leave every queue until someone restores them. Tickets under a legal hold are kept for the hold.
    </p>

    @if (list.error() && list.items().length === 0) {
      <sd-empty-state icon="triangle-alert" title="Deleted tickets could not be loaded" [message]="list.error()!.userMessage">
        <button sdButton size="sm" icon="refresh" type="button" (click)="list.load()">Try again</button>
      </sd-empty-state>
    } @else if (list.loading() && list.items().length === 0) {
      <sd-skeleton [lines]="6" />
    } @else if (list.isEmpty()) {
      <sd-empty-state
        icon="trash"
        [title]="query() ? 'No deleted tickets match' : 'The recycle bin is empty'"
        [message]="query() ? 'Try a different number or subject.' : 'Tickets you delete or mark as spam appear here and can be restored.'"
      />
    } @else {
      <ul class="rows" aria-label="Deleted tickets">
        @for (t of list.items(); track t.id) {
          <li class="row">
            <div class="main">
              <div class="line">
                <span class="num sd-num">{{ t.displayNumber }}</span>
                <span class="subject sd-truncate" [title]="t.subject">{{ t.subject }}</span>
              </div>
              <div class="meta sd-small sd-muted">
                <span>{{ t.customerName ?? t.contactName ?? 'No customer' }}</span>
                <span aria-hidden="true">·</span>
                <span>{{ t.departmentName }}</span>
                <span aria-hidden="true">·</span>
                <span>{{ t.isSpam ? 'Marked as spam' : 'Deleted' }} {{ t.deletedAt | sdRelativeTime }}@if (t.deletedByName) { by {{ t.deletedByName }}}</span>
              </div>
              @if (t.isSpam || t.underLegalHold) {
                <div class="badges">
                  @if (t.isSpam) {
                    <sd-badge tone="warning" icon="shield-off" size="sm">Spam</sd-badge>
                  }
                  @if (t.underLegalHold) {
                    <sd-badge tone="info" icon="scale" size="sm" sdTooltip="An active legal hold covers this ticket: it is kept for the hold.">Legal hold</sd-badge>
                  }
                </div>
              }
            </div>
            <button sdButton size="sm" icon="archive-restore" type="button" [attr.aria-label]="'Restore ' + t.displayNumber" [loading]="restoring() === t.id" (click)="restore(t)">
              Restore
            </button>
          </li>
        }
      </ul>
      @if (list.hasMore()) {
        <div class="more">
          <button sdButton size="sm" variant="ghost" type="button" [loading]="list.loadingMore()" (click)="list.loadMore()">Load more</button>
        </div>
      }
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: 8px;
      min-height: 0;
      height: 100%;
      padding: 8px 12px 12px;
      overflow-y: auto;
    }
    .toolbar {
      display: flex;
      align-items: center;
      gap: 8px;
      min-height: 36px;
      flex-wrap: wrap;
    }
    h2 {
      font-size: var(--sd-text-lg);
      font-weight: var(--sd-weight-semibold);
    }
    .search {
      display: flex;
      align-items: center;
      gap: 6px;
      width: min(280px, 100%);
      height: 32px;
      padding: 0 8px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      color: var(--sd-text-3);
    }
    .search:focus-within {
      border-color: var(--sd-primary);
      box-shadow: var(--sd-focus-ring);
    }
    .search input {
      flex: 1;
      min-width: 0;
      border: 0;
      outline: none;
      background: transparent;
      color: var(--sd-text);
      font: inherit;
      font-size: var(--sd-text-sm);
    }
    .hint {
      display: flex;
      align-items: center;
      gap: 6px;
      color: var(--sd-text-2);
    }
    .rows {
      display: grid;
      gap: 6px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .row {
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 10px 12px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
    }
    .main {
      display: grid;
      gap: 3px;
      flex: 1;
      min-width: 0;
    }
    .line {
      display: flex;
      align-items: baseline;
      gap: 8px;
      min-width: 0;
    }
    .num {
      flex: none;
      color: var(--sd-text-2);
      font-weight: var(--sd-weight-medium);
    }
    .subject {
      min-width: 0;
      font-weight: var(--sd-weight-medium);
    }
    .meta {
      display: flex;
      flex-wrap: wrap;
      gap: 2px 6px;
    }
    .badges {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    .more {
      display: flex;
      justify-content: center;
      padding: 8px;
    }
    @media (max-width: 767px) {
      :host {
        padding: 8px;
      }
      .search {
        width: 100%;
        order: 5;
      }
      .row {
        flex-direction: column;
        align-items: stretch;
      }
    }
  `,
})
export class RecycleBinComponent {
  private readonly workbench = inject(WorkbenchApi);
  private readonly store = inject(TicketWorkbenchStore);
  private readonly dialogs = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  protected readonly query = signal('');
  protected readonly restoring = signal<string | null>(null);
  private readonly query$ = new Subject<string>();
  protected readonly list = cursorList<DeletedTicketDto>((cursor) => this.workbench.deletedTickets({ cursor, limit: 50 }));

  constructor() {
    effect(() => {
      const q = this.query().trim();
      untracked(() => this.list.setFetcher((cursor) => this.workbench.deletedTickets({ q: q || null, cursor, limit: 50 })));
    });
    const sub = this.query$.pipe(debounceTime(300)).subscribe((q) => this.query.set(q));
    const reload = this.store.listReload.subscribe(() => this.list.load());
    inject(DestroyRef).onDestroy(() => {
      sub.unsubscribe();
      reload.unsubscribe();
    });
  }

  protected onQuery(value: string): void {
    this.query$.next(value);
  }

  protected async restore(t: DeletedTicketDto): Promise<void> {
    const ok = await this.dialogs.confirm({
      title: `Restore ${t.displayNumber}?`,
      message: t.isSpam
        ? 'The ticket returns to its queues and is no longer treated as spam.'
        : 'The ticket returns to its queues with its conversation and history.',
      confirmLabel: 'Restore',
    });
    if (!ok) return;
    this.restoring.set(t.id);
    this.workbench.restoreTicket(t.id).subscribe({
      next: () => {
        this.restoring.set(null);
        this.list.remove(t.id);
        this.store.refreshCounts();
        this.toast.show({
          kind: 'success',
          title: `Restored ${t.displayNumber}`,
          message: t.subject,
          action: { label: 'Open', run: () => this.open(t) },
        });
      },
      error: (err: unknown) => {
        this.restoring.set(null);
        toastApiError(this.toast, err, 'Could not restore the ticket');
        this.list.load();
      },
    });
  }

  private open(t: DeletedTicketDto): void {
    this.store.openTab({ id: t.id, displayNumber: t.displayNumber, subject: t.subject });
    void this.router.navigate(['/tickets', t.id]);
  }
}
