import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Guid, StatusCategory, TicketListItemDto, TicketsApi, cursorList } from '@sd/core';
import { SdBadge, SdButton, SdEmptyState, SdInput, SdPriorityBadge, SdRelativeTimePipe, SdSkeleton, SdStatusPill } from '@sd/ui';
import { TABLE_STYLES } from './shared/styles';

type Scope = 'open' | 'closed' | 'all';
const OPEN: StatusCategory[] = ['Open', 'Assigned', 'InProgress', 'Waiting'];
const CLOSED: StatusCategory[] = ['Resolved', 'Closed'];

/** Current and historical tickets of one customer or contact, filtered by state/priority/text (ticket data scope applies). */
@Component({
  selector: 'desk-c360-tickets',
  imports: [FormsModule, RouterLink, SdButton, SdInput, SdPriorityBadge, SdStatusPill, SdBadge, SdSkeleton, SdEmptyState, SdRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="toolbar">
      <div class="seg" role="group" aria-label="Ticket state">
        @for (s of scopes; track s.id) {
          <button sdButton size="sm" [variant]="scope() === s.id ? 'primary' : 'secondary'" [pressed]="scope() === s.id" (click)="scope.set(s.id)">{{ s.label }}</button>
        }
      </div>
      <select sdInput size="sm" class="prio" aria-label="Priority" [ngModel]="priority()" (ngModelChange)="priority.set($event)">
        <option value="">All priorities</option>
        @for (p of priorities; track p) {
          <option [value]="p">{{ p }}</option>
        }
      </select>
      <input sdInput size="sm" type="search" class="q" placeholder="Search subject…" aria-label="Search tickets" [ngModel]="q()" (ngModelChange)="onSearch($event)" />
      @if (list.total() !== null) {
        <span class="sd-small sd-muted">{{ list.total() }} tickets</span>
      }
    </div>
    @if (list.loading()) {
      <sd-skeleton [lines]="5" />
    } @else if (list.error()) {
      <sd-empty-state icon="circle-alert" title="Tickets could not be loaded" [message]="list.error()!.userMessage" compact />
    } @else if (list.isEmpty()) {
      <sd-empty-state icon="ticket" title="No tickets" message="Nothing matches these filters — or the tickets are outside your departments." compact />
    } @else {
      <div class="dt-wrap">
        <table class="dt">
          <thead>
            <tr>
              <th scope="col">#</th>
              <th scope="col">Subject</th>
              <th scope="col">Status</th>
              <th scope="col">Priority</th>
              <th scope="col">Contact</th>
              <th scope="col">Assignee</th>
              <th scope="col">Created</th>
            </tr>
          </thead>
          <tbody>
            @for (t of list.items(); track t.id) {
              <tr>
                <td class="nowrap"><a [routerLink]="['/tickets', t.id]">{{ t.displayNumber }}</a></td>
                <td>
                  <a [routerLink]="['/tickets', t.id]">{{ t.subject }}</a>
                  @if (t.isEscalated) {
                    <sd-badge tone="danger" size="sm">Escalated</sd-badge>
                  }
                  @if (t.slaState === 'Breached') {
                    <sd-badge tone="danger" variant="outline" size="sm">SLA breached</sd-badge>
                  }
                </td>
                <td><sd-status-pill [label]="t.status" [color]="t.statusColor" /></td>
                <td><sd-priority-badge [priority]="t.priority" /></td>
                <td>{{ t.contactName ?? '—' }}</td>
                <td>{{ t.assigneeName ?? 'Unassigned' }}</td>
                <td class="nowrap sd-muted">{{ t.createdAt | sdRelativeTime }}</td>
              </tr>
            }
          </tbody>
        </table>
      </div>
      @if (list.hasMore()) {
        <div class="more">
          <button sdButton size="sm" [loading]="list.loadingMore()" (click)="list.loadMore()">Load more</button>
        </div>
      }
    }
  `,
  styles: [
    TABLE_STYLES,
    `
      .toolbar {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sd-space-2);
        margin-bottom: var(--sd-space-3);
      }
      .seg {
        display: inline-flex;
        gap: 4px;
      }
      .prio {
        width: 150px;
      }
      .q {
        width: 220px;
      }
      .more {
        display: flex;
        justify-content: center;
        margin-top: var(--sd-space-3);
      }
      sd-badge {
        margin-left: 6px;
      }
    `,
  ],
})
export class C360TicketsComponent {
  private readonly tickets = inject(TicketsApi);
  readonly customerId = input<Guid | null>(null);
  readonly contactId = input<Guid | null>(null);

  protected readonly scopes: { id: Scope; label: string }[] = [
    { id: 'open', label: 'Open' },
    { id: 'closed', label: 'Resolved & closed' },
    { id: 'all', label: 'All' },
  ];
  protected readonly priorities = ['Critical', 'High', 'Medium', 'Low'];
  protected readonly scope = signal<Scope>('open');
  protected readonly priority = signal('');
  protected readonly q = signal('');
  private timer: ReturnType<typeof setTimeout> | null = null;

  protected readonly list = cursorList<TicketListItemDto>((cursor) =>
    this.tickets.list({
      customerId: this.customerId(),
      contactId: this.contactId(),
      statusCategory: this.scope() === 'open' ? OPEN : this.scope() === 'closed' ? CLOSED : null,
      priority: this.priority() || null,
      q: this.q() || null,
      sort: 'created',
      dir: 'desc',
      cursor,
      limit: 25,
      includeTotal: !cursor,
    }),
  );

  constructor() {
    effect(() => {
      this.customerId();
      this.contactId();
      this.scope();
      this.priority();
      this.q();
      untracked(() => this.list.load());
    });
  }

  protected onSearch(value: string): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => this.q.set(value), 300);
  }
}
