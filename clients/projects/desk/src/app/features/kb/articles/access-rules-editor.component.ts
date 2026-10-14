import { ChangeDetectionStrategy, Component, booleanAttribute, computed, inject, input, model } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { KnowledgeApi, uniqueId } from '@sd/core';
import { SdButton, SdCombobox, SdIcon, SdInput, SdOption, SdOptionSource } from '@sd/ui';
import { map, tap } from 'rxjs';
import { comboOne } from '../kb-common';
import { KbStore } from '../kb.store';
import { RuleDraft, emptyRule, ruleIsEmpty } from './article-draft';

/**
 * Access-rule rows for Restricted articles: each rule combines customer, customer group, tier and product
 * (all set conditions must match; any matching rule grants access).
 * `<desk-kb-access-rules [(rules)]="rules" [disabled]="readonly" [error]="errors.accessRules" />`
 */
@Component({
  selector: 'desk-kb-access-rules',
  imports: [FormsModule, SdButton, SdIcon, SdCombobox, SdInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <fieldset class="rules" [attr.aria-describedby]="error() ? errorId : helpId">
      <legend>Access rules</legend>
      <p class="help" [id]="helpId">A customer can read this article when they match <strong>any</strong> rule. Within a rule, every chosen condition must match.</p>
      @for (rule of rules(); track rule.key; let i = $index) {
        <div class="rule" [class.is-empty]="ruleIsEmpty(rule)" role="group" [attr.aria-label]="'Rule ' + (i + 1)">
          <div class="rule-head">
            <span class="rule-title">Rule {{ i + 1 }}</span>
            @if (!disabled()) {
              <button sdButton variant="ghost" size="sm" icon="trash" iconOnly type="button" [attr.aria-label]="'Remove rule ' + (i + 1)" (click)="remove(i)"></button>
            }
          </div>
          <div class="grid">
            <label class="cell">
              <span>Customer</span>
              <sd-combobox
                size="sm"
                [ariaLabel]="'Rule ' + (i + 1) + ' customer'"
                placeholder="Any customer"
                [options]="seeds().get(rule.key) ?? noOptions"
                [search]="searchCustomers"
                [disabled]="disabled()"
                [value]="rule.customerId"
                (valueChange)="setCustomer(i, comboOne($event))"
              />
            </label>
            <label class="cell">
              <span>Customer group</span>
              <sd-combobox
                size="sm"
                [ariaLabel]="'Rule ' + (i + 1) + ' customer group'"
                placeholder="Any group"
                [options]="store.groupOptions()"
                [disabled]="disabled()"
                [value]="rule.customerGroupId"
                (valueChange)="patch(i, { customerGroupId: comboOne($event) })"
              />
            </label>
            <label class="cell">
              <span>Tier</span>
              <select
                sdInput
                size="sm"
                [attr.aria-label]="'Rule ' + (i + 1) + ' customer tier'"
                [disabled]="disabled()"
                [ngModel]="rule.customerTier ?? ''"
                (ngModelChange)="patch(i, { customerTier: $event || null })"
              >
                <option value="">Any tier</option>
                @for (t of store.options().customerTiers; track t) {
                  <option [value]="t">{{ t }}</option>
                }
              </select>
            </label>
            <label class="cell">
              <span>Product</span>
              <sd-combobox
                size="sm"
                [ariaLabel]="'Rule ' + (i + 1) + ' product'"
                placeholder="Any product"
                [options]="store.productOptions()"
                [disabled]="disabled()"
                [value]="rule.productId"
                (valueChange)="patch(i, { productId: comboOne($event) })"
              />
            </label>
          </div>
          @if (ruleIsEmpty(rule)) {
            <p class="warn"><sd-icon name="triangle-alert" [size]="13" /> Choose at least one condition — empty rules are ignored.</p>
          }
        </div>
      } @empty {
        <p class="none">No rules yet — nobody outside your team can read a Restricted article until you add one.</p>
      }
      @if (!disabled()) {
        <button sdButton size="sm" variant="subtle" icon="plus" type="button" (click)="add()">Add rule</button>
      }
      @if (error()) {
        <p class="error" [id]="errorId" role="alert"><sd-icon name="circle-alert" [size]="13" /> {{ error() }}</p>
      }
    </fieldset>
  `,
  styles: `
    .rules {
      display: grid;
      gap: var(--sd-space-2);
      min-width: 0;
      margin: 0;
      padding: var(--sd-space-3);
      border: 1px solid var(--sd-warning-border);
      border-radius: var(--sd-radius-md);
      background: color-mix(in oklab, var(--sd-warning-bg) 55%, var(--sd-surface));
    }
    legend {
      padding: 0 4px;
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-semibold);
    }
    .help,
    .none {
      color: var(--sd-text-2);
      font-size: var(--sd-text-xs);
      line-height: 1.45;
    }
    .rule {
      display: grid;
      gap: 6px;
      padding: var(--sd-space-2);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-sm);
      background: var(--sd-surface);
    }
    .rule.is-empty {
      border-style: dashed;
    }
    .rule-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
    }
    .rule-title {
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      color: var(--sd-text-2);
    }
    .grid {
      display: grid;
      grid-template-columns: 1fr;
      gap: 6px;
    }
    .cell {
      display: grid;
      gap: 2px;
      min-width: 0;
    }
    .cell > span {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .warn,
    .error {
      display: flex;
      align-items: center;
      gap: 4px;
      font-size: var(--sd-text-xs);
    }
    .warn {
      color: var(--sd-warning);
    }
    .error {
      color: var(--sd-danger);
    }
  `,
})
export class AccessRulesEditor {
  readonly rules = model<RuleDraft[]>([]);
  readonly disabled = input(false, { transform: booleanAttribute });
  readonly error = input<string | null | undefined>(null);

  protected readonly store = inject(KbStore);
  private readonly api = inject(KnowledgeApi);
  protected readonly comboOne = comboOne;
  protected readonly ruleIsEmpty = ruleIsEmpty;
  protected readonly helpId = uniqueId('kb-rules-help');
  protected readonly errorId = uniqueId('kb-rules-error');
  protected readonly noOptions: SdOption[] = [];
  /** Names of customers seen in search results (the picker only reports ids). */
  private readonly customerNames = new Map<string, string>();

  protected readonly searchCustomers: SdOptionSource = (q: string) =>
    this.api.customers(q).pipe(
      tap((list) => list.forEach((c) => this.customerNames.set(c.id, c.name))),
      map((list) => list.map((c) => ({ value: c.id, label: c.name, icon: 'building' }))),
    );

  /** Known option for each rule's selected customer (stable arrays until the rules change). */
  protected readonly seeds = computed(() => {
    const result = new Map<string, SdOption[]>();
    for (const r of this.rules()) {
      if (!r.customerId) continue;
      const label = r.customerName ?? this.customerNames.get(r.customerId) ?? 'Selected customer';
      result.set(r.key, [{ value: r.customerId, label, icon: 'building' }]);
    }
    return result;
  });

  protected add(): void {
    this.rules.update((list) => [...list, emptyRule()]);
  }

  protected remove(index: number): void {
    this.rules.update((list) => list.filter((_, i) => i !== index));
  }

  protected patch(index: number, changes: Partial<RuleDraft>): void {
    this.rules.update((list) => list.map((r, i) => (i === index ? { ...r, ...changes } : r)));
  }

  protected setCustomer(index: number, id: string | null): void {
    this.patch(index, { customerId: id, customerName: id ? (this.customerNames.get(id) ?? null) : null });
  }
}
