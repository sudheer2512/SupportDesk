import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { AiApi, AnomalyAlertDto, AnomalyStatus, MeStore } from '@sd/core';
import { SdBadge, SdButton, SdCard, SdChart, SdCombobox, SdEmptyState, SdFormField, SdOption, SdSkeleton, SdTone, ToastService } from '@sd/ui';
import type { EChartsCoreOption } from 'echarts/core';
import { aiErrorMessage, humanize } from '../shared/ai-ui';

/** Tickets-list filter per anomaly dimension; category and version spikes open all of that day's tickets. */
const DRILL_PARAM: Record<string, string> = { product: 'productId', customer: 'customerId' };

/**
 * Unusual daily ticket volume per product, version, customer or category (z-score ≥ 3 against the trailing 28 days
 * and at least five tickets). Supervisors acknowledge or dismiss alerts and drill into the tickets behind them.
 */
@Component({
  selector: 'desk-ai-anomalies',
  imports: [SdCard, SdButton, SdBadge, SdChart, SdCombobox, SdFormField, SdEmptyState, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="layout">
      <sd-card>
        <div class="filters">
          <sd-form-field label="Status">
            <sd-combobox [options]="statusOptions" [value]="status()" (valueChange)="status.set($any($event)); load()" placeholder="Any status" [searchable]="false" />
          </sd-form-field>
          <sd-form-field label="Period">
            <sd-combobox [options]="dayOptions" [value]="days()" (valueChange)="days.set($any($event) ?? '30'); load()" [clearable]="false" [searchable]="false" />
          </sd-form-field>
          <button sdButton icon="refresh" [loading]="loading()" (click)="load()">Refresh</button>
        </div>
        <p class="sd-small sd-muted note">
          Detection runs hourly and compares each day's tickets with the previous 28 days for every product, version, customer and category.
        </p>
      </sd-card>

      @if (error()) {
        <div class="error" role="alert">{{ error() }}</div>
      }

      @if (loading() && alerts().length === 0) {
        <sd-card><sd-skeleton [lines]="6" /></sd-card>
      } @else if (alerts().length === 0) {
        <sd-card><sd-empty-state icon="activity" title="No unusual volume" message="No product, version, customer or category had a spike in this period." /></sd-card>
      } @else {
        <sd-card title="Volume vs. typical day" subtitle="Tickets on the alert day compared with the 28-day average">
          <sd-chart [options]="chart()" height="260px" ariaLabel="Ticket counts on anomaly days compared with their baseline" (chartClick)="drill(alerts()[$event.dataIndex])" />
        </sd-card>
        <sd-card padding="none">
          <div class="table-wrap">
            <table class="grid">
              <caption class="sd-visually-hidden">Anomaly alerts</caption>
              <thead>
                <tr>
                  <th scope="col">Day</th>
                  <th scope="col">What</th>
                  <th scope="col" class="num">Tickets</th>
                  <th scope="col" class="num">Typical</th>
                  <th scope="col" class="num">z-score</th>
                  <th scope="col">Status</th>
                  <th scope="col"><span class="sd-visually-hidden">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                @for (a of alerts(); track a.id) {
                  <tr>
                    <td>{{ a.day }}</td>
                    <td>
                      <sd-badge size="sm" variant="outline">{{ label(a.dimension) }}</sd-badge>
                      <strong class="name">{{ a.dimensionLabel }}</strong>
                    </td>
                    <td class="num">{{ a.count }}</td>
                    <td class="num">{{ a.baseline.toFixed(1) }} <span class="sd-muted">± {{ a.stdDev.toFixed(1) }}</span></td>
                    <td class="num"><sd-badge size="sm" [tone]="a.score >= 6 ? 'danger' : 'warning'">{{ a.score.toFixed(1) }}</sd-badge></td>
                    <td><sd-badge size="sm" [tone]="tone(a.status)" dot>{{ a.status }}</sd-badge></td>
                    <td class="actions">
                      <button sdButton size="sm" variant="ghost" icon="external-link" (click)="drill(a)">Tickets</button>
                      @if (canManage() && a.status === 'Open') {
                        <button sdButton size="sm" icon="check" [loading]="busy() === a.id + 'ack'" (click)="setStatus(a, 'Acknowledged')">Acknowledge</button>
                        <button sdButton size="sm" variant="ghost" icon="x" [loading]="busy() === a.id + 'dis'" (click)="setStatus(a, 'Dismissed')">Dismiss</button>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </sd-card>
      }
    </div>
  `,
  styles: `
    .layout {
      display: grid;
      gap: var(--sd-space-4);
    }
    .filters {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
      gap: 12px;
      align-items: end;
    }
    .note {
      margin-top: 10px;
    }
    .error {
      padding: 10px 14px;
      border: 1px solid var(--sd-danger-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-danger-bg);
      color: var(--sd-danger);
    }
    .table-wrap {
      overflow: auto;
    }
    .grid {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--sd-text-sm);
    }
    .grid th,
    .grid td {
      padding: 9px 14px;
      border-bottom: 1px solid var(--sd-divider);
      text-align: start;
      vertical-align: middle;
    }
    .grid th {
      background: var(--sd-surface-2);
      color: var(--sd-text-2);
      font-weight: var(--sd-weight-semibold);
    }
    .num {
      text-align: end !important;
      font-variant-numeric: tabular-nums;
    }
    .name {
      margin-left: 8px;
    }
    .actions {
      display: flex;
      gap: 6px;
      justify-content: flex-end;
      flex-wrap: wrap;
    }
  `,
})
export class AnomaliesPage implements OnInit {
  private readonly ai = inject(AiApi);
  private readonly me = inject(MeStore);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly statusOptions: SdOption[] = [
    { value: 'Open', label: 'Open' },
    { value: 'Acknowledged', label: 'Acknowledged' },
    { value: 'Dismissed', label: 'Dismissed' },
  ];
  protected readonly dayOptions: SdOption[] = [
    { value: '7', label: 'Last 7 days' },
    { value: '30', label: 'Last 30 days' },
    { value: '90', label: 'Last 90 days' },
  ];
  protected readonly status = signal<string | null>(null);
  protected readonly days = signal<string>('30');
  protected readonly alerts = signal<AnomalyAlertDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal<string | null>(null);
  protected readonly canManage = computed(() => this.me.canAny(['supervisor.monitor', 'admin.ai']));
  protected readonly label = humanize;

  protected readonly chart = computed<EChartsCoreOption>(() => {
    const list = this.alerts();
    return {
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
      legend: { top: 0, right: 0 },
      grid: { left: 8, right: 16, top: 32, bottom: 8, containLabel: true },
      xAxis: { type: 'category', data: list.map((a) => `${a.dimensionLabel} (${a.day.slice(5)})`), axisLabel: { width: 120, overflow: 'truncate', interval: 0, rotate: list.length > 5 ? 30 : 0 } },
      yAxis: { type: 'value', name: 'tickets' },
      series: [
        { name: 'Tickets', type: 'bar', barMaxWidth: 24, data: list.map((a) => a.count) },
        { name: 'Typical day', type: 'bar', barMaxWidth: 24, data: list.map((a) => Number(a.baseline.toFixed(2))) },
      ],
    };
  });

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.ai
      .anomalies((this.status() as AnomalyStatus | null) ?? null, Number(this.days()), { silent: true })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (list) => {
          this.alerts.set(list);
          this.loading.set(false);
        },
        error: (err: unknown) => {
          this.loading.set(false);
          this.error.set(aiErrorMessage(err));
        },
      });
  }

  protected tone(status: string): SdTone {
    return status === 'Open' ? 'warning' : status === 'Acknowledged' ? 'success' : 'neutral';
  }

  protected setStatus(a: AnomalyAlertDto, status: 'Acknowledged' | 'Dismissed'): void {
    this.busy.set(a.id + (status === 'Acknowledged' ? 'ack' : 'dis'));
    const call = status === 'Acknowledged' ? this.ai.acknowledgeAnomaly(a.id) : this.ai.dismissAnomaly(a.id);
    call.subscribe({
      next: (updated) => {
        this.busy.set(null);
        this.alerts.update((list) => list.map((x) => (x.id === updated.id ? updated : x)));
        this.toast.success(`${status}: ${updated.dimensionLabel}`);
      },
      error: () => this.busy.set(null),
    });
  }

  /** Opens the tickets list for the alert's day (and product/customer when that is the dimension). */
  protected drill(a: AnomalyAlertDto | undefined): void {
    if (!a) return;
    const from = new Date(`${a.day}T00:00:00Z`);
    const to = new Date(from.getTime() + 86_400_000);
    const params: Record<string, string> = { createdFrom: from.toISOString(), createdTo: to.toISOString() };
    const p = DRILL_PARAM[a.dimension];
    if (p && a.dimensionId) params[p] = a.dimensionId;
    void this.router.navigate(['/tickets'], { queryParams: params });
  }
}
