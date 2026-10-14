import { DestroyRef, inject, signal } from '@angular/core';
import {
  ApiError,
  ArticleStatus,
  ArticleType,
  FlowNodeType,
  KbCategoryDto,
  KbOptionsDto,
  KbVisibility,
  TopicStatus,
  TranslationStatus,
} from '@sd/core';
import { IconNode, SdOption, SdTone, registerIcons } from '@sd/ui';
import {
  ArchiveRestore,
  FileDown,
  FileUp,
  FolderTree,
  GitBranch,
  GitCompare,
  ListTree,
  Pin,
  PinOff,
  Reply,
  Route,
  Save,
  SearchX,
  Signpost,
  StarOff,
} from 'lucide';
import { Observable, Subscription } from 'rxjs';

/** Base path of the module (the desk mounts it at /kb). */
export const KB_BASE = '/kb';

/** Pages with unsaved edits implement this; the router asks before leaving (canDeactivate). */
export interface KbLeaveGuarded {
  canDeactivate(): boolean | Promise<boolean>;
}

// ───── Icons used by the module that the shared registry does not ship ─────

let iconsRegistered = false;

/** Registers the extra Lucide icons the knowledge-base screens use (idempotent). */
export function registerKbIcons(): void {
  if (iconsRegistered) return;
  iconsRegistered = true;
  registerIcons({
    'archive-restore': ArchiveRestore,
    'file-down': FileDown,
    'file-up': FileUp,
    'folder-tree': FolderTree,
    'git-branch': GitBranch,
    'git-compare': GitCompare,
    'list-tree': ListTree,
    pin: Pin,
    'pin-off': PinOff,
    reply: Reply,
    route: Route,
    save: Save,
    'search-x': SearchX,
    signpost: Signpost,
    'star-off': StarOff,
  } as unknown as Record<string, IconNode>);
}

// ───── Labels & tones ─────

export const ARTICLE_STATUS_LABEL: Record<ArticleStatus, string> = {
  Draft: 'Draft',
  InReview: 'In review',
  Approved: 'Approved',
  Published: 'Published',
  Archived: 'Archived',
  Expired: 'Expired',
};

export const ARTICLE_STATUS_TONE: Record<ArticleStatus, SdTone> = {
  Draft: 'neutral',
  InReview: 'warning',
  Approved: 'info',
  Published: 'success',
  Archived: 'neutral',
  Expired: 'danger',
};

export const TRANSLATION_STATUS_LABEL: Record<TranslationStatus, string> = {
  Draft: 'Draft',
  InTranslation: 'In translation',
  InReview: 'In review',
  Published: 'Published',
  Outdated: 'Outdated',
};

export const TRANSLATION_STATUS_TONE: Record<TranslationStatus, SdTone> = {
  Draft: 'neutral',
  InTranslation: 'info',
  InReview: 'warning',
  Published: 'success',
  Outdated: 'danger',
};

export const TOPIC_STATUS_TONE: Record<TopicStatus, SdTone> = {
  Open: 'info',
  Answered: 'success',
  Closed: 'neutral',
  Hidden: 'danger',
};

export const ARTICLE_TYPE_LABEL: Record<ArticleType, string> = {
  FAQ: 'FAQ',
  HowTo: 'How-to',
  Troubleshooting: 'Troubleshooting',
  Reference: 'Reference',
  ReleaseNote: 'Release note',
  ProductDoc: 'Product doc',
};

export interface VisibilityMeta {
  label: string;
  icon: string;
  description: string;
  tone: SdTone;
}

export const VISIBILITY_META: Record<KbVisibility, VisibilityMeta> = {
  Public: { label: 'Public', icon: 'globe', description: 'Anyone can read it on the help center.', tone: 'success' },
  Authenticated: { label: 'Signed-in customers', icon: 'user-check', description: 'Customers who have signed in to the portal.', tone: 'info' },
  Internal: { label: 'Internal', icon: 'lock', description: 'Agents only; never shown in the portal.', tone: 'neutral' },
  Restricted: { label: 'Restricted', icon: 'shield-check', description: 'Only customers matching an access rule.', tone: 'warning' },
};

export const ALL_ARTICLE_STATUSES: readonly ArticleStatus[] = ['Draft', 'InReview', 'Approved', 'Published', 'Archived', 'Expired'];
export const ALL_ARTICLE_TYPES: readonly ArticleType[] = ['FAQ', 'HowTo', 'Troubleshooting', 'Reference', 'ReleaseNote', 'ProductDoc'];
export const ALL_VISIBILITIES: readonly KbVisibility[] = ['Public', 'Authenticated', 'Internal', 'Restricted'];

export const FLOW_NODE_META: Record<FlowNodeType, { label: string; icon: string; description: string }> = {
  question: { label: 'Question', icon: 'help', description: 'Asks the customer to pick an answer.' },
  solution: { label: 'Solution', icon: 'lightbulb', description: 'Shows the fix; ends the flow.' },
  article: { label: 'Article', icon: 'file-text', description: 'Points to a knowledge-base article.' },
  escalate: { label: 'Escalate', icon: 'headphones', description: 'Hands over to an agent (opens a ticket).' },
};

export const EMPTY_KB_OPTIONS: KbOptionsDto = {
  articleTypes: [...ALL_ARTICLE_TYPES],
  visibilities: [...ALL_VISIBILITIES],
  statuses: [...ALL_ARTICLE_STATUSES],
  locales: [],
  brands: [],
  products: [],
  customerGroups: [],
  customerTiers: [],
  owners: [],
  tags: [],
};

// ───── Formatting helpers ─────

const displayNames = new Map<string, Intl.DisplayNames | null>();

/** "de-DE" → "German (Germany) · de-DE" (falls back to the tag). */
export function localeLabel(tag: string, displayLocale = 'en'): string {
  if (!tag) return '';
  let names = displayNames.get(displayLocale);
  if (names === undefined) {
    try {
      names = new Intl.DisplayNames([displayLocale], { type: 'language' });
    } catch {
      names = null;
    }
    displayNames.set(displayLocale, names);
  }
  try {
    const name = names?.of(tag);
    return name && name !== tag ? `${name} · ${tag}` : tag;
  } catch {
    return tag;
  }
}

/** Share of helpful votes (0–100) or null when nobody voted. */
export function helpfulPercent(helpful: number, notHelpful: number): number | null {
  const total = helpful + notHelpful;
  return total > 0 ? Math.round((helpful / total) * 100) : null;
}

export function formatPercent(value: number | null | undefined): string {
  return value === null || value === undefined ? '—' : `${Math.round(value)}%`;
}

export function formatRating(value: number | null | undefined): string {
  return value === null || value === undefined ? '—' : value.toFixed(1);
}

export function formatCount(value: number | null | undefined): string {
  return value === null || value === undefined ? '—' : value.toLocaleString();
}

/** Plain text of an HTML fragment (for snippets and SVG labels). */
export function htmlToText(html: string | null | undefined): string {
  if (!html) return '';
  if (typeof DOMParser === 'undefined') return html.replace(/<[^>]*>/g, ' ').replace(/\s+/g, ' ').trim();
  const doc = new DOMParser().parseFromString(html, 'text/html');
  return (doc.body.textContent ?? '').replace(/\s+/g, ' ').trim();
}

export function truncate(text: string, max: number): string {
  return text.length > max ? `${text.slice(0, Math.max(0, max - 1)).trimEnd()}…` : text;
}

/** ISO timestamp → "YYYY-MM-DD" (local) for <sd-date-input type="date">. */
export function isoToDateValue(iso: string | null | undefined): string | null {
  if (!iso) return null;
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return null;
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/** "YYYY-MM-DD" → ISO timestamp at local midnight (null when empty). */
export function dateValueToIso(value: string | null | undefined): string | null {
  if (!value) return null;
  const d = new Date(`${value}T00:00:00`);
  return Number.isNaN(d.getTime()) ? null : d.toISOString();
}

export function isPast(iso: string | null | undefined): boolean {
  if (!iso) return false;
  const t = new Date(iso).getTime();
  return !Number.isNaN(t) && t < Date.now();
}

/** Single value from an sd-combobox change. */
export function comboOne(value: string | string[] | null): string | null {
  if (Array.isArray(value)) return value[0] ?? null;
  return value || null;
}

/** Multi value from an sd-combobox change. */
export function comboMany(value: string | string[] | null): string[] {
  if (Array.isArray(value)) return value;
  return value ? [value] : [];
}

export function errorMessage(err: unknown): string {
  return ApiError.from(err).userMessage;
}

/** True when the error was already toasted globally (network, 5xx, 403, 429). */
export function toastedGlobally(err: unknown): boolean {
  const e = ApiError.from(err);
  return e.isNetworkError || e.isServerError || e.status === 403 || e.status === 429;
}

/** Short unique id for flow nodes ("n" + 7 base-36 characters). */
export function newNodeId(existing: ReadonlySet<string>): string {
  const alphabet = 'abcdefghijklmnopqrstuvwxyz0123456789';
  for (;;) {
    const bytes = new Uint8Array(7);
    if (globalThis.crypto?.getRandomValues) globalThis.crypto.getRandomValues(bytes);
    else for (let i = 0; i < bytes.length; i++) bytes[i] = Math.floor(Math.random() * 256);
    let id = 'n';
    for (const b of bytes) id += alphabet[b % alphabet.length];
    if (!existing.has(id)) return id;
  }
}

/** Date stamp for export file names. */
export function fileStamp(date = new Date()): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}${pad(date.getMonth() + 1)}${pad(date.getDate())}-${pad(date.getHours())}${pad(date.getMinutes())}`;
}

// ───── Categories ─────

export interface CategoryNode {
  category: KbCategoryDto;
  depth: number;
  /** "Parent › Child" */
  path: string;
  childCount: number;
}

/** Depth-first ordering of the flat category list (children sorted by sortOrder, then name). */
export function flattenCategories(list: readonly KbCategoryDto[]): CategoryNode[] {
  const ids = new Set(list.map((c) => c.id));
  const children = new Map<string | null, KbCategoryDto[]>();
  for (const c of list) {
    const parent = c.parentId && ids.has(c.parentId) ? c.parentId : null;
    const bucket = children.get(parent) ?? [];
    bucket.push(c);
    children.set(parent, bucket);
  }
  for (const bucket of children.values()) bucket.sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name));
  const out: CategoryNode[] = [];
  const seen = new Set<string>();
  const walk = (parent: string | null, depth: number, prefix: string) => {
    for (const c of children.get(parent) ?? []) {
      if (seen.has(c.id)) continue; // defensive: cycles
      seen.add(c.id);
      const path = prefix ? `${prefix} › ${c.name}` : c.name;
      out.push({ category: c, depth, path, childCount: children.get(c.id)?.length ?? 0 });
      walk(c.id, depth + 1, path);
    }
  };
  walk(null, 0, '');
  return out;
}

/** Ids of a category and all of its descendants. */
export function categorySubtree(list: readonly KbCategoryDto[], rootId: string): Set<string> {
  const result = new Set<string>([rootId]);
  let grew = true;
  while (grew) {
    grew = false;
    for (const c of list) {
      if (c.parentId && result.has(c.parentId) && !result.has(c.id)) {
        result.add(c.id);
        grew = true;
      }
    }
  }
  return result;
}

export function categoryOptions(nodes: readonly CategoryNode[]): SdOption[] {
  return nodes.map((n) => ({ value: n.category.id, label: n.path, icon: n.category.icon && n.depth === 0 ? n.category.icon : null }));
}

// ───── Async state helper ─────

/**
 * Tiny signal-based async holder for one remote value: `load(() => api.x())` cancels the previous
 * request, tracks loading/error, and `reload()` repeats the last request (retry buttons).
 */
export class Remote<T> {
  readonly value = signal<T | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  private sub: Subscription | null = null;
  private last: (() => Observable<T>) | null = null;

  load(factory: () => Observable<T>, options: { keepValue?: boolean } = {}): void {
    this.last = factory;
    this.sub?.unsubscribe();
    this.loading.set(true);
    this.error.set(null);
    if (!options.keepValue) this.value.set(null);
    this.sub = factory().subscribe({
      next: (value) => {
        this.value.set(value);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.error.set(errorMessage(err));
        this.loading.set(false);
      },
    });
  }

  reload(): void {
    if (this.last) this.load(this.last, { keepValue: true });
  }

  set(value: T | null): void {
    this.value.set(value);
  }

  cancel(): void {
    this.sub?.unsubscribe();
    this.sub = null;
  }
}

/** Creates a {@link Remote} that cancels its request when the component is destroyed. */
export function remote<T>(): Remote<T> {
  const r = new Remote<T>();
  inject(DestroyRef).onDestroy(() => r.cancel());
  return r;
}

// ───── Shared styles (component `styles` arrays) ─────

/** Section/toolbars/tables/pager styling shared by the knowledge-base pages (tokens only). */
export const KB_STYLES = `
  .kb-head {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: var(--sd-space-3);
    margin-bottom: var(--sd-space-4);
  }
  .kb-head h2 {
    font-size: var(--sd-text-xl);
    font-weight: var(--sd-weight-semibold);
  }
  .kb-head .kb-head__titles {
    flex: 1 1 240px;
    min-width: 0;
  }
  .kb-head .kb-head__sub {
    margin-top: 2px;
    color: var(--sd-text-3);
    font-size: var(--sd-text-sm);
  }
  .kb-head .kb-head__actions {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: var(--sd-space-2);
  }
  .kb-toolbar {
    display: flex;
    flex-wrap: wrap;
    align-items: flex-end;
    gap: var(--sd-space-2) var(--sd-space-3);
    margin-bottom: var(--sd-space-3);
  }
  .kb-filter {
    display: grid;
    gap: 4px;
    min-width: 150px;
    flex: 0 1 190px;
  }
  .kb-filter--grow {
    flex: 1 1 260px;
  }
  .kb-filter > label,
  .kb-filter > .kb-filter__label {
    color: var(--sd-text-3);
    font-size: var(--sd-text-xs);
    font-weight: var(--sd-weight-medium);
  }
  .kb-search {
    position: relative;
  }
  .kb-search sd-icon {
    position: absolute;
    left: 9px;
    top: 50%;
    transform: translateY(-50%);
    color: var(--sd-text-3);
    pointer-events: none;
  }
  .kb-search input {
    padding-left: 30px;
  }
  .kb-table-wrap {
    overflow-x: auto;
    border: 1px solid var(--sd-border);
    border-radius: var(--sd-radius-lg);
    background: var(--sd-surface);
  }
  .kb-table {
    width: 100%;
    border-collapse: collapse;
    font-size: var(--sd-text-sm);
  }
  .kb-table th,
  .kb-table td {
    padding: 9px 12px;
    text-align: left;
    vertical-align: middle;
    border-bottom: 1px solid var(--sd-divider);
  }
  .kb-table th {
    background: var(--sd-surface-2);
    color: var(--sd-text-2);
    font-size: var(--sd-text-xs);
    font-weight: var(--sd-weight-semibold);
    text-transform: uppercase;
    letter-spacing: 0.035em;
    white-space: nowrap;
  }
  .kb-table tbody tr:last-child td {
    border-bottom: 0;
  }
  .kb-table tbody tr:hover {
    background: var(--sd-row-hover);
  }
  .kb-table .num {
    text-align: right;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }
  .kb-table a {
    font-weight: var(--sd-weight-medium);
  }
  .kb-error {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: var(--sd-space-2);
    padding: var(--sd-space-3) var(--sd-space-4);
    border: 1px solid var(--sd-danger-border);
    border-radius: var(--sd-radius-md);
    background: var(--sd-danger-bg);
    color: var(--sd-danger);
  }
  .kb-error p {
    flex: 1 1 200px;
  }
  .kb-note {
    display: flex;
    gap: var(--sd-space-2);
    padding: var(--sd-space-3);
    border: 1px solid var(--sd-warning-border);
    border-radius: var(--sd-radius-md);
    background: var(--sd-warning-bg);
    color: var(--sd-text);
  }
  .kb-note sd-icon {
    color: var(--sd-warning);
    flex: none;
    margin-top: 2px;
  }
  .kb-chips {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
  }
  .kb-meta {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 4px 12px;
    color: var(--sd-text-3);
    font-size: var(--sd-text-xs);
  }
  .kb-meta > span {
    display: inline-flex;
    align-items: center;
    gap: 4px;
  }
`;
