import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { InstallBaseDto, InstallBaseProductDto, ProductsApi } from '@sd/core';
import { SdBadge, SdCard, SdChart, SdChartClick, SdEmptyState, SdInput, SdKpiTile, SdPageHeader, SdSkeleton } from '@sd/ui';
import type { EChartsCoreOption } from 'echarts/core';
import { DateOnlyPipe, HumanizePipe, statusTone } from '../customers/shared/customer-ui';
import { ModuleTabsComponent, PRODUCT_TABS } from '../customers/shared/module-tabs.component';
import { SECTION_STYLES, TABLE_STYLES } from '../customers/shared/styles';

/** /products/install-base — installations per product version (production vs other environments) with ticket load per version. */
@Component({
  selector: 'desk-install-base-page',
  imports: [FormsModule, SdPageHeader, SdCard, SdChart, SdKpiTile, SdBadge, SdSkeleton, SdEmptyState, SdInput, DateOnlyPipe, HumanizePipe, ModuleTabsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Install base" subtitle="Which versions customers run — the basis for upgrade campaigns and product-quality analytics." icon="chart-column" />
      <desk-module-tabs [tabs]="tabs" ariaLabel="Products & Assets sections" />
      <div class="desk-page__body">
        @if (!data()) {
          <sd-skeleton [lines]="8" />
        } @else if (data()!.products.length === 0) {
          <sd-empty-state icon="server" title="No installations yet" />
        } @else {
          @let d = data()!;
          <div class="kpis">
            <sd-kpi-tile label="Installations" [value]="d.totalInstallations" icon="server" />
            <sd-kpi-tile label="On end-of-support versions" [value]="d.onEndOfSupport" icon="triangle-alert" [deltaLabel]="eosShare()" />
            <sd-kpi-tile label="Products" [value]="d.products.length" icon="package" />
            <sd-kpi-tile label="Customers" [value]="customerCount()" icon="building" />
          </div>
          <div class="toolbar">
            <label class="sd-small" for="ib-product">Product</label>
            <select sdInput size="sm" id="ib-product" class="f" [ngModel]="productId()" (ngModelChange)="productId.set($event)">
              @for (p of d.products; track p.productId) {
                <option [value]="p.productId">{{ p.productName }} ({{ p.installations }})</option>
              }
            </select>
          </div>
          @if (selected(); as p) {
            <sd-card [title]="p.productName + ' by version'" subtitle="Click a bar to open those installations.">
              <sd-chart [options]="chart()" height="300px" [ariaLabel]="'Installations of ' + p.productName + ' per version, production and other environments, with open tickets'" (chartClick)="drill($event)" />
            </sd-card>
            <div class="dt-wrap table">
              <table class="dt">
                <thead>
                  <tr>
                    <th scope="col">Version</th>
                    <th scope="col">Status</th>
                    <th scope="col">End of support</th>
                    <th scope="col" class="num">Installations</th>
                    <th scope="col" class="num">Production</th>
                    <th scope="col" class="num">Customers</th>
                    <th scope="col" class="num">Users</th>
                    <th scope="col" class="num">Open tickets</th>
                    <th scope="col" class="num">Tickets (90 d) / installation</th>
                  </tr>
                </thead>
                <tbody>
                  @for (v of p.versions; track v.version) {
                    <tr class="clickable" tabindex="0" (click)="open(v.versionId)" (keydown.enter)="open(v.versionId)">
                      <td><strong>{{ v.version }}</strong></td>
                      <td>
                        <sd-badge [tone]="statusTone(v.status)" size="sm" dot>{{ v.status | deskHumanize }}</sd-badge>
                        @if (v.isEndOfSupport && v.status !== 'EndOfLife') {
                          <sd-badge tone="danger" size="sm">EOS</sd-badge>
                        }
                      </td>
                      <td class="nowrap">{{ v.endOfSupportDate | deskDate }}</td>
                      <td class="num">{{ v.installations }}</td>
                      <td class="num">{{ v.production }}</td>
                      <td class="num">{{ v.customers }}</td>
                      <td class="num">{{ v.users }}</td>
                      <td class="num">{{ v.openTickets }}</td>
                      <td class="num">{{ v.installations ? (v.ticketsLast90Days / v.installations).toFixed(1) : '—' }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        }
      </div>
    </div>
  `,
  styles: [
    TABLE_STYLES,
    SECTION_STYLES,
    `
      .toolbar { display: flex; align-items: center; gap: var(--sd-space-2); margin-bottom: var(--sd-space-3); }
      .f { width: 280px; }
      .table { margin-top: var(--sd-space-4); }
      sd-badge { margin-right: 4px; }
    `,
  ],
})
export class InstallBasePage {
  private readonly api = inject(ProductsApi);
  private readonly router = inject(Router);
  protected readonly tabs = PRODUCT_TABS;
  protected readonly statusTone = statusTone;
  protected readonly data = signal<InstallBaseDto | null>(null);
  protected readonly productId = signal<string>('');
  protected readonly selected = computed<InstallBaseProductDto | null>(() => {
    const d = this.data();
    return d?.products.find((p) => p.productId === this.productId()) ?? d?.products[0] ?? null;
  });
  protected readonly customerCount = computed(() => Math.max(0, ...(this.data()?.products ?? []).map((p) => p.customers)));
  protected readonly eosShare = computed(() => {
    const d = this.data();
    return d && d.totalInstallations ? `${Math.round((d.onEndOfSupport / d.totalInstallations) * 100)}% of the base` : null;
  });

  protected readonly chart = computed<EChartsCoreOption>(() => {
    const p = this.selected();
    const versions = [...(p?.versions ?? [])].reverse();
    return {
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
      legend: { top: 0, left: 0 },
      grid: { left: 40, right: 40, top: 40, bottom: 28 },
      xAxis: { type: 'category', data: versions.map((v) => (v.isEndOfSupport ? `${v.version} (EOS)` : v.version)) },
      yAxis: [
        { type: 'value', minInterval: 1 },
        { type: 'value', minInterval: 1, splitLine: { show: false } },
      ],
      series: [
        { name: 'Production', type: 'bar', stack: 'inst', data: versions.map((v) => v.production), barMaxWidth: 36 },
        { name: 'Other environments', type: 'bar', stack: 'inst', data: versions.map((v) => v.nonProduction), barMaxWidth: 36 },
        { name: 'Open tickets', type: 'line', yAxisIndex: 1, data: versions.map((v) => v.openTickets), symbolSize: 7 },
      ],
    };
  });

  constructor() {
    this.api.installBase().subscribe((d) => {
      this.data.set(d);
      if (d.products.length) this.productId.set(d.products[0].productId);
    });
  }

  protected drill(e: SdChartClick): void {
    const versions = [...(this.selected()?.versions ?? [])].reverse();
    this.open(versions[e.dataIndex]?.versionId ?? null);
  }

  protected open(versionId: string | null): void {
    const p = this.selected();
    if (!p) return;
    void this.router.navigate(['/products/installations'], { queryParams: { productId: p.productId, versionId } });
  }
}
