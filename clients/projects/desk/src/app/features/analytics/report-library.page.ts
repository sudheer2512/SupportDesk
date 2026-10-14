import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { SdButton, SdEmptyState, SdIcon, SdSkeleton } from '@sd/ui';
import { AnalyticsStore } from './analytics.store';
import './analytics-format';

/** /analytics/reports — the standard report library grouped by theme. */
@Component({
  selector: 'desk-analytics-report-library',
  imports: [RouterLink, SdButton, SdEmptyState, SdIcon, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (store.loading() && !store.catalog()) {
      <div class="cards" aria-busy="true">
        @for (i of [1, 2, 3, 4, 5, 6]; track i) {
          <sd-skeleton height="110px" radius="var(--sd-radius-lg)" />
        }
      </div>
    } @else if (store.error()) {
      <sd-empty-state icon="circle-alert" title="Reports could not be loaded" [message]="store.error()">
        <button sdButton type="button" (click)="store.reload()">Retry</button>
      </sd-empty-state>
    } @else if (groups().length === 0) {
      <sd-empty-state icon="chart-column" title="No reports available" />
    } @else {
      @if (!store.teamScope()) {
        <p class="sd-small sd-muted scope">Reports cover the tickets assigned to you. Team-wide analytics need the “analytics.team” permission.</p>
      }
      @for (g of groups(); track g.name) {
        <section class="group" [attr.aria-labelledby]="'g-' + g.name">
          <h2 class="group-title" [id]="'g-' + g.name">{{ g.name }}</h2>
          <div class="cards">
            @for (r of g.reports; track r.key) {
              <a class="card" [routerLink]="['/analytics/reports', r.key]">
                <span class="icon" aria-hidden="true"><sd-icon [name]="r.icon" [size]="18" /></span>
                <span class="body">
                  <span class="name">{{ r.name }}</span>
                  <span class="sd-small sd-muted">{{ r.description }}</span>
                </span>
                <sd-icon name="chevron-right" [size]="16" class="chev" />
              </a>
            }
          </div>
        </section>
      }
    }
  `,
  styles: `
    :host {
      display: grid;
      grid-template-columns: minmax(0, 1fr);
      gap: var(--sd-space-4);
    }
    .scope {
      margin: 0;
    }
    .group-title {
      margin: 0 0 var(--sd-space-2);
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-semibold);
      color: var(--sd-text-2);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .cards {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 300px), 1fr));
      gap: var(--sd-space-3);
    }
    .card {
      display: flex;
      align-items: flex-start;
      gap: 12px;
      padding: 14px 16px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
      box-shadow: var(--sd-shadow-xs);
      color: var(--sd-text);
      text-decoration: none;
      transition: box-shadow var(--sd-dur) var(--sd-ease), border-color var(--sd-dur) var(--sd-ease);
    }
    .card:hover {
      border-color: var(--sd-border-strong);
      box-shadow: var(--sd-shadow-md);
    }
    .card:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .icon {
      display: inline-flex;
      padding: 8px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    .body {
      display: grid;
      flex: 1;
      gap: 2px;
      min-width: 0;
    }
    .name {
      font-weight: var(--sd-weight-semibold);
    }
    .chev {
      align-self: center;
      color: var(--sd-text-3);
    }
  `,
})
export class AnalyticsReportLibraryPage {
  protected readonly store = inject(AnalyticsStore);

  protected readonly groups = computed(() => {
    const map = new Map<string, ReturnType<AnalyticsStore['reports']>>();
    for (const r of this.store.reports()) map.set(r.group, [...(map.get(r.group) ?? []), r]);
    return [...map.entries()].map(([name, reports]) => ({ name, reports }));
  });

  constructor() {
    this.store.ensure();
  }
}
