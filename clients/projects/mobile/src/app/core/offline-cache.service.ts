import { Injectable, inject } from '@angular/core';
import { OfflineDatabase } from './idb';
import { SessionSnapshotStore } from './session-snapshot';

/** What a cached row holds; `kind` drives how many rows of that sort are kept. */
export type CacheKind = 'ticket' | 'list' | 'article' | 'meta';

interface CacheRow<T> {
  key: string;
  scope: string;
  kind: CacheKind;
  savedAt: number;
  value: T;
}

export interface CachedValue<T> {
  value: T;
  savedAt: number;
}

/** One shared database handle for the cache, outbox and drafts. */
@Injectable({ providedIn: 'root' })
export class OfflineDb {
  readonly db = new OfflineDatabase();
}

/** Most-recent rows kept per kind and identity (older ones are evicted on write). */
const LIMITS: Record<CacheKind, number> = { ticket: 40, list: 20, article: 30, meta: 20 };

/**
 * Read-through cache of API responses for offline viewing, scoped to the signed-in identity (an agent never sees
 * another agent's or a customer's cached data on a shared device). Keeps the most recently used rows per kind.
 * Sign-out clears the identity's cache.
 */
@Injectable({ providedIn: 'root' })
export class OfflineCache {
  private readonly db = inject(OfflineDb).db;
  private readonly session = inject(SessionSnapshotStore);
  private readonly writes = new Map<CacheKind, number>();

  async read<T>(key: string): Promise<CachedValue<T> | null> {
    const scope = this.session.scope();
    if (!scope) return null;
    const row = await this.db.get<CacheRow<T>>('cache', `${scope}|${key}`);
    return row ? { value: row.value, savedAt: row.savedAt } : null;
  }

  async write<T>(key: string, kind: CacheKind, value: T): Promise<void> {
    const scope = this.session.scope();
    if (!scope) return;
    await this.db.put<CacheRow<T>>('cache', { key: `${scope}|${key}`, scope, kind, savedAt: Date.now(), value });
    // Trim occasionally rather than on every write.
    const n = (this.writes.get(kind) ?? 0) + 1;
    this.writes.set(kind, n);
    if (n % 5 === 1) await this.trim(scope, kind);
  }

  async remove(key: string): Promise<void> {
    const scope = this.session.scope();
    if (scope) await this.db.delete('cache', `${scope}|${key}`);
  }

  /** Drops everything cached for the current identity (sign-out). */
  async clearCurrent(): Promise<void> {
    const scope = this.session.scope();
    if (scope) await this.db.clearScope('cache', scope);
  }

  private async trim(scope: string, kind: CacheKind): Promise<void> {
    const rows = (await this.db.byScope<CacheRow<unknown>>('cache', scope)).filter((r) => r.kind === kind);
    if (rows.length <= LIMITS[kind]) return;
    rows.sort((a, b) => b.savedAt - a.savedAt);
    for (const old of rows.slice(LIMITS[kind])) await this.db.delete('cache', old.key);
  }
}
