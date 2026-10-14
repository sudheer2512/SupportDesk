import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TicketDetailDto, TicketsApi } from '@sd/core';
import { SdBadge, SdButton, SdCard, SdEmptyState, SdPriorityBadge, SdSkeleton, SdStatusPill } from '@sd/ui';
import { BlueprintPanelComponent } from './blueprint-panel.component';

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Stand-alone host of the ticket blueprint panel: /automation/tickets/{number or id}. */
@Component({
  selector: 'desk-ticket-blueprint-page',
  imports: [RouterLink, SdBadge, SdButton, SdCard, SdEmptyState, SdPriorityBadge, SdSkeleton, SdStatusPill, BlueprintPanelComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="wrap">
      <a sdButton variant="ghost" size="sm" icon="arrow-left" routerLink="/automation/blueprints">Blueprints</a>
      @if (loading()) {
        <sd-skeleton [lines]="4" />
      } @else if (ticket(); as t) {
        <sd-card>
          <div class="ticket">
            <span class="num">{{ t.displayNumber }}</span>
            <h2>{{ t.subject }}</h2>
            <div class="meta">
              <sd-status-pill [label]="t.status" [color]="t.statusColor" />
              <sd-priority-badge [priority]="t.priority" />
              @if (t.severity) {
                <sd-badge size="sm" variant="outline">{{ t.severity }}</sd-badge>
              }
              <span class="sd-muted">{{ t.departmentName }}{{ t.assigneeName ? ' · ' + t.assigneeName : '' }}</span>
            </div>
          </div>
        </sd-card>
        @if (t.workflowId) {
          <desk-blueprint-panel [ticketId]="t.id" (changed)="refreshTicket()" />
        } @else {
          <sd-empty-state compact icon="workflow" title="This ticket does not follow a blueprint" message="Blueprints are applied to new tickets that match a blueprint's department or category." />
        }
      } @else {
        <sd-empty-state icon="ticket" title="Ticket not found" [message]="'No ticket matches ' + ticketRef() + '.'" />
      }
    </div>
  `,
  styles: `
    .wrap {
      display: flex;
      flex-direction: column;
      gap: 12px;
      max-width: 720px;
    }
    .wrap > a {
      align-self: flex-start;
    }
    .num {
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
    h2 {
      margin: 2px 0 8px;
      font-size: var(--sd-text-lg);
    }
    .meta {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      font-size: var(--sd-text-sm);
    }
  `,
})
export class TicketBlueprintPage {
  private readonly tickets = inject(TicketsApi);

  /** Ticket number or id (route parameter). */
  readonly ticketRef = input.required<string>();

  protected readonly ticket = signal<TicketDetailDto | null>(null);
  protected readonly loading = signal(true);

  constructor() {
    effect(() => {
      const ref = this.ticketRef();
      untracked(() => this.load(ref));
    });
  }

  private load(ref: string): void {
    this.loading.set(true);
    const call = GUID.test(ref) ? this.tickets.get(ref) : this.tickets.getByNumber(ref.replace(/[^0-9]/g, ''));
    call.subscribe({
      next: (t) => {
        this.ticket.set(t);
        this.loading.set(false);
      },
      error: () => {
        this.ticket.set(null);
        this.loading.set(false);
      },
    });
  }

  protected refreshTicket(): void {
    const t = this.ticket();
    if (t) this.tickets.get(t.id).subscribe((fresh) => this.ticket.set(fresh));
  }
}
