import { DOCUMENT } from '@angular/common';
import { DestroyRef, Pipe, PipeTransform, Signal, inject, signal } from '@angular/core';
import { ApiError, formatDate } from '@sd/core';
import { SdTone } from '@sd/ui';

/** User-facing message for anything thrown by an API call (problem detail, `message` body, or a fallback). */
export function errorMessage(err: unknown, fallback = 'Something went wrong. Please try again.'): string {
  const e = ApiError.from(err);
  if (e.isNetworkError) return 'Cannot reach the support portal. Check your connection and try again.';
  if (e.status === 429) return 'Too many attempts. Wait a moment and try again.';
  const message = e.extension<unknown>('message');
  if (typeof message === 'string' && message.trim()) return message;
  if (e.detail) return e.detail;
  if (e.status >= 500 || e.status < 0) return fallback;
  return e.title && !/^HTTP \d+$/.test(e.title) ? e.title : fallback;
}

/** 404 (or 403) — the record does not exist or is outside the contact's access. */
export function isNotFound(err: unknown): boolean {
  if (!err) return false;
  const e = ApiError.from(err);
  return e.status === 404 || e.status === 403;
}

/** Badge tone for a customer-facing ticket status category. */
export function statusTone(category: string | null | undefined): SdTone {
  switch ((category ?? '').toLowerCase()) {
    case 'open':
      return 'info';
    case 'assigned':
    case 'inprogress':
      return 'primary';
    case 'waiting':
      return 'warning';
    case 'resolved':
      return 'success';
    case 'closed':
      return 'neutral';
    default:
      return 'neutral';
  }
}

const HTML_ESCAPES: Record<string, string> = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };

export function escapeHtml(text: string): string {
  return text.replace(/[&<>"']/g, (c) => HTML_ESCAPES[c] ?? c);
}

/** Plain text of an HTML fragment (no scripts run: DOMParser documents are inert). */
export function htmlToText(html: string | null | undefined): string {
  if (!html) return '';
  if (typeof DOMParser === 'undefined') return html.replace(/<[^>]*>/g, ' ').replace(/\s+/g, ' ').trim();
  const doc = new DOMParser().parseFromString(`<body>${html}</body>`, 'text/html');
  return (doc.body.textContent ?? '').replace(/\s+/g, ' ').trim();
}

/** Plain multi-line text → escaped HTML paragraphs (blank lines split paragraphs, single newlines become <br>). */
export function textToHtml(text: string): string {
  return text
    .trim()
    .split(/\n{2,}/)
    .map((p) => `<p>${escapeHtml(p).replace(/\n/g, '<br>')}</p>`)
    .join('');
}

/** Formats a calendar date ("YYYY-MM-DD", C# DateOnly) without a time-zone shift. */
export function formatIsoDate(value: string | null | undefined, style: 'short' | 'medium' | 'long' = 'medium'): string {
  if (!value) return '';
  const iso = /^\d{4}-\d{2}-\d{2}$/.test(value) ? `${value}T00:00:00Z` : value;
  return formatDate(iso, { timeZone: 'UTC', style });
}

/** `{{ contract.endDate | portalDate }}` — calendar dates (DateOnly) rendered without a time-zone shift. */
@Pipe({ name: 'portalDate' })
export class PortalDatePipe implements PipeTransform {
  transform(value: string | null | undefined, style: 'short' | 'medium' | 'long' = 'medium'): string {
    return formatIsoDate(value, style);
  }
}

let displayNames: Intl.DisplayNames | null | undefined;

/** "fr" → "French", "pt-BR" → "Portuguese (Brazil)" (falls back to the code). */
export function localeName(code: string): string {
  if (displayNames === undefined) {
    try {
      displayNames = new Intl.DisplayNames([globalThis.navigator?.language ?? 'en'], { type: 'language' });
    } catch {
      displayNames = null;
    }
  }
  try {
    return displayNames?.of(code) ?? code;
  } catch {
    return code;
  }
}

/** "1 reply" / "3 replies". */
export function plural(count: number, one: string, many: string): string {
  return `${count} ${count === 1 ? one : many}`;
}

/** Reactive `matchMedia` (injection context required); cleans up with the caller. */
export function mediaQuery(query: string): Signal<boolean> {
  const view = inject(DOCUMENT).defaultView;
  const mql = view?.matchMedia?.(query) ?? null;
  const matches = signal(mql?.matches ?? false);
  if (mql) {
    const onChange = (e: MediaQueryListEvent) => matches.set(e.matches);
    mql.addEventListener('change', onChange);
    inject(DestroyRef).onDestroy(() => mql.removeEventListener('change', onChange));
  }
  return matches.asReadonly();
}

/** The browser's preferred language tag ("en-GB"). */
export function browserLanguage(): string {
  return globalThis.navigator?.language ?? 'en';
}

/** Icon name for an attachment content type. */
export function fileIcon(contentType: string | null | undefined): string {
  const t = (contentType ?? '').toLowerCase();
  if (t.startsWith('image/')) return 'image';
  if (t.includes('pdf') || t.includes('word') || t.startsWith('text/')) return 'file-text';
  return 'paperclip';
}
