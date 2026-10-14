import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ModulePlaceholderComponent } from '../../shared/module-placeholder.component';

/** SLA landing page — an honest placeholder until the module's screens are built. */
@Component({
  selector: 'desk-sla-home',
  imports: [ModulePlaceholderComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<desk-module-placeholder title="SLA" icon="timer" [subtitle]="subtitle" [planned]="planned" />`,
})
export class SlaHomePage {
  protected readonly subtitle = 'SLA policies, business hours, holidays and live breach risk.';
  protected readonly planned = [
    'SLA policies and targets per priority and customer tier',
    'Business hours and holiday calendars',
    'Escalation levels and breach notifications',
    'Live at-risk and breached tickets',
  ];
}
