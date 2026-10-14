import {
  CHANNELS,
  ConditionFieldDto,
  ENTITLEMENT_STATUSES,
  FilterFieldDef,
  FilterOp,
  OptionDto,
  PRIORITIES,
  SENTIMENTS,
  SEVERITIES,
  SLA_STATES,
  STATUS_CATEGORIES,
  TicketFormMetaDto,
} from '@sd/core';

/**
 * Fields a saved ticket view can filter on: the server's condition catalog (GET /meta/condition-fields)
 * intersected with what the SQL view filter translates (TicketFilterTranslator), with operators limited to
 * the ones that translate for each type. Look-up fields get their options from GET /meta/ticket-form.
 */
const VIEW_FIELDS = new Set([
  'subject',
  'priority',
  'severity',
  'statusId',
  'statusCategory',
  'channel',
  'departmentId',
  'teamId',
  'assigneeId',
  'productId',
  'moduleId',
  'versionId',
  'categoryId',
  'subcategoryId',
  'issueTypeId',
  'tags',
  'language',
  'sentiment',
  'slaState',
  'isEscalated',
  'entitlementStatus',
  'brandId',
  'createdAt',
  'dueAt',
  'lastActivityAt',
  'reopenCount',
  'csatScore',
]);

const ENUM_OPS: FilterOp[] = ['eq', 'neq', 'in', 'not_in'];
const NULLABLE_ENUM_OPS: FilterOp[] = ['eq', 'neq', 'in', 'not_in', 'is_empty', 'is_not_empty'];
const TEXT_OPS: FilterOp[] = ['contains', 'not_contains', 'starts_with', 'eq', 'neq', 'is_empty', 'is_not_empty'];
const NUMBER_OPS: FilterOp[] = ['eq', 'neq', 'gt', 'gte', 'lt', 'lte', 'between'];
const DATE_OPS: FilterOp[] = ['within_last_minutes', 'older_than_minutes', 'before', 'after', 'between', 'is_empty', 'is_not_empty'];

const NULLABLE = new Set(['severity', 'teamId', 'assigneeId', 'productId', 'moduleId', 'versionId', 'categoryId', 'subcategoryId', 'issueTypeId', 'brandId', 'dueAt', 'csatScore']);

const opts = (values: readonly string[]): OptionDto[] => values.map((v) => ({ value: v, label: v }));

export function buildViewFields(catalog: readonly ConditionFieldDto[], meta: TicketFormMetaDto | null, myName = 'Me'): FilterFieldDef[] {
  const fields: FilterFieldDef[] = [];
  for (const f of catalog) {
    if (!VIEW_FIELDS.has(f.field)) continue;
    const def = toDef(f, meta, myName);
    if (def) fields.push(def);
  }
  return fields;
}

function toDef(f: ConditionFieldDto, meta: TicketFormMetaDto | null, myName: string): FilterFieldDef | null {
  const base = { key: f.field, label: f.label };
  const enumOps = NULLABLE.has(f.field) ? NULLABLE_ENUM_OPS : ENUM_OPS;
  const enumOf = (options: OptionDto[], group = 'Ticket'): FilterFieldDef => ({ ...base, type: 'enum', options, ops: enumOps, group });
  switch (f.field) {
    case 'priority':
      return enumOf(opts(PRIORITIES));
    case 'severity':
      return enumOf(opts(SEVERITIES));
    case 'statusCategory':
      return enumOf(opts(STATUS_CATEGORIES));
    case 'channel':
      return enumOf(opts(CHANNELS));
    case 'sentiment':
      return enumOf(opts(SENTIMENTS));
    case 'slaState':
      return enumOf(opts(SLA_STATES), 'SLA');
    case 'entitlementStatus':
      return enumOf(opts(ENTITLEMENT_STATUSES), 'SLA');
    case 'statusId':
      return enumOf((meta?.statuses ?? []).map((s) => ({ value: s.id, label: s.name })));
    case 'departmentId':
      return enumOf((meta?.departments ?? []).map((d) => ({ value: d.id, label: d.name })), 'Routing');
    case 'teamId':
      return enumOf((meta?.teams ?? []).map((t) => ({ value: t.id, label: t.name })), 'Routing');
    case 'assigneeId':
      return {
        ...base,
        type: 'user',
        ops: NULLABLE_ENUM_OPS,
        group: 'Routing',
        options: [{ value: 'me', label: `${myName} (whoever views it)` }, ...(meta?.agents ?? []).map((a) => ({ value: a.userId, label: a.name }))],
      };
    case 'brandId':
      return enumOf((meta?.brands ?? []).map((b) => ({ value: b.id, label: b.name })), 'Routing');
    case 'productId':
      return enumOf((meta?.products ?? []).map((p) => ({ value: p.id, label: p.name })), 'Product');
    case 'moduleId':
      return enumOf(
        (meta?.products ?? []).flatMap((p) => p.modules.map((m) => ({ value: m.id, label: `${p.name} › ${m.name}` }))),
        'Product',
      );
    case 'versionId':
      return enumOf(
        (meta?.products ?? []).flatMap((p) => p.versions.map((v) => ({ value: v.id, label: `${p.name} ${v.name}` }))),
        'Product',
      );
    case 'categoryId':
      return enumOf((meta?.categories ?? []).filter((c) => !c.parentId).map((c) => ({ value: c.id, label: c.name })), 'Classification');
    case 'subcategoryId':
      return enumOf((meta?.categories ?? []).filter((c) => !!c.parentId).map((c) => ({ value: c.id, label: c.name })), 'Classification');
    case 'issueTypeId':
      return enumOf((meta?.issueTypes ?? []).map((i) => ({ value: i.id, label: i.name })), 'Classification');
    case 'tags':
      return { ...base, type: 'tags', ops: ['contains_any', 'not_contains'], options: opts(meta?.tags ?? []), group: 'Classification' };
    case 'isEscalated':
      return { ...base, type: 'boolean', group: 'SLA' };
    default:
      break;
  }
  switch (f.type) {
    case 'text':
      return { ...base, type: 'text', ops: TEXT_OPS, group: 'Ticket' };
    case 'number':
      return { ...base, type: 'number', ops: NUMBER_OPS, group: 'Ticket' };
    case 'datetime':
      return { ...base, type: 'datetime', ops: DATE_OPS, group: 'Dates' };
    case 'bool':
      return { ...base, type: 'boolean', group: 'Ticket' };
    default:
      return null;
  }
}

export const VIEW_SORTS = [
  { value: 'lastActivity', label: 'Last activity (newest first)', dir: 'desc' },
  { value: 'created', label: 'Created (newest first)', dir: 'desc' },
  { value: 'due', label: 'Due date (soonest first)', dir: 'asc' },
  { value: 'priority', label: 'Priority (highest first)', dir: 'desc' },
  { value: 'number', label: 'Ticket number (highest first)', dir: 'desc' },
] as const;
