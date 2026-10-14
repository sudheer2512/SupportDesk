import { ChangeDetectionStrategy, Component } from '@angular/core';
import { SdPageHeader } from '@sd/ui';
import { ActivitiesCalendarComponent } from './activities-calendar.component';
import { CUSTOMER_TABS, ModuleTabsComponent } from './shared/module-tabs.component';

/** /customers/activities — service visits and scheduled activities across all (visible) customers. */
@Component({
  selector: 'desk-activities-page',
  imports: [SdPageHeader, ModuleTabsComponent, ActivitiesCalendarComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Service activities" subtitle="On-site visits, remote sessions, trainings, maintenance and upgrades." icon="calendar" />
      <desk-module-tabs [tabs]="tabs" ariaLabel="Customers sections" />
      <div class="desk-page__body">
        <desk-activities-calendar />
      </div>
    </div>
  `,
})
export class ActivitiesPage {
  protected readonly tabs = CUSTOMER_TABS;
}
