import { ApiError, FilterGroup, JsonValue, ValidationIssueDto, emptyFilterGroup } from '@sd/core';
import { SdTone } from '@sd/ui';

/** Validation errors keyed by definition path ("actions[0].params.url"), from an ApiError or a validation result. */
export type IssueMap = Record<string, string>;

export function issuesFromError(err: unknown): IssueMap {
  if (!(err instanceof ApiError)) return {};
  const out: IssueMap = {};
  for (const [k, v] of Object.entries(err.errors as Record<string, unknown>)) {
    out[k] = Array.isArray(v) ? String(v[0] ?? '') : String(v);
  }
  return out;
}

export function issuesFromList(list: ValidationIssueDto[]): IssueMap {
  const out: IssueMap = {};
  for (const i of list) out[i.path] = out[i.path] ? `${out[i.path]} ${i.message}` : i.message;
  return out;
}

/** Issues whose path starts with `prefix` (e.g. "actions[2]"), with the prefix removed. */
export function issuesUnder(issues: IssueMap, prefix: string): IssueMap {
  const out: IssueMap = {};
  for (const [k, v] of Object.entries(issues)) {
    if (k === prefix) out[''] = v;
    else if (k.startsWith(prefix + '.') || k.startsWith(prefix + '[')) out[k.slice(prefix.length).replace(/^\./, '')] = v;
  }
  return out;
}

export function errorMessage(err: unknown, fallback = 'Something went wrong.'): string {
  if (err instanceof ApiError) return err.userMessage;
  return err instanceof Error ? err.message : fallback;
}

export function executionTone(status: string): SdTone {
  switch (status) {
    case 'Succeeded':
      return 'success';
    case 'PartiallyFailed':
      return 'warning';
    case 'Failed':
      return 'danger';
    case 'SkippedLoop':
      return 'accent';
    case 'Simulated':
      return 'info';
    default:
      return 'neutral';
  }
}

export function actionTone(status: string): SdTone {
  switch (status) {
    case 'succeeded':
      return 'success';
    case 'failed':
      return 'danger';
    case 'queued':
      return 'info';
    case 'would_run':
      return 'primary';
    default:
      return 'neutral';
  }
}

export function humanStatus(status: string): string {
  return status.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/_/g, ' ');
}

/** Normalises a stored condition group (server omits empty arrays only when null). */
export function asFilterGroup(value: unknown): FilterGroup {
  if (!value || typeof value !== 'object') return emptyFilterGroup();
  const g = value as Partial<FilterGroup>;
  return {
    logic: g.logic === 'or' ? 'or' : 'and',
    conditions: Array.isArray(g.conditions) ? g.conditions.map((c) => ({ field: c.field, op: c.op, value: c.value ?? null })) : [],
    groups: Array.isArray(g.groups) ? g.groups.map(asFilterGroup) : [],
  };
}

export function prettyJson(value: unknown): string {
  return JSON.stringify(value ?? null, null, 2);
}

export interface DiffLine {
  kind: 'same' | 'add' | 'remove';
  text: string;
}

/** Line diff of two texts (LCS; definitions are small). */
export function diffLines(before: string, after: string): DiffLine[] {
  const a = before.split('\n');
  const b = after.split('\n');
  const n = a.length;
  const m = b.length;
  const lcs: number[][] = Array.from({ length: n + 1 }, () => new Array<number>(m + 1).fill(0));
  for (let i = n - 1; i >= 0; i--)
    for (let j = m - 1; j >= 0; j--) lcs[i][j] = a[i] === b[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1]);
  const out: DiffLine[] = [];
  let i = 0;
  let j = 0;
  while (i < n && j < m) {
    if (a[i] === b[j]) {
      out.push({ kind: 'same', text: a[i] });
      i++;
      j++;
    } else if (lcs[i + 1][j] >= lcs[i][j + 1]) out.push({ kind: 'remove', text: a[i++] });
    else out.push({ kind: 'add', text: b[j++] });
  }
  while (i < n) out.push({ kind: 'remove', text: a[i++] });
  while (j < m) out.push({ kind: 'add', text: b[j++] });
  return out;
}

/** Deep clone of plain JSON data. */
export function clone<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}

/** Removes null/undefined members recursively (the server omits them when storing definitions). */
export function stripNulls(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(stripNulls);
  if (value && typeof value === 'object') {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) if (v !== null && v !== undefined) out[k] = stripNulls(v);
    return out;
  }
  return value;
}

export function isJsonObject(v: JsonValue | undefined): v is { [key: string]: JsonValue } {
  return !!v && typeof v === 'object' && !Array.isArray(v);
}
