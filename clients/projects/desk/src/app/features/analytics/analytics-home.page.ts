import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ModulePlaceholderComponent } from '../../shared/module-placeholder.component';

/** Analytics landing page — an honest placeholder until the module's screens are built. */
@Component({
  selector: 'desk-analytics-home',
  imports: [ModulePlaceholderComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<desk-module-placeholder title="Analytics" icon="chart-column" [subtitle]="subtitle" [planned]="planned" />`,
})
export class AnalyticsHomePage {
  protected readonly subtitle = 'Dashboards, reports, scheduled exports and drill-downs.';
  protected readonly planned = [
    'Dashboards with drill-down charts',
    'Agent, team and SLA performance reports',
    'CSAT and sentiment trends',
    'Scheduled and exported reports',
  ];
}
