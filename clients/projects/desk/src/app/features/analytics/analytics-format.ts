import { AnalyticsFilterValues, AnalyticsQueryParams } from '@sd/core';
import { registerIcons } from '@sd/ui';
import { CalendarClock, ChartLine, FileSpreadsheet, Repeat, Save, Smile } from 'lucide';

/** Icons the analytics screens need beyond the @sd/ui defaults (bundled Lucide data). */
registerIcons({
  smile: Smile,
  repeat: Repeat,
  'chart-line': ChartLine,
  'calendar-clock': CalendarClock,
  'file-spreadsheet': FileSpreadsheet,
  save: Save,
} as Parameters<typeof registerIcons>[0]);

const NUM = new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 });
const NUM2 = new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 });

/** "3.25" hours → "3h 15m". */
export function formatHours(hours: number): string {
  const total = Math.round(hours * 60);
  const h = Math.floor(total / 60);
  const m = total % 60;
  if (h === 0) return `${m}m`;
  if (h >= 48 && m === 0) return `${h}h`;
  return m === 0 ? `${h}h` : `${h}h ${String(m).padStart(2, '0')}m`;
}

/** Display a metric value by its unit (tickets, hours, minutes, percent, score…). */
export function formatValue(value: number | null | undefined, unit: string | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '—';
  switch (unit) {
    case 'hours':
      return formatHours(value);
    case 'minutes':
      return `${NUM2.format(value)} min`;
    case 'percent':
      return `${NUM2.format(Math.round(value * 10) / 10)}%`;
    case 'score':
      return value.toFixed(2);
    default:
      return Number.isInteger(value) ? NUM.format(value) : NUM2.format(value);
  }
}

/** Short axis label by unit. */
export function axisLabel(value: number, unit: string | null | undefined): string {
  switch (unit) {
    case 'hours':
      return value >= 1 || value === 0 ? `${NUM2.format(value)}h` : `${Math.round(value * 60)}m`;
    case 'percent':
      return `${NUM.format(value)}%`;
    default:
      return NUM2.format(value);
  }
}

export function formatDelta(delta: number | null | undefined): string {
  if (delta === null || delta === undefined) return '';
  return `${delta > 0 ? '+' : ''}${delta.toFixed(1)}%`;
}

/** Period, comparison and filters shared by the dashboard and report pages. */
export interface AnalyticsFilterState {
  period: string;
  from: string | null;
  to: string | null;
  compare: '' | 'previous' | 'last_year';
  filters: AnalyticsFilterValues;
}

export const DEFAULT_FILTER_STATE: AnalyticsFilterState = { period: 'last_30_days', from: null, to: null, compare: '', filters: {} };

/** Converts the filter state into query parameters (empty values are dropped by the API client). */
export function stateParams(state: AnalyticsFilterState): AnalyticsQueryParams {
  return {
    period: state.period,
    from: state.period === 'custom' ? (state.from ?? undefined) : undefined,
    to: state.period === 'custom' ? (state.to ?? undefined) : undefined,
    compare: state.compare || undefined,
    ...state.filters,
  };
}

/** Widget/schedule parameter keys use the catalogue's snake_case; the query string uses camelCase for status. */
export function filtersToParams(filters: Record<string, string> | null | undefined): AnalyticsFilterValues {
  const out: AnalyticsFilterValues = {};
  for (const [k, v] of Object.entries(filters ?? {})) {
    if (!v) continue;
    const key = (k === 'status_category' ? 'statusCategory' : k) as keyof AnalyticsFilterValues;
    out[key] = v;
  }
  return out;
}

/** Inverse of {@link filtersToParams}: query/filter-bar values → stored parameters (exports, schedules). */
export function paramsToStored(params: AnalyticsQueryParams): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(params)) {
    if (v === undefined || v === null || v === '') continue;
    if (['metric', 'metrics', 'limit', 'sort', 'key', 'bucket', 'cursor', 'pageSize'].includes(k)) continue;
    out[k === 'statusCategory' ? 'status_category' : k] = String(v);
  }
  return out;
}

/** Merge page-level filters with a widget's own filters (the widget wins for the same field). */
export function mergeFilters(page: AnalyticsFilterValues, widget: AnalyticsFilterValues): AnalyticsFilterValues {
  return { ...page, ...Object.fromEntries(Object.entries(widget).filter(([, v]) => !!v)) };
}

export const FILTER_LABELS: Record<keyof AnalyticsFilterValues, string> = {
  department: 'Department',
  team: 'Team',
  assignee: 'Agent',
  channel: 'Channel',
  priority: 'Priority',
  customer: 'Customer',
  product: 'Product',
  module: 'Module',
  version: 'Version',
  category: 'Category',
  tier: 'Tier',
  statusCategory: 'Status',
};

/** Downloads a blob through a temporary object URL. */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 2000);
}
