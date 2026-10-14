import { Injectable, computed, inject, signal } from '@angular/core';
import { AnalyticsApi, AnalyticsCatalogDto, AnalyticsFilterOptionsDto, AnalyticsMetricDto, ApiError } from '@sd/core';
import { SdOption } from '@sd/ui';

/** Catalogue (metrics, dimensions, periods, reports) and filter values for the analytics screens, loaded once. */
@Injectable({ providedIn: 'root' })
export class AnalyticsStore {
  private readonly api = inject(AnalyticsApi);

  readonly catalog = signal<AnalyticsCatalogDto | null>(null);
  readonly options = signal<AnalyticsFilterOptionsDto | null>(null);
  readonly error = signal<string | null>(null);
  readonly loading = signal(false);

  readonly metrics = computed(() => this.catalog()?.metrics ?? []);
  readonly periods = computed(() => this.catalog()?.periods ?? []);
  readonly reports = computed(() => this.catalog()?.reports ?? []);
  readonly teamScope = computed(() => this.catalog()?.teamScope ?? false);

  /** Metric options grouped by catalogue group (combobox). */
  readonly metricOptions = computed<SdOption[]>(() =>
    this.metrics().map((m) => ({ value: m.key, label: m.label, group: m.group, description: m.description })),
  );

  ensure(): void {
    if (this.catalog() || this.loading()) return;
    this.reload();
  }

  reload(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.catalog().subscribe({
      next: (c) => {
        this.catalog.set(c);
        this.loading.set(false);
      },
      error: (e: unknown) => {
        this.error.set(ApiError.from(e).userMessage);
        this.loading.set(false);
      },
    });
    this.api.filterOptions().subscribe({ next: (o) => this.options.set(o), error: () => this.options.set(null) });
  }

  metric(key: string | null | undefined): AnalyticsMetricDto | undefined {
    return this.metrics().find((m) => m.key === key);
  }

  dimensionLabel(key: string | null | undefined): string {
    return this.catalog()?.dimensions.find((d) => d.key === key)?.label ?? key ?? '';
  }

  periodLabel(key: string | null | undefined): string {
    return this.periods().find((p) => p.key === key)?.label ?? key ?? '';
  }
}
