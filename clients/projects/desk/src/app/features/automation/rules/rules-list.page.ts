import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDropList, moveItemInArray } from '@angular/cdk/drag-drop';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AutomationApi, RuleConflictDto, RuleSummaryDto } from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdEmptyState,
  SdIcon,
  SdInput,
  SdRelativeTimePipe,
  SdSkeleton,
  SdToggle,
  SdTooltip,
  ToastService,
} from '@sd/ui';
import { AutomationContext } from '../shared/automation-context.service';
import { errorMessage } from '../shared/automation-ui';

interface TriggerGroup {
  trigger: string;
  label: string;
  rules: RuleSummaryDto[];
}

/** Rules grouped by trigger, in execution order (drag to reorder), with activation, versions, conflicts and run stats. */
@Component({
  selector: 'desk-rules-list',
  imports: [FormsModule, RouterLink, CdkDropList, CdkDrag, CdkDragHandle, SdButton, SdBadge, SdIcon, SdToggle, SdTooltip, SdInput, SdEmptyState, SdSkeleton, SdRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="toolbar">
      <input sdInput type="search" class="search" placeholder="Search rules" aria-label="Search rules" [(ngModel)]="query" />
      <select sdInput class="trigger" aria-label="Trigger" [(ngModel)]="triggerFilter">
        <option value="">All triggers</option>
        @for (t of ctx.catalog()?.triggers ?? []; track t.key) {
          <option [value]="t.key">{{ t.label }}</option>
        }
      </select>
      <span class="sd-spacer"></span>
      @if (conflicts().length) {
        <sd-badge tone="warning" icon="triangle-alert">{{ conflicts().length }} conflict{{ conflicts().length === 1 ? '' : 's' }}</sd-badge>
      }
      <a sdButton variant="primary" icon="plus" routerLink="new">New rule</a>
    </div>

    @if (loading()) {
      <div class="skeletons"><sd-skeleton [lines]="6" height="44px" /></div>
    } @else if (rules().length === 0) {
      <sd-empty-state icon="zap" title="No automation rules yet" message="Rules react to ticket events, run on a schedule, or are called by other systems through a webhook.">
        <a sdButton variant="primary" icon="plus" routerLink="new">Create the first rule</a>
      </sd-empty-state>
    } @else {
      @for (g of groups(); track g.trigger) {
        <section class="group" [attr.aria-label]="g.label + ' rules'">
          <h2>
            {{ g.label }} <span class="sd-muted count">{{ g.rules.length }}</span>
          </h2>
          <ol class="rules" cdkDropList [cdkDropListDisabled]="!canReorder()" (cdkDropListDropped)="drop(g, $event)">
            @for (r of g.rules; track r.id; let i = $index; let last = $last) {
              <li class="rule" cdkDrag cdkDragLockAxis="y" [class.inactive]="!r.isActive">
                @if (canReorder()) {
                  <button type="button" class="grip" cdkDragHandle [attr.aria-label]="'Drag to reorder ' + r.name"><sd-icon name="grip" [size]="14" /></button>
                }
                <span class="order sd-num" aria-label="Execution order">{{ i + 1 }}</span>
                <div class="main">
                  <a class="name" [routerLink]="r.id">{{ r.name }}</a>
                  @if (r.description) {
                    <span class="desc sd-truncate">{{ r.description }}</span>
                  }
                  <div class="meta">
                    @if (r.publishedVersionNumber) {
                      <sd-badge size="sm" tone="success" variant="outline">v{{ r.publishedVersionNumber }} published</sd-badge>
                    } @else {
                      <sd-badge size="sm" tone="neutral" variant="outline">Draft only</sd-badge>
                    }
                    @if (r.hasUnpublishedDraft && r.publishedVersionNumber) {
                      <sd-badge size="sm" tone="info">Unpublished draft v{{ r.latestVersionNumber }}</sd-badge>
                    }
                    @if (r.stopProcessing) {
                      <sd-badge size="sm" tone="accent" icon="octagon-alert">Stops processing</sd-badge>
                    }
                    @if (conflictsFor(r.id); as list) {
                      @if (list.length) {
                        <span [sdTooltip]="conflictText(list)">
                          <sd-badge size="sm" tone="warning" icon="triangle-alert">{{ list.length }} conflict{{ list.length === 1 ? '' : 's' }}</sd-badge>
                        </span>
                      }
                    }
                    @if (r.scheduleIntervalMinutes) {
                      <span class="sd-muted small"><sd-icon name="clock" [size]="12" /> every {{ r.scheduleIntervalMinutes }} min</span>
                    }
                    @if (r.webhookUrl) {
                      <span class="sd-muted small mono">{{ r.webhookUrl }}</span>
                    }
                  </div>
                </div>
                <div class="stats">
                  <span class="sd-num">{{ r.executionCount }}</span>
                  <span class="sd-muted small">{{ r.lastExecutedAt ? 'last ' + (r.lastExecutedAt | sdRelativeTime) : 'never run' }}</span>
                </div>
                <sd-toggle
                  [value]="r.isActive"
                  [disabled]="!r.publishedVersionNumber || busy() === r.id"
                  [ariaLabel]="(r.isActive ? 'Deactivate ' : 'Activate ') + r.name"
                  (valueChange)="toggle(r, $event)"
                />
                <div class="move">
                  <button sdButton size="sm" variant="ghost" icon="arrow-up" iconOnly [attr.aria-label]="'Run ' + r.name + ' earlier'" [disabled]="i === 0 || !canReorder()" (click)="nudge(g, i, -1)"></button>
                  <button sdButton size="sm" variant="ghost" icon="arrow-down" iconOnly [attr.aria-label]="'Run ' + r.name + ' later'" [disabled]="last || !canReorder()" (click)="nudge(g, i, 1)"></button>
                </div>
              </li>
            }
          </ol>
        </section>
      }
      @if (!canReorder()) {
        <p class="sd-muted small">Clear the search to reorder rules.</p>
      }
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
    .search {
      width: 240px;
    }
    .trigger {
      width: 200px;
    }
    .group {
      margin-bottom: 20px;
    }
    h2 {
      margin: 0 0 8px;
      font-size: var(--sd-text-md);
      font-weight: var(--sd-weight-semibold);
    }
    .count {
      font-weight: var(--sd-weight-regular);
    }
    .rules {
      margin: 0;
      padding: 0;
      list-style: none;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      overflow: hidden;
    }
    .rule {
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 10px 12px;
      background: var(--sd-surface);
      border-bottom: 1px solid var(--sd-divider);
    }
    .rule:last-child {
      border-bottom: 0;
    }
    .rule.inactive .name {
      color: var(--sd-text-3);
    }
    .rule.cdk-drag-preview {
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      box-shadow: var(--sd-shadow-lg);
    }
    .rule.cdk-drag-placeholder {
      opacity: 0.4;
    }
    .grip {
      display: inline-flex;
      padding: 4px;
      border: 0;
      background: transparent;
      color: var(--sd-text-3);
      cursor: grab;
    }
    .grip:focus-visible {
      box-shadow: var(--sd-focus-ring);
      border-radius: var(--sd-radius-xs);
    }
    .order {
      width: 22px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
      text-align: right;
    }
    .main {
      display: flex;
      flex-direction: column;
      gap: 3px;
      flex: 1;
      min-width: 0;
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
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .meta {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
    }
    .small {
      font-size: var(--sd-text-xs);
      display: inline-flex;
      align-items: center;
      gap: 3px;
    }
    .mono {
      font-family: var(--sd-font-mono);
    }
    .stats {
      display: flex;
      flex-direction: column;
      align-items: flex-end;
      min-width: 90px;
    }
    .move {
      display: flex;
    }
    .skeletons {
      padding: 8px 0;
    }
    @media (max-width: 720px) {
      .stats,
      .move {
        display: none;
      }
    }
  `,
})
export class RulesListPage implements OnInit {
  protected readonly ctx = inject(AutomationContext);
  private readonly api = inject(AutomationApi);
  private readonly toast = inject(ToastService);

  protected readonly rules = signal<RuleSummaryDto[]>([]);
  protected readonly conflicts = signal<RuleConflictDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly busy = signal<string | null>(null);
  protected readonly query = signal('');
  protected readonly triggerFilter = signal('');

  protected readonly canReorder = computed(() => !this.query().trim());

  protected readonly groups = computed<TriggerGroup[]>(() => {
    const q = this.query().trim().toLowerCase();
    const t = this.triggerFilter();
    const order = (this.ctx.catalog()?.triggers ?? []).map((x) => x.key);
    const filtered = this.rules().filter((r) => (!t || r.trigger === t) && (!q || r.name.toLowerCase().includes(q) || (r.description ?? '').toLowerCase().includes(q)));
    const triggers = [...new Set(filtered.map((r) => r.trigger))].sort((a, b) => order.indexOf(a) - order.indexOf(b));
    return triggers.map((trigger) => ({
      trigger,
      label: this.ctx.triggerLabel(trigger),
      rules: filtered.filter((r) => r.trigger === trigger).sort((a, b) => a.executionOrder - b.executionOrder || a.name.localeCompare(b.name)),
    }));
  });

  ngOnInit(): void {
    this.reload();
  }

  protected reload(): void {
    this.loading.set(true);
    this.api.list().subscribe({
      next: (rules) => {
        this.rules.set(rules);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
    this.api.conflicts().subscribe((c) => this.conflicts.set(c));
  }

  protected conflictsFor(ruleId: string): RuleConflictDto[] {
    return this.conflicts().filter((c) => c.ruleId === ruleId || c.otherRuleId === ruleId);
  }

  protected conflictText(list: RuleConflictDto[]): string {
    return list.map((c) => c.explanation).join(' • ');
  }

  protected toggle(rule: RuleSummaryDto, active: boolean): void {
    this.busy.set(rule.id);
    this.api.setActive(rule.id, active).subscribe({
      next: (detail) => {
        this.rules.update((list) => list.map((r) => (r.id === rule.id ? detail.rule : r)));
        this.busy.set(null);
        this.toast.success(active ? 'Rule activated' : 'Rule deactivated', rule.name);
        this.api.conflicts().subscribe((c) => this.conflicts.set(c));
      },
      error: (err: unknown) => {
        this.busy.set(null);
        this.toast.error('Could not change the rule', errorMessage(err));
        this.rules.update((list) => [...list]);
      },
    });
  }

  protected drop(group: TriggerGroup, event: CdkDragDrop<unknown>): void {
    if (event.previousIndex === event.currentIndex) return;
    const ordered = group.rules.slice();
    moveItemInArray(ordered, event.previousIndex, event.currentIndex);
    this.saveOrder(ordered);
  }

  protected nudge(group: TriggerGroup, index: number, delta: number): void {
    const ordered = group.rules.slice();
    moveItemInArray(ordered, index, index + delta);
    this.saveOrder(ordered);
  }

  private saveOrder(ordered: RuleSummaryDto[]): void {
    // Optimistic: renumber locally, then persist.
    const orders = new Map(ordered.map((r, i) => [r.id, (i + 1) * 10]));
    this.rules.update((list) => list.map((r) => (orders.has(r.id) ? { ...r, executionOrder: orders.get(r.id)! } : r)));
    this.api.reorder(ordered.map((r) => r.id)).subscribe({
      next: (saved) => {
        const byId = new Map(saved.map((r) => [r.id, r]));
        this.rules.update((list) => list.map((r) => byId.get(r.id) ?? r));
        this.api.conflicts().subscribe((c) => this.conflicts.set(c));
      },
      error: () => this.reload(),
    });
  }
}
