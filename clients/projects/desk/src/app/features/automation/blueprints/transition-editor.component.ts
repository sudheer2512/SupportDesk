import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BlueprintDefinition, BlueprintTransitionDef, FilterGroup, RuleActionDef, emptyFilterGroup } from '@sd/core';
import { SdButton, SdCombobox, SdFilterBuilder, SdFormField, SdInput, SdOption, SdToggle } from '@sd/ui';
import { ActionListEditor } from '../shared/action-list-editor.component';
import { AutomationContext } from '../shared/automation-context.service';
import { IssueMap, asFilterGroup } from '../shared/automation-ui';

type ApprovalMode = 'none' | 'user' | 'role' | 'supervisors';

/**
 * Side-panel editor of one blueprint transition: name/key, source and target states, allowed roles, mandatory
 * fields, validation condition group, approval (user, role or department supervisors), mandatory comment and
 * after-actions (the rule action editors).
 */
@Component({
  selector: 'desk-transition-editor',
  imports: [FormsModule, SdFormField, SdInput, SdCombobox, SdToggle, SdButton, SdFilterBuilder, ActionListEditor],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let t = transition();
    <div class="form">
      @if (issues()['']; as e) {
        <p class="err" role="alert">{{ e }}</p>
      }
      <sd-form-field label="Name" required [error]="issues()['name']">
        <input sdInput [ngModel]="t.name" (ngModelChange)="patch({ name: $event })" />
      </sd-form-field>
      <sd-form-field label="Key" hint="Stable id used by the API." [error]="issues()['key']">
        <input sdInput class="mono" [ngModel]="t.key" (change)="rename($any($event.target).value)" />
      </sd-form-field>
      <sd-form-field label="From" [error]="issues()['from']">
        <sd-combobox multiple [options]="fromOptions()" [value]="fromList()" (valueChange)="patch({ from: $any($event) ?? [] })" />
      </sd-form-field>
      <sd-form-field label="To" [error]="issues()['to']">
        <sd-combobox [clearable]="false" [options]="stateOptions()" [value]="t.to" (valueChange)="patch({ to: $any($event) ?? t.to })" />
      </sd-form-field>
      <sd-form-field label="Allowed roles" hint="Empty: anyone who can edit the ticket." [error]="issues()['allowedRoleIds']">
        <sd-combobox multiple [options]="ctx.roleOptions()" [value]="t.allowedRoleIds ?? []" (valueChange)="patch({ allowedRoleIds: $any($event) ?? [] })" placeholder="Anyone" />
      </sd-form-field>
      <sd-form-field label="Mandatory fields" hint="Asked for when the transition runs." [error]="issues()['requiredFields']">
        <sd-combobox multiple [options]="requirableOptions()" [value]="t.requiredFields ?? []" (valueChange)="patch({ requiredFields: $any($event) ?? [] })" placeholder="None" />
      </sd-form-field>
      <sd-toggle [value]="!!t.requireComment" (valueChange)="patch({ requireComment: $event })">Comment required</sd-toggle>

      <fieldset>
        <legend>Approval</legend>
        <select sdInput aria-label="Approval" [ngModel]="approvalMode()" (ngModelChange)="setApprovalMode($event)">
          <option value="none">No approval</option>
          <option value="user">A specific person</option>
          <option value="role">Anyone with a role</option>
          <option value="supervisors">Department supervisors</option>
        </select>
        @if (approvalMode() === 'user') {
          <sd-form-field label="Approver" [error]="issues()['approval.userId']">
            <sd-combobox [options]="ctx.agentOptions()" [value]="t.approval?.userId ?? null" (valueChange)="patch({ approval: { ...(t.approval ?? {}), userId: $any($event), roleId: null } })" />
          </sd-form-field>
        }
        @if (approvalMode() === 'role') {
          <sd-form-field label="Approver role" [error]="issues()['approval.roleId']">
            <sd-combobox [options]="ctx.roleOptions()" [value]="t.approval?.roleId ?? null" (valueChange)="patch({ approval: { ...(t.approval ?? {}), roleId: $any($event), userId: null } })" />
          </sd-form-field>
        }
        @if (approvalMode() !== 'none') {
          <sd-form-field label="Note for the approver" optional>
            <input sdInput [ngModel]="t.approval?.note ?? ''" (ngModelChange)="patch({ approval: { ...(t.approval ?? {}), note: $event || null } })" />
          </sd-form-field>
        }
      </fieldset>

      <fieldset>
        <legend>Validation</legend>
        <p class="sd-muted small">The ticket must match these conditions (values entered with the transition count).</p>
        <sd-filter-builder [fields]="validationFields()" [value]="validation()" (valueChange)="setValidation($event)" [maxDepth]="3" />
        <sd-form-field label="Message when not met" optional>
          <input sdInput [ngModel]="t.validationMessage ?? ''" (ngModelChange)="patch({ validationMessage: $event || null })" />
        </sd-form-field>
      </fieldset>

      <fieldset>
        <legend>After the transition</legend>
        <desk-action-list [actions]="t.actions ?? []" (actionsChange)="setActions($event)" [issues]="issues()" pathPrefix="actions" />
      </fieldset>

      <button sdButton variant="danger" size="sm" icon="trash" (click)="remove.emit()">Delete transition</button>
    </div>
  `,
  styles: `
    .form {
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
    fieldset {
      display: flex;
      flex-direction: column;
      gap: 8px;
      margin: 0;
      padding: 10px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
    }
    legend {
      padding: 0 4px;
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-semibold);
    }
    .mono {
      font-family: var(--sd-font-mono);
    }
    .small {
      margin: 0;
      font-size: var(--sd-text-xs);
    }
    .err {
      margin: 0;
      color: var(--sd-danger);
      font-size: var(--sd-text-sm);
    }
  `,
})
export class TransitionEditor {
  protected readonly ctx = inject(AutomationContext);

  readonly transition = input.required<BlueprintTransitionDef>();
  readonly definition = input.required<BlueprintDefinition>();
  /** Issues for this transition (relative: "name", "from", "actions[0].params.x"). */
  readonly issues = input<IssueMap>({});
  readonly changed = output<BlueprintTransitionDef>();
  readonly renamed = output<{ from: string; to: string }>();
  readonly remove = output<void>();

  protected readonly stateOptions = computed<SdOption[]>(() => this.definition().states.map((s) => ({ value: s.key, label: s.name || s.key })));
  protected readonly fromOptions = computed<SdOption[]>(() => [
    { value: '*', label: 'Any non-final state' },
    ...this.definition()
      .states.filter((s) => !s.isFinal)
      .map((s) => ({ value: s.key, label: s.name || s.key })),
  ]);
  protected readonly fromList = computed(() => {
    const f = this.transition().from;
    return Array.isArray(f) ? f : f ? [f] : [];
  });
  protected readonly requirableOptions = computed<SdOption[]>(() =>
    (this.ctx.catalog()?.blueprintRequiredFields ?? [])
      .filter((f) => !f.startsWith('cf:<'))
      .map((f) => ({ value: f, label: this.ctx.requirableLabel(f) }))
      .concat(this.ctx.customFieldOptions().map((o) => ({ value: `cf:${o.value}`, label: `${o.label} (custom)` }))),
  );
  protected readonly validationFields = computed(() => this.ctx.conditionFields('blueprint'));
  protected readonly validation = computed<FilterGroup>(() => asFilterGroup(this.transition().validation ?? emptyFilterGroup()));
  protected readonly approvalMode = computed<ApprovalMode>(() => {
    const a = this.transition().approval;
    if (!a) return 'none';
    if (a.userId) return 'user';
    if (a.roleId) return 'role';
    return 'supervisors';
  });

  protected patch(change: Partial<BlueprintTransitionDef>): void {
    this.changed.emit({ ...this.transition(), ...change });
  }

  protected rename(key: string): void {
    const next = key.trim().toLowerCase().replace(/[^a-z0-9_-]+/g, '_').slice(0, 50);
    if (!next || next === this.transition().key) return;
    this.renamed.emit({ from: this.transition().key, to: next });
  }

  protected setApprovalMode(mode: ApprovalMode): void {
    if (mode === 'none') this.patch({ approval: null });
    else if (mode === 'supervisors') this.patch({ approval: { note: this.transition().approval?.note ?? null } });
    else if (mode === 'user') this.patch({ approval: { userId: this.ctx.agentOptions()[0]?.value ?? null, note: this.transition().approval?.note ?? null } });
    else this.patch({ approval: { roleId: this.ctx.roleOptions()[0]?.value ?? null, note: this.transition().approval?.note ?? null } });
  }

  protected setValidation(group: FilterGroup): void {
    this.patch({ validation: group.conditions.length || group.groups.length ? group : null });
  }

  protected setActions(actions: RuleActionDef[]): void {
    this.patch({ actions });
  }
}
