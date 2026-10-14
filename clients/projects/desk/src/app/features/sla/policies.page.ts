import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { MeStore, SlaAdminApi, SlaPolicyListItemDto, saveErrorMessage } from '@sd/core';
import { SdBadge, SdButton, SdEmptyState, SdPageHeader, SdRelativeTimePipe, SdSkeleton, ToastService } from '@sd/ui';
import { ModuleTabsComponent } from '../customers/shared/module-tabs.component';
import { SECTION_STYLES, TABLE_STYLES } from '../customers/shared/styles';
import { PAGE_STYLES, errorText } from '../teams/shared/org-ui';
import { SLA_TABS } from './sla-ui';

/**
 * /sla/policies — SLA policies in evaluation order (first match wins; the default policy is the fallback). Reorder with
 * the arrows and save; open a policy to edit targets, escalations and conditions or to simulate it against a ticket.
 */
@Component({
  selector: 'desk-sla-policies-page',
  imports: [RouterLink, SdPageHeader, SdButton, SdBadge, SdSkeleton, SdEmptyState, SdRelativeTimePipe, ModuleTabsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="SLA" subtitle="Service commitments per priority, business hours and escalations." icon="timer">
        @if (canManage) {
          <a sdButton sdPageActions variant="primary" icon="plus" routerLink="/sla/policies/new">New policy</a>
        }
      </sd-page-header>
      <desk-module-tabs [tabs]="tabs" ariaLabel="SLA sections" />
      <div class="desk-page__body">
        <p class="small muted intro">
          Tickets get the SLA of their contract, otherwise of the first active policy below whose conditions match, otherwise of the default policy.
          Changing a policy recalculates the due dates of open tickets (time already elapsed is kept).
        </p>
        @if (dirty()) {
          <div class="toolbar">
            <span class="small">Evaluation order changed.</span>
            <button sdButton size="sm" variant="primary" icon="check" (click)="saveOrder()" [loading]="saving()">Save order</button>
            <button sdButton size="sm" variant="ghost" (click)="load()">Discard</button>
          </div>
        }
        @if (error()) {
          <div class="error-box" role="alert"><span>{{ error() }}</span><button sdButton size="sm" (click)="load()">Try again</button></div>
        } @else if (loading()) {
          <div aria-busy="true"><sd-skeleton [lines]="6" /></div>
        } @else if (policies().length === 0) {
          <sd-empty-state icon="timer" title="No SLA policies" message="Create a policy with targets per priority." />
        } @else {
          <div class="dt-wrap">
            <table class="dt">
              <caption class="sd-visually-hidden">SLA policies in evaluation order</caption>
              <thead>
                <tr>
                  <th scope="col" class="num">#</th>
                  <th scope="col">Policy</th>
                  <th scope="col">Calendar</th>
                  <th scope="col" class="num">Conditions</th>
                  <th scope="col" class="num">Targets</th>
                  <th scope="col" class="num">Escalations</th>
                  <th scope="col" class="num">Running</th>
                  <th scope="col">Updated</th>
                  @if (canManage) {
                    <th scope="col"><span class="sd-visually-hidden">Order</span></th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (p of policies(); track p.id; let i = $index; let last = $last) {
                  <tr class="clickable" tabindex="0" (click)="open(p)" (keydown.enter)="open(p)" [attr.aria-label]="'Open ' + p.name">
                    <td class="num">{{ p.isDefault ? '—' : i + 1 }}</td>
                    <td>
                      <strong>{{ p.name }}</strong>
                      <span class="pills">
                        @if (p.isDefault) {
                          <sd-badge size="sm" tone="primary">Default</sd-badge>
                        }
                        @if (!p.isActive) {
                          <sd-badge size="sm">Inactive</sd-badge>
                        }
                        <sd-badge size="sm" tone="neutral">v{{ p.version }}</sd-badge>
                      </span>
                      @if (p.description) {
                        <div class="xs muted">{{ p.description }}</div>
                      }
                    </td>
                    <td class="small">{{ p.calendarName ?? 'Department / 24x7' }}</td>
                    <td class="num">{{ p.isDefault ? 'Fallback' : p.conditionCount || 'All tickets' }}</td>
                    <td class="num">{{ p.targetCount }}</td>
                    <td class="num">{{ p.escalationCount }}</td>
                    <td class="num">{{ p.activeTickets }}</td>
                    <td class="small nowrap">{{ p.updatedAt | sdRelativeTime }}</td>
                    @if (canManage) {
                      <td class="row-actions" (click)="$event.stopPropagation()" (keydown.enter)="$event.stopPropagation()">
                        @if (!p.isDefault) {
                          <button sdButton size="sm" variant="ghost" icon="arrow-up" iconOnly [disabled]="i === 0" [attr.aria-label]="'Move ' + p.name + ' up'" (click)="move(i, -1)"></button>
                          <button sdButton size="sm" variant="ghost" icon="arrow-down" iconOnly [disabled]="last || policies()[i + 1].isDefault" [attr.aria-label]="'Move ' + p.name + ' down'"
                            (click)="move(i, 1)"></button>
                        }
                      </td>
                    }
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </div>
    </div>
  `,
  styles: [TABLE_STYLES, SECTION_STYLES, PAGE_STYLES, `.intro { margin: 0 0 var(--sd-space-3); max-width: 80ch; } .pills { margin-left: 6px; display: inline-flex; }`],
})
export class PoliciesPage {
  private readonly api = inject(SlaAdminApi);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  protected readonly canManage = inject(MeStore).can('admin.sla');
  protected readonly tabs = SLA_TABS;
  protected readonly policies = signal<SlaPolicyListItemDto[]>([]);
  private readonly original = signal<string>('');
  protected readonly dirty = computed(() => this.original() !== '' && this.order() !== this.original());
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    this.load();
  }

  private order(): string {
    return this.policies().map((p) => p.id).join(',');
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.policies().subscribe({
      next: (list) => {
        const sorted = [...list].sort((a, b) => Number(a.isDefault) - Number(b.isDefault) || a.evaluationOrder - b.evaluationOrder);
        this.policies.set(sorted);
        this.original.set(sorted.map((p) => p.id).join(','));
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.error.set(errorText(err));
        this.loading.set(false);
      },
    });
  }

  protected open(p: SlaPolicyListItemDto): void {
    void this.router.navigate(['/sla/policies', p.id]);
  }

  protected move(index: number, delta: number): void {
    const list = [...this.policies()];
    const target = index + delta;
    if (target < 0 || target >= list.length || list[target].isDefault) return;
    [list[index], list[target]] = [list[target], list[index]];
    this.policies.set(list);
  }

  protected saveOrder(): void {
    this.saving.set(true);
    this.api.reorder(this.policies().filter((p) => !p.isDefault).map((p) => p.id)).subscribe({
      next: () => {
        this.saving.set(false);
        this.toast.success('Evaluation order saved', 'Open tickets are being re-evaluated.');
        this.load();
      },
      error: (err: unknown) => {
        this.saving.set(false);
        this.toast.error('Could not save the order', saveErrorMessage(err));
      },
    });
  }
}
