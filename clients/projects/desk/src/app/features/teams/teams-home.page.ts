import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ModulePlaceholderComponent } from '../../shared/module-placeholder.component';

/** Teams & Agents landing page — an honest placeholder until the module's screens are built. */
@Component({
  selector: 'desk-teams-home',
  imports: [ModulePlaceholderComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<desk-module-placeholder title="Teams &amp; Agents" icon="users" [subtitle]="subtitle" [planned]="planned" />`,
})
export class TeamsHomePage {
  protected readonly subtitle = 'Departments, teams, skills, shifts and agent availability.';
  protected readonly planned = [
    'Departments, teams and skills',
    'Agent capacity and live availability',
    'Shift calendar and holidays',
    'Supervisor monitoring of queues and agents',
  ];
}
