import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  booleanAttribute,
  effect,
  inject,
  input,
  output,
  untracked,
  viewChild,
} from '@angular/core';
import { ThemeService } from '@sd/core';
import type { ECharts, EChartsCoreOption } from 'echarts/core';
import { SdSkeleton } from '../skeleton/skeleton.component';
import { ensureEcharts, themeFromTokens } from './echarts-setup';

/** Payload of `chartClick` (a subset of ECharts' event params) for drill-down. */
export interface SdChartClick {
  componentType: string;
  seriesName?: string;
  seriesIndex?: number;
  name: string;
  dataIndex: number;
  value: unknown;
  data: unknown;
}

/**
 * `<sd-chart [options]="opts" height="280px" ariaLabel="Tickets created per day" (chartClick)="drill($event)" />`
 * ECharts wrapper: themed from the @sd/ui tokens (re-themes on light/dark switch), resizes with its
 * container (ResizeObserver), disposes on destroy, and emits clicks for drill-down. Provide a meaningful
 * `ariaLabel` (or a data table nearby) — canvas charts are opaque to screen readers.
 */
@Component({
  selector: 'sd-chart',
  imports: [SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div #el class="canvas" role="img" [attr.aria-label]="ariaLabel()" [style.height]="height()"></div>
    @if (loading()) {
      <div class="loading"><sd-skeleton height="100%" radius="var(--sd-radius-md)" /></div>
    }
  `,
  styles: `
    :host {
      position: relative;
      display: block;
      min-width: 0;
    }
    .canvas {
      width: 100%;
    }
    .loading {
      position: absolute;
      inset: 0;
      display: flex;
      background: var(--sd-surface);
    }
    .loading sd-skeleton {
      height: 100%;
    }
  `,
})
export class SdChart {
  readonly options = input.required<EChartsCoreOption>();
  readonly height = input('280px');
  readonly loading = input(false, { transform: booleanAttribute });
  readonly ariaLabel = input('Chart');
  /** Merge new options into the existing chart instead of replacing them. */
  readonly merge = input(false, { transform: booleanAttribute });
  readonly chartClick = output<SdChartClick>();

  private readonly el = viewChild.required<ElementRef<HTMLDivElement>>('el');
  private readonly theme = inject(ThemeService);
  private chart: ECharts | null = null;
  private observer: ResizeObserver | null = null;
  private themeKey = '';

  constructor() {
    afterNextRender(() => {
      this.init();
      this.observer = new ResizeObserver(() => this.chart?.resize({ animation: { duration: 0 } }));
      this.observer.observe(this.el().nativeElement);
    });
    effect(() => {
      const options = this.options();
      untracked(() => this.chart?.setOption(options, { notMerge: !this.merge(), lazyUpdate: true }));
    });
    effect(() => {
      const key = this.theme.effective();
      untracked(() => {
        if (this.chart && key !== this.themeKey) {
          // Tokens change with the theme: rebuild the instance with the new palette.
          this.chart.dispose();
          this.chart = null;
          requestAnimationFrame(() => this.init());
        }
      });
    });
    inject(DestroyRef).onDestroy(() => {
      this.observer?.disconnect();
      this.chart?.dispose();
      this.chart = null;
    });
  }

  /** The underlying ECharts instance (null until rendered). */
  instance(): ECharts | null {
    return this.chart;
  }

  private init(): void {
    const echarts = ensureEcharts();
    const el = this.el().nativeElement;
    this.themeKey = this.theme.effective();
    const themeName = `sd-${this.themeKey}`;
    echarts.registerTheme(themeName, themeFromTokens(el));
    this.chart = echarts.init(el, themeName, { renderer: 'canvas' });
    this.chart.setOption(this.options(), { notMerge: true });
    this.chart.on('click', (params) => {
      const p = params as unknown as SdChartClick;
      this.chartClick.emit({
        componentType: p.componentType,
        seriesName: p.seriesName,
        seriesIndex: p.seriesIndex,
        name: p.name,
        dataIndex: p.dataIndex,
        value: p.value,
        data: p.data,
      });
    });
  }
}
