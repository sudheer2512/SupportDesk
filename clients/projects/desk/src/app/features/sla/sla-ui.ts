import { ConditionFieldDto, ENTITLEMENT_STATUSES, FilterFieldDef, FilterFieldType, FilterOp, OptionDto, PRIORITIES, SENTIMENTS, SLA_STATES, STATUS_CATEGORIES, TicketFormMetaDto } from '@sd/core';
import { ModuleTab } from '../customers/shared/module-tabs.component';

/** Secondary navigation of the SLA module. */
export const SLA_TABS: readonly ModuleTab[] = [
  { link: '/sla/policies', label: 'Policies', icon: 'timer' },
  { link: '/sla/calendars', label: 'Calendars & holidays', icon: 'calendar' },
  { link: '/sla/breaches', label: 'Live breaches', icon: 'octagon-alert' },
];

export const MILESTONE_LABELS: Record<string, string> = {
  FirstResponse: 'First response',
  NextResponse: 'Next response',
  Resolution: 'Resolution',
};

export const ACTION_LABELS: Record<string, string> = {
  Notify: 'Notify',
  Reassign: 'Reassign',
  RaisePriority: 'Raise priority',
  NotifyAndReassign: 'Notify and reassign',
};

const PRIORITY_OPS: FilterOp[] = ['eq', 'neq', 'in', 'not_in', 'gt', 'gte', 'lt', 'lte'];

function humanize(value: string): string {
  return value.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/_/g, ' ');
}

/**
 * Filter-builder field definitions for SLA policy conditions: the shared condition catalog (GET /meta/condition-fields)
 * with options from the ticket metadata (statuses, departments, teams, agents, categories, products, brands, tags).
 */
export function slaConditionFields(fields: readonly ConditionFieldDto[], meta: TicketFormMetaDto | null): FilterFieldDef[] {
  if (!meta) return [];
  const opts = (values: readonly string[]): OptionDto[] => values.map((v) => ({ value: v, label: humanize(v) }));
  const byId = new Map(meta.categories.map((c) => [c.id, c]));
  const defs: FilterFieldDef[] = [];
  for (const f of fields) {
    let type: FilterFieldType = 'text';
    let options: OptionDto[] | undefined;
    let ops: FilterOp[] | undefined;
    switch (f.type) {
      case 'number':
        type = 'number';
        break;
      case 'datetime':
        type = 'datetime';
        break;
      case 'bool':
        type = 'boolean';
        break;
      case 'priority':
        type = 'enum';
        options = opts(PRIORITIES);
        ops = PRIORITY_OPS;
        break;
      case 'status':
        type = 'enum';
        options = meta.statuses.map((s) => ({ value: s.id, label: s.name }));
        break;
      case 'department':
        type = 'enum';
        options = meta.departments.map((d) => ({ value: d.id, label: d.name }));
        break;
      case 'team':
        type = 'enum';
        options = meta.teams.map((t) => ({ value: t.id, label: t.name }));
        break;
      case 'user':
        type = 'user';
        options = meta.agents.map((a) => ({ value: a.userId, label: a.name }));
        break;
      case 'category':
        type = 'enum';
        options = meta.categories.map((c) => ({ value: c.id, label: c.parentId ? `${byId.get(c.parentId)?.name ?? ''} › ${c.name}` : c.name }));
        break;
      case 'issueType':
        type = 'enum';
        options = meta.issueTypes.map((i) => ({ value: i.id, label: i.name }));
        break;
      case 'product':
        type = 'enum';
        options = meta.products.map((p) => ({ value: p.id, label: p.name }));
        break;
      case 'module':
        type = 'enum';
        options = meta.products.flatMap((p) => p.modules.map((m) => ({ value: m.id, label: `${p.name} › ${m.name}` })));
        break;
      case 'version':
        type = 'enum';
        options = meta.products.flatMap((p) => p.versions.map((v) => ({ value: v.id, label: `${p.name} ${v.name}` })));
        break;
      case 'brand':
        type = 'enum';
        options = meta.brands.map((b) => ({ value: b.id, label: b.name }));
        break;
      case 'tags':
        type = 'tags';
        options = meta.tags.map((t) => ({ value: t, label: t }));
        break;
      case 'enum':
        type = 'enum';
        options = enumOptions(f.field, meta, opts);
        break;
      default:
        type = 'text';
    }
    defs.push({ key: f.field, label: f.label, type, options, ops, group: groupOf(f.field, f.type) });
  }
  for (const cf of meta.customFields.filter((c) => c.entity === 'Ticket' && c.isActive)) {
    defs.push({ key: `cf:${cf.key}`, label: cf.label, type: 'text', group: 'Custom fields' });
  }
  return defs;
}

function enumOptions(field: string, meta: TicketFormMetaDto, opts: (v: readonly string[]) => OptionDto[]): OptionDto[] | undefined {
  switch (field) {
    case 'severity':
      return opts(meta.severities);
    case 'statusCategory':
      return opts(STATUS_CATEGORIES);
    case 'channel':
      return opts(meta.channels);
    case 'sentiment':
      return opts(SENTIMENTS);
    case 'slaState':
      return opts(SLA_STATES);
    case 'entitlementStatus':
      return opts(ENTITLEMENT_STATUSES);
    case 'contract.type':
      return opts(['Warranty', 'Amc', 'AnnualSupport', 'PrepaidHours', 'TimeAndMaterial', 'Subscription']);
    default:
      return undefined;
  }
}

function groupOf(field: string, type: string): string {
  if (field.startsWith('customer.') || field.startsWith('contact.') || field.startsWith('contract.') || ['customerId', 'contactId', 'entitlementStatus'].includes(field))
    return 'Customer';
  if (type === 'datetime' || field.startsWith('hours') || field === 'slaState') return 'Dates & SLA';
  if (['productId', 'moduleId', 'versionId', 'installationId'].includes(field)) return 'Product';
  return 'Ticket';
}

/** Minutes ⇄ value + unit for target inputs. */
export type DurationUnit = 'minutes' | 'hours' | 'days';

export function splitMinutes(minutes: number | null): { value: number | null; unit: DurationUnit } {
  if (minutes === null) return { value: null, unit: 'hours' };
  if (minutes % 1440 === 0 && minutes >= 1440) return { value: minutes / 1440, unit: 'days' };
  if (minutes % 60 === 0 && minutes >= 60) return { value: minutes / 60, unit: 'hours' };
  return { value: minutes, unit: 'minutes' };
}

export function joinMinutes(value: number | null, unit: DurationUnit): number | null {
  if (value === null || (value as unknown) === '' || Number.isNaN(Number(value))) return null;
  const n = Number(value);
  return Math.round(unit === 'days' ? n * 1440 : unit === 'hours' ? n * 60 : n);
}
