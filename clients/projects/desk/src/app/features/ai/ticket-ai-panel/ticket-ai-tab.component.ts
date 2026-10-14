import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { Guid, TicketDetailDto } from '@sd/core';
import { TicketContext } from '../../tickets/ticket/ticket-context.service';
import { TicketAiPanelComponent } from './ticket-ai-panel.component';

/**
 * "AI" side tab of the ticket view (registered in tickets/extensions/ticket-panels.ts): drafts go into the
 * composer and applied suggestions refresh the open ticket.
 */
@Component({
  selector: 'desk-ticket-ai-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TicketAiPanelComponent],
  template: `<desk-ticket-ai-panel [ticketId]="ticketId()" (insertReply)="ctx.insertIntoComposer($event)" (ticketChanged)="changed()" />`,
})
export class TicketAiTabComponent {
  readonly ticketId = input.required<Guid>();
  readonly ticket = input<TicketDetailDto | null>(null);

  protected readonly ctx = inject(TicketContext);

  protected changed(): void {
    this.ctx.refresh();
    this.ctx.loadTimeline();
  }
}
