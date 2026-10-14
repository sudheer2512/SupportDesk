import { Pipe, PipeTransform } from '@angular/core';
import type { SdTone } from '@sd/ui';

/** Shared presentation helpers for the Customers and Products & Assets modules. */

/** "AnnualSupport" → "Annual support", "OnsiteVisit" → "Onsite visit", "EndOfLife" → "End of life". */
export function humanize(value: string | null | undefined): string {
  if (!value) return '';
  const spaced = value
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
    .replace(/_/g, ' ');
  const special: Record<string, string> = { Amc: 'AMC', 'Time And Material': 'Time & material', 'On Premise': 'On-premise' };
  if (special[spaced]) return special[spaced];
  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase().replace(/\bamc\b/g, 'AMC').replace(/\bsla\b/g, 'SLA');
}

export function tierTone(tier: string | null | undefined): SdTone {
  switch (tier) {
    case 'Platinum':
      return 'primary';
    case 'Gold':
      return 'warning';
    case 'Silver':
      return 'info';
    case 'Bronze':
      return 'accent';
    default:
      return 'neutral';
  }
}

export function statusTone(status: string | null | undefined): SdTone {
  switch (status) {
    case 'Active':
    case 'InService':
    case 'Completed':
    case 'Paid':
    case 'Cleared':
    case 'Delivered':
    case 'Entitled':
    case 'Supported':
    case 'Released':
    case 'OptedIn':
      return 'success';
    case 'Expired':
    case 'Revoked':
    case 'Overdue':
    case 'Missed':
    case 'EndOfLife':
    case 'NotEntitled':
    case 'OptedOut':
    case 'Decommissioned':
      return 'danger';
    case 'Suspended':
    case 'OnHold':
    case 'InRepair':
    case 'InProgress':
    case 'Posted':
    case 'Open':
      return 'warning';
    case 'Scheduled':
    case 'Prospect':
    case 'Planned':
    case 'Invoiced':
      return 'info';
    default:
      return 'neutral';
  }
}

export function urgencyTone(days: number): SdTone {
  return days < 0 ? 'danger' : days <= 7 ? 'danger' : days <= 30 ? 'warning' : days <= 90 ? 'info' : 'neutral';
}

const moneyFormats = new Map<string, Intl.NumberFormat>();

/** Indian-grouped currency without decimals: 1800000 INR → "₹18,00,000". */
export function formatMoney(value: number | null | undefined, currency = 'INR', decimals = 0): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '—';
  const key = `${currency}:${decimals}`;
  let fmt = moneyFormats.get(key);
  if (!fmt) {
    try {
      fmt = new Intl.NumberFormat('en-IN', { style: 'currency', currency, maximumFractionDigits: decimals, minimumFractionDigits: decimals });
    } catch {
      fmt = new Intl.NumberFormat('en-IN', { maximumFractionDigits: decimals, minimumFractionDigits: decimals });
    }
    moneyFormats.set(key, fmt);
  }
  return fmt.format(value);
}

/** "2026-10-14" → "14 Oct 2026" (date-only values are not shifted by time zones). */
export function formatDateOnly(value: string | null | undefined): string {
  if (!value) return '—';
  const [y, m, d] = value.slice(0, 10).split('-').map(Number);
  if (!y || !m || !d) return value;
  return new Date(Date.UTC(y, m - 1, d)).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' });
}

/** Today as "YYYY-MM-DD" (local). */
export function todayIso(offsetDays = 0): string {
  const d = new Date();
  d.setDate(d.getDate() + offsetDays);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

export function addMonthsIso(iso: string, months: number, days = 0): string {
  const [y, m, d] = iso.split('-').map(Number);
  const date = new Date(Date.UTC(y, m - 1 + months, d + days));
  return date.toISOString().slice(0, 10);
}

/** `{{ 'AnnualSupport' | deskHumanize }}` → "Annual support". */
@Pipe({ name: 'deskHumanize' })
export class HumanizePipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    return humanize(value);
  }
}

/** `{{ amount | deskMoney: 'INR' }}` → "₹1,80,000". */
@Pipe({ name: 'deskMoney' })
export class MoneyPipe implements PipeTransform {
  transform(value: number | null | undefined, currency = 'INR', decimals = 0): string {
    return formatMoney(value, currency, decimals);
  }
}

/** `{{ '2026-10-14' | deskDate }}` → "14 Oct 2026". */
@Pipe({ name: 'deskDate' })
export class DateOnlyPipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    return formatDateOnly(value);
  }
}
