import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ModulePlaceholderComponent } from '../../shared/module-placeholder.component';

/** AI Assistant landing page — an honest placeholder until the module's screens are built. */
@Component({
  selector: 'desk-ai-home',
  imports: [ModulePlaceholderComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<desk-module-placeholder title="AI Assistant" icon="sparkles" [subtitle]="subtitle" [planned]="planned" />`,
})
export class AiHomePage {
  protected readonly subtitle = 'Summaries, reply drafts, triage suggestions and approved AI actions.';
  protected readonly planned = [
    'Ticket summaries and sentiment',
    'Reply drafts grounded in the knowledge base',
    'Triage suggestions: category, priority and assignee',
    'AI actions that wait for human approval',
  ];
}
