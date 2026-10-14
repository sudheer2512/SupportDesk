/** Small formatting helpers shared by the mobile screens. */

const ESCAPES: Record<string, string> = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };

/** Plain text from a textarea → safe HTML paragraphs (blank lines split paragraphs, single newlines become <br>). */
export function textToHtml(text: string): string {
  const escaped = text.replace(/[&<>"']/g, (c) => ESCAPES[c]);
  return escaped
    .split(/\n{2,}/)
    .map((p) => p.trim())
    .filter(Boolean)
    .map((p) => `<p>${p.replace(/\n/g, '<br>')}</p>`)
    .join('');
}

/** HTML (e.g. a rendered reply template) → plain text for the composer textarea. */
export function htmlToText(html: string): string {
  if (typeof DOMParser === 'undefined') return html.replace(/<[^>]+>/g, '');
  const withBreaks = html
    .replace(/<br\s*\/?>/gi, '\n')
    .replace(/<\/(p|div|li|h[1-6])>/gi, '\n\n')
    .replace(/<li[^>]*>/gi, '• ');
  const doc = new DOMParser().parseFromString(withBreaks, 'text/html');
  return (doc.body.textContent ?? '').replace(/\n{3,}/g, '\n\n').trim();
}

export function fileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export function initials(name: string | null | undefined): string {
  const parts = (name ?? '').trim().split(/\s+/).filter(Boolean);
  if (!parts.length) return '?';
  return ((parts[0][0] ?? '') + (parts.length > 1 ? (parts[parts.length - 1][0] ?? '') : '')).toUpperCase();
}

/** Ionic colour for a ticket priority. */
export function priorityColor(priority: string | null | undefined): string {
  switch ((priority ?? '').toLowerCase()) {
    case 'critical':
      return 'danger';
    case 'high':
      return 'warning';
    case 'low':
      return 'medium';
    default:
      return 'primary';
  }
}

/** Ionic colour for a status category. */
export function statusColor(category: string | null | undefined): string {
  switch ((category ?? '').toLowerCase()) {
    case 'open':
      return 'primary';
    case 'assigned':
    case 'inprogress':
      return 'tertiary';
    case 'waiting':
      return 'warning';
    case 'resolved':
      return 'success';
    case 'closed':
      return 'medium';
    default:
      return 'medium';
  }
}

/** "Saved 10:42" / "Saved yesterday" — when a cached copy was stored. */
export function savedLabel(savedAt: number | null): string {
  if (!savedAt) return '';
  const d = new Date(savedAt);
  const sameDay = new Date().toDateString() === d.toDateString();
  return sameDay
    ? `saved ${d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })}`
    : `saved ${d.toLocaleDateString(undefined, { day: 'numeric', month: 'short' })}`;
}
