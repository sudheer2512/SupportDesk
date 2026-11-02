/**
 * Date & duration helpers. The API speaks UTC ISO-8601; the UI renders in the user's time zone
 * (MeDto.timeZone) via Intl, with formatters cached per (locale, zone, style).
 */

export type DateInput = string | number | Date | null | undefined;

export function toDate(value: DateInput): Date | null {
  if (value === null || value === undefined || value === '') return null;
  const d = value instanceof Date ? value : new Date(value);
  return Number.isNaN(d.getTime()) ? null : d;
}

export function toMillis(value: DateInput): number | null {
  return toDate(value)?.getTime() ?? null;
}

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/** "1h 05m", "2d 3h", "45m", "30s" — compact duration for a span in milliseconds (sign ignored). */
export function formatDurationMs(ms: number, options: { seconds?: boolean } = {}): string {
  const abs = Math.abs(ms);
  if (abs < MINUTE) return options.seconds ? `${Math.floor(abs / 1000)}s` : abs < 30_000 ? '<1m' : '1m';
  const days = Math.floor(abs / DAY);
  const hours = Math.floor((abs % DAY) / HOUR);
  const minutes = Math.floor((abs % HOUR) / MINUTE);
  if (days > 0) return hours > 0 ? `${days}d ${hours}h` : `${days}d`;
  if (hours > 0) return `${hours}h ${String(minutes).padStart(2, '0')}m`;
  return `${minutes}m`;
}

/** Same as formatDurationMs for a number of minutes (time entries, SLA targets). */
export function formatDurationMinutes(minutes: number | null | undefined): string {
  if (minutes === null || minutes === undefined || Number.isNaN(minutes)) return '—';
  if (minutes === 0) return '0m';
  return formatDurationMs(minutes * MINUTE);
}

/** "just now", "5m ago", "in 3h", "2d ago"; beyond a week falls back to a short date. */
export function formatRelative(value: DateInput, now: number = Date.now(), locale?: string): string {
  const d = toDate(value);
  if (!d) return '';
  const diff = d.getTime() - now;
  const abs = Math.abs(diff);
  if (abs < 45_000) return 'just now';
  if (abs >= 7 * DAY) return formatDate(d, { locale, style: 'short' });
  const label = formatDurationMs(abs).split(' ')[0];
  return diff < 0 ? `${label} ago` : `in ${label}`;
}

export type SlaTone = 'ontrack' | 'atrisk' | 'breached' | 'paused' | 'met' | 'none';

export interface SlaCountdown {
  label: string;
  tone: SlaTone;
  /** Milliseconds until due (negative when overdue); null when not applicable. */
  remainingMs: number | null;
}

/**
 * Live SLA label: "Due in 1h 05m", "Breached 12m ago", "Paused", "Met".
 * `atRiskWithinMs` turns an on-track timer amber when close to the deadline (default 30 min).
 */
export function slaCountdown(
  dueAt: DateInput,
  state: string | null | undefined,
  now: number = Date.now(),
  atRiskWithinMs = 30 * MINUTE,
): SlaCountdown {
  const s = (state ?? '').toLowerCase();
  if (s === 'paused') return { label: 'Paused', tone: 'paused', remainingMs: null };
  if (s === 'met') return { label: 'Met', tone: 'met', remainingMs: null };
  const due = toMillis(dueAt);
  if (due === null) return { label: s === 'breached' ? 'Breached' : 'No SLA', tone: s === 'breached' ? 'breached' : 'none', remainingMs: null };
  const remaining = due - now;
  if (remaining <= 0 || s === 'breached') {
    return { label: `Breached ${formatDurationMs(Math.min(remaining, 0))} ago`, tone: 'breached', remainingMs: remaining };
  }
  const tone: SlaTone = s === 'atrisk' || remaining <= atRiskWithinMs ? 'atrisk' : 'ontrack';
  return { label: `Due in ${formatDurationMs(remaining)}`, tone, remainingMs: remaining };
}

const formatterCache = new Map<string, Intl.DateTimeFormat>();

export interface FormatDateOptions {
  locale?: string;
  timeZone?: string;
  style?: 'short' | 'medium' | 'long';
}

function formatter(kind: 'date' | 'datetime' | 'time', options: FormatDateOptions): Intl.DateTimeFormat {
  const key = `${kind}|${options.locale ?? ''}|${options.timeZone ?? ''}|${options.style ?? 'medium'}`;
  let f = formatterCache.get(key);
  if (!f) {
    const style = options.style ?? 'medium';
    const opts: Intl.DateTimeFormatOptions = { timeZone: options.timeZone || undefined };
    if (kind !== 'time') opts.dateStyle = style;
    if (kind !== 'date') opts.timeStyle = 'short';
    try {
      f = new Intl.DateTimeFormat(options.locale || undefined, opts);
    } catch {
      f = new Intl.DateTimeFormat(undefined, { ...opts, timeZone: undefined });
    }
    formatterCache.set(key, f);
  }
  return f;
}

export function formatDate(value: DateInput, options: FormatDateOptions = {}): string {
  const d = toDate(value);
  return d ? formatter('date', options).format(d) : '';
}

export function formatDateTime(value: DateInput, options: FormatDateOptions = {}): string {
  const d = toDate(value);
  return d ? formatter('datetime', options).format(d) : '';
}

export function formatTime(value: DateInput, options: FormatDateOptions = {}): string {
  const d = toDate(value);
  return d ? formatter('time', options).format(d) : '';
}

// ───── Calendar helpers (local time) ─────

export function startOfDay(d: Date): Date {
  return new Date(d.getFullYear(), d.getMonth(), d.getDate());
}

export function addDays(d: Date, days: number): Date {
  const r = new Date(d);
  r.setDate(r.getDate() + days);
  return r;
}

export function addMonths(d: Date, months: number): Date {
  return new Date(d.getFullYear(), d.getMonth() + months, 1);
}

/** Start of the week containing `d` (weekStartsOn: 0 = Sunday, 1 = Monday). */
export function startOfWeek(d: Date, weekStartsOn = 1): Date {
  const day = startOfDay(d);
  const diff = (day.getDay() - weekStartsOn + 7) % 7;
  return addDays(day, -diff);
}

export function isSameDay(a: Date, b: Date): boolean {
  return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
}

/** Value for <input type="date">: "YYYY-MM-DD" in local time. */
export function toDateInputValue(value: DateInput): string {
  const d = toDate(value);
  if (!d) return '';
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/** Value for <input type="datetime-local">: "YYYY-MM-DDTHH:mm" in local time. */
export function toDateTimeInputValue(value: DateInput): string {
  const d = toDate(value);
  if (!d) return '';
  return `${toDateInputValue(d)}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** Parses a datetime-local value (local time) into a UTC ISO string; '' → null. */
export function fromDateTimeInputValue(value: string): string | null {
  if (!value) return null;
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? null : d.toISOString();
}

function pad(n: number): string {
  return String(n).padStart(2, '0');
}
