import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import {
  AnalyticsApi,
  AnalyticsBreakdownDto,
  AnalyticsFilterValues,
  AnalyticsQueryParams,
  AnalyticsResultDto,
  AnalyticsWidgetDto,
  ApiError,
} from '@sd/core';
import { SdButton, SdChart, SdChartClick, SdEmptyState, SdIcon, SdSkeleton } from '@sd/ui';
import type { EChartsCoreOption } from 'echarts/core';
import { Subscription } from 'rxjs';
import { barChart, barIsHorizontal, lineChart, pieChart } from './analytics-charts';
import { filtersToParams, formatDelta, formatValue, mergeFilters } from './analytics-format';
import { AnalyticsStore } from './analytics.store';

/** A drill request raised by a widget or report chart. */
export interface AnalyticsDrill {
  metric: string;
  dimension?: string | null;
  key?: string | null;
  bucket?: string | null;
  filters?: AnalyticsFilterValues;
  period?: string;
}

/**
 * Renders one dashboard widget (KPI, line, bar, pie or table) from catalogue queries with its own loading, empty and
 * error states. Clicking a value raises `drill` (the parent opens the ticket list).
 */
@Component({
  selector: 'desk-analytics-widget',
  imports: [SdButton, SdChart, SdEmptyState, SdIcon, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="widget" [attr.aria-labelledby]="'w-' + widget().id" [attr.aria-busy]="loading()">
      <header class="head">
        <h3 class="title sd-truncate" [id]="'w-' + widget().id">{{ widget().title }}</h3>
        <span class="sd-xs sd-muted sd-truncate period">{{ periodLabel() }}</span>
        <ng-content />
      </header>
      <div class="body">
        @if (loading() && !hasData()) {
          <sd-skeleton height="100%" radius="var(--sd-radius-md)" />
        } @else if (error()) {
          <sd-empty-state icon="circle-alert" title="Could not load" [message]="error()" compact>
            <button sdButton size="sm" type="button" (click)="reload()">Retry</button>
          </sd-empty-state>
        } @else if (widget().type === 'kpi') {
          <button type="button" class="kpi" [disabled]="!drillable()" (click)="drillTotal()" [attr.aria-label]="kpiAria()">
            <span class="kpi-value sd-num">{{ kpiValue() }}</span>
            @if (result()?.deltaPercent !== null && result()?.deltaPercent !== undefined) {
              <span class="delta" [class.good]="deltaGood() === true" [class.bad]="deltaGood() === false">
                <sd-icon [name]="result()!.deltaPercent! > 0 ? 'arrow-up' : result()!.deltaPercent! < 0 ? 'arrow-down' : 'minus'" [size]="12" />
                {{ delta() }} <span class="sd-muted">vs {{ result()?.comparePeriod?.label?.toLowerCase() }}</span>
              </span>
            } @else if (result()?.comparePeriod) {
              <span class="sd-xs sd-muted">No comparison value</span>
            }
            <span class="sd-xs sd-muted">{{ result()?.totalCount ?? 0 }} ticket{{ result()?.totalCount === 1 ? '' : 's' }} counted</span>
          </button>
        } @else if (empty()) {
          <sd-empty-state icon="chart-column" title="No data" message="Nothing matches this period and these filters." compact />
        } @else if (widget().type === 'table') {
          <div class="table-wrap">
            <table class="tbl">
              <caption class="sd-visually-hidden">{{ widget().title }}</caption>
              <thead>
                <tr>
                  <th scope="col">{{ store.dimensionLabel(breakdown()!.dimension) }}</th>
                  @for (c of breakdown()!.columns; track c.key) {
                    <th scope="col" class="num">{{ c.label }}</th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (r of breakdown()!.rows; track r.key ?? r.label) {
                  <tr>
                    <th scope="row">
                      @if (drillable()) {
                        <button type="button" class="link" (click)="drillKey(r.key)">{{ r.label }}</button>
                      } @else {
                        {{ r.label }}
                      }
                    </th>
                    @for (v of r.values; track $index) {
                      <td class="num">{{ format(v, breakdown()!.columns[$index].unit) }}</td>
                    }
                  </tr>
                }
              </tbody>
              <tfoot>
                <tr>
                  <th scope="row">Total</th>
                  @for (v of breakdown()!.totals; track $index) {
                    <td class="num">{{ format(v, breakdown()!.columns[$index].unit) }}</td>
                  }
                </tr>
              </tfoot>
            </table>
          </div>
        } @else {
          <sd-chart [options]="chart()" height="100%" [ariaLabel]="chartAria()" (chartClick)="onChartClick($event)" />
        }
      </div>
    </section>
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
      height: 100%;
    }
    .widget {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 0;
      padding: 12px 14px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
      box-shadow: var(--sd-shadow-xs);
    }
    .head {
      display: flex;
      align-items: center;
      gap: 8px;
      min-width: 0;
      margin-bottom: 6px;
    }
    .title {
      flex: 0 1 auto;
      min-width: 0;
      margin: 0;
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-semibold);
    }
    .period {
      flex: 1;
      min-width: 0;
    }
    .body {
      position: relative;
      flex: 1;
      min-height: 0;
      display: flex;
      flex-direction: column;
    }
    .body > sd-chart {
      flex: 1;
      min-height: 120px;
    }
    .kpi {
      display: flex;
      flex: 1;
      flex-direction: column;
      justify-content: center;
      gap: 4px;
      padding: 0;
      border: 0;
      background: transparent;
      color: var(--sd-text);
      font: inherit;
      text-align: left;
      cursor: pointer;
      border-radius: var(--sd-radius-sm);
    }
    .kpi:disabled {
      cursor: default;
    }
    .kpi:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .kpi-value {
      font-size: var(--sd-text-3xl);
      font-weight: var(--sd-weight-bold);
      letter-spacing: -0.02em;
      line-height: 1.1;
    }
    .delta {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      font-size: var(--sd-text-xs);
      color: var(--sd-text-2);
    }
    .delta.good {
      color: var(--sd-success);
    }
    .delta.bad {
      color: var(--sd-danger);
    }
    .table-wrap {
      flex: 1;
      min-height: 0;
      overflow: auto;
    }
    .tbl {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--sd-text-sm);
    }
    .tbl th,
    .tbl td {
      padding: 6px 8px;
      border-bottom: 1px solid var(--sd-divider);
      text-align: left;
      white-space: nowrap;
    }
    .tbl thead th {
      position: sticky;
      top: 0;
      background: var(--sd-surface);
      color: var(--sd-text-2);
      font-weight: var(--sd-weight-medium);
    }
    .tbl tbody th {
      font-weight: var(--sd-weight-regular, 400);
    }
    .tbl tfoot th,
    .tbl tfoot td {
      font-weight: var(--sd-weight-semibold);
    }
    .num {
      text-align: right !important;
      font-variant-numeric: tabular-nums;
    }
    .link {
      padding: 0;
      border: 0;
      background: none;
      color: var(--sd-link, var(--sd-primary));
      font: inherit;
      text-align: left;
      cursor: pointer;
    }
    .link:hover {
      text-decoration: underline;
    }
    .link:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
  `,
})
export class AnalyticsWidgetComponent {
  private readonly api = inject(AnalyticsApi);
  protected readonly store = inject(AnalyticsStore);

  readonly widget = input.required<AnalyticsWidgetDto>();
  /** Dashboard period (the widget's own period wins). */
  readonly period = input('last_30_days');
  readonly from = input<string | null>(null);
  readonly to = input<string | null>(null);
  /** Page-level comparison ('' = only when the widget asks for it). */
  readonly compare = input<'' | 'previous' | 'last_year'>('');
  readonly filters = input<AnalyticsFilterValues>({});
  readonly drill = output<AnalyticsDrill>();

  protected readonly result = signal<AnalyticsResultDto | null>(null);
  protected readonly breakdown = signal<AnalyticsBreakdownDto | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  private sub: Subscription | null = null;

  private readonly metric = computed(() => this.store.metric(this.widget().metric));
  private readonly effectivePeriod = computed(() => this.widget().period || this.period());
  private readonly effectiveFilters = computed(() => mergeFilters(this.filters(), filtersToParams(this.widget().filters)));
  protected readonly drillable = computed(() => this.metric()?.drillable ?? false);
  protected readonly hasData = computed(() => !!this.result() || !!this.breakdown());
  protected readonly periodLabel = computed(() => {
    const p = this.effectivePeriod();
    return p === 'custom' && this.from() ? `${this.from()} – ${this.to()}` : this.store.periodLabel(p);
  });

  private readonly params = computed<AnalyticsQueryParams>(() => {
    const w = this.widget();
    const period = this.effectivePeriod();
    const custom = period === 'custom';
    return {
      metric: w.metric,
      dimension: w.type === 'kpi' ? undefined : (w.dimension ?? undefined),
      limit: w.limit ?? undefined,
      period,
      from: custom ? (this.from() ?? undefined) : undefined,
      to: custom ? (this.to() ?? undefined) : undefined,
      compare: this.compare() || (w.compare ? 'previous' : undefined),
      ...this.effectiveFilters(),
    };
  });

  /** Changes only when the request changes (title edits or layout moves do not refetch). */
  private readonly requestKey = computed(() => {
    const w = this.widget();
    return JSON.stringify([this.params(), w.type === 'table', w.metrics ?? []]);
  });

  protected readonly empty = computed(() => {
    if (this.widget().type === 'table') return (this.breakdown()?.rows.length ?? 0) === 0;
    const r = this.result();
    return !r || r.rows.length === 0 || r.rows.every((x) => x.value === null || x.value === 0);
  });

  protected readonly kpiValue = computed(() => formatValue(this.result()?.total ?? null, this.metric()?.unit));
  protected readonly delta = computed(() => formatDelta(this.result()?.deltaPercent));
  protected readonly deltaGood = computed<boolean | null>(() => {
    const d = this.result()?.deltaPercent;
    if (d === null || d === undefined || d === 0) return null;
    return this.metric()?.higherIsBetter ? d > 0 : d < 0;
  });
  protected readonly kpiAria = computed(
    () => `${this.widget().title}: ${this.kpiValue()}${this.delta() ? `, ${this.delta()} versus comparison period` : ''}${this.drillable() ? '. Open the tickets' : ''}`,
  );
  protected readonly chartAria = computed(() => {
    const r = this.result();
    if (!r) return this.widget().title;
    const rows = r.rows.slice(0, 12).map((x) => `${x.label} ${formatValue(x.value, r.metric.unit)}`);
    return `${this.widget().title}: ${rows.join(', ')}`;
  });

  protected readonly chart = computed<EChartsCoreOption>(() => {
    const r = this.result();
    const w = this.widget();
    if (!r) return {};
    const labels = r.rows.map((x) => x.label);
    const series = [
      {
        name: r.metric.label,
        unit: r.metric.unit,
        values: r.rows.map((x) => x.value),
        compareValues: r.comparePeriod ? r.rows.map((x) => x.compareValue) : null,
        compareName: r.comparePeriod?.label,
      },
    ];
    if (w.type === 'line') return lineChart(labels, series);
    if (w.type === 'pie') return pieChart(labels, series[0].values, r.metric.unit);
    return barChart(labels, series, { time: ['day', 'week', 'month'].includes(r.dimension) });
  });

  constructor() {
    effect(() => {
      this.requestKey();
      untracked(() => this.reload());
    });
    inject(DestroyRef).onDestroy(() => this.sub?.unsubscribe());
  }

  reload(): void {
    this.sub?.unsubscribe();
    this.loading.set(true);
    this.error.set(null);
    const w = this.widget();
    const p = this.params();
    if (w.type === 'table') {
      this.sub = this.api.breakdown({ ...p, metrics: [w.metric, ...(w.metrics ?? [])].join(','), compare: undefined }).subscribe({
        next: (b) => {
          this.breakdown.set(b);
          this.loading.set(false);
        },
        error: (e: unknown) => this.fail(e),
      });
      return;
    }
    this.sub = this.api.query(p).subscribe({
      next: (r) => {
        this.result.set(r);
        this.loading.set(false);
      },
      error: (e: unknown) => this.fail(e),
    });
  }

  private fail(e: unknown): void {
    this.error.set(ApiError.from(e).userMessage);
    this.loading.set(false);
  }

  protected format(v: number | null, unit: string): string {
    return formatValue(v, unit);
  }

  private base(): AnalyticsDrill {
    return { metric: this.widget().metric, filters: this.effectiveFilters(), period: this.effectivePeriod() };
  }

  protected drillTotal(): void {
    if (this.drillable()) this.drill.emit(this.base());
  }

  protected drillKey(key: string | null): void {
    if (!this.drillable()) return;
    const dim = this.breakdown()?.dimension ?? this.widget().dimension ?? null;
    const time = dim === 'day' || dim === 'week' || dim === 'month';
    this.drill.emit({ ...this.base(), dimension: dim, key: time ? null : (key ?? 'none'), bucket: time ? key : null });
  }

  protected onChartClick(e: SdChartClick): void {
    const r = this.result();
    if (!r || !this.drillable() || e.componentType !== 'series') return;
    let index = e.dataIndex;
    const w = this.widget();
    if (w.type === 'bar' && barIsHorizontal(r.rows.map((x) => x.label), ['day', 'week', 'month'].includes(r.dimension))) index = r.rows.length - 1 - index;
    const row = r.rows[index];
    if (!row) return;
    const time = r.dimension === 'day' || r.dimension === 'week' || r.dimension === 'month';
    this.drill.emit({ ...this.base(), dimension: r.dimension, key: time ? null : (row.key ?? 'none'), bucket: time ? row.key : null });
  }
}
