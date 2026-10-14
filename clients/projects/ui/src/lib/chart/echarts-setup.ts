import { BarChart, FunnelChart, GaugeChart, HeatmapChart, LineChart, PieChart, ScatterChart, SankeyChart } from 'echarts/charts';
import {
  DataZoomComponent,
  DatasetComponent,
  GridComponent,
  LegendComponent,
  MarkAreaComponent,
  MarkLineComponent,
  MarkPointComponent,
  TitleComponent,
  TooltipComponent,
  VisualMapComponent,
} from 'echarts/components';
import * as echarts from 'echarts/core';
import { CanvasRenderer } from 'echarts/renderers';

let registered = false;

/** Registers the tree-shaken ECharts modules used across SupportDesk (charts + components + canvas). */
export function ensureEcharts(): typeof echarts {
  if (!registered) {
    echarts.use([
      LineChart,
      BarChart,
      PieChart,
      ScatterChart,
      HeatmapChart,
      FunnelChart,
      GaugeChart,
      SankeyChart,
      GridComponent,
      TooltipComponent,
      LegendComponent,
      TitleComponent,
      DatasetComponent,
      DataZoomComponent,
      VisualMapComponent,
      MarkLineComponent,
      MarkPointComponent,
      MarkAreaComponent,
      CanvasRenderer,
    ]);
    registered = true;
  }
  return echarts;
}

/** Builds an ECharts theme object from the live @sd/ui CSS tokens (so charts follow light/dark + brand). */
export function themeFromTokens(el: Element): Record<string, unknown> {
  const css = getComputedStyle(el);
  const v = (name: string, fallback: string) => css.getPropertyValue(name).trim() || fallback;
  const text = v('--sd-text', '#161a23');
  const text2 = v('--sd-text-2', '#454d5e');
  const text3 = v('--sd-text-3', '#646c7e');
  const border = v('--sd-border', '#e1e4ea');
  const divider = v('--sd-divider', '#eceef2');
  const surface = v('--sd-surface', '#ffffff');
  const font = v('--sd-font', 'system-ui, sans-serif');
  const palette = [1, 2, 3, 4, 5, 6, 7, 8].map((i) => v(`--sd-chart-${i}`, '#3343cf'));
  const axis = {
    axisLine: { lineStyle: { color: border } },
    axisTick: { lineStyle: { color: border } },
    axisLabel: { color: text3 },
    splitLine: { lineStyle: { color: divider } },
    nameTextStyle: { color: text3 },
  };
  return {
    color: palette,
    backgroundColor: 'transparent',
    textStyle: { fontFamily: font, color: text2 },
    title: { textStyle: { color: text, fontWeight: 600 }, subtextStyle: { color: text3 } },
    legend: { textStyle: { color: text2 }, pageTextStyle: { color: text2 } },
    tooltip: {
      backgroundColor: surface,
      borderColor: border,
      textStyle: { color: text },
      extraCssText: 'box-shadow: 0 8px 24px -8px rgba(0,0,0,.25); border-radius: 8px;',
    },
    categoryAxis: axis,
    valueAxis: axis,
    timeAxis: axis,
    logAxis: axis,
    line: { symbol: 'circle', symbolSize: 5, smooth: false, lineStyle: { width: 2 } },
    bar: { itemStyle: { borderRadius: [3, 3, 0, 0] } },
    pie: { itemStyle: { borderColor: surface, borderWidth: 2 } },
    gauge: { axisLine: { lineStyle: { color: [[1, border]] } } },
    dataZoom: { textStyle: { color: text3 }, borderColor: border },
    visualMap: { textStyle: { color: text2 } },
  };
}
