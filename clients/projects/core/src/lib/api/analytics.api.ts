import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { Guid } from '../models/common';
import {
  AnalyticsAgeingDto,
  AnalyticsBreakdownDto,
  AnalyticsCatalogDto,
  AnalyticsCohortsDto,
  AnalyticsDashboardDto,
  AnalyticsDashboardSummaryDto,
  AnalyticsDrilldownDto,
  AnalyticsExportDto,
  AnalyticsFilterOptionsDto,
  AnalyticsMetricsStatusDto,
  AnalyticsParetoDto,
  AnalyticsQueryParams,
  AnalyticsReportDefinitionDto,
  AnalyticsReportResultDto,
  AnalyticsResultDto,
  AnalyticsScheduleDto,
  AnalyticsScheduleRunDto,
  CreateAnalyticsExportRequest,
  SaveAnalyticsDashboardRequest,
  SaveAnalyticsScheduleRequest,
} from '../models/analytics';

const enc = encodeURIComponent;

/**
 * Analytics module client (`/analytics…`): the metric catalogue, whitelisted metric × dimension × filter × period
 * queries (totals with comparison, series, breakdowns, Pareto, cohorts, ageing, keyset drill-down), standard reports,
 * dashboards, server-side exports and scheduled reports. Every number respects the caller's analytics scope.
 */
@Injectable({ providedIn: 'root' })
export class AnalyticsApi {
  private readonly api = inject(ApiClient);

  catalog(): Observable<AnalyticsCatalogDto> {
    return this.api.get<AnalyticsCatalogDto>('analytics/catalog');
  }

  filterOptions(): Observable<AnalyticsFilterOptionsDto> {
    return this.api.get<AnalyticsFilterOptionsDto>('analytics/filter-options');
  }

  query(params: AnalyticsQueryParams): Observable<AnalyticsResultDto> {
    return this.api.get<AnalyticsResultDto>('analytics/query', { params, silent: true });
  }

  breakdown(params: AnalyticsQueryParams): Observable<AnalyticsBreakdownDto> {
    return this.api.get<AnalyticsBreakdownDto>('analytics/breakdown', { params, silent: true });
  }

  pareto(params: AnalyticsQueryParams): Observable<AnalyticsParetoDto> {
    return this.api.get<AnalyticsParetoDto>('analytics/pareto', { params, silent: true });
  }

  cohorts(params: AnalyticsQueryParams): Observable<AnalyticsCohortsDto> {
    return this.api.get<AnalyticsCohortsDto>('analytics/cohorts', { params, silent: true });
  }

  ageing(params: AnalyticsQueryParams): Observable<AnalyticsAgeingDto> {
    return this.api.get<AnalyticsAgeingDto>('analytics/ageing', { params, silent: true });
  }

  drilldown(params: AnalyticsQueryParams): Observable<AnalyticsDrilldownDto> {
    return this.api.get<AnalyticsDrilldownDto>('analytics/drilldown', { params, silent: true });
  }

  reports(): Observable<AnalyticsReportDefinitionDto[]> {
    return this.api.get<AnalyticsReportDefinitionDto[]>('analytics/reports');
  }

  report(key: string, params: AnalyticsQueryParams): Observable<AnalyticsReportResultDto> {
    return this.api.get<AnalyticsReportResultDto>(`analytics/reports/${enc(key)}`, { params, silent: true });
  }

  metricsStatus(): Observable<AnalyticsMetricsStatusDto> {
    return this.api.get<AnalyticsMetricsStatusDto>('analytics/metrics/status');
  }

  rebuildMetrics(days = 120): Observable<AnalyticsMetricsStatusDto> {
    return this.api.post<AnalyticsMetricsStatusDto>('analytics/metrics/rebuild', { days });
  }

  // ── Dashboards ──

  dashboards(): Observable<AnalyticsDashboardSummaryDto[]> {
    return this.api.get<AnalyticsDashboardSummaryDto[]>('analytics/dashboards');
  }

  dashboard(id: Guid): Observable<AnalyticsDashboardDto> {
    return this.api.get<AnalyticsDashboardDto>(`analytics/dashboards/${enc(id)}`, { silent: true });
  }

  createDashboard(request: SaveAnalyticsDashboardRequest): Observable<AnalyticsDashboardDto> {
    return this.api.post<AnalyticsDashboardDto>('analytics/dashboards', request, { silent: true });
  }

  updateDashboard(id: Guid, request: SaveAnalyticsDashboardRequest): Observable<AnalyticsDashboardDto> {
    return this.api.put<AnalyticsDashboardDto>(`analytics/dashboards/${enc(id)}`, request, { silent: true });
  }

  deleteDashboard(id: Guid): Observable<void> {
    return this.api.delete<void>(`analytics/dashboards/${enc(id)}`, { silent: true });
  }

  duplicateDashboard(id: Guid): Observable<AnalyticsDashboardDto> {
    return this.api.post<AnalyticsDashboardDto>(`analytics/dashboards/${enc(id)}/duplicate`);
  }

  // ── Exports ──

  createExport(request: CreateAnalyticsExportRequest): Observable<AnalyticsExportDto> {
    return this.api.post<AnalyticsExportDto>('analytics/exports', request, { silent: true });
  }

  exports(): Observable<AnalyticsExportDto[]> {
    return this.api.get<AnalyticsExportDto[]>('analytics/exports');
  }

  export(id: Guid): Observable<AnalyticsExportDto> {
    return this.api.get<AnalyticsExportDto>(`analytics/exports/${enc(id)}`, { silent: true });
  }

  /** Authenticated binary download of a finished export. */
  downloadExport(id: Guid): Observable<Blob> {
    return this.api.blob(`analytics/exports/${enc(id)}/download`);
  }

  // ── Scheduled reports ──

  schedules(): Observable<AnalyticsScheduleDto[]> {
    return this.api.get<AnalyticsScheduleDto[]>('analytics/schedules');
  }

  createSchedule(request: SaveAnalyticsScheduleRequest): Observable<AnalyticsScheduleDto> {
    return this.api.post<AnalyticsScheduleDto>('analytics/schedules', request, { silent: true });
  }

  updateSchedule(id: Guid, request: SaveAnalyticsScheduleRequest): Observable<AnalyticsScheduleDto> {
    return this.api.put<AnalyticsScheduleDto>(`analytics/schedules/${enc(id)}`, request, { silent: true });
  }

  deleteSchedule(id: Guid): Observable<void> {
    return this.api.delete<void>(`analytics/schedules/${enc(id)}`);
  }

  runSchedule(id: Guid): Observable<AnalyticsScheduleRunDto> {
    return this.api.post<AnalyticsScheduleRunDto>(`analytics/schedules/${enc(id)}/run`);
  }

  scheduleRuns(id: Guid): Observable<AnalyticsScheduleRunDto[]> {
    return this.api.get<AnalyticsScheduleRunDto[]>(`analytics/schedules/${enc(id)}/runs`);
  }
}
