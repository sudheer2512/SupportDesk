import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AnalyticsApi, AnalyticsQueryParams, AnalyticsReportResultDto, AnalyticsScheduleDto, ApiError } from '@sd/core';
import { SdButton, SdDialogService, SdEmptyState, SdKpiTile, SdSkeleton, ToastService } from '@sd/ui';
import { Subscription } from 'rxjs';
import { AnalyticsFilterState, DEFAULT_FILTER_STATE, formatDelta, formatValue, paramsToStored, stateParams } from './analytics-format';
import { AnalyticsStore } from './analytics.store';
import { AnalyticsDrilldownComponent } from './drilldown-drawer.component';
import { AnalyticsExportMenuComponent } from './export-menu.component';
import { AnalyticsFilterBarComponent } from './filter-bar.component';
import { ReportChartComponent, ReportDrill, ReportTableComponent } from './report-parts.component';
import { ScheduleFormData, ScheduleFormDialog } from './schedule-form.dialog';

/**
 * /analytics/reports/:key — one standard report: period/compare/filters, KPIs with change, charts and tables with
 * drill-down to tickets, server-side export (CSV/Excel/PDF) and scheduling.
 */
@Component({
  selector: 'desk-analytics-report',
  imports: [
    RouterLink,
    SdButton,
    SdEmptyState,
    SdKpiTile,
    SdSkeleton,
    AnalyticsDrilldownComponent,
    AnalyticsExportMenuComponent,
    AnalyticsFilterBarComponent,
    ReportChartComponent,
    ReportTableComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="head">
      <a sdButton size="sm" variant="ghost" icon="arrow-left" routerLink="/analytics/reports">All reports</a>
      <div class="title-block">
        <h2 class="title">{{ definition()?.name ?? report()?.name ?? 'Report' }}</h2>
        <p class="sd-small sd-muted desc">{{ definition()?.description ?? report()?.description }}</p>
      </div>
      <div class="actions">
        <button sdButton size="sm" icon="refresh" type="button" iconOnly aria-label="Refresh report" (click)="load()"></button>
        @if (store.catalog()?.canSchedule) {
          <button sdButton size="sm" icon="calendar-clock" type="button" (click)="schedule()">Schedule</button>
        }
        @if (store.catalog()?.canExport) {
          <desk-analytics-export-menu [reportKey]="key()" [parameters]="storedParams()" />
        }
      </div>
    </div>
    <desk-analytics-filter-bar [(state)]="filterState" />

    @if (loading() && !report()) {
      <div aria-busy="true" class="kpis">
        @for (i of [1, 2, 3, 4]; track i) {
          <sd-kpi-tile label="Loading" [loading]="true" />
        }
      </div>
      <sd-skeleton height="280px" />
    } @else if (error()) {
      <sd-empty-state icon="circle-alert" title="The report could not be built" [message]="error()">
        <button sdButton type="button" (click)="load()">Retry</button>
      </sd-empty-state>
    } @else if (report(); as r) {
      <p class="sd-xs sd-muted meta" aria-live="polite">
        {{ r.period.label }} ({{ r.period.fromDay }} – {{ r.period.toDay }})
        @if (r.comparePeriod) {
          · compared with {{ r.comparePeriod.label.toLowerCase() }} ({{ r.comparePeriod.fromDay }} – {{ r.comparePeriod.toDay }})
        }
        @for (f of r.appliedFilters; track f) {
          · {{ f }}
        }
        · times in {{ r.timeZone }}
        @if (loading()) {
          · refreshing…
        }
      </p>
      <section class="kpis" aria-label="Key figures">
        @for (k of r.kpis; track k.metric) {
          <sd-kpi-tile
            [label]="k.label"
            [value]="value(k.value, k.unit)"
            [delta]="k.deltaPercent"
            [deltaLabel]="k.deltaPercent !== null ? 'vs comparison' : null"
            [positiveIsGood]="k.higherIsBetter"
            [interactive]="isDrillable(k.metric)"
            (activate)="drillTotal(k.metric)"
          />
        }
      </section>
      @if (r.charts.length) {
        <div class="charts">
          @for (c of r.charts; track c.id) {
            <desk-report-chart [chart]="c" (drill)="openDrill($event)" />
          }
        </div>
      }
      @for (t of r.tables; track t.id) {
        <desk-report-table [table]="t" (drill)="openDrill($event)" />
      }
      @if (r.kpis.length === 0 && r.tables.length === 0) {
        <sd-empty-state icon="chart-column" title="No data" message="Nothing matches this period and these filters." />
      }
    }
    <desk-analytics-drilldown [(open)]="drillOpen" [params]="drillParams()" />
  `,
  styles: `
    :host {
      display: grid;
      grid-template-columns: minmax(0, 1fr);
      gap: var(--sd-space-3);
    }
    .head {
      display: flex;
      flex-wrap: wrap;
      align-items: flex-start;
      gap: var(--sd-space-2) var(--sd-space-3);
    }
    .title-block {
      flex: 1;
      min-width: 220px;
    }
    .title {
      margin: 0;
      font-size: var(--sd-text-lg);
      font-weight: var(--sd-weight-semibold);
    }
    .desc,
    .meta {
      margin: 2px 0 0;
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--sd-space-2);
    }
    .kpis {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 150px), 1fr));
      gap: var(--sd-space-3);
    }
    .charts {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 420px), 1fr));
      gap: var(--sd-space-3);
    }
    .charts > *,
    .kpis > * {
      min-width: 0;
    }
  `,
})
export class AnalyticsReportPage {
  private readonly api = inject(AnalyticsApi);
  private readonly dialogs = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  protected readonly store = inject(AnalyticsStore);

  /** Route parameter. */
  readonly key = input.required<string>();

  protected readonly filterState = signal<AnalyticsFilterState>({ ...DEFAULT_FILTER_STATE });
  protected readonly report = signal<AnalyticsReportResultDto | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly drillOpen = signal(false);
  protected readonly drillParams = signal<AnalyticsQueryParams | null>(null);
  private sub: Subscription | null = null;

  protected readonly definition = computed(() => this.store.reports().find((r) => r.key === this.key()));
  private readonly params = computed(() => stateParams(this.filterState()));
  protected readonly storedParams = computed(() => paramsToStored(this.params()));

  constructor() {
    this.store.ensure();
    effect(() => {
      this.key();
      this.params();
      untracked(() => this.load());
    });
    inject(DestroyRef).onDestroy(() => this.sub?.unsubscribe());
  }

  protected load(): void {
    this.sub?.unsubscribe();
    this.loading.set(true);
    this.error.set(null);
    this.sub = this.api.report(this.key(), this.params()).subscribe({
      next: (r) => {
        this.report.set(r);
        this.loading.set(false);
      },
      error: (e: unknown) => {
        this.error.set(ApiError.from(e).userMessage);
        this.loading.set(false);
      },
    });
  }

  protected value(v: number | null, unit: string): string {
    return formatValue(v, unit);
  }

  protected delta(v: number | null): string {
    return formatDelta(v);
  }

  protected isDrillable(metric: string): boolean {
    return this.store.metric(metric)?.drillable ?? false;
  }

  protected drillTotal(metric: string): void {
    if (!this.isDrillable(metric)) return;
    this.openDrill({ metric, dimension: null, key: null, bucket: null });
  }

  protected openDrill(d: ReportDrill): void {
    const p = this.params();
    this.drillParams.set({
      ...p,
      compare: undefined,
      metric: d.metric,
      dimension: d.dimension ?? undefined,
      key: d.key ?? undefined,
      bucket: d.bucket ?? undefined,
    });
    this.drillOpen.set(true);
  }

  protected schedule(): void {
    const ref = this.dialogs.open<AnalyticsScheduleDto, ScheduleFormData>(ScheduleFormDialog, {
      data: { schedule: null, reportKey: this.key(), parameters: this.storedParams() },
      size: 'lg',
    });
    ref.closed.subscribe((s) => {
      if (s) this.toast.success('Report scheduled', `${s.name} · next run ${new Date(s.nextRunAt).toLocaleString()}`);
    });
  }
}
