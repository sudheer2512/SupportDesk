import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AiApi, AnalyticsAnswerDto, AnalyticsRowDto } from '@sd/core';
import { SdBadge, SdButton, SdCard, SdChart, SdChartClick, SdEmptyState, SdIcon, SdJsonViewer, SdKpiTile, SdSkeleton } from '@sd/ui';
import type { EChartsCoreOption } from 'echarts/core';
import { aiErrorMessage, humanize } from '../shared/ai-ui';

const EXAMPLES = [
  'Which customers had the most SLA violations this month?',
  'Average resolution time by priority this quarter',
  'Daily ticket trend over the last 30 days',
  'Open tickets per agent',
  'Tickets by channel this month',
  'CSAT by department in the last 90 days',
];

/** Ticket-list query parameter for a drillable breakdown (the tickets list understands these filters). */
const DRILL_PARAM: Record<string, string> = {
  customer: 'customerId',
  product: 'productId',
  assignee: 'assigneeId',
  department: 'departmentId',
  team: 'teamId',
  priority: 'priority',
  channel: 'channel',
  status: 'statusId',
};

/**
 * Natural-language analytics: the question becomes a structured intent (metric, breakdown, filters, period),
 * which the server validates against whitelists and runs as a safe query — never SQL written by a model.
 */
@Component({
  selector: 'desk-ai-ask',
  imports: [FormsModule, SdCard, SdButton, SdBadge, SdIcon, SdChart, SdKpiTile, SdEmptyState, SdSkeleton, SdJsonViewer],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="layout">
      <sd-card>
        <form class="ask" (ngSubmit)="ask()">
          <label for="ai-question" class="sd-visually-hidden">Question</label>
          <sd-icon name="sparkles" [size]="18" class="lead" aria-hidden="true" />
          <input
            id="ai-question"
            class="sd-input question"
            name="question"
            [(ngModel)]="question"
            placeholder="Ask about your tickets, e.g. which customers had the most SLA violations this month?"
            autocomplete="off"
            maxlength="500"
          />
          <button sdButton variant="primary" type="submit" icon="send" [loading]="loading()" [disabled]="question.trim().length < 5">Ask</button>
        </form>
        <div class="examples" aria-label="Example questions">
          @for (q of examples(); track q) {
            <button type="button" class="chip" (click)="useExample(q)">{{ q }}</button>
          }
        </div>
      </sd-card>

      @if (error()) {
        <div class="error" role="alert"><sd-icon name="circle-alert" [size]="16" />{{ error() }}</div>
      }

      @if (loading() && !answer()) {
        <sd-card><sd-skeleton [lines]="6" /></sd-card>
      }

      @if (answer(); as a) {
        <sd-card [title]="a.interpretation" [subtitle]="'“' + a.question + '”'">
          <div sdCardActions>
            <button sdButton size="sm" variant="ghost" [icon]="showIntent() ? 'eye-off' : 'code'" (click)="showIntent.set(!showIntent())">
              {{ showIntent() ? 'Hide' : 'Show' }} structured intent
            </button>
          </div>
          <div class="intent" aria-label="How the question was understood">
            <span class="part"><span class="k">Metric</span><sd-badge tone="primary">{{ label(a.intent.metric) }}</sd-badge></span>
            <span class="part"><span class="k">Breakdown</span><sd-badge>{{ label(a.intent.dimension) }}</sd-badge></span>
            <span class="part"><span class="k">Period</span><sd-badge>{{ label(a.intent.period) }}</sd-badge></span>
            @if (!isTime(a)) {
              <span class="part"><span class="k">Order</span><sd-badge>{{ a.intent.sort === 'asc' ? 'Lowest' : 'Highest' }} first · top {{ a.intent.limit }}</sd-badge></span>
            }
            @for (f of a.intent.filters; track f.field + f.value) {
              <span class="part"><span class="k">{{ label(f.field) }}</span><sd-badge tone="info">{{ f.label ?? f.value }}</sd-badge></span>
            }
            <span class="part"><span class="k">Parsed by</span><sd-badge variant="outline">{{ a.parser }}</sd-badge></span>
          </div>
          @if (showIntent()) {
            <sd-json-viewer class="json" [value]="a.intent" [expandDepth]="3" ariaLabel="Structured intent" />
          }
        </sd-card>

        @if (a.rows.length === 0) {
          <sd-card><sd-empty-state icon="chart-column" title="No matching tickets" message="Nothing matched this question in the selected period. Try a longer period." /></sd-card>
        } @else {
          <div class="results">
            <sd-card title="Chart" class="chart-card">
              @if (a.chart.type === 'kpi') {
                <sd-kpi-tile [label]="valueLabel(a)" [value]="a.rows[0].value" [unit]="unit(a)" icon="chart-column" />
              } @else {
                <sd-chart [options]="chart()" height="340px" [ariaLabel]="a.interpretation" (chartClick)="drillChart($event)" />
                @if (drillable(a)) {
                  <p class="sd-small sd-muted hint">Click a bar or row to open the matching tickets.</p>
                }
              }
            </sd-card>
            <sd-card title="Results" padding="none">
              <div class="table-wrap">
                <table class="grid">
                  <caption class="sd-visually-hidden">{{ a.interpretation }}</caption>
                  <thead>
                    <tr>
                      @for (c of a.columns; track c.key) {
                        <th scope="col" [class.num]="c.type === 'number'">{{ c.label }}</th>
                      }
                    </tr>
                  </thead>
                  <tbody>
                    @for (row of a.rows; track row.key ?? row.label) {
                      <tr>
                        @for (c of a.columns; track c.key) {
                          <td [class.num]="c.type === 'number'">
                            @if (c.key === 'label' && drillable(a) && row.key) {
                              <a href="#" (click)="$event.preventDefault(); drill(row)">{{ row.label }}</a>
                            } @else {
                              {{ format(row[c.key]) }}
                            }
                          </td>
                        }
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            </sd-card>
          </div>
        }
      } @else if (!loading() && !error()) {
        <sd-card>
          <sd-empty-state
            icon="chart-column"
            title="Ask a question about your support data"
            message="Metrics: tickets created, open, resolved, SLA violations, resolution and first-response time, CSAT, reopened, escalated and frustrated tickets — broken down by customer, product, category, priority, channel, department, agent, team, status or over time."
          />
        </sd-card>
      }
    </div>
  `,
  styles: `
    .layout {
      display: grid;
      gap: var(--sd-space-4);
    }
    .ask {
      display: flex;
      align-items: center;
      gap: 10px;
    }
    .lead {
      color: var(--sd-accent);
      flex: none;
    }
    .question {
      flex: 1;
      min-width: 0;
    }
    .examples {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      margin-top: 12px;
    }
    .chip {
      padding: 4px 10px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface-2);
      color: var(--sd-text-2);
      font: inherit;
      font-size: var(--sd-text-xs);
      cursor: pointer;
    }
    .chip:hover {
      border-color: var(--sd-primary-border);
      color: var(--sd-primary);
    }
    .chip:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .error {
      display: flex;
      gap: 8px;
      align-items: center;
      padding: 10px 14px;
      border: 1px solid var(--sd-danger-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-danger-bg);
      color: var(--sd-danger);
    }
    .intent {
      display: flex;
      flex-wrap: wrap;
      gap: 10px 16px;
    }
    .part {
      display: inline-flex;
      align-items: center;
      gap: 6px;
    }
    .k {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .json {
      display: block;
      margin-top: 12px;
      max-height: 260px;
    }
    .results {
      display: grid;
      grid-template-columns: minmax(0, 3fr) minmax(0, 2fr);
      gap: var(--sd-space-4);
      align-items: start;
    }
    @media (max-width: 1100px) {
      .results {
        grid-template-columns: 1fr;
      }
    }
    .hint {
      margin-top: 6px;
    }
    .table-wrap {
      max-height: 420px;
      overflow: auto;
    }
    .grid {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--sd-text-sm);
    }
    .grid th,
    .grid td {
      padding: 8px 14px;
      border-bottom: 1px solid var(--sd-divider);
      text-align: start;
    }
    .grid th {
      position: sticky;
      top: 0;
      background: var(--sd-surface-2);
      color: var(--sd-text-2);
      font-weight: var(--sd-weight-semibold);
    }
    .grid .num {
      text-align: end;
      font-variant-numeric: tabular-nums;
    }
    .grid tbody tr:hover {
      background: var(--sd-row-hover);
    }
  `,
})
export class AskPage {
  private readonly ai = inject(AiApi);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  protected question = '';
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly answer = signal<AnalyticsAnswerDto | null>(null);
  protected readonly showIntent = signal(false);
  private readonly recent = signal<string[]>([]);
  protected readonly examples = computed(() => [...new Set([...this.recent(), ...EXAMPLES])].slice(0, 8));
  protected readonly label = humanize;

  protected readonly chart = computed<EChartsCoreOption>(() => {
    const a = this.answer();
    if (!a || a.rows.length === 0) return {};
    const labels = a.rows.map((r) => r.label);
    const values = a.rows.map((r) => r.value);
    const name = this.valueLabel(a);
    if (a.chart.type === 'pie') {
      return {
        tooltip: { trigger: 'item' },
        legend: { bottom: 0, type: 'scroll' },
        series: [{ name, type: 'pie', radius: ['38%', '68%'], data: a.rows.map((r) => ({ name: r.label, value: r.value })) }],
      };
    }
    if (a.chart.type === 'line') {
      return {
        tooltip: { trigger: 'axis' },
        grid: { left: 44, right: 16, top: 24, bottom: 40 },
        xAxis: { type: 'category', data: labels },
        yAxis: { type: 'value', name: this.unit(a) ?? undefined },
        dataZoom: labels.length > 40 ? [{ type: 'inside' }, { type: 'slider', height: 16, bottom: 4 }] : undefined,
        series: [{ name, type: 'line', smooth: true, smoothMonotone: 'x', areaStyle: { opacity: 0.12 }, data: values }],
      };
    }
    // Categorical breakdowns: horizontal bars keep long names readable.
    return {
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
      grid: { left: 8, right: 24, top: 8, bottom: 8, containLabel: true },
      xAxis: { type: 'value' },
      yAxis: { type: 'category', inverse: true, data: labels, axisLabel: { width: 160, overflow: 'truncate' } },
      series: [{ name, type: 'bar', barMaxWidth: 22, data: values }],
    };
  });

  protected useExample(q: string): void {
    this.question = q;
    this.ask();
  }

  protected ask(): void {
    const q = this.question.trim();
    if (q.length < 5 || this.loading()) return;
    this.loading.set(true);
    this.error.set(null);
    this.ai
      .ask(q, { silent: true })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (a) => {
          this.answer.set(a);
          this.loading.set(false);
          this.recent.update((r) => [q, ...r.filter((x) => x !== q)].slice(0, 3));
        },
        error: (err: unknown) => {
          this.loading.set(false);
          this.answer.set(null);
          this.error.set(aiErrorMessage(err));
        },
      });
  }

  protected isTime(a: AnalyticsAnswerDto): boolean {
    return ['day', 'week', 'month'].includes(a.intent.dimension);
  }

  protected drillable(a: AnalyticsAnswerDto): boolean {
    return a.intent.dimension in DRILL_PARAM;
  }

  protected valueLabel(a: AnalyticsAnswerDto): string {
    return a.columns.find((c) => c.key === 'value')?.label ?? 'Value';
  }

  protected unit(a: AnalyticsAnswerDto): string | null {
    const m = /\(([^)]+)\)$/.exec(this.valueLabel(a));
    return m ? m[1] : null;
  }

  protected format(value: string | number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    return typeof value === 'number' ? value.toLocaleString(undefined, { maximumFractionDigits: 2 }) : value;
  }

  protected drillChart(e: SdChartClick): void {
    const row = this.answer()?.rows[e.dataIndex];
    if (row) this.drill(row);
  }

  protected drill(row: AnalyticsRowDto): void {
    const a = this.answer();
    if (!a || !row.key || !this.drillable(a)) return;
    const params: Record<string, string> = { [DRILL_PARAM[a.intent.dimension]]: row.key, createdFrom: a.periodFrom, createdTo: a.periodTo };
    for (const f of a.intent.filters) {
      const p = DRILL_PARAM[f.field];
      if (p && !(p in params)) params[p] = f.value;
    }
    if (a.intent.metric === 'sla_breaches') params['slaState'] = 'Breached';
    if (a.intent.metric === 'open_tickets') params['statusCategory'] = 'Open,Assigned,InProgress,Waiting';
    if (a.intent.metric === 'escalated_tickets') params['escalated'] = 'true';
    void this.router.navigate(['/tickets'], { queryParams: params });
  }
}
