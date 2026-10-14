import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, untracked } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { KbAnalyticsDto, KbArticleStatDto, KnowledgeApi, MeStore, formatDate } from '@sd/core';
import { SdBadge, SdButton, SdChart, SdEmptyState, SdIcon, SdKpiTile, SdRelativeTimePipe, SdSkeleton } from '@sd/ui';
import type { EChartsCoreOption } from 'echarts/core';
import { KbStars } from '../components/kb-badges.component';
import { KB_BASE, KB_STYLES, formatCount, formatPercent, formatRating, registerKbIcons, remote } from '../kb-common';

const RANGES = [7, 30, 90] as const;

function warnIf(condition: boolean): 'warn' | undefined {
  return condition ? 'warn' : undefined;
}

interface Tile {
  label: string;
  value: string;
  icon: string;
  /** Navigates to the matching list when activated. */
  link?: { path: string[]; query?: Record<string, string> };
  tone?: 'warn';
}

/** /kb/analytics — knowledge-base usage: KPIs, daily searches/feedback, top/least helpful, content gaps, flow usage. */
@Component({
  selector: 'desk-kb-analytics',
  imports: [NgTemplateOutlet, RouterLink, SdButton, SdBadge, SdIcon, SdKpiTile, SdChart, SdSkeleton, SdEmptyState, SdRelativeTimePipe, KbStars],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section aria-labelledby="kb-an-title">
      <header class="kb-head">
        <div class="kb-head__titles">
          <h2 id="kb-an-title">Analytics</h2>
          <p class="kb-head__sub">How customers and agents use the knowledge base — and where content is missing.</p>
        </div>
        <div class="kb-head__actions">
          <div class="range" role="group" aria-label="Period">
            @for (d of ranges; track d) {
              <button sdButton size="sm" variant="ghost" type="button" [pressed]="period() === d" (click)="setDays(d)">{{ d }} days</button>
            }
          </div>
          <button sdButton variant="ghost" size="sm" icon="refresh" iconOnly type="button" aria-label="Refresh" [loading]="data.loading()" (click)="data.reload()"></button>
        </div>
      </header>

      @if (data.error(); as err) {
        <div class="kb-error" role="alert">
          <sd-icon name="circle-alert" [size]="16" />
          <p>Could not load analytics. {{ err }}</p>
          <button sdButton size="sm" type="button" icon="refresh" (click)="data.reload()">Retry</button>
        </div>
      } @else {
        <div class="tiles" [attr.aria-busy]="data.loading() || null">
          @for (t of tiles(); track t.label) {
            <sd-kpi-tile
              [label]="t.label"
              [value]="t.value"
              [icon]="t.icon"
              [loading]="!data.value()"
              [interactive]="!!t.link"
              [class.warn]="t.tone === 'warn'"
              (activate)="go(t)"
            />
          }
        </div>

        <section class="panel" aria-labelledby="kb-an-daily">
          <header class="panel-head">
            <h3 id="kb-an-daily">Searches and feedback per day</h3>
            <span class="sd-xs sd-muted">Last {{ period() }} days</span>
          </header>
          @if (data.value(); as d) {
            @if (d.daily.length) {
              <sd-chart [options]="chart()" height="300px" [ariaLabel]="chartLabel()" />
              <details class="data-table">
                <summary>Show the numbers</summary>
                <div class="kb-table-wrap">
                  <table class="kb-table">
                    <caption class="sd-visually-hidden">Daily searches and feedback</caption>
                    <thead>
                      <tr>
                        <th scope="col">Date</th>
                        <th scope="col" class="num">Searches</th>
                        <th scope="col" class="num">No results</th>
                        <th scope="col" class="num">Helpful</th>
                        <th scope="col" class="num">Not helpful</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (p of d.daily; track p.date) {
                        <tr>
                          <td>{{ dayLabel(p.date) }}</td>
                          <td class="num">{{ p.searches }}</td>
                          <td class="num">{{ p.noResults }}</td>
                          <td class="num">{{ p.helpful }}</td>
                          <td class="num">{{ p.notHelpful }}</td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              </details>
            } @else {
              <sd-empty-state icon="chart-column" title="No activity in this period" compact />
            }
          } @else {
            <sd-skeleton height="300px" radius="var(--sd-radius-md)" />
          }
        </section>

        @if (data.value(); as d) {
          <div class="two">
            <section class="panel" aria-labelledby="kb-an-top">
              <header class="panel-head"><h3 id="kb-an-top">Most viewed articles</h3></header>
              <ng-container [ngTemplateOutlet]="articleTable" [ngTemplateOutletContext]="{ $implicit: d.topViewed, empty: 'No article views yet.' }" />
            </section>
            <section class="panel" aria-labelledby="kb-an-least">
              <header class="panel-head">
                <h3 id="kb-an-least">Least helpful articles</h3>
                <span class="sd-xs sd-muted">Candidates for a rewrite</span>
              </header>
              <ng-container [ngTemplateOutlet]="articleTable" [ngTemplateOutletContext]="{ $implicit: d.leastHelpful, empty: 'No negative feedback yet.' }" />
            </section>
          </div>

          <div class="two">
            <section class="panel" aria-labelledby="kb-an-searches">
              <header class="panel-head"><h3 id="kb-an-searches">Top searches</h3></header>
              @if (d.topSearches.length) {
                <div class="kb-table-wrap">
                  <table class="kb-table">
                    <thead>
                      <tr>
                        <th scope="col">Query</th>
                        <th scope="col" class="num">Searches</th>
                        <th scope="col" class="num">Avg. results</th>
                        <th scope="col">Last searched</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (s of d.topSearches; track s.query) {
                        <tr>
                          <td><a [routerLink]="base" [queryParams]="{ q: s.query }">{{ s.query }}</a></td>
                          <td class="num">{{ s.count }}</td>
                          <td class="num">{{ s.averageResults.toFixed(1) }}</td>
                          <td class="nowrap">{{ s.lastSearchedAt | sdRelativeTime }}</td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              } @else {
                <p class="sd-small sd-muted">No searches in this period.</p>
              }
            </section>
            <section class="panel gap" aria-labelledby="kb-an-gaps">
              <header class="panel-head">
                <h3 id="kb-an-gaps"><sd-icon name="search-x" [size]="15" /> Searches with no results</h3>
                <span class="sd-xs sd-muted">Content gaps</span>
              </header>
              @if (d.noResultSearches.length) {
                <div class="kb-table-wrap">
                  <table class="kb-table">
                    <thead>
                      <tr>
                        <th scope="col">Query</th>
                        <th scope="col" class="num">Searches</th>
                        <th scope="col">Last searched</th>
                        @if (canAuthor()) {
                          <th scope="col"><span class="sd-visually-hidden">Action</span></th>
                        }
                      </tr>
                    </thead>
                    <tbody>
                      @for (s of d.noResultSearches; track s.query) {
                        <tr>
                          <td>{{ s.query }}</td>
                          <td class="num">{{ s.count }}</td>
                          <td class="nowrap">{{ s.lastSearchedAt | sdRelativeTime }}</td>
                          @if (canAuthor()) {
                            <td class="action">
                              <a sdButton size="sm" variant="ghost" icon="file-plus" [routerLink]="[base, 'articles', 'new']" [queryParams]="{ title: s.query }" [attr.aria-label]="'Create an article for ' + s.query">
                                Create article
                              </a>
                            </td>
                          }
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              } @else {
                <p class="sd-small sd-muted ok"><sd-icon name="circle-check" [size]="14" /> Every search found something.</p>
              }
            </section>
          </div>

          <section class="panel" aria-labelledby="kb-an-flows">
            <header class="panel-head"><h3 id="kb-an-flows">Guided troubleshooting</h3></header>
            @if (d.flows.length) {
              <div class="kb-table-wrap">
                <table class="kb-table">
                  <thead>
                    <tr>
                      <th scope="col">Flow</th>
                      <th scope="col" class="num">Started</th>
                      <th scope="col" class="num">Resolved</th>
                      <th scope="col" class="num">Escalated</th>
                      <th scope="col">Outcome</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (f of d.flows; track f.id) {
                      <tr>
                        <td><a [routerLink]="[base, 'flows', f.id]">{{ f.name }}</a></td>
                        <td class="num">{{ f.started }}</td>
                        <td class="num">{{ f.resolved }} <small class="sd-muted">{{ pct(f.resolved, f.started) }}</small></td>
                        <td class="num">{{ f.escalated }} <small class="sd-muted">{{ pct(f.escalated, f.started) }}</small></td>
                        <td>
                          <span class="bar" role="img" [attr.aria-label]="pct(f.resolved, f.started) + ' resolved, ' + pct(f.escalated, f.started) + ' escalated'">
                            <span class="bar-resolved" [style.width.%]="share(f.resolved, f.started)"></span>
                            <span class="bar-escalated" [style.width.%]="share(f.escalated, f.started)"></span>
                          </span>
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            } @else {
              <p class="sd-small sd-muted">No troubleshooting flows were used in this period.</p>
            }
          </section>
        }
      }
    </section>

    <ng-template #articleTable let-rows let-empty="empty">
      @if (asStats(rows).length) {
        <div class="kb-table-wrap">
          <table class="kb-table">
            <thead>
              <tr>
                <th scope="col">Article</th>
                <th scope="col" class="num">Views</th>
                <th scope="col" class="num">Helpful</th>
                <th scope="col">Rating</th>
              </tr>
            </thead>
            <tbody>
              @for (a of asStats(rows); track a.id) {
                <tr>
                  <td><a [routerLink]="[base, 'articles', a.id]">{{ a.title }}</a></td>
                  <td class="num">{{ count(a.views) }}</td>
                  <td class="num">
                    @if (a.helpfulPercent !== null && a.helpfulPercent < 50) {
                      <sd-badge tone="danger" size="sm">{{ percent(a.helpfulPercent) }}</sd-badge>
                    } @else {
                      {{ percent(a.helpfulPercent) }}
                    }
                    <small class="sd-muted votes">{{ a.helpful }}/{{ a.helpful + a.notHelpful }}</small>
                  </td>
                  <td class="nowrap">
                    <desk-kb-stars [rating]="a.ratingAverage" [size]="12" />
                    <span class="sd-xs sd-muted">{{ rating(a.ratingAverage) }}</span>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      } @else {
        <p class="sd-small sd-muted">{{ empty }}</p>
      }
    </ng-template>
  `,
  styles: [
    KB_STYLES,
    `
      .range {
        display: inline-flex;
        gap: 2px;
        padding: 2px;
        border-radius: var(--sd-radius-md);
        background: var(--sd-surface-3);
      }
      .tiles {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(min(100%, 180px), 1fr));
        gap: var(--sd-space-3);
        margin-bottom: var(--sd-space-4);
      }
      sd-kpi-tile.warn {
        border-radius: var(--sd-radius-lg);
        box-shadow: inset 3px 0 0 var(--sd-warning);
      }
      .panel {
        display: grid;
        gap: var(--sd-space-3);
        min-width: 0;
        margin-bottom: var(--sd-space-4);
        padding: var(--sd-space-4);
        border: 1px solid var(--sd-border);
        border-radius: var(--sd-radius-lg);
        background: var(--sd-surface);
      }
      .panel-head {
        display: flex;
        flex-wrap: wrap;
        align-items: baseline;
        justify-content: space-between;
        gap: var(--sd-space-2);
      }
      .panel-head h3 {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        font-size: var(--sd-text-md);
        font-weight: var(--sd-weight-semibold);
      }
      .two {
        display: grid;
        grid-template-columns: repeat(2, minmax(0, 1fr));
        gap: var(--sd-space-4);
      }
      .two .panel {
        margin-bottom: var(--sd-space-4);
        align-content: start;
      }
      @media (max-width: 1100px) {
        .two {
          grid-template-columns: minmax(0, 1fr);
        }
      }
      .data-table summary {
        cursor: pointer;
        color: var(--sd-text-2);
        font-size: var(--sd-text-sm);
      }
      .data-table .kb-table-wrap {
        margin-top: var(--sd-space-2);
        max-height: 320px;
        overflow: auto;
      }
      .nowrap {
        white-space: nowrap;
      }
      .votes {
        display: block;
        font-size: var(--sd-text-xs);
      }
      .action {
        text-align: right;
      }
      .ok {
        display: flex;
        align-items: center;
        gap: 6px;
      }
      .bar {
        display: flex;
        width: 140px;
        height: 8px;
        overflow: hidden;
        border-radius: var(--sd-radius-pill);
        background: var(--sd-surface-3);
      }
      .bar-resolved {
        background: var(--sd-success);
      }
      .bar-escalated {
        background: var(--sd-warning);
      }
    `,
  ],
})
export class KbAnalyticsPage {
  /** ?days=7|30|90 */
  readonly days = input<string>();

  private readonly api = inject(KnowledgeApi);
  private readonly me = inject(MeStore);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly base = KB_BASE;
  protected readonly ranges = RANGES;
  protected readonly data = remote<KbAnalyticsDto>();
  protected readonly canAuthor = computed(() => this.me.can('kb.author'));
  protected readonly period = computed<number>(() => {
    const n = Number(this.days());
    return (RANGES as readonly number[]).includes(n) ? n : 30;
  });

  protected readonly tiles = computed<Tile[]>(() => {
    const t = this.data.value()?.totals;
    const v = (n: number | null | undefined) => (t ? formatCount(n) : '');
    const tiles: Tile[] = [
      { label: 'Articles', value: v(t?.articles), icon: 'file-text', link: { path: [KB_BASE] } },
      { label: 'Live', value: v(t?.live), icon: 'globe', link: { path: [KB_BASE], query: { live: '1' } } },
      { label: 'Drafts', value: v(t?.drafts), icon: 'pencil', link: { path: [KB_BASE], query: { status: 'Draft' } } },
      { label: 'In review', value: v(t?.inReview), icon: 'hourglass', link: { path: [KB_BASE], query: { status: 'InReview' } } },
      { label: 'Review overdue', value: v(t?.reviewOverdue), icon: 'clock', link: { path: [KB_BASE], query: { reviewDue: '1' } }, tone: warnIf((t?.reviewOverdue ?? 0) > 0) },
      { label: 'Expiring soon', value: v(t?.expiringSoon), icon: 'calendar' },
      { label: 'Views', value: v(t?.views), icon: 'eye' },
      { label: 'Helpful', value: t ? formatPercent(t.helpfulPercent) : '', icon: 'thumbs-up' },
      { label: 'Average rating', value: t ? formatRating(t.averageRating) : '', icon: 'star' },
      { label: 'Feedback to review', value: v(t?.pendingFeedback), icon: 'messages', link: { path: [KB_BASE, 'feedback'] }, tone: warnIf((t?.pendingFeedback ?? 0) > 0) },
      { label: 'Searches', value: v(t?.searches), icon: 'search' },
      { label: 'Searches without results', value: v(t?.searchesWithoutResults), icon: 'search-x', tone: warnIf((t?.searchesWithoutResults ?? 0) > 0) },
    ];
    return tiles;
  });

  protected readonly chart = computed<EChartsCoreOption>(() => {
    const daily = this.data.value()?.daily ?? [];
    return {
      tooltip: { trigger: 'axis' },
      legend: { top: 0, right: 0 },
      grid: { left: 40, right: 16, top: 36, bottom: 28 },
      xAxis: { type: 'category', data: daily.map((p) => this.dayLabel(p.date)) },
      yAxis: { type: 'value', minInterval: 1 },
      series: [
        { name: 'Searches', type: 'line', smooth: true, showSymbol: false, data: daily.map((p) => p.searches) },
        { name: 'No results', type: 'line', smooth: true, showSymbol: false, data: daily.map((p) => p.noResults) },
        { name: 'Helpful', type: 'bar', stack: 'feedback', barMaxWidth: 18, data: daily.map((p) => p.helpful) },
        { name: 'Not helpful', type: 'bar', stack: 'feedback', barMaxWidth: 18, data: daily.map((p) => p.notHelpful) },
      ],
    };
  });

  protected readonly chartLabel = computed(() => {
    const t = this.data.value()?.totals;
    return t
      ? `Daily searches and feedback for the last ${this.period()} days: ${t.searches} searches, ${t.searchesWithoutResults} without results, ${t.helpful} helpful and ${t.notHelpful} not helpful votes.`
      : 'Daily searches and feedback';
  });

  constructor() {
    registerKbIcons();
    effect(() => {
      const days = this.period();
      untracked(() => this.data.load(() => this.api.analytics(days), { keepValue: true }));
    });
  }

  protected setDays(days: number): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: { days: days === 30 ? null : days }, queryParamsHandling: 'merge', replaceUrl: true });
  }

  protected go(tile: Tile): void {
    if (tile.link) void this.router.navigate(tile.link.path, { queryParams: tile.link.query ?? {} });
  }

  protected dayLabel(date: string): string {
    // DateOnly "YYYY-MM-DD" → short local date without a time-zone shift.
    const [y, m, d] = date.split('-').map(Number);
    if (!y || !m || !d) return date;
    return formatDate(new Date(y, m - 1, d), { locale: this.me.locale(), style: 'short' });
  }

  protected asStats(rows: unknown): KbArticleStatDto[] {
    return Array.isArray(rows) ? (rows as KbArticleStatDto[]) : [];
  }

  protected count(n: number): string {
    return formatCount(n);
  }

  protected percent(n: number | null): string {
    return formatPercent(n);
  }

  protected rating(n: number | null): string {
    return formatRating(n);
  }

  protected pct(part: number, total: number): string {
    return total > 0 ? formatPercent((part / total) * 100) : '—';
  }

  protected share(part: number, total: number): number {
    return total > 0 ? Math.min(100, (part / total) * 100) : 0;
  }
}
