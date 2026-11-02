import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { InvoiceSummaryDto, UsageMeterDto } from '@sd/core';
import { SdTone } from '@sd/ui';

/**
 * Shared presentation helpers for the SaaS screens (subscription, platform console, compliance, audit, configuration
 * transfer): money/number formatting, status tones and a responsive table + callout stylesheet built from tokens.
 */

export function money(amount: number | null | undefined, currency = 'USD'): string {
  if (amount === null || amount === undefined) return '—';
  try {
    return new Intl.NumberFormat(undefined, { style: 'currency', currency, maximumFractionDigits: 2 }).format(amount);
  } catch {
    return `${amount.toFixed(2)} ${currency}`;
  }
}

export function compactNumber(value: number | null | undefined): string {
  if (value === null || value === undefined) return '—';
  return new Intl.NumberFormat(undefined, { notation: value >= 100_000 ? 'compact' : 'standard', maximumFractionDigits: 1 }).format(value);
}

export function bytes(value: number | null | undefined): string {
  if (value === null || value === undefined) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let v = value;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v >= 10 || i === 0 ? Math.round(v) : v.toFixed(1)} ${units[i]}`;
}

export function meterValue(unit: string, value: number | null | undefined): string {
  return unit === 'bytes' ? bytes(value) : compactNumber(value);
}

export function tenantTone(status: string | null | undefined): SdTone {
  switch (status) {
    case 'Active':
      return 'success';
    case 'Trial':
    case 'Trialing':
      return 'info';
    case 'PastDue':
      return 'warning';
    case 'Suspended':
    case 'Cancelled':
    case 'PendingDeletion':
      return 'danger';
    default:
      return 'neutral';
  }
}

export function invoiceTone(invoice: Pick<InvoiceSummaryDto, 'status' | 'overdue'>): SdTone {
  if (invoice.status === 'Open') return invoice.overdue ? 'danger' : 'warning';
  if (invoice.status === 'Paid') return 'success';
  return 'neutral';
}

export function invoiceLabel(invoice: Pick<InvoiceSummaryDto, 'status' | 'overdue'>): string {
  return invoice.status === 'Open' && invoice.overdue ? 'Overdue' : invoice.status;
}

export function requestTone(status: string): SdTone {
  switch (status) {
    case 'Completed':
    case 'Approved':
      return 'success';
    case 'Pending':
      return 'warning';
    case 'Processing':
      return 'info';
    case 'Rejected':
    case 'Failed':
    case 'Revoked':
      return 'danger';
    default:
      return 'neutral';
  }
}

export function humanize(value: string | null | undefined): string {
  if (!value) return '';
  return value.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[_.]/g, ' ').replace(/^\w/, (c) => c.toUpperCase());
}

/** "ends in 3d" / "ended 2d ago": trial ends and access expiries that may already be in the past. */
export function endsOrEnded(at: string | null | undefined): string {
  return at && new Date(at).getTime() < Date.now() ? 'ended' : 'ends';
}

/** Tenant / subscription status for badges: "PastDue" → "Past due", "PendingDeletion" → "Pending deletion". */
export function statusLabel(status: string | null | undefined): string {
  const words = humanize(status);
  return words ? words.charAt(0) + words.slice(1).toLowerCase() : '';
}

/** Responsive data table + callouts + section headings used across the SaaS screens. */
export const SAAS_CSS = `
  .table-wrap { overflow-x: auto; border: 1px solid var(--sd-border); border-radius: var(--sd-radius-lg); background: var(--sd-surface); }
  table.data { width: 100%; border-collapse: collapse; font-size: var(--sd-text-sm); }
  table.data th { position: sticky; top: 0; text-align: left; font-weight: var(--sd-weight-semibold); color: var(--sd-text-2); background: var(--sd-surface-2);
    padding: 8px 12px; border-bottom: 1px solid var(--sd-border); white-space: nowrap; }
  table.data td { padding: 9px 12px; border-bottom: 1px solid var(--sd-divider); vertical-align: middle; }
  table.data tr:last-child td { border-bottom: 0; }
  table.data tbody tr.clickable { cursor: pointer; }
  table.data tbody tr.clickable:hover, table.data tbody tr.clickable:focus-visible { background: var(--sd-row-hover); outline: none; }
  table.data td.num, table.data th.num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
  table.data td.actions { text-align: right; white-space: nowrap; }
  .callout { display: flex; align-items: flex-start; gap: 10px; padding: 12px 14px; border: 1px solid transparent; border-radius: var(--sd-radius-md);
    font-size: var(--sd-text-sm); margin-bottom: var(--sd-space-4); }
  .callout > sd-icon { margin-top: 1px; flex: none; }
  .callout .grow { flex: 1; min-width: 0; }
  .callout--info { background: var(--sd-info-bg); border-color: var(--sd-info-border); color: var(--sd-text); }
  .callout--warning { background: var(--sd-warning-bg); border-color: var(--sd-warning-border); color: var(--sd-text); }
  .callout--danger { background: var(--sd-danger-bg); border-color: var(--sd-danger-border); color: var(--sd-text); }
  .callout--success { background: var(--sd-success-bg); border-color: var(--sd-success-border); color: var(--sd-text); }
  .callout--demo { background: var(--sd-accent-soft); border-color: color-mix(in oklab, var(--sd-accent) 35%, var(--sd-surface)); color: var(--sd-text); }
  .section-title { display: flex; align-items: center; gap: 8px; margin: var(--sd-space-5) 0 var(--sd-space-3); font-size: var(--sd-text-md);
    font-weight: var(--sd-weight-semibold); }
  .muted { color: var(--sd-text-3); }
  .small { font-size: var(--sd-text-sm); }
  .mono { font-family: var(--sd-font-mono); font-size: var(--sd-text-xs); word-break: break-all; }
  .err { color: var(--sd-danger); font-size: var(--sd-text-sm); margin: 0; }
  .toolbar { display: flex; flex-wrap: wrap; align-items: flex-end; gap: 10px; margin-bottom: var(--sd-space-4); }
  .toolbar > * { min-width: 0; }
  .form { display: grid; gap: 14px; }
  .form-grid { display: grid; gap: 14px; grid-template-columns: repeat(auto-fill, minmax(min(100%, 200px), 1fr)); }
  .footer { display: flex; justify-content: flex-end; gap: 8px; }
  .more { display: flex; justify-content: center; padding: 12px; }
  dl.facts { display: grid; grid-template-columns: max-content 1fr; gap: 6px 16px; margin: 0; font-size: var(--sd-text-sm); }
  dl.facts dt { color: var(--sd-text-3); }
  dl.facts dd { margin: 0; min-width: 0; overflow-wrap: anywhere; }
  @media (max-width: 640px) {
    table.data .hide-sm { display: none; }
  }
`;

/**
 * `<desk-usage-meters [meters]="sub.usage" />` — limits vs usage as labelled progress bars (ok / warning ≥ 80 % /
 * exceeded / unlimited), each an accessible progressbar with its value text.
 */
@Component({
  selector: 'desk-usage-meters',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ul class="meters">
      @for (m of meters(); track m.metric) {
        <li>
          <div class="top">
            <span class="label">{{ m.label }}</span>
            <span class="value sd-num">
              {{ value(m.unit, m.used) }}
              @if (m.limit !== null) {
                <span class="of">of {{ value(m.unit, m.limit) }}</span>
              } @else {
                <span class="of">unlimited</span>
              }
            </span>
          </div>
          <div
            class="bar"
            role="progressbar"
            [attr.aria-label]="m.label"
            aria-valuemin="0"
            [attr.aria-valuemax]="m.limit ?? null"
            [attr.aria-valuenow]="m.used"
            [attr.aria-valuetext]="m.limit === null ? value(m.unit, m.used) + ' used, unlimited' : (m.percent ?? 0) + '% used'"
            [class]="'bar bar--' + m.state"
          >
            <span [style.width.%]="m.limit === null ? 0 : min(100, m.percent ?? 0)"></span>
          </div>
          @if (m.state === 'exceeded') {
            <span class="hint hint--danger">Over the plan limit — new items are blocked.</span>
          } @else if (m.state === 'warning') {
            <span class="hint hint--warning">{{ m.percent }}% used</span>
          }
        </li>
      } @empty {
        <li class="empty">No usage recorded yet.</li>
      }
    </ul>
  `,
  styles: `
    .meters { list-style: none; margin: 0; padding: 0; display: grid; gap: 14px; grid-template-columns: repeat(auto-fill, minmax(min(100%, 220px), 1fr)); }
    .top { display: flex; justify-content: space-between; gap: 8px; font-size: var(--sd-text-sm); margin-bottom: 6px; }
    .label { color: var(--sd-text-2); font-weight: var(--sd-weight-medium); }
    .value { color: var(--sd-text); text-align: right; }
    .of { color: var(--sd-text-3); margin-left: 4px; }
    .bar { height: 8px; border-radius: var(--sd-radius-pill); background: var(--sd-surface-3); overflow: hidden; }
    .bar span { display: block; height: 100%; border-radius: inherit; background: var(--sd-primary); transition: width var(--sd-dur) var(--sd-ease); }
    .bar--warning span { background: var(--sd-warning); }
    .bar--exceeded span { background: var(--sd-danger); width: 100% !important; }
    .bar--unlimited { background: repeating-linear-gradient(45deg, var(--sd-surface-3), var(--sd-surface-3) 6px, var(--sd-surface-2) 6px, var(--sd-surface-2) 12px); }
    .hint { display: block; margin-top: 4px; font-size: var(--sd-text-xs); }
    .hint--warning { color: var(--sd-warning); }
    .hint--danger { color: var(--sd-danger); }
    .empty { color: var(--sd-text-3); font-size: var(--sd-text-sm); }
  `,
})
export class UsageMetersComponent {
  readonly meters = input<UsageMeterDto[]>([]);
  protected readonly value = meterValue;
  protected readonly min = Math.min;
}

/** `<desk-plan-limits [plan]="p" />` — compact list of a plan's limits. */
@Component({
  selector: 'desk-plan-limits',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <dl class="facts">
      @for (row of rows(); track row.label) {
        <dt>{{ row.label }}</dt>
        <dd class="sd-num">{{ row.value }}</dd>
      }
    </dl>
  `,
  styles: `
    dl.facts { display: grid; grid-template-columns: max-content 1fr; gap: 4px 14px; margin: 0; font-size: var(--sd-text-sm); }
    dt { color: var(--sd-text-3); }
    dd { margin: 0; }
  `,
})
export class PlanLimitsComponent {
  readonly plan = input.required<{
    maxAgents: number | null;
    maxLightAgents: number | null;
    maxPortalUsers: number | null;
    storageMb: number | null;
    maxChannels: number | null;
    aiTokensMonthly: number | null;
    apiCallsMonthly: number | null;
    maxAttachmentMb: number | null;
    maxBrands: number | null;
  }>();
  protected readonly rows = computed(() => {
    const p = this.plan();
    const n = (v: number | null) => (v === null ? 'Unlimited' : compactNumber(v));
    return [
      { label: 'Agents', value: n(p.maxAgents) },
      { label: 'Light agents', value: n(p.maxLightAgents) },
      { label: 'Portal users', value: n(p.maxPortalUsers) },
      { label: 'Storage', value: p.storageMb === null ? 'Unlimited' : bytes(p.storageMb * 1024 * 1024) },
      { label: 'Channels', value: n(p.maxChannels) },
      { label: 'Brands', value: n(p.maxBrands) },
      { label: 'AI tokens / month', value: n(p.aiTokensMonthly) },
      { label: 'API calls / month', value: n(p.apiCallsMonthly) },
      { label: 'Attachment size', value: p.maxAttachmentMb === null ? 'Unlimited' : `${p.maxAttachmentMb} MB` },
    ];
  });
}
