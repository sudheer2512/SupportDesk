import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { Guid, TicketDetailDto } from '@sd/core';
import { TicketContext } from '../../tickets/ticket/ticket-context.service';
import { EngineeringPanelComponent } from './engineering-panel.component';

/**
 * "Engineering" side tab of the ticket view (registered in tickets/extensions/ticket-panels.ts as `engineering.links`):
 * escalations and links refresh the open ticket (substatus, timeline).
 */
@Component({
  selector: 'desk-engineering-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [EngineeringPanelComponent],
  template: `<desk-engineering-panel [ticketId]="ticketId()" (ticketChanged)="changed()" />`,
})
export class EngineeringTabComponent {
  readonly ticketId = input.required<Guid>();
  readonly ticket = input<TicketDetailDto | null>(null);

  private readonly ctx = inject(TicketContext);

  protected changed(): void {
    this.ctx.refresh();
    this.ctx.loadTimeline();
  }
}
