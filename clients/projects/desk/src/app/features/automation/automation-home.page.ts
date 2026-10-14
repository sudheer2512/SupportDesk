import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ModulePlaceholderComponent } from '../../shared/module-placeholder.component';

/** Automation landing page — an honest placeholder until the module's screens are built. */
@Component({
  selector: 'desk-automation-home',
  imports: [ModulePlaceholderComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<desk-module-placeholder title="Automation" icon="workflow" [subtitle]="subtitle" [planned]="planned" />`,
})
export class AutomationHomePage {
  protected readonly subtitle = 'Assignment rules, workflows, macros, escalations and schedules.';
  protected readonly planned = [
    'Assignment and routing rules (round robin, load, skills)',
    'Workflows with state transitions and required fields',
    'Macros and scheduled automations',
    'Webhooks and approval steps',
  ];
}
