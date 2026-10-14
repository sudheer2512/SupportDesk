import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AnalyticsApi, ApiError } from '@sd/core';
import { SdDropdownMenu, SdIcon, SdMenuItem, SdPageHeader, ToastService } from '@sd/ui';
import { AnalyticsStore } from './analytics.store';
import './analytics-format';

/** /analytics — page header and section navigation (dashboards, reports, schedules, exports) around the child pages. */
@Component({
  selector: 'desk-analytics-shell',
  imports: [RouterLink, RouterLinkActive, RouterOutlet, SdDropdownMenu, SdIcon, SdPageHeader],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Analytics" icon="chart-column" [subtitle]="subtitle()">
        @if (store.teamScope()) {
          <sd-dropdown-menu sdPageActions size="sm" icon="ellipsis" iconOnly ariaLabel="Analytics maintenance" align="end" [items]="menu" (select)="onMenu($event)" />
        }
      </sd-page-header>
      <div class="desk-page__body">
        <nav class="tabs" aria-label="Analytics sections">
          @for (t of tabs(); track t.path) {
            <a class="tab" [routerLink]="t.path" routerLinkActive="is-active" ariaCurrentWhenActive="page">
              <sd-icon [name]="t.icon" [size]="15" />
              <span>{{ t.label }}</span>
            </a>
          }
        </nav>
        <router-outlet />
      </div>
    </div>
  `,
  styles: `
    .tabs {
      display: flex;
      gap: 4px;
      margin-bottom: var(--sd-space-4);
      border-bottom: 1px solid var(--sd-border);
      overflow-x: auto;
      scrollbar-width: none;
    }
    .tab {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 8px 12px;
      border-bottom: 2px solid transparent;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-medium);
      text-decoration: none;
      white-space: nowrap;
    }
    .tab:hover {
      color: var(--sd-text);
    }
    .tab.is-active {
      border-bottom-color: var(--sd-primary);
      color: var(--sd-primary);
    }
    .tab:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
      border-radius: var(--sd-radius-sm);
    }
  `,
})
export class AnalyticsShellComponent {
  protected readonly store = inject(AnalyticsStore);
  private readonly api = inject(AnalyticsApi);
  private readonly toast = inject(ToastService);

  protected readonly subtitle = computed(() => {
    const c = this.store.catalog();
    if (!c) return 'Dashboards, reports, drill-downs, exports and scheduled delivery.';
    return c.teamScope
      ? `Numbers cover the tickets in your data scope · times in ${c.timeZone}.`
      : `Personal analytics: tickets assigned to you · times in ${c.timeZone}.`;
  });

  protected readonly tabs = computed(() => {
    const c = this.store.catalog();
    const list = [
      { path: '/analytics/dashboards', label: 'Dashboards', icon: 'dashboard' },
      { path: '/analytics/reports', label: 'Reports', icon: 'file-text' },
    ];
    if (!c || c.canSchedule) list.push({ path: '/analytics/schedules', label: 'Scheduled', icon: 'calendar-clock' });
    if (!c || c.canExport) list.push({ path: '/analytics/exports', label: 'Exports', icon: 'download' });
    return list;
  });

  protected readonly menu: SdMenuItem[] = [{ id: 'rebuild', label: 'Rebuild metrics from tickets', icon: 'refresh' }];

  constructor() {
    this.store.ensure();
  }

  protected onMenu(item: SdMenuItem): void {
    if (item.id !== 'rebuild') return;
    this.api.rebuildMetrics(120).subscribe({
      next: (s) =>
        this.toast.success(
          s.rebuildQueued ? 'Rebuild queued' : 'Metrics are current',
          `${s.metrics} of ${s.tickets} tickets have metrics · ${s.missing} missing · ${s.stale} stale.`,
        ),
      error: (e: unknown) => this.toast.error('Could not queue the rebuild', ApiError.from(e).userMessage),
    });
  }
}
