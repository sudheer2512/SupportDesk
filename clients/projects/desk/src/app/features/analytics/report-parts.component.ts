import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AnalyticsReportChartDto, AnalyticsReportRow, AnalyticsReportTableDto } from '@sd/core';
import { SdChart, SdChartClick, SdDateTimePipe, SdEmptyState } from '@sd/ui';
import type { EChartsCoreOption } from 'echarts/core';
import { barChart, barIsHorizontal, lineChart, paretoChart, pieChart } from './analytics-charts';
import { formatValue } from './analytics-format';

/** Drill request from a report chart or table: the metric plus a dimension key or time bucket. */
export interface ReportDrill {
  metric: string;
  dimension: string | null;
  key: string | null;
  bucket: string | null;
}

const TIME = new Set(['day', 'week', 'month']);

/** One report chart (line, bar, stacked bar, pie or Pareto) with drill-down on click. */
@Component({
  selector: 'desk-report-chart',
  imports: [SdChart, SdEmptyState],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="card" [attr.aria-labelledby]="'c-' + chart().id">
      <h3 class="title" [id]="'c-' + chart().id">{{ chart().title }}</h3>
      @if (empty()) {
        <sd-empty-state icon="chart-column" title="No data for this period" compact />
      } @else {
        <sd-chart [options]="options()" height="260px" [ariaLabel]="aria()" (chartClick)="click($event)" />
        @if (chart().drillMetric) {
          <p class="sd-xs sd-muted hint">Select a {{ chart().type === 'line' ? 'point' : 'bar or slice' }} to see its tickets.</p>
        }
      }
    </section>
  `,
  styles: `
    .card {
      height: 100%;
      padding: 12px 14px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
      box-shadow: var(--sd-shadow-xs);
    }
    .title {
      margin: 0 0 6px;
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-semibold);
    }
    .hint {
      margin: 4px 0 0;
    }
  `,
})
export class ReportChartComponent {
  readonly chart = input.required<AnalyticsReportChartDto>();
  readonly drill = output<ReportDrill>();

  protected readonly empty = computed(() => {
    const c = this.chart();
    return c.categories.length === 0 || c.series.every((s) => s.values.every((v) => v === null || v === 0));
  });

  protected readonly options = computed<EChartsCoreOption>(() => {
    const c = this.chart();
    const series = c.series.map((s) => ({ name: s.label, unit: s.unit, values: s.values, compareValues: s.compareValues, compareName: `${s.label} (compare)` }));
    switch (c.type) {
      case 'line':
        return lineChart(c.categories, series);
      case 'pie':
        return pieChart(c.categories, series[0]?.values ?? [], series[0]?.unit ?? 'tickets');
      case 'pareto':
        return paretoChart(c.categories, c.series[0]?.values ?? [], c.series[1]?.values ?? [], c.series[0]?.label ?? '', c.series[0]?.unit ?? 'tickets');
      case 'stacked-bar':
        return barChart(c.categories, series, { stacked: true, time: !!c.dimension && TIME.has(c.dimension) });
      default:
        return barChart(c.categories, series, { time: !!c.dimension && TIME.has(c.dimension) });
    }
  });

  protected readonly aria = computed(() => {
    const c = this.chart();
    const s = c.series[0];
    if (!s) return c.title;
    return `${c.title}: ` + c.categories.slice(0, 15).map((cat, i) => `${cat} ${formatValue(s.values[i] ?? null, s.unit)}`).join(', ');
  });

  protected click(e: SdChartClick): void {
    const c = this.chart();
    if (!c.drillMetric || e.componentType !== 'series') return;
    let index = e.dataIndex;
    if ((c.type === 'bar' || c.type === 'stacked-bar') && barIsHorizontal(c.categories, !!c.dimension && TIME.has(c.dimension))) index = c.categories.length - 1 - index;
    const key = c.categoryKeys[index];
    if (key === undefined) return;
    const time = !!c.dimension && TIME.has(c.dimension);
    this.drill.emit({ metric: c.drillMetric, dimension: c.dimension, key: time ? null : (key ?? 'none'), bucket: time ? key : null });
  }
}

/** One report table: formatted cells, totals row, ticket links and per-row drill-down. */
@Component({
  selector: 'desk-report-table',
  imports: [RouterLink, SdDateTimePipe, SdEmptyState],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="card" [attr.aria-labelledby]="'t-' + table().id">
      <h3 class="title" [id]="'t-' + table().id">{{ table().title }} <span class="sd-xs sd-muted">({{ dataRows() }} row{{ dataRows() === 1 ? '' : 's' }})</span></h3>
      @if (table().rows.length === 0) {
        <sd-empty-state icon="table" title="No rows for this period" compact />
      } @else {
        <div class="wrap" tabindex="0" [attr.aria-label]="table().title + ' (scrollable)'">
          <table>
            <thead>
              <tr>
                @for (c of table().columns; track c.key) {
                  <th scope="col" [class.num]="isNum(c.type)">{{ c.label }}</th>
                }
              </tr>
            </thead>
            <tbody>
              @for (r of table().rows; track $index) {
                <tr [class.total]="r['_total']">
                  @for (c of table().columns; track c.key; let first = $first) {
                    @if (first) {
                      <th scope="row">
                        @if (c.type === 'ticket' && r['_ticket']) {
                          <a [routerLink]="['/tickets', r['_ticket']]">{{ r[c.key] }}</a>
                        } @else if (canDrill(r)) {
                          <button type="button" class="link" (click)="drillRow(r)" [attr.aria-label]="'Show tickets for ' + r[c.key]">{{ r[c.key] }}</button>
                        } @else {
                          {{ r[c.key] ?? '—' }}
                        }
                      </th>
                    } @else {
                      <td [class.num]="isNum(c.type)" [class.text]="c.type === 'text'">
                        @if (c.type === 'datetime' || c.type === 'date') {
                          {{ r[c.key] ? (asText(r[c.key]) | sdDateTime: (c.type === 'date' ? 'date' : undefined)) : '—' }}
                        } @else if (c.type === 'ticket' && r['_ticket']) {
                          <a [routerLink]="['/tickets', r['_ticket']]">{{ r[c.key] }}</a>
                        } @else {
                          {{ cell(r[c.key], c.type) }}
                        }
                      </td>
                    }
                  }
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </section>
  `,
  styles: `
    .card {
      padding: 12px 14px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
      box-shadow: var(--sd-shadow-xs);
      min-width: 0;
    }
    .title {
      margin: 0 0 8px;
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-semibold);
    }
    .wrap {
      max-height: 480px;
      overflow: auto;
    }
    .wrap:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    table {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--sd-text-sm);
    }
    th,
    td {
      padding: 6px 10px;
      border-bottom: 1px solid var(--sd-divider);
      text-align: left;
      white-space: nowrap;
      vertical-align: top;
    }
    thead th {
      position: sticky;
      top: 0;
      z-index: 1;
      background: var(--sd-surface);
      color: var(--sd-text-2);
      font-weight: var(--sd-weight-medium);
    }
    tbody th {
      font-weight: var(--sd-weight-regular, 400);
      max-width: 340px;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    td.text {
      white-space: normal;
      min-width: 120px;
      max-width: 420px;
    }
    tr.total th,
    tr.total td {
      font-weight: var(--sd-weight-semibold);
      background: var(--sd-surface-2);
    }
    .num {
      text-align: right;
      font-variant-numeric: tabular-nums;
    }
    a,
    .link {
      color: var(--sd-link, var(--sd-primary));
    }
    .link {
      padding: 0;
      border: 0;
      background: none;
      font: inherit;
      text-align: left;
      cursor: pointer;
    }
    .link:hover,
    a:hover {
      text-decoration: underline;
    }
    .link:focus-visible,
    a:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
  `,
})
export class ReportTableComponent {
  readonly table = input.required<AnalyticsReportTableDto>();
  readonly drill = output<ReportDrill>();

  protected readonly dataRows = computed(() => this.table().rows.filter((r) => !r['_total']).length);

  protected isNum(type: string): boolean {
    return type !== 'text' && type !== 'ticket' && type !== 'date' && type !== 'datetime';
  }

  protected asText(v: unknown): string {
    return String(v);
  }

  protected cell(v: unknown, type: string): string {
    if (v === null || v === undefined || v === '') return '—';
    if (typeof v === 'number') return formatValue(v, type === 'number' ? 'tickets' : type);
    return String(v);
  }

  protected canDrill(r: AnalyticsReportRow): boolean {
    const t = this.table();
    return !!t.dimension && !!t.drillMetric && !r['_total'] && r['_key'] !== undefined && r['_key'] !== null;
  }

  protected drillRow(r: AnalyticsReportRow): void {
    const t = this.table();
    if (!t.dimension || !t.drillMetric) return;
    const key = String(r['_key']);
    const time = TIME.has(t.dimension);
    this.drill.emit({ metric: t.drillMetric, dimension: t.dimension, key: time ? null : key, bucket: time ? key : null });
  }
}
