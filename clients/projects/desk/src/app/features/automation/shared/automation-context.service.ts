import { Injectable, computed, inject, signal } from '@angular/core';
import {
  AutomationApi,
  AutomationCatalogDto,
  AutomationLookupsDto,
  BlueprintSummaryDto,
  BlueprintsApi,
  ENTITLEMENT_STATUSES,
  FilterFieldDef,
  FilterFieldType,
  FilterOp,
  MetaApi,
  OptionDto,
  PRIORITIES,
  SENTIMENTS,
  SLA_STATES,
  STATUS_CATEGORIES,
  TicketFormMetaDto,
} from '@sd/core';
import { SdOption } from '@sd/ui';
import { catchError, forkJoin, of } from 'rxjs';

/** Kinds of triggers that support "changed / changed to / changed from" operators. */
const EVENT_TRIGGERS = new Set([
  'ticket.created',
  'ticket.updated',
  'ticket.status_changed',
  'ticket.assigned',
  'ticket.reply_added',
  'ticket.customer_replied',
  'ticket.note_added',
  'sla.warning',
  'sla.breached',
  'customer.activity',
]);

const PRIORITY_OPS: FilterOp[] = ['eq', 'neq', 'in', 'not_in', 'gt', 'gte', 'lt', 'lte'];

/**
 * Loads (once per session) everything the automation designers need: the server catalog (triggers, fields,
 * operators, action schemas), ticket metadata (/meta/ticket-form), roles and reply templates, and the blueprint list.
 * Exposes ready-made option lists for pickers and filter-builder field definitions.
 */
@Injectable({ providedIn: 'root' })
export class AutomationContext {
  private readonly automation = inject(AutomationApi);
  private readonly blueprintsApi = inject(BlueprintsApi);
  private readonly metaApi = inject(MetaApi);

  readonly catalog = signal<AutomationCatalogDto | null>(null);
  readonly meta = signal<TicketFormMetaDto | null>(null);
  readonly lookups = signal<AutomationLookupsDto | null>(null);
  readonly blueprints = signal<BlueprintSummaryDto[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  private loaded = false;

  readonly ready = computed(() => this.catalog() !== null && this.meta() !== null && this.lookups() !== null);

  /** Loads once; `force` reloads (after admins change statuses, fields, templates…). */
  load(force = false): void {
    if ((this.loaded || this.loading()) && !force) return;
    this.loading.set(true);
    this.error.set(null);
    forkJoin({
      catalog: this.automation.catalog(),
      meta: this.metaApi.ticketForm$(force),
      lookups: this.automation.lookups(),
      blueprints: this.blueprintsApi.list().pipe(catchError(() => of([] as BlueprintSummaryDto[]))),
    }).subscribe({
      next: (r) => {
        this.catalog.set(r.catalog);
        this.meta.set(r.meta);
        this.lookups.set(r.lookups);
        this.blueprints.set(r.blueprints);
        this.loaded = true;
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.error.set(err instanceof Error ? err.message : 'Could not load the automation designer.');
        this.loading.set(false);
      },
    });
  }

  refreshBlueprints(): void {
    this.blueprintsApi
      .list()
      .pipe(catchError(() => of([] as BlueprintSummaryDto[])))
      .subscribe((list) => this.blueprints.set(list));
  }

  // ───── Option lists ─────

  readonly agentOptions = computed<SdOption[]>(() =>
    (this.meta()?.agents ?? []).map((a) => ({ value: a.userId, label: a.name, avatar: a.name, description: a.isLightAgent ? 'Light agent' : a.email })),
  );
  readonly assignableAgentOptions = computed<SdOption[]>(() => this.agentOptions().filter((o) => o.description !== 'Light agent'));
  readonly teamOptions = computed<SdOption[]>(() => {
    const m = this.meta();
    const dept = new Map((m?.departments ?? []).map((d) => [d.id, d.name]));
    return (m?.teams ?? []).map((t) => ({ value: t.id, label: t.name, group: dept.get(t.departmentId) ?? null }));
  });
  readonly departmentOptions = computed<SdOption[]>(() => (this.meta()?.departments ?? []).map((d) => ({ value: d.id, label: d.name })));
  readonly statusOptions = computed<SdOption[]>(() =>
    (this.meta()?.statuses ?? []).filter((s) => s.isActive).map((s) => ({ value: s.id, label: s.name, color: s.color, description: s.category })),
  );
  readonly categoryOptions = computed<SdOption[]>(() => {
    const cats = this.meta()?.categories ?? [];
    return cats.filter((c) => !c.parentId).map((c) => ({ value: c.id, label: c.name }));
  });
  readonly allCategoryOptions = computed<SdOption[]>(() => {
    const cats = this.meta()?.categories ?? [];
    const byId = new Map(cats.map((c) => [c.id, c]));
    return cats.map((c) => ({ value: c.id, label: c.parentId ? `${byId.get(c.parentId)?.name ?? ''} › ${c.name}` : c.name }));
  });
  readonly issueTypeOptions = computed<SdOption[]>(() => (this.meta()?.issueTypes ?? []).map((i) => ({ value: i.id, label: i.name })));
  readonly roleOptions = computed<SdOption[]>(() => (this.lookups()?.roles ?? []).map((r) => ({ value: r.id, label: r.name })));
  readonly templateOptions = computed<SdOption[]>(() => (this.lookups()?.replyTemplates ?? []).map((r) => ({ value: r.id, label: r.name })));
  readonly customFieldOptions = computed<SdOption[]>(() =>
    (this.meta()?.customFields ?? []).filter((f) => f.entity === 'Ticket' && f.isActive).map((f) => ({ value: f.key, label: f.label, description: f.fieldType })),
  );
  readonly tags = computed(() => this.meta()?.tags ?? []);

  subcategoryOptions(categoryId: string | null): SdOption[] {
    return (this.meta()?.categories ?? []).filter((c) => c.parentId && c.parentId === categoryId).map((c) => ({ value: c.id, label: c.name }));
  }

  substatusOptions(statusId: string | null): string[] {
    return this.meta()?.statuses.find((s) => s.id === statusId)?.substatuses ?? [];
  }

  customFieldType(key: string | null): string | null {
    if (!key) return null;
    const k = key.startsWith('cf:') ? key.slice(3) : key;
    return this.meta()?.customFields.find((f) => f.key === k)?.fieldType ?? null;
  }

  /** id → display name across the configuration lists (statuses, departments, teams, agents, categories…). */
  private readonly nameIndex = computed(() => {
    const m = new Map<string, string>();
    const meta = this.meta();
    const add = (list: SdOption[]) => list.forEach((o) => m.set(o.value.toLowerCase(), o.label));
    add(this.statusOptions());
    add(this.departmentOptions());
    add(this.teamOptions());
    add(this.agentOptions());
    add(this.allCategoryOptions());
    add(this.issueTypeOptions());
    add(this.roleOptions());
    for (const p of meta?.products ?? []) {
      m.set(p.id.toLowerCase(), p.name);
      p.modules.forEach((x) => m.set(x.id.toLowerCase(), `${p.name} › ${x.name}`));
      p.versions.forEach((x) => m.set(x.id.toLowerCase(), `${p.name} ${x.name}`));
    }
    (meta?.brands ?? []).forEach((b) => m.set(b.id.toLowerCase(), b.name));
    this.blueprints().forEach((b) => m.set(b.id.toLowerCase(), b.name));
    return m;
  });

  /** Replaces ids in a trace value ("…guid…" or ["guid", …]) with names where known. */
  describeValue(raw: string | null): string {
    if (raw === null || raw === '') return '—';
    const names = this.nameIndex();
    return raw.replace(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi, (id) => names.get(id.toLowerCase()) ?? id);
  }

  // ───── Labels ─────

  triggerLabel(key: string): string {
    return this.catalog()?.triggers.find((t) => t.key === key)?.label ?? key;
  }

  actionLabel(type: string): string {
    return this.catalog()?.actions.find((a) => a.type === type)?.label ?? type;
  }

  /** Label of a condition field (catalog, custom field or webhook payload path). */
  fieldLabel(field: string): string {
    if (field.startsWith('cf:')) return this.meta()?.customFields.find((f) => f.key === field.slice(3))?.label ?? field;
    return this.catalog()?.fields.find((f) => f.field === field)?.label ?? field;
  }

  /** Label for mandatory blueprint fields ("severity", "cf:key", …). */
  requirableLabel(field: string): string {
    if (field.startsWith('cf:')) return this.fieldLabel(field);
    return this.catalog()?.fields.find((f) => f.field === field)?.label ?? REQUIRABLE_LABELS[field] ?? field;
  }

  // ───── Filter builder fields ─────

  /**
   * Field definitions for the condition builder: the server catalog with options from ticket metadata, plus ticket
   * custom fields ("cf:key") and, for webhook rules, payload fields ("webhook.x"). Change operators are offered only
   * for event triggers.
   */
  conditionFields(trigger: string | null, extraWebhookFields: readonly string[] = []): FilterFieldDef[] {
    const catalog = this.catalog();
    const meta = this.meta();
    if (!catalog || !meta) return [];
    const track = trigger === null || EVENT_TRIGGERS.has(trigger);
    const opts = (values: readonly string[]): OptionDto[] => values.map((v) => ({ value: v, label: humanize(v) }));
    const fromSd = (list: SdOption[]): OptionDto[] => list.map((o) => ({ value: o.value, label: o.label }));
    const defs: FilterFieldDef[] = [];
    for (const f of catalog.fields) {
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
          options = fromSd(this.statusOptions());
          break;
        case 'department':
          type = 'enum';
          options = fromSd(this.departmentOptions());
          break;
        case 'team':
          type = 'enum';
          options = fromSd(this.teamOptions());
          break;
        case 'user':
          type = 'user';
          options = fromSd(this.agentOptions());
          break;
        case 'category':
          type = 'enum';
          options = fromSd(this.allCategoryOptions());
          break;
        case 'issueType':
          type = 'enum';
          options = fromSd(this.issueTypeOptions());
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
        case 'blueprint':
          type = 'enum';
          options = this.blueprints().map((b) => ({ value: b.id, label: b.name }));
          break;
        case 'tags':
          type = 'tags';
          options = meta.tags.map((t) => ({ value: t, label: t }));
          break;
        case 'enum':
          type = 'enum';
          options = this.enumOptions(f.field, meta);
          break;
        default:
          type = 'text';
      }
      defs.push({ key: f.field, label: f.label, type, options, ops, group: groupOf(f.field, f.type), trackChanges: track });
    }
    for (const cf of meta.customFields.filter((c) => c.entity === 'Ticket' && c.isActive)) {
      defs.push({ key: `cf:${cf.key}`, label: cf.label, ...customFieldType(cf.fieldType, cf.options), group: 'Custom fields', trackChanges: track });
    }
    if (trigger === 'webhook') {
      for (const w of extraWebhookFields) defs.push({ key: w, label: w.replace(/^webhook\./, 'Payload: '), type: 'text', group: 'Webhook payload' });
    }
    return defs;
  }

  private enumOptions(field: string, meta: TicketFormMetaDto): OptionDto[] | undefined {
    const opts = (values: readonly string[]): OptionDto[] => values.map((v) => ({ value: v, label: humanize(v) }));
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
}

const REQUIRABLE_LABELS: Record<string, string> = {
  resolutionCode: 'Resolution code',
  rootCause: 'Root cause',
};

function groupOf(field: string, type: string): string {
  if (field.startsWith('customer.') || field.startsWith('contact.') || field.startsWith('contract.') || ['customerId', 'contactId', 'entitlementStatus'].includes(field))
    return 'Customer';
  if (type === 'datetime' || field.startsWith('hours') || field === 'slaState') return 'Dates & SLA';
  if (['productId', 'moduleId', 'versionId', 'installationId'].includes(field)) return 'Product';
  if (type === 'blueprint' || field === 'blueprintState') return 'Blueprint';
  return 'Ticket';
}

function customFieldType(fieldType: string, rawOptions: unknown): Pick<FilterFieldDef, 'type' | 'options'> {
  const options = Array.isArray(rawOptions)
    ? rawOptions.map((o) => {
        if (o && typeof o === 'object' && 'value' in o) {
          const r = o as { value: unknown; label?: unknown };
          return { value: String(r.value), label: String(r.label ?? r.value) };
        }
        return { value: String(o), label: String(o) };
      })
    : undefined;
  switch (fieldType) {
    case 'Number':
    case 'Decimal':
    case 'Currency':
      return { type: 'number' };
    case 'Date':
      return { type: 'date' };
    case 'DateTime':
      return { type: 'datetime' };
    case 'Checkbox':
      return { type: 'boolean' };
    case 'Picklist':
      return { type: 'enum', options };
    case 'MultiPicklist':
      return { type: 'multi_enum', options };
    default:
      return { type: 'text' };
  }
}

/** "InProgress" → "In progress", "ticket.status_changed" stays as is. */
export function humanize(value: string): string {
  if (value.includes('.') || value.includes('_')) return value;
  const spaced = value.replace(/([a-z])([A-Z])/g, '$1 $2');
  return spaced.charAt(0) + spaced.slice(1).toLowerCase();
}

/** Collects every "webhook.x" field used in a condition tree. */
export function webhookFieldsIn(group: { conditions: { field: string }[]; groups: unknown[] }): string[] {
  const out: string[] = [];
  const walk = (g: { conditions: { field: string }[]; groups: unknown[] }) => {
    for (const c of g.conditions) if (c.field.startsWith('webhook.') && !out.includes(c.field)) out.push(c.field);
    for (const child of g.groups) walk(child as typeof g);
  };
  walk(group);
  return out;
}
