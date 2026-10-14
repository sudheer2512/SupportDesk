import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { Guid, TicketDetailDto } from '@sd/core';
import { TicketContext } from '../../tickets/ticket/ticket-context.service';
import { BlueprintPanelComponent } from './blueprint-panel.component';

/** "Blueprint" side tab of the ticket view (registered in tickets/extensions/ticket-panels.ts). */
@Component({
  selector: 'desk-blueprint-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BlueprintPanelComponent],
  template: `<desk-blueprint-panel [ticketId]="ticketId()" [showEmpty]="true" (changed)="changed()" />`,
})
export class BlueprintTabComponent {
  readonly ticketId = input.required<Guid>();
  readonly ticket = input<TicketDetailDto | null>(null);

  private readonly ctx = inject(TicketContext);

  protected changed(): void {
    this.ctx.refresh();
    this.ctx.loadTimeline();
  }
}
