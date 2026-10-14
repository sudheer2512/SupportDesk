import { ChangeDetectionStrategy, Component, effect, inject, input, model, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AnalyticsApi, AnalyticsDrilldownTicketDto, AnalyticsQueryParams, ApiError } from '@sd/core';
import { SdButton, SdDateTimePipe, SdDrawer, SdEmptyState, SdPriorityBadge, SdSkeleton } from '@sd/ui';
import { formatHours } from './analytics-format';

/**
 * The tickets behind a number: metric population for the period, optionally narrowed to a dimension value or time
 * bucket. Keyset paged ("Load more"); every row opens the ticket in the workspace.
 */
@Component({
  selector: 'desk-analytics-drilldown',
  imports: [RouterLink, SdButton, SdDateTimePipe, SdDrawer, SdEmptyState, SdPriorityBadge, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-drawer [(open)]="open" [title]="title()" ariaLabel="Tickets behind this number" width="560px">
      @if (period()) {
        <p class="sd-small sd-muted meta">{{ period() }} · {{ total() }} ticket{{ total() === 1 ? '' : 's' }}</p>
      }
      @if (loading() && items().length === 0) {
        <div aria-busy="true"><sd-skeleton [lines]="8" /></div>
      } @else if (error()) {
        <sd-empty-state icon="circle-alert" title="Could not load the tickets" [message]="error()" compact>
          <button sdButton size="sm" type="button" (click)="load(true)">Retry</button>
        </sd-empty-state>
      } @else if (items().length === 0) {
        <sd-empty-state icon="inbox" title="No tickets" message="No ticket contributes to this number." compact />
      } @else {
        <ul class="list">
          @for (t of items(); track t.ticketId) {
            <li>
              <a class="row" [routerLink]="['/tickets', t.number]" (click)="open.set(false)">
                <span class="top">
                  <span class="num">{{ t.displayNumber }}</span>
                  <span class="subject sd-truncate">{{ t.subject || '(no subject)' }}</span>
                  <sd-priority-badge [priority]="t.priority" />
                </span>
                <span class="sub sd-xs sd-muted">
                  {{ t.status }} · {{ t.assignee ?? 'Unassigned' }}@if (t.customer) { · {{ t.customer }} } · created {{ t.createdAt | sdDateTime }}
                </span>
                <span class="facts sd-xs">
                  @if (t.firstResponseBusinessMinutes !== null) {
                    <span>First response {{ hours(t.firstResponseBusinessMinutes) }} bh</span>
                  }
                  @if (t.resolutionBusinessMinutes !== null) {
                    <span>Resolution {{ hours(t.resolutionBusinessMinutes) }} bh</span>
                  }
                  @if (t.slaBreached) {
                    <span class="bad">SLA breached</span>
                  }
                  @if (t.reopenCount > 0) {
                    <span>Reopened ×{{ t.reopenCount }}</span>
                  }
                  @if (t.csatScore !== null) {
                    <span>CSAT {{ t.csatScore }}/5</span>
                  }
                </span>
              </a>
            </li>
          }
        </ul>
        @if (nextCursor()) {
          <div class="more">
            <button sdButton size="sm" type="button" [loading]="loading()" (click)="load(false)">Load more</button>
          </div>
        }
      }
    </sd-drawer>
  `,
  styles: `
    .meta {
      margin: 0 0 var(--sd-space-3);
    }
    .list {
      display: grid;
      gap: 2px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .row {
      display: grid;
      gap: 2px;
      padding: 8px;
      border-radius: var(--sd-radius-sm);
      color: var(--sd-text);
      text-decoration: none;
    }
    .row:hover {
      background: var(--sd-surface-2);
    }
    .row:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .top {
      display: flex;
      align-items: center;
      gap: 8px;
      min-width: 0;
    }
    .num {
      flex: none;
      color: var(--sd-text-2);
      font-variant-numeric: tabular-nums;
    }
    .subject {
      flex: 1;
      min-width: 0;
      font-weight: var(--sd-weight-medium);
    }
    .facts {
      display: flex;
      flex-wrap: wrap;
      gap: 4px 12px;
      color: var(--sd-text-2);
    }
    .bad {
      color: var(--sd-danger);
    }
    .more {
      display: flex;
      justify-content: center;
      padding: var(--sd-space-3);
    }
  `,
})
export class AnalyticsDrilldownComponent {
  private readonly api = inject(AnalyticsApi);

  readonly open = model(false);
  /** Metric, period, filters and the dimension key or time bucket to open. */
  readonly params = input<AnalyticsQueryParams | null>(null);

  protected readonly items = signal<AnalyticsDrilldownTicketDto[]>([]);
  protected readonly title = signal('Tickets');
  protected readonly period = signal('');
  protected readonly total = signal(0);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    effect(() => {
      const p = this.params();
      const open = this.open();
      if (p && open) untracked(() => this.load(true));
    });
  }

  protected hours(minutes: number): string {
    return formatHours(minutes / 60);
  }

  protected load(reset: boolean): void {
    const p = this.params();
    if (!p) return;
    this.loading.set(true);
    this.error.set(null);
    if (reset) {
      this.items.set([]);
      this.nextCursor.set(null);
    }
    this.api.drilldown({ ...p, cursor: reset ? undefined : (this.nextCursor() ?? undefined), pageSize: 50 }).subscribe({
      next: (r) => {
        this.items.update((list) => (reset ? r.items : [...list, ...r.items]));
        this.title.set(r.title);
        this.period.set(`${r.period.label} (${r.period.fromDay} – ${r.period.toDay})`);
        this.total.set(r.total);
        this.nextCursor.set(r.nextCursor);
        this.loading.set(false);
      },
      error: (e: unknown) => {
        this.error.set(ApiError.from(e).userMessage);
        this.loading.set(false);
      },
    });
  }
}
