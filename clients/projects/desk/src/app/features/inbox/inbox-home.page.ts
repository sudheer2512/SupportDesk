import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ModulePlaceholderComponent } from '../../shared/module-placeholder.component';

/** Inbox landing page — an honest placeholder until the module's screens are built. */
@Component({
  selector: 'desk-inbox-home',
  imports: [ModulePlaceholderComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<desk-module-placeholder title="Inbox" icon="inbox" [subtitle]="subtitle" [planned]="planned" />`,
})
export class InboxHomePage {
  protected readonly subtitle = 'Live chat, social and messaging conversations across channels.';
  protected readonly planned = [
    'Unified live chat, WhatsApp, social and SMS conversations',
    'Conversation routing with agent presence',
    'Canned responses and AI reply drafts',
    'Convert a conversation into a ticket',
  ];
}
