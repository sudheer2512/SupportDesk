import { OptionDto } from './common';

/**
 * Condition tree shared by saved views, automation rules, SLA targeting and analytics filters.
 * Wire shape (exactly): {"logic":"and"|"or","conditions":[{"field","op","value"}],"groups":[…]}.
 * Unary operators (is_empty, is_not_empty, changed) carry `value: null`.
 */

export type FilterLogic = 'and' | 'or';

export const FILTER_OPS = [
  'eq',
  'neq',
  'in',
  'not_in',
  'contains',
  'not_contains',
  'starts_with',
  'ends_with',
  'is_empty',
  'is_not_empty',
  'gt',
  'gte',
  'lt',
  'lte',
  'between',
  'before',
  'after',
  'within_last_minutes',
  'older_than_minutes',
  'changed',
  'changed_to',
  'changed_from',
  'matches',
  'contains_any',
] as const;
export type FilterOp = (typeof FILTER_OPS)[number];

export type FilterScalar = string | number | boolean | null;
export type FilterValue = FilterScalar | FilterScalar[];

export interface FilterCondition {
  field: string;
  op: FilterOp;
  value: FilterValue;
}

export interface FilterGroup {
  logic: FilterLogic;
  conditions: FilterCondition[];
  groups: FilterGroup[];
}

/** Field data types understood by the filter builder; each maps to a default operator set. */
export type FilterFieldType =
  | 'text'
  | 'number'
  | 'date'
  | 'datetime'
  | 'boolean'
  | 'enum'
  | 'multi_enum'
  | 'user'
  | 'tags';

/** A field the user can filter on. `ops` overrides the type's default operator list. */
export interface FilterFieldDef {
  key: string;
  label: string;
  type: FilterFieldType;
  /** Choices for enum / multi_enum / user / tags fields. */
  options?: OptionDto[];
  ops?: FilterOp[];
  /** Optional grouping label in the field picker (e.g. "Ticket", "Customer", "Custom fields"). */
  group?: string;
  /** Allow change-tracking ops (changed / changed_to / changed_from) — for automation triggers. */
  trackChanges?: boolean;
}

export const DEFAULT_OPS_BY_TYPE: Readonly<Record<FilterFieldType, readonly FilterOp[]>> = {
  text: ['eq', 'neq', 'contains', 'not_contains', 'starts_with', 'ends_with', 'matches', 'is_empty', 'is_not_empty'],
  number: ['eq', 'neq', 'gt', 'gte', 'lt', 'lte', 'between', 'is_empty', 'is_not_empty'],
  date: ['eq', 'before', 'after', 'between', 'within_last_minutes', 'older_than_minutes', 'is_empty', 'is_not_empty'],
  datetime: ['before', 'after', 'between', 'within_last_minutes', 'older_than_minutes', 'is_empty', 'is_not_empty'],
  boolean: ['eq', 'neq'],
  enum: ['eq', 'neq', 'in', 'not_in', 'is_empty', 'is_not_empty'],
  multi_enum: ['contains_any', 'in', 'not_in', 'is_empty', 'is_not_empty'],
  user: ['eq', 'neq', 'in', 'not_in', 'is_empty', 'is_not_empty'],
  tags: ['contains_any', 'contains', 'not_contains', 'is_empty', 'is_not_empty'],
};

export const CHANGE_OPS: readonly FilterOp[] = ['changed', 'changed_to', 'changed_from'];

/** Human labels for operators (used by the builder UI and summaries). */
export const FILTER_OP_LABELS: Readonly<Record<FilterOp, string>> = {
  eq: 'is',
  neq: 'is not',
  in: 'is any of',
  not_in: 'is none of',
  contains: 'contains',
  not_contains: 'does not contain',
  starts_with: 'starts with',
  ends_with: 'ends with',
  is_empty: 'is empty',
  is_not_empty: 'is not empty',
  gt: '>',
  gte: '≥',
  lt: '<',
  lte: '≤',
  between: 'is between',
  before: 'is before',
  after: 'is after',
  within_last_minutes: 'within the last (min)',
  older_than_minutes: 'older than (min)',
  changed: 'changed',
  changed_to: 'changed to',
  changed_from: 'changed from',
  matches: 'matches regex',
  contains_any: 'contains any of',
};

/** Operators that take no value. */
export const UNARY_OPS: readonly FilterOp[] = ['is_empty', 'is_not_empty', 'changed'];
/** Operators whose value is a list. */
export const LIST_OPS: readonly FilterOp[] = ['in', 'not_in', 'contains_any'];

export function emptyFilterGroup(logic: FilterLogic = 'and'): FilterGroup {
  return { logic, conditions: [], groups: [] };
}

/** Number of leaf conditions in a tree. */
export function countConditions(group: FilterGroup): number {
  return group.conditions.length + group.groups.reduce((n, g) => n + countConditions(g), 0);
}

/** Operators allowed for a field (explicit list, else the type default, plus change ops when tracked). */
export function opsForField(field: FilterFieldDef): readonly FilterOp[] {
  const base = field.ops ?? DEFAULT_OPS_BY_TYPE[field.type];
  return field.trackChanges ? [...base, ...CHANGE_OPS.filter((o) => !base.includes(o))] : base;
}

/** The value a condition should start with when its operator changes. */
export function defaultValueFor(op: FilterOp, previous: FilterValue): FilterValue {
  if (UNARY_OPS.includes(op)) return null;
  if (LIST_OPS.includes(op)) return Array.isArray(previous) ? previous : previous == null || previous === '' ? [] : [previous];
  if (op === 'between') return Array.isArray(previous) && previous.length === 2 ? previous : [null, null];
  if (Array.isArray(previous)) return previous[0] ?? null;
  return previous;
}
