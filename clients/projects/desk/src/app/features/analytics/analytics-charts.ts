import type { EChartsCoreOption } from 'echarts/core';
import { axisLabel, formatValue } from './analytics-format';

/** One plotted series (compare values render as a dashed companion line/bar). */
export interface ChartSeriesInput {
  name: string;
  unit: string;
  values: (number | null)[];
  compareValues?: (number | null)[] | null;
  compareName?: string;
}

const GRID = { left: 8, right: 16, top: 36, bottom: 8, containLabel: true };

function tooltipFormatter(unitBySeries: Map<string, string>) {
  return (params: unknown) => {
    const list = (Array.isArray(params) ? params : [params]) as { seriesName: string; value: number | null; marker: string; name: string }[];
    if (list.length === 0) return '';
    const head = `<div style="font-weight:600;margin-bottom:4px">${escapeHtml(list[0].name)}</div>`;
    return (
      head +
      list
        .map((p) => `<div>${p.marker} ${escapeHtml(p.seriesName)}: <b>${escapeHtml(formatValue(p.value, unitBySeries.get(p.seriesName)))}</b></div>`)
        .join('')
    );
  };
}

function escapeHtml(s: string): string {
  return s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!);
}

/** The i-th chart palette colour from the live tokens (so a compare series can reuse its metric's colour). */
function paletteColor(i: number): string | undefined {
  if (typeof document === 'undefined') return undefined;
  const v = getComputedStyle(document.documentElement).getPropertyValue(`--sd-chart-${(i % 8) + 1}`).trim();
  return v || undefined;
}

/** Time series (one or more metrics; the comparison period is a dashed line in the same colour). */
export function lineChart(categories: string[], series: ChartSeriesInput[]): EChartsCoreOption {
  const units = new Map<string, string>();
  const out: object[] = [];
  series.forEach((s, i) => {
    const color = paletteColor(i);
    units.set(s.name, s.unit);
    out.push({ type: 'line', name: s.name, data: s.values, color, connectNulls: false, showSymbol: categories.length <= 40, emphasis: { focus: 'series' } });
    if (s.compareValues?.some((v) => v !== null)) {
      const n = s.compareName ?? `${s.name} (compare)`;
      units.set(n, s.unit);
      out.push({ type: 'line', name: n, data: s.compareValues, color, lineStyle: { type: 'dashed', width: 1.5, opacity: 0.7 }, showSymbol: false, emphasis: { focus: 'series' } });
    }
  });
  const unit = series[0]?.unit;
  return {
    grid: GRID,
    tooltip: { trigger: 'axis', formatter: tooltipFormatter(units) },
    legend: { top: 0, type: 'scroll' },
    xAxis: { type: 'category', data: categories, boundaryGap: false, axisLabel: { hideOverlap: true } },
    yAxis: { type: 'value', minInterval: unit === 'tickets' || unit === 'replies' ? 1 : undefined, axisLabel: { formatter: (v: number) => axisLabel(v, unit) } },
    series: out,
  };
}

/** Category bars (horizontal when labels are long or many). */
export function barChart(categories: string[], series: ChartSeriesInput[], opts: { stacked?: boolean; time?: boolean } = {}): EChartsCoreOption {
  const units = new Map<string, string>(series.map((s) => [s.name, s.unit]));
  const horizontal = barIsHorizontal(categories, opts.time);
  const unit = series[0]?.unit;
  const valueAxis = { type: 'value', minInterval: unit === 'tickets' ? 1 : undefined, axisLabel: { formatter: (v: number) => axisLabel(v, unit) } };
  const categoryAxis = horizontal
    ? { type: 'category', data: [...categories].reverse(), axisLabel: { width: 140, overflow: 'truncate' } }
    : { type: 'category', data: categories, axisLabel: { hideOverlap: true } };
  const data = (values: (number | null)[]) => (horizontal ? [...values].reverse() : values);
  const out: object[] = series.map((s, i) => ({
    type: 'bar',
    name: s.name,
    data: data(s.values),
    color: paletteColor(i),
    stack: opts.stacked ? 'total' : undefined,
    barMaxWidth: 36,
    emphasis: { focus: 'series' },
  }));
  series.forEach((s, i) => {
    if (!s.compareValues?.some((v) => v !== null)) return;
    const n = s.compareName ?? `${s.name} (compare)`;
    units.set(n, s.unit);
    out.push({ type: 'bar', name: n, data: data(s.compareValues), color: paletteColor(i), barMaxWidth: 36, itemStyle: { opacity: 0.4 } });
  });
  return {
    grid: GRID,
    tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' }, formatter: tooltipFormatter(units) },
    legend: series.length > 1 || out.length > 1 ? { top: 0, type: 'scroll' } : undefined,
    xAxis: horizontal ? valueAxis : categoryAxis,
    yAxis: horizontal ? categoryAxis : valueAxis,
    series: out,
  };
}

/**
 * Whether {@link barChart} lays bars out horizontally (many or long category labels; never for time buckets).
 * Horizontal charts list categories bottom-up, so click indexes must be mapped back.
 */
export function barIsHorizontal(categories: string[], time = false): boolean {
  if (time) return false;
  return categories.length > 6 || (categories.length > 3 && categories.some((c) => c.length > 9)) || categories.some((c) => c.length > 16);
}

export function pieChart(categories: string[], values: (number | null)[], unit: string): EChartsCoreOption {
  return {
    tooltip: { trigger: 'item', formatter: (p: { name: string; value: number; percent: number }) => `${escapeHtml(p.name)}: <b>${escapeHtml(formatValue(p.value, unit))}</b> (${p.percent}%)` },
    legend: { bottom: 0, type: 'scroll' },
    series: [
      {
        type: 'pie',
        radius: ['45%', '70%'],
        center: ['50%', '45%'],
        avoidLabelOverlap: true,
        label: { show: false },
        data: categories.map((name, i) => ({ name, value: values[i] ?? 0 })),
      },
    ],
  };
}

/** Pareto: descending bars plus the cumulative share line on a 0–100 % axis. */
export function paretoChart(categories: string[], values: (number | null)[], cumulative: (number | null)[], name: string, unit: string): EChartsCoreOption {
  const units = new Map([
    [name, unit],
    ['Cumulative share', 'percent'],
  ]);
  return {
    grid: { ...GRID, right: 40 },
    tooltip: { trigger: 'axis', formatter: tooltipFormatter(units) },
    legend: { top: 0 },
    xAxis: { type: 'category', data: categories, axisLabel: { interval: 0, rotate: categories.length > 5 ? 30 : 0, width: 110, overflow: 'truncate' } },
    yAxis: [
      { type: 'value', minInterval: 1 },
      { type: 'value', min: 0, max: 100, axisLabel: { formatter: '{value}%' }, splitLine: { show: false } },
    ],
    series: [
      { type: 'bar', name, data: values, barMaxWidth: 36 },
      { type: 'line', name: 'Cumulative share', yAxisIndex: 1, data: cumulative, markLine: { silent: true, symbol: 'none', data: [{ yAxis: 80 }], lineStyle: { type: 'dashed' } } },
    ],
  };
}
