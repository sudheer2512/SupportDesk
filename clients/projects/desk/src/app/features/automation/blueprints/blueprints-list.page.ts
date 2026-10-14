import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { BlueprintSummaryDto, BlueprintsApi } from '@sd/core';
import { SdBadge, SdButton, SdEmptyState, SdInput, SdRelativeTimePipe, SdSkeleton, SdToggle, ToastService } from '@sd/ui';
import { AutomationContext } from '../shared/automation-context.service';
import { errorMessage } from '../shared/automation-ui';

/** Blueprints (state-transition workflows) with applicability, published version, activation and tickets in flight. */
@Component({
  selector: 'desk-blueprints-list',
  imports: [FormsModule, RouterLink, SdBadge, SdButton, SdEmptyState, SdInput, SdSkeleton, SdToggle, SdRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="toolbar">
      <form class="open-ticket" (ngSubmit)="openTicket()">
        <input sdInput name="ticket" inputmode="numeric" aria-label="Ticket number" placeholder="Ticket #" [(ngModel)]="ticketNumber" />
        <button sdButton type="submit" variant="ghost" icon="ticket">Open ticket blueprint</button>
      </form>
      <span class="sd-spacer"></span>
      <a sdButton variant="primary" icon="plus" routerLink="new">New blueprint</a>
    </div>
    @if (loading()) {
      <sd-skeleton [lines]="4" height="44px" />
    } @else if (items().length === 0) {
      <sd-empty-state icon="workflow" title="No blueprints yet" message="Blueprints guide tickets through defined states with mandatory fields, role checks and approvals.">
        <a sdButton variant="primary" icon="plus" routerLink="new">Design a blueprint</a>
      </sd-empty-state>
    } @else {
      <ul class="cards">
        @for (b of items(); track b.id) {
          <li class="card" [class.inactive]="!b.isActive">
            <div class="head">
              <a class="name" [routerLink]="b.id">{{ b.name }}</a>
              <span class="sd-spacer"></span>
              <sd-toggle [value]="b.isActive" [disabled]="!b.publishedVersionNumber" [ariaLabel]="(b.isActive ? 'Deactivate ' : 'Activate ') + b.name" (valueChange)="toggle(b, $event)" />
            </div>
            @if (b.description) {
              <p class="desc">{{ b.description }}</p>
            }
            <div class="meta">
              <sd-badge size="sm" variant="outline">{{ b.departmentName ?? 'All departments' }}</sd-badge>
              @if (b.categoryName) {
                <sd-badge size="sm" variant="outline">{{ b.categoryName }}</sd-badge>
              }
              @if (b.publishedVersionNumber) {
                <sd-badge size="sm" tone="success">v{{ b.publishedVersionNumber }} published</sd-badge>
              } @else {
                <sd-badge size="sm">Draft</sd-badge>
              }
              @if (b.hasUnpublishedDraft && b.publishedVersionNumber) {
                <sd-badge size="sm" tone="info">Draft v{{ b.latestVersionNumber }}</sd-badge>
              }
            </div>
            <div class="foot sd-muted">
              <span>{{ b.activeTickets }} open ticket{{ b.activeTickets === 1 ? '' : 's' }}</span>
              <span>updated {{ b.updatedAt | sdRelativeTime }}</span>
            </div>
          </li>
        }
      </ul>
    }
  `,
  styles: `
    .toolbar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      margin-bottom: 16px;
    }
    .open-ticket {
      display: flex;
      gap: 6px;
    }
    .open-ticket input {
      width: 110px;
    }
    .cards {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 320px), 1fr));
      gap: 12px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .card {
      display: flex;
      flex-direction: column;
      gap: 8px;
      padding: 14px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    .card.inactive {
      opacity: 0.75;
    }
    .head {
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .name {
      color: var(--sd-text);
      font-weight: var(--sd-weight-semibold);
      text-decoration: none;
    }
    .name:hover {
      color: var(--sd-primary);
      text-decoration: underline;
    }
    .desc {
      margin: 0;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .meta {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    .foot {
      display: flex;
      justify-content: space-between;
      font-size: var(--sd-text-xs);
    }
  `,
})
export class BlueprintsListPage implements OnInit {
  private readonly api = inject(BlueprintsApi);
  private readonly ctx = inject(AutomationContext);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  protected readonly items = signal<BlueprintSummaryDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly ticketNumber = signal('');

  ngOnInit(): void {
    this.api.list().subscribe({
      next: (list) => {
        this.items.set(list);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  protected toggle(b: BlueprintSummaryDto, active: boolean): void {
    this.api.setActive(b.id, active).subscribe({
      next: (d) => {
        this.items.update((list) => list.map((x) => (x.id === b.id ? d.blueprint : x)));
        this.ctx.refreshBlueprints();
        this.toast.success(active ? 'Blueprint activated' : 'Blueprint deactivated', b.name);
      },
      error: (err: unknown) => {
        this.items.update((list) => [...list]);
        this.toast.error('Could not change the blueprint', errorMessage(err));
      },
    });
  }

  protected openTicket(): void {
    const n = this.ticketNumber().replace(/[^0-9]/g, '');
    if (n) void this.router.navigate(['/automation/tickets', n]);
  }
}
