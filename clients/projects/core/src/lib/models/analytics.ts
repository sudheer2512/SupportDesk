import { Guid, IsoDateTime, OptionDto } from './common';

/**
 * Analytics module wire types (mirror SupportDesk.Contracts.Analytics). Names are prefixed with "Analytics" because
 * the AI module already exports generic analytics answer types.
 */

/** Unit of a catalogue metric. */
export type AnalyticsUnit = 'tickets' | 'hours' | 'minutes' | 'percent' | 'score' | 'replies' | string;

export type AnalyticsWidgetType = 'kpi' | 'line' | 'bar' | 'pie' | 'table';

export type AnalyticsReportFormat = 'Csv' | 'Xlsx' | 'Pdf';

export type AnalyticsRunStatus = 'Queued' | 'Running' | 'Succeeded' | 'Failed';

export interface AnalyticsMetricDto {
  key: string;
  label: string;
  description: string;
  group: string;
  unit: AnalyticsUnit;
  /** count | sum | average | median | p90 | percent | ratio */
  aggregation: string;
  /** created | resolved | closed | open_at_end | day */
  dateBasis: string;
  higherIsBetter: boolean;
  /** tickets | agent_daily */
  source: string;
  dimensions: string[];
  filters: string[];
  drillable: boolean;
}

export interface AnalyticsDimensionDto {
  key: string;
  label: string;
  isTime: boolean;
}

export interface AnalyticsFilterFieldDto {
  key: string;
  label: string;
  /** entity | enum | text */
  kind: string;
}

export interface AnalyticsPeriodDto {
  key: string;
  label: string;
}

export interface AnalyticsReportDefinitionDto {
  key: string;
  name: string;
  description: string;
  group: string;
  icon: string;
  filters: string[];
}

export interface AnalyticsCatalogDto {
  metrics: AnalyticsMetricDto[];
  dimensions: AnalyticsDimensionDto[];
  filters: AnalyticsFilterFieldDto[];
  periods: AnalyticsPeriodDto[];
  reports: AnalyticsReportDefinitionDto[];
  timeZone: string;
  /** False = personal scope (only tickets assigned to the caller). */
  teamScope: boolean;
  canExport: boolean;
  canSchedule: boolean;
}

export interface AnalyticsFilterOptionsDto {
  departments: OptionDto[];
  teams: OptionDto[];
  agents: OptionDto[];
  products: OptionDto[];
  modules: OptionDto[];
  versions: OptionDto[];
  categories: OptionDto[];
  tiers: OptionDto[];
  channels: OptionDto[];
  priorities: OptionDto[];
  statusCategories: OptionDto[];
}

/** Filter fields shared by queries, widgets, exports and schedules (comma-separated values). */
export interface AnalyticsFilterValues {
  department?: string;
  team?: string;
  assignee?: string;
  channel?: string;
  priority?: string;
  customer?: string;
  product?: string;
  module?: string;
  version?: string;
  category?: string;
  tier?: string;
  statusCategory?: string;
}

/** Query string of every analytics read. */
export interface AnalyticsQueryParams extends AnalyticsFilterValues {
  metric?: string;
  metrics?: string;
  dimension?: string;
  period?: string;
  from?: string;
  to?: string;
  /** previous | last_year */
  compare?: string;
  limit?: number;
  sort?: 'asc' | 'desc';
  key?: string;
  bucket?: string;
  cursor?: string;
  pageSize?: number;
}

export interface AnalyticsPeriodRangeDto {
  key: string;
  label: string;
  from: IsoDateTime;
  to: IsoDateTime;
  fromDay: string;
  toDay: string;
}

export interface AnalyticsResultRowDto {
  key: string | null;
  label: string;
  value: number | null;
  count: number;
  compareValue: number | null;
  deltaPercent: number | null;
}

export interface AnalyticsResultDto {
  metric: AnalyticsMetricDto;
  dimension: string;
  period: AnalyticsPeriodRangeDto;
  comparePeriod: AnalyticsPeriodRangeDto | null;
  total: number | null;
  totalCount: number;
  compareTotal: number | null;
  deltaPercent: number | null;
  rows: AnalyticsResultRowDto[];
  truncated: boolean;
}

export interface AnalyticsBreakdownColumnDto {
  key: string;
  label: string;
  unit: AnalyticsUnit;
  higherIsBetter: boolean;
}

export interface AnalyticsBreakdownRowDto {
  key: string | null;
  label: string;
  values: (number | null)[];
}

export interface AnalyticsBreakdownDto {
  dimension: string;
  period: AnalyticsPeriodRangeDto;
  columns: AnalyticsBreakdownColumnDto[];
  rows: AnalyticsBreakdownRowDto[];
  totals: (number | null)[];
}

export interface AnalyticsParetoRowDto {
  key: string | null;
  label: string;
  value: number;
  share: number;
  cumulativeShare: number;
}

export interface AnalyticsParetoDto {
  metric: string;
  dimension: string;
  period: AnalyticsPeriodRangeDto;
  total: number;
  rows: AnalyticsParetoRowDto[];
  vitalFew: number;
}

export interface AnalyticsCohortRowDto {
  cohort: string;
  label: string;
  size: number;
  resolvedPercent: (number | null)[];
}

export interface AnalyticsCohortsDto {
  period: AnalyticsPeriodRangeDto;
  offsetsDays: number[];
  rows: AnalyticsCohortRowDto[];
}

export interface AnalyticsAgeingBucketDto {
  key: string;
  label: string;
}

export interface AnalyticsAgeingRowDto {
  key: string | null;
  label: string;
  counts: number[];
  total: number;
  breached: number;
}

export interface AnalyticsAgeingDto {
  dimension: string;
  asOf: IsoDateTime;
  buckets: AnalyticsAgeingBucketDto[];
  rows: AnalyticsAgeingRowDto[];
  totals: number[];
}

export interface AnalyticsDrilldownTicketDto {
  ticketId: Guid;
  number: number;
  displayNumber: string;
  subject: string;
  status: string;
  statusCategory: string;
  priority: string;
  channel: string;
  department: string | null;
  assignee: string | null;
  customer: string | null;
  createdAt: IsoDateTime;
  firstResponseAt: IsoDateTime | null;
  resolvedAt: IsoDateTime | null;
  firstResponseMinutes: number | null;
  firstResponseBusinessMinutes: number | null;
  resolutionMinutes: number | null;
  resolutionBusinessMinutes: number | null;
  slaBreached: boolean;
  reopenCount: number;
  csatScore: number | null;
}

export interface AnalyticsDrilldownDto {
  title: string;
  period: AnalyticsPeriodRangeDto;
  items: AnalyticsDrilldownTicketDto[];
  nextCursor: string | null;
  total: number;
}

// ── Reports ──

export interface AnalyticsReportKpiDto {
  metric: string;
  label: string;
  unit: AnalyticsUnit;
  value: number | null;
  compareValue: number | null;
  deltaPercent: number | null;
  higherIsBetter: boolean;
}

export interface AnalyticsReportSeriesDto {
  key: string;
  label: string;
  unit: AnalyticsUnit;
  values: (number | null)[];
  compareValues: (number | null)[] | null;
}

export interface AnalyticsReportChartDto {
  id: string;
  title: string;
  /** line | bar | stacked-bar | pie | pareto */
  type: string;
  categories: string[];
  categoryKeys: (string | null)[];
  series: AnalyticsReportSeriesDto[];
  dimension: string | null;
  drillMetric: string | null;
}

export interface AnalyticsReportColumnDto {
  key: string;
  label: string;
  /** text | number | percent | hours | minutes | score | date | datetime | ticket */
  type: string;
}

/** Table row: values keyed by column key; "_key" = dimension value, "_ticket" = ticket number, "_total" marks the totals row. */
export type AnalyticsReportRow = Record<string, string | number | boolean | null>;

export interface AnalyticsReportTableDto {
  id: string;
  title: string;
  columns: AnalyticsReportColumnDto[];
  rows: AnalyticsReportRow[];
  dimension: string | null;
  drillMetric: string | null;
}

export interface AnalyticsReportResultDto {
  key: string;
  name: string;
  description: string;
  period: AnalyticsPeriodRangeDto;
  comparePeriod: AnalyticsPeriodRangeDto | null;
  kpis: AnalyticsReportKpiDto[];
  charts: AnalyticsReportChartDto[];
  tables: AnalyticsReportTableDto[];
  appliedFilters: string[];
  timeZone: string;
  generatedAt: IsoDateTime;
}

// ── Dashboards ──

/** Widget on a 12-column grid (x 0–11, w 1–12, h 1–8 rows). Filters use AnalyticsFilterValues keys (snake_case on the wire for status). */
export interface AnalyticsWidgetDto {
  id: string;
  title: string;
  type: AnalyticsWidgetType;
  metric: string;
  metrics?: string[] | null;
  dimension?: string | null;
  limit?: number | null;
  filters?: Record<string, string> | null;
  period?: string | null;
  compare?: boolean;
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface AnalyticsDashboardSummaryDto {
  id: Guid;
  name: string;
  description: string | null;
  audience: string;
  isShared: boolean;
  isDefault: boolean;
  isMine: boolean;
  canEdit: boolean;
  widgetCount: number;
  updatedAt: IsoDateTime;
}

export interface AnalyticsDashboardDto {
  id: Guid;
  name: string;
  description: string | null;
  audience: string;
  isShared: boolean;
  isDefault: boolean;
  isMine: boolean;
  canEdit: boolean;
  period: string;
  widgets: AnalyticsWidgetDto[];
  rowVersion: string;
  updatedAt: IsoDateTime;
}

export interface SaveAnalyticsDashboardRequest {
  name: string;
  description?: string | null;
  isShared: boolean;
  period?: string | null;
  widgets: AnalyticsWidgetDto[];
  rowVersion?: string | null;
}

// ── Exports & schedules ──

export interface CreateAnalyticsExportRequest {
  reportKey: string;
  format: AnalyticsReportFormat;
  parameters?: Record<string, string>;
}

export interface AnalyticsExportDto {
  id: Guid;
  reportKey: string;
  title: string;
  format: AnalyticsReportFormat;
  status: AnalyticsRunStatus;
  fileName: string | null;
  sizeBytes: number;
  rowCount: number;
  error: string | null;
  createdAt: IsoDateTime;
  completedAt: IsoDateTime | null;
  downloadUrl: string | null;
}

export interface AnalyticsScheduleDto {
  id: Guid;
  name: string;
  reportKey: string;
  reportName: string;
  parameters: Record<string, string>;
  format: AnalyticsReportFormat;
  frequency: 'Daily' | 'Weekly' | 'Monthly';
  runAtLocal: string;
  dayOfWeek: string | null;
  dayOfMonth: number | null;
  timeZone: string;
  recipients: string[];
  ownerUserId: Guid;
  ownerName: string | null;
  isActive: boolean;
  nextRunAt: IsoDateTime;
  lastRunAt: IsoDateTime | null;
  lastStatus: string | null;
  canEdit: boolean;
  rowVersion: string;
}

export interface SaveAnalyticsScheduleRequest {
  name: string;
  reportKey: string;
  parameters?: Record<string, string>;
  format: AnalyticsReportFormat;
  frequency: 'Daily' | 'Weekly' | 'Monthly';
  runAtLocal: string;
  dayOfWeek?: string | null;
  dayOfMonth?: number | null;
  timeZone?: string | null;
  recipients: string[];
  isActive: boolean;
  rowVersion?: string | null;
}

export interface AnalyticsScheduleRunDto {
  id: Guid;
  scheduledFor: IsoDateTime;
  manual: boolean;
  status: AnalyticsRunStatus;
  createdAt: IsoDateTime;
  startedAt: IsoDateTime | null;
  completedAt: IsoDateTime | null;
  recipients: string[];
  delivered: number;
  fileName: string | null;
  sizeBytes: number;
  rowCount: number;
  error: string | null;
}

export interface AnalyticsMetricsStatusDto {
  tickets: number;
  metrics: number;
  missing: number;
  stale: number;
  lastMetricUpdate: IsoDateTime | null;
  lastAgentDay: string | null;
  lastBacklogDay: string | null;
  rebuildQueued: boolean;
}
