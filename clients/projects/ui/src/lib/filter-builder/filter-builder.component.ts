import { ChangeDetectionStrategy, Component, computed, forwardRef, input, output } from '@angular/core';
import { FormsModule, NG_VALUE_ACCESSOR } from '@angular/forms';
import {
  FILTER_OP_LABELS,
  FilterCondition,
  FilterFieldDef,
  FilterGroup,
  FilterLogic,
  FilterOp,
  FilterScalar,
  FilterValue,
  LIST_OPS,
  UNARY_OPS,
  countConditions,
  defaultValueFor,
  emptyFilterGroup,
  opsForField,
} from '@sd/core';
import { model } from '@angular/core';
import { SdButton } from '../button/button.component';
import { SdChipsInput } from '../forms/chips-input.component';
import { SdCombobox, SdOption } from '../forms/combobox.component';
import { SdControlBase } from '../forms/control-base';
import { SdDateInput } from '../forms/date-input.component';
import { SdIcon } from '../icon/icon.component';

/** One condition row: field → operator → value editor (type/operator aware). */
@Component({
  selector: 'sd-filter-condition',
  imports: [FormsModule, SdCombobox, SdChipsInput, SdDateInput, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="cond" role="group" [attr.aria-label]="'Condition ' + (index() + 1)">
      <sd-combobox
        class="field"
        size="sm"
        ariaLabel="Field"
        [clearable]="false"
        [options]="fieldOptions()"
        [value]="condition().field"
        (valueChange)="setField($any($event))"
      />
      <select class="sd-input sd-input-sm op" aria-label="Operator" [ngModel]="condition().op" (ngModelChange)="setOp($event)">
        @for (op of ops(); track op) {
          <option [value]="op">{{ opLabel(op) }}</option>
        }
      </select>
      <div class="value">
        @switch (editor()) {
          @case ('none') {}
          @case ('multi-select') {
            <sd-combobox
              size="sm"
              multiple
              ariaLabel="Values"
              [options]="valueOptions()"
              [value]="listValue()"
              (valueChange)="setValue($any($event) ?? [])"
            />
          }
          @case ('chips') {
            <sd-chips-input ariaLabel="Values" [value]="listValue()" (valueChange)="setValue($event)" placeholder="Type and press Enter" />
          }
          @case ('select') {
            <sd-combobox
              size="sm"
              ariaLabel="Value"
              [options]="valueOptions()"
              [value]="scalarString()"
              (valueChange)="setValue($any($event))"
            />
          }
          @case ('boolean') {
            <select class="sd-input sd-input-sm" aria-label="Value" [ngModel]="scalarString()" (ngModelChange)="setValue($event === 'true')">
              <option value="true">Yes</option>
              <option value="false">No</option>
            </select>
          }
          @case ('number') {
            <input class="sd-input sd-input-sm" type="number" aria-label="Value" [ngModel]="condition().value" (ngModelChange)="setValue(toNumber($event))" />
          }
          @case ('minutes') {
            <span class="with-suffix">
              <input class="sd-input sd-input-sm" type="number" min="0" aria-label="Minutes" [ngModel]="condition().value" (ngModelChange)="setValue(toNumber($event))" />
              <span class="suffix">min</span>
            </span>
          }
          @case ('date') {
            <sd-date-input size="sm" [type]="dateType()" ariaLabel="Value" [value]="scalarString()" (valueChange)="setValue($event)" />
          }
          @case ('range') {
            <span class="range">
              @if (rangeKind() === 'number') {
                <input class="sd-input sd-input-sm" type="number" aria-label="From" [ngModel]="rangeAt(0)" (ngModelChange)="setRange(0, toNumber($event))" />
                <span class="and">and</span>
                <input class="sd-input sd-input-sm" type="number" aria-label="To" [ngModel]="rangeAt(1)" (ngModelChange)="setRange(1, toNumber($event))" />
              } @else {
                <sd-date-input size="sm" [type]="dateType()" ariaLabel="From" [value]="rangeString(0)" (valueChange)="setRange(0, $event)" />
                <span class="and">and</span>
                <sd-date-input size="sm" [type]="dateType()" ariaLabel="To" [value]="rangeString(1)" (valueChange)="setRange(1, $event)" />
              }
            </span>
          }
          @default {
            <input class="sd-input sd-input-sm" type="text" aria-label="Value" [ngModel]="scalarString() ?? ''" (ngModelChange)="setValue($event)" />
          }
        }
      </div>
      <button type="button" class="icon-btn" aria-label="Remove condition" (click)="remove.emit()">
        <sd-icon name="trash" [size]="14" />
      </button>
    </div>
  `,
  styles: `
    .cond {
      display: grid;
      grid-template-columns: minmax(150px, 1fr) minmax(130px, 0.8fr) minmax(160px, 1.4fr) auto;
      gap: 6px;
      align-items: center;
    }
    @media (max-width: 720px) {
      .cond {
        grid-template-columns: 1fr auto;
      }
      .op,
      .value {
        grid-column: 1;
      }
    }
    .value {
      min-width: 0;
    }
    .range,
    .with-suffix {
      display: flex;
      align-items: center;
      gap: 6px;
    }
    .and,
    .suffix {
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
    .icon-btn {
      display: inline-flex;
      padding: 6px;
      border: 0;
      border-radius: var(--sd-radius-sm);
      background: transparent;
      color: var(--sd-text-3);
      cursor: pointer;
    }
    .icon-btn:hover {
      color: var(--sd-danger);
      background: var(--sd-danger-bg);
    }
  `,
})
export class SdFilterCondition {
  readonly condition = input.required<FilterCondition>();
  readonly fields = input.required<FilterFieldDef[]>();
  readonly index = input(0);
  readonly changed = output<FilterCondition>();
  readonly remove = output<void>();

  protected readonly field = computed(() => this.fields().find((f) => f.key === this.condition().field) ?? null);
  protected readonly fieldOptions = computed<SdOption[]>(() =>
    this.fields().map((f) => ({ value: f.key, label: f.label, group: f.group ?? null })),
  );
  protected readonly ops = computed<readonly FilterOp[]>(() => {
    const f = this.field();
    return f ? opsForField(f) : [this.condition().op];
  });
  protected readonly valueOptions = computed<SdOption[]>(() =>
    (this.field()?.options ?? []).map((o) => ({ value: o.value, label: o.label })),
  );
  protected readonly dateType = computed<'date' | 'datetime'>(() => (this.field()?.type === 'datetime' ? 'datetime' : 'date'));
  protected readonly rangeKind = computed(() => (this.field()?.type === 'number' ? 'number' : 'date'));
  protected readonly editor = computed(() => {
    const op = this.condition().op;
    const f = this.field();
    if (UNARY_OPS.includes(op)) return 'none';
    if (op === 'within_last_minutes' || op === 'older_than_minutes') return 'minutes';
    if (op === 'between') return 'range';
    if (LIST_OPS.includes(op)) return f?.options?.length ? 'multi-select' : 'chips';
    if (op === 'matches') return 'text';
    switch (f?.type) {
      case 'boolean':
        return 'boolean';
      case 'number':
        return 'number';
      case 'date':
      case 'datetime':
        return op === 'before' || op === 'after' || op === 'eq' || op === 'neq' || op === 'changed_to' || op === 'changed_from'
          ? 'date'
          : 'text';
      case 'enum':
      case 'multi_enum':
      case 'user':
        return f.options?.length ? 'select' : 'text';
      default:
        return 'text';
    }
  });
  protected readonly listValue = computed(() => {
    const v = this.condition().value;
    return Array.isArray(v) ? v.filter((x): x is string | number => x !== null && typeof x !== 'boolean').map(String) : [];
  });
  protected readonly scalarString = computed(() => {
    const v = this.condition().value;
    return v === null || v === undefined || Array.isArray(v) ? null : String(v);
  });

  protected opLabel(op: FilterOp): string {
    return FILTER_OP_LABELS[op];
  }

  protected setField(key: string | null): void {
    if (!key) return;
    const f = this.fields().find((x) => x.key === key);
    if (!f) return;
    const op = opsForField(f)[0] ?? 'eq';
    this.changed.emit({ field: key, op, value: defaultValueFor(op, null) });
  }

  protected setOp(op: FilterOp): void {
    const c = this.condition();
    this.changed.emit({ ...c, op, value: defaultValueFor(op, c.value) });
  }

  protected setValue(value: FilterValue): void {
    this.changed.emit({ ...this.condition(), value });
  }

  protected rangeAt(i: 0 | 1): FilterScalar {
    const v = this.condition().value;
    return Array.isArray(v) ? (v[i] ?? null) : null;
  }

  protected rangeString(i: 0 | 1): string | null {
    const v = this.rangeAt(i);
    return v === null ? null : String(v);
  }

  protected setRange(i: 0 | 1, value: FilterScalar): void {
    const v = this.condition().value;
    const pair: FilterScalar[] = Array.isArray(v) && v.length === 2 ? [...v] : [null, null];
    pair[i] = value;
    this.setValue(pair);
  }

  protected toNumber(raw: unknown): number | null {
    if (raw === null || raw === undefined || raw === '') return null;
    const n = Number(raw);
    return Number.isFinite(n) ? n : null;
  }
}

/** A group: AND/OR toggle, its conditions, nested groups (recursive) and add/remove controls. */
@Component({
  selector: 'sd-filter-group',
  imports: [SdFilterCondition, SdButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="group" role="group" [attr.aria-label]="'Conditions joined by ' + group().logic.toUpperCase()" [class.is-nested]="depth() > 0">
      <div class="bar">
        <div class="logic" role="radiogroup" aria-label="Match">
          <button type="button" role="radio" [attr.aria-checked]="group().logic === 'and'" (click)="setLogic('and')">All (AND)</button>
          <button type="button" role="radio" [attr.aria-checked]="group().logic === 'or'" (click)="setLogic('or')">Any (OR)</button>
        </div>
        <span class="sd-spacer"></span>
        @if (removable()) {
          <button sdButton type="button" size="sm" variant="ghost" icon="trash" (click)="remove.emit()">Remove group</button>
        }
      </div>
      <div class="items">
        @for (cond of group().conditions; track $index; let i = $index) {
          <sd-filter-condition [condition]="cond" [fields]="fields()" [index]="i" (changed)="setCondition(i, $event)" (remove)="removeCondition(i)" />
        }
        @for (child of group().groups; track $index; let i = $index) {
          <sd-filter-group
            [group]="child"
            [fields]="fields()"
            [depth]="depth() + 1"
            [maxDepth]="maxDepth()"
            [removable]="true"
            (changed)="setGroup(i, $event)"
            (remove)="removeGroup(i)"
          />
        }
        @if (group().conditions.length === 0 && group().groups.length === 0) {
          <p class="empty">No conditions — matches everything.</p>
        }
      </div>
      <div class="add">
        <button sdButton type="button" size="sm" variant="subtle" icon="plus" [disabled]="fields().length === 0" (click)="addCondition()">
          Condition
        </button>
        @if (depth() < maxDepth() - 1) {
          <button sdButton type="button" size="sm" variant="ghost" icon="layers" [disabled]="fields().length === 0" (click)="addGroup()">
            Group
          </button>
        }
      </div>
    </div>
  `,
  styles: `
    .group {
      display: flex;
      flex-direction: column;
      gap: 10px;
      padding: 12px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
    }
    .group.is-nested {
      background: var(--sd-surface-2);
      border-left: 3px solid var(--sd-primary-border);
    }
    .bar {
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .logic {
      display: inline-flex;
      padding: 2px;
      border-radius: var(--sd-radius-sm);
      background: var(--sd-surface-3);
    }
    .logic button {
      height: 24px;
      padding: 0 10px;
      border: 0;
      border-radius: 4px;
      background: transparent;
      color: var(--sd-text-2);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      cursor: pointer;
    }
    .logic button[aria-checked='true'] {
      background: var(--sd-surface);
      color: var(--sd-primary);
      box-shadow: var(--sd-shadow-xs);
    }
    .items {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .empty {
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
    .add {
      display: flex;
      gap: 6px;
    }
  `,
})
export class SdFilterGroup {
  readonly group = input.required<FilterGroup>();
  readonly fields = input.required<FilterFieldDef[]>();
  readonly depth = input(0);
  readonly maxDepth = input(3);
  readonly removable = input(false);
  readonly changed = output<FilterGroup>();
  readonly remove = output<void>();

  protected setLogic(logic: FilterLogic): void {
    this.changed.emit({ ...this.group(), logic });
  }

  protected addCondition(): void {
    const f = this.fields()[0];
    if (!f) return;
    const op = opsForField(f)[0] ?? 'eq';
    const cond: FilterCondition = { field: f.key, op, value: defaultValueFor(op, null) };
    this.changed.emit({ ...this.group(), conditions: [...this.group().conditions, cond] });
  }

  protected addGroup(): void {
    const logic: FilterLogic = this.group().logic === 'and' ? 'or' : 'and';
    this.changed.emit({ ...this.group(), groups: [...this.group().groups, emptyFilterGroup(logic)] });
  }

  protected setCondition(index: number, cond: FilterCondition): void {
    const conditions = this.group().conditions.slice();
    conditions[index] = cond;
    this.changed.emit({ ...this.group(), conditions });
  }

  protected removeCondition(index: number): void {
    this.changed.emit({ ...this.group(), conditions: this.group().conditions.filter((_, i) => i !== index) });
  }

  protected setGroup(index: number, child: FilterGroup): void {
    const groups = this.group().groups.slice();
    groups[index] = child;
    this.changed.emit({ ...this.group(), groups });
  }

  protected removeGroup(index: number): void {
    this.changed.emit({ ...this.group(), groups: this.group().groups.filter((_, i) => i !== index) });
  }
}

/**
 * Nested AND/OR condition-tree editor producing exactly
 * `{"logic":"and"|"or","conditions":[{"field","op","value"}],"groups":[…]}`.
 * ```html
 * <sd-filter-builder [fields]="fieldDefs" [(value)]="filter" [maxDepth]="3" />
 * ```
 * Field definitions declare a type (text/number/date/datetime/boolean/enum/multi_enum/user/tags) which
 * selects the allowed operators (override per field with `ops`, add change operators with `trackChanges`)
 * and the value editor. Works with ngModel / formControl too.
 */
@Component({
  selector: 'sd-filter-builder',
  imports: [SdFilterGroup],
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => SdFilterBuilder), multi: true }],
  template: `
    <sd-filter-group [group]="value()" [fields]="fields()" [depth]="0" [maxDepth]="maxDepth()" (changed)="commit($event)" />
    <p class="summary sd-visually-hidden" aria-live="polite">{{ conditionCount() }} conditions</p>
  `,
  styles: `
    :host {
      display: block;
    }
  `,
})
export class SdFilterBuilder extends SdControlBase<FilterGroup> {
  readonly value = model<FilterGroup>(emptyFilterGroup());
  readonly fields = input.required<FilterFieldDef[]>();
  readonly maxDepth = input(3);
  protected readonly conditionCount = computed(() => countConditions(this.value()));

  protected emptyValue(): FilterGroup {
    return emptyFilterGroup();
  }

  protected override commit(value: FilterGroup): void {
    super.commit(value);
    this.touched();
  }
}
