import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { ApiError, RealtimeService, TicketListItemDto, TicketsApi } from '@sd/core';
import { SdAvatar, SdButton, SdEmptyState, SdIcon, SdPriorityBadge, SdSkeleton, SdSlaCountdown } from '@sd/ui';
import { debounceTime, merge } from 'rxjs';

/**
 * Live SLA risk board: open tickets whose SLA is at risk or breached (soonest due first), with countdowns.
 * Re-fetches on `sla.alert` and ticket changes; tickets that just raised an alert are highlighted.
 */
@Component({
  selector: 'desk-sla-risk-board',
  imports: [SdAvatar, SdButton, SdEmptyState, SdIcon, SdPriorityBadge, SdSkeleton, SdSlaCountdown],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="summary">
      <span class="pill pill--danger"><strong>{{ breached().length }}</strong> breached</span>
      <span class="pill pill--warning"><strong>{{ atRisk().length }}</strong> at risk</span>
      <span class="sd-spacer"></span>
      <button sdButton size="sm" variant="ghost" iconOnly icon="refresh" type="button" aria-label="Refresh SLA board" (click)="load()"></button>
    </div>
    @if (loading() && items().length === 0) {
      <sd-skeleton [lines]="5" />
    } @else if (error()) {
      <p class="error" role="alert">{{ error() }} <button sdButton size="sm" variant="link" type="button" (click)="load()">Retry</button></p>
    } @else if (items().length === 0) {
      <sd-empty-state icon="shield-check" title="No SLA risk right now" message="Every open ticket is on track." compact />
    } @else {
      <ul class="board" aria-live="polite">
        @for (t of items(); track t.id) {
          <li [class.is-flash]="flashed().has(t.id)" [class.is-breached]="t.slaState === 'Breached'">
            <button type="button" class="row" (click)="open(t)">
              <sd-sla-countdown [dueAt]="t.dueAt" [state]="t.slaState" compact />
              <span class="num">{{ t.displayNumber }}</span>
              <span class="subject sd-truncate">{{ t.subject }}</span>
              <sd-priority-badge [priority]="t.priority" />
              @if (t.assigneeName) {
                <sd-avatar [name]="t.assigneeName" size="xs" />
              } @else {
                <span class="unassigned" title="Unassigned"><sd-icon name="user-minus" [size]="14" label="Unassigned" /></span>
              }
            </button>
          </li>
        }
      </ul>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: 8px;
    }
    .summary {
      display: flex;
      align-items: center;
      gap: 6px;
    }
    .pill {
      padding: 2px 10px;
      border-radius: var(--sd-radius-pill);
      font-size: var(--sd-text-sm);
    }
    .pill--danger {
      background: var(--sd-sla-breached-bg);
      color: var(--sd-sla-breached);
    }
    .pill--warning {
      background: var(--sd-sla-atrisk-bg);
      color: var(--sd-sla-atrisk);
    }
    .board {
      display: grid;
      gap: 2px;
      max-height: 360px;
      overflow-y: auto;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .board li {
      border-radius: var(--sd-radius-sm);
      transition: background-color 1s var(--sd-ease);
    }
    .board li.is-breached {
      box-shadow: inset 3px 0 0 var(--sd-danger);
    }
    .board li.is-flash {
      background: var(--sd-warning-bg);
    }
    .row {
      display: grid;
      grid-template-columns: 112px 64px 1fr auto auto;
      align-items: center;
      gap: 8px;
      width: 100%;
      min-height: 36px;
      padding: 4px 8px;
      border: 0;
      border-radius: var(--sd-radius-sm);
      background: transparent;
      color: var(--sd-text);
      font: inherit;
      font-size: var(--sd-text-sm);
      text-align: left;
      cursor: pointer;
    }
    .row:hover {
      background: var(--sd-surface-2);
    }
    .num {
      color: var(--sd-text-2);
      font-variant-numeric: tabular-nums;
    }
    .subject {
      min-width: 0;
    }
    .unassigned {
      color: var(--sd-warning);
    }
    .error {
      color: var(--sd-danger);
    }
    @media (max-width: 560px) {
      .row {
        grid-template-columns: 100px 1fr auto;
      }
      .num,
      sd-priority-badge {
        display: none;
      }
    }
  `,
})
export class SlaRiskBoardComponent {
  private readonly tickets = inject(TicketsApi);
  private readonly realtime = inject(RealtimeService);
  private readonly router = inject(Router);

  protected readonly items = signal<TicketListItemDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly flashed = signal<ReadonlySet<string>>(new Set());
  protected readonly breached = computed(() => this.items().filter((t) => t.slaState === 'Breached'));
  protected readonly atRisk = computed(() => this.items().filter((t) => t.slaState === 'AtRisk'));

  constructor() {
    this.load();
    const alerts = this.realtime.slaAlert$.subscribe((e) => {
      this.flashed.update((s) => new Set(s).add(e.ticketId));
      setTimeout(() => this.flashed.update((s) => new Set([...s].filter((id) => id !== e.ticketId))), 6000);
    });
    const refresh = merge(this.realtime.slaAlert$.pipe(debounceTime(800)), this.realtime.ticketChanged$.pipe(debounceTime(5000)), this.realtime.reconnected$).subscribe(
      () => this.load(true),
    );
    // Countdowns tick on their own; re-sort every minute so newly at-risk tickets move up.
    const timer = setInterval(() => this.load(true), 60_000);
    inject(DestroyRef).onDestroy(() => {
      alerts.unsubscribe();
      refresh.unsubscribe();
      clearInterval(timer);
    });
  }

  load(quiet = false): void {
    if (!quiet) this.loading.set(true);
    this.tickets.list({ slaState: ['AtRisk', 'Breached'], statusCategory: 'open', sort: 'due', dir: 'asc', limit: 50 }, { silent: true }).subscribe({
      next: (page) => {
        this.items.set(page.items);
        this.loading.set(false);
        this.error.set(null);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        if (!quiet || this.items().length === 0) this.error.set(ApiError.from(err).userMessage);
      },
    });
  }

  protected open(t: TicketListItemDto): void {
    void this.router.navigate(['/tickets', t.id]);
  }
}
