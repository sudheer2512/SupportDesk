import { SdTone } from '@sd/ui';

/** Badge tone for contract / licence / installation / service statuses (free-text server values). */
export function recordTone(status: string | null | undefined): SdTone {
  const s = (status ?? '').toLowerCase();
  if (/(active|valid|live|production|completed|done|confirmed|running)/.test(s)) return 'success';
  if (/(scheduled|planned|pending|draft|upcoming|in ?progress|trial)/.test(s)) return 'info';
  if (/(suspend|hold|overdue|expiring|warning|degraded|renewal)/.test(s)) return 'warning';
  if (/(expired|cancel|terminated|failed|revoked|blocked)/.test(s)) return 'danger';
  return 'neutral';
}

/** "Prepaid hours", "OnPremise" → "On premise" for enum-ish values. */
export function humanize(value: string | null | undefined): string {
  if (!value) return '';
  return value
    .replace(/([a-z])([A-Z])/g, '$1 $2')
    .replace(/[_-]+/g, ' ')
    .replace(/^\w/, (c) => c.toUpperCase());
}

export function scopeLabel(scope: string | null | undefined): string {
  switch ((scope ?? '').toLowerCase()) {
    case 'organisation':
    case 'organization':
      return 'Your whole organisation';
    case 'location':
      return 'Your location';
    default:
      return 'Your own tickets';
  }
}
