/**
 * Small fuzzy matcher for the command palette and searchable pickers. Every query character must appear
 * in order; contiguous runs, word starts and early matches score higher. Returns null when no match.
 */
export interface FuzzyMatch {
  score: number;
  /** Indexes in `text` that matched (for highlighting). */
  indexes: number[];
}

export function fuzzyMatch(query: string, text: string): FuzzyMatch | null {
  const q = query.trim().toLowerCase();
  if (!q) return { score: 0, indexes: [] };
  const t = text.toLowerCase();

  // Exact substring gets a strong bonus.
  const sub = t.indexOf(q);
  if (sub >= 0) {
    const indexes = Array.from({ length: q.length }, (_, i) => sub + i);
    const wordStart = sub === 0 || /[\s\-_/.›>]/.test(t[sub - 1]);
    return { score: 1000 - sub + (wordStart ? 200 : 0) - t.length * 0.1, indexes };
  }

  const indexes: number[] = [];
  let score = 0;
  let ti = 0;
  let prev = -2;
  for (const ch of q) {
    if (ch === ' ') continue;
    const found = t.indexOf(ch, ti);
    if (found < 0) return null;
    const wordStart = found === 0 || /[\s\-_/.›>]/.test(t[found - 1]);
    score += 10 + (found === prev + 1 ? 15 : 0) + (wordStart ? 20 : 0) - Math.min(found - ti, 10);
    indexes.push(found);
    prev = found;
    ti = found + 1;
  }
  return { score: score - t.length * 0.1, indexes };
}

/** Filters and ranks items by fuzzy score over one or more text fields. */
export function fuzzyFilter<T>(items: readonly T[], query: string, text: (item: T) => string | string[], limit = 50): T[] {
  if (!query.trim()) return items.slice(0, limit);
  const scored: { item: T; score: number }[] = [];
  for (const item of items) {
    const fields = text(item);
    let best: number | null = null;
    for (const field of Array.isArray(fields) ? fields : [fields]) {
      const m = fuzzyMatch(query, field);
      if (m && (best === null || m.score > best)) best = m.score;
    }
    if (best !== null) scored.push({ item, score: best });
  }
  return scored
    .sort((a, b) => b.score - a.score)
    .slice(0, limit)
    .map((s) => s.item);
}
