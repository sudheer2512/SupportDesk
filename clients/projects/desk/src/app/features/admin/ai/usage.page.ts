import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { AiAdminApi, AiUsageSummaryDto } from '@sd/core';
import { SdButton, SdCard, SdChart, SdEmptyState, SdKpiTile, SdSkeleton } from '@sd/ui';
import type { EChartsCoreOption } from 'echarts/core';
import { compact, humanize, money, pct } from '../../ai/shared/ai-ui';

/** AI usage & cost: budget and token allowance, requests/cost per feature, model and month, and suggestion outcomes. */
@Component({
  selector: 'desk-ai-usage',
  imports: [SdCard, SdButton, SdKpiTile, SdChart, SdSkeleton, SdEmptyState],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (usage(); as u) {
      <div class="layout">
        <div class="kpis">
          <sd-kpi-tile label="Spent this month" [value]="fmtMoney(u.spentThisMonth, u.currency)" icon="wallet" />
          <sd-kpi-tile label="Monthly budget" [value]="u.monthlyBudget > 0 ? fmtMoney(u.monthlyBudget, u.currency) : 'No limit'" icon="scale" />
          <sd-kpi-tile label="Budget used" [value]="u.monthlyBudget > 0 ? fmtPct(u.spentThisMonth / u.monthlyBudget) : '—'" icon="gauge" />
          <sd-kpi-tile label="Metered tokens (external)" [value]="fmtCompact(u.tokensThisMonth)" [unit]="u.tokenAllowance ? 'of ' + fmtCompact(u.tokenAllowance) : null" icon="activity" />
          <sd-kpi-tile label="AI calls this month" [value]="requests()" icon="zap" />
        </div>
        <div class="charts">
          <sd-card title="By feature" subtitle="This month">
            <sd-chart [options]="byFeature()" height="300px" ariaLabel="AI requests and cost by feature this month" />
          </sd-card>
          <sd-card title="By model" subtitle="Requests this month">
            <sd-chart [options]="byModel()" height="300px" ariaLabel="AI requests by model this month" />
          </sd-card>
          <sd-card title="By month" subtitle="Cost and requests" class="wide">
            <sd-chart [options]="byMonth()" height="260px" ariaLabel="AI cost and requests per month" />
          </sd-card>
        </div>
        <sd-card title="Suggestion outcomes" subtitle="Last 30 days — how often people used what the AI produced" padding="none">
          @if (u.outcomes.length === 0) {
            <sd-empty-state icon="chart-column" title="No AI calls yet" compact />
          } @else {
            <div class="table-wrap">
              <table class="grid">
                <caption class="sd-visually-hidden">AI suggestion outcomes per feature</caption>
                <thead>
                  <tr>
                    <th scope="col">Feature</th>
                    <th scope="col" class="num">Calls</th>
                    <th scope="col" class="num">Accepted</th>
                    <th scope="col" class="num">Applied</th>
                    <th scope="col" class="num">Rejected</th>
                    <th scope="col" class="num">Reverted</th>
                    <th scope="col" class="num">Failed</th>
                    <th scope="col" class="num">Use rate</th>
                  </tr>
                </thead>
                <tbody>
                  @for (o of u.outcomes; track o.feature) {
                    <tr>
                      <td>{{ label(o.feature) }}</td>
                      <td class="num">{{ o.total }}</td>
                      <td class="num">{{ o.accepted }}</td>
                      <td class="num">{{ o.applied }}</td>
                      <td class="num">{{ o.rejected }}</td>
                      <td class="num">{{ o.reverted }}</td>
                      <td class="num">{{ o.failed }}</td>
                      <td class="num">{{ useRate(o.accepted + o.applied, o.accepted + o.applied + o.rejected + o.reverted) }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </sd-card>
        <div class="row">
          <label class="sd-small" for="months">History</label>
          <select id="months" class="sd-input sd-input-sm" [value]="months()" (change)="setMonths($any($event.target).value)">
            <option value="3">3 months</option>
            <option value="6">6 months</option>
            <option value="12">12 months</option>
          </select>
          <button sdButton size="sm" icon="refresh" (click)="load()">Refresh</button>
        </div>
      </div>
    } @else {
      <sd-card><sd-skeleton [lines]="10" /></sd-card>
    }
  `,
  styles: `
    .layout {
      display: grid;
      gap: var(--sd-space-4);
    }
    .kpis {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(190px, 1fr));
      gap: 12px;
    }
    .charts {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 420px), 1fr));
      gap: var(--sd-space-4);
    }
    .wide {
      grid-column: 1 / -1;
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
      padding: 8px 14px;
      border-top: 1px solid var(--sd-divider);
      text-align: start;
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
    .row {
      display: flex;
      gap: 8px;
      align-items: center;
      justify-content: flex-end;
    }
    .row select {
      width: auto;
    }
  `,
})
export class AiUsagePage implements OnInit {
  private readonly api = inject(AiAdminApi);

  protected readonly usage = signal<AiUsageSummaryDto | null>(null);
  protected readonly months = signal('6');
  protected readonly fmtMoney = money;
  protected readonly fmtCompact = compact;
  protected readonly fmtPct = pct;
  protected readonly label = humanize;
  protected readonly requests = computed(() => (this.usage()?.byFeature ?? []).reduce((sum, f) => sum + f.requests, 0));

  protected readonly byFeature = computed<EChartsCoreOption>(() => {
    const rows = this.usage()?.byFeature ?? [];
    return {
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
      legend: { top: 0, right: 0 },
      grid: { left: 8, right: 16, top: 32, bottom: 8, containLabel: true },
      xAxis: [{ type: 'value', name: 'calls' }],
      yAxis: { type: 'category', inverse: true, data: rows.map((r) => humanize(r.key)), axisLabel: { width: 130, overflow: 'truncate' } },
      series: [
        { name: 'Calls', type: 'bar', barMaxWidth: 18, data: rows.map((r) => r.requests) },
        { name: 'Tokens (k)', type: 'bar', barMaxWidth: 18, data: rows.map((r) => Math.round(r.tokens / 100) / 10) },
      ],
    };
  });

  protected readonly byModel = computed<EChartsCoreOption>(() => ({
    tooltip: { trigger: 'item' },
    legend: { bottom: 0, type: 'scroll' },
    series: [{ name: 'Calls', type: 'pie', radius: ['40%', '68%'], data: (this.usage()?.byModel ?? []).map((m) => ({ name: m.key, value: m.requests })) }],
  }));

  protected readonly byMonth = computed<EChartsCoreOption>(() => {
    const u = this.usage();
    const rows = u?.byMonth ?? [];
    return {
      tooltip: { trigger: 'axis' },
      legend: { top: 0, right: 0 },
      grid: { left: 52, right: 52, top: 32, bottom: 28 },
      xAxis: { type: 'category', data: rows.map((r) => r.key) },
      yAxis: [
        { type: 'value', name: u?.currency ?? 'cost' },
        { type: 'value', name: 'calls', splitLine: { show: false } },
      ],
      series: [
        { name: 'Cost', type: 'bar', barMaxWidth: 28, data: rows.map((r) => r.cost) },
        { name: 'Calls', type: 'line', yAxisIndex: 1, smooth: true, data: rows.map((r) => r.requests) },
      ],
    };
  });

  ngOnInit(): void {
    this.load();
  }

  protected setMonths(value: string): void {
    this.months.set(value);
    this.load();
  }

  protected load(): void {
    this.api.usage(Number(this.months())).subscribe((u) => this.usage.set(u));
  }

  protected useRate(used: number, decided: number): string {
    return decided === 0 ? '—' : pct(used / decided);
  }
}
