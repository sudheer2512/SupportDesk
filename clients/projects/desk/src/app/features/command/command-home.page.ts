import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ModulePlaceholderComponent } from '../../shared/module-placeholder.component';

/** Command Center landing page — an honest placeholder until the module's screens are built. */
@Component({
  selector: 'desk-command-home',
  imports: [ModulePlaceholderComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<desk-module-placeholder title="Command Center" icon="radar" [subtitle]="subtitle" [planned]="planned" />`,
})
export class CommandHomePage {
  protected readonly subtitle = 'Your live queue, SLA risk and team activity at a glance.';
  protected readonly planned = [
    'My open, due-today and overdue tickets',
    'Live SLA risk board with countdowns',
    'Team workload and agent availability',
    'Recent customer replies and @mentions',
    'Escalations and approvals waiting for you',
  ];
}
