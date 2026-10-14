import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDropList, moveItemInArray } from '@angular/cdk/drag-drop';
import { ChangeDetectionStrategy, Component, computed, inject, input, model } from '@angular/core';
import { ActionTypeDto, RuleActionDef } from '@sd/core';
import { SdButton, SdDropdownMenu, SdIcon, SdMenuItem } from '@sd/ui';
import { ActionParamsEditor } from './action-params-editor.component';
import { AutomationContext } from './automation-context.service';
import { IssueMap, issuesUnder } from './automation-ui';

/**
 * Ordered list of automation actions: add (grouped by category), reorder by drag handle or ↑/↓ buttons, remove,
 * and edit parameters with the schema-driven editor. Shared by the rule designer and blueprint transitions.
 * `<desk-action-list [(actions)]="actions" [issues]="issues" pathPrefix="actions" />`
 */
@Component({
  selector: 'desk-action-list',
  imports: [CdkDropList, CdkDrag, CdkDragHandle, SdButton, SdIcon, SdDropdownMenu, ActionParamsEditor],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ol class="actions" cdkDropList (cdkDropListDropped)="drop($event)" aria-label="Actions (run in order)">
      @for (a of actions(); track $index; let i = $index; let last = $last) {
        <li class="action" cdkDrag cdkDragLockAxis="y" [class.has-error]="hasIssues(i)">
          <div class="head">
            <button type="button" class="grip" cdkDragHandle [attr.aria-label]="'Drag to reorder action ' + (i + 1)">
              <sd-icon name="grip" [size]="14" />
            </button>
            <span class="num">{{ i + 1 }}</span>
            <div class="title">
              <strong>{{ specFor(a.type)?.label ?? a.type }}</strong>
              <span class="sd-muted">{{ specFor(a.type)?.description }}</span>
            </div>
            <span class="sd-spacer"></span>
            <button sdButton size="sm" variant="ghost" icon="arrow-up" iconOnly aria-label="Move up" [disabled]="i === 0" (click)="move(i, -1)"></button>
            <button sdButton size="sm" variant="ghost" icon="arrow-down" iconOnly aria-label="Move down" [disabled]="last" (click)="move(i, 1)"></button>
            <button sdButton size="sm" variant="ghost" icon="trash" iconOnly [attr.aria-label]="'Remove action ' + (i + 1)" (click)="remove(i)"></button>
          </div>
          @if (issuesFor(i)['type']; as typeError) {
            <p class="err" role="alert">{{ typeError }}</p>
          }
          @if (issuesFor(i)['params']; as paramsError) {
            <p class="err" role="alert">{{ paramsError }}</p>
          }
          @if (specFor(a.type); as spec) {
            <desk-action-params [action]="a" [spec]="spec" [issues]="issuesFor(i)" (changed)="update(i, $event)" />
          }
        </li>
      }
    </ol>
    @if (actions().length === 0) {
      <p class="empty">No actions yet.</p>
    }
    @if (issues()[pathPrefix()]; as listError) {
      <p class="err" role="alert">{{ listError }}</p>
    }
    <sd-dropdown-menu label="Add action" icon="plus" variant="subtle" size="sm" [items]="menu()" (select)="add($event)" />
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: 10px;
    }
    .actions {
      display: flex;
      flex-direction: column;
      gap: 10px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .action {
      display: flex;
      flex-direction: column;
      gap: 10px;
      padding: 12px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
    }
    .action.has-error {
      border-color: var(--sd-danger-border);
    }
    .action.cdk-drag-preview {
      box-shadow: var(--sd-shadow-lg);
    }
    .action.cdk-drag-placeholder {
      opacity: 0.4;
    }
    .actions.cdk-drop-list-dragging .action:not(.cdk-drag-placeholder) {
      transition: transform var(--sd-dur) var(--sd-ease);
    }
    .head {
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .grip {
      display: inline-flex;
      padding: 4px;
      border: 0;
      background: transparent;
      color: var(--sd-text-3);
      cursor: grab;
      border-radius: var(--sd-radius-xs);
    }
    .grip:focus-visible {
      box-shadow: var(--sd-focus-ring);
    }
    .num {
      display: inline-grid;
      place-items: center;
      width: 22px;
      height: 22px;
      border-radius: var(--sd-radius-pill);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
    }
    .title {
      display: flex;
      flex-direction: column;
      min-width: 0;
    }
    .title .sd-muted {
      font-size: var(--sd-text-xs);
    }
    .err {
      color: var(--sd-danger);
      font-size: var(--sd-text-sm);
    }
    .empty {
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
  `,
})
export class ActionListEditor {
  private readonly ctx = inject(AutomationContext);

  readonly actions = model<RuleActionDef[]>([]);
  /** All issues of the definition; this list reads those under `pathPrefix` ("actions", "transitions[2].actions"). */
  readonly issues = input<IssueMap>({});
  readonly pathPrefix = input('actions');

  protected readonly menu = computed<SdMenuItem[]>(() => {
    const specs = this.ctx.catalog()?.actions ?? [];
    const items: SdMenuItem[] = [];
    for (const category of [...new Set(specs.map((s) => s.category))]) {
      items.push({ id: `h:${category}`, label: category, heading: true });
      for (const s of specs.filter((x) => x.category === category)) items.push({ id: s.type, label: s.label });
    }
    return items;
  });

  protected specFor(type: string): ActionTypeDto | null {
    return this.ctx.catalog()?.actions.find((a) => a.type === type) ?? null;
  }

  protected issuesFor(i: number): IssueMap {
    return issuesUnder(this.issues(), `${this.pathPrefix()}[${i}]`);
  }

  protected hasIssues(i: number): boolean {
    return Object.keys(this.issuesFor(i)).length > 0;
  }

  protected add(item: SdMenuItem): void {
    if (item.heading) return;
    this.actions.update((list) => [...list, { type: item.id, params: {} }]);
  }

  protected update(i: number, action: RuleActionDef): void {
    this.actions.update((list) => list.map((a, j) => (j === i ? action : a)));
  }

  protected remove(i: number): void {
    this.actions.update((list) => list.filter((_, j) => j !== i));
  }

  protected move(i: number, delta: number): void {
    this.actions.update((list) => {
      const next = list.slice();
      moveItemInArray(next, i, i + delta);
      return next;
    });
  }

  protected drop(event: CdkDragDrop<unknown>): void {
    if (event.previousIndex === event.currentIndex) return;
    this.actions.update((list) => {
      const next = list.slice();
      moveItemInArray(next, event.previousIndex, event.currentIndex);
      return next;
    });
  }
}
