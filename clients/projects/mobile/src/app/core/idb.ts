/**
 * Minimal promise wrapper over IndexedDB for the mobile app's offline data (no dependency).
 * Three object stores:
 *  - `cache`  — recently opened tickets, lists and KB articles (`{ key, scope, kind, savedAt, value }`)
 *  - `outbox` — queued replies / notes / new tickets waiting to be sent (`{ id, scope, … }`)
 *  - `drafts` — composer text that survives app restarts (`{ key, scope, … }`)
 * When IndexedDB is unavailable (private browsing, old WebView) an in-memory fallback keeps the app working
 * for the session — nothing is persisted then, but nothing throws either.
 */

export type StoreName = 'cache' | 'outbox' | 'drafts';

const DB_NAME = 'sd-mobile';
const DB_VERSION = 1;
const KEY_PATHS: Record<StoreName, string> = { cache: 'key', outbox: 'id', drafts: 'key' };

export interface KeyValueStore {
  get<T>(store: StoreName, key: string): Promise<T | undefined>;
  put<T extends object>(store: StoreName, value: T): Promise<void>;
  delete(store: StoreName, key: string): Promise<void>;
  /** All rows of a store whose `scope` equals the given value. */
  byScope<T>(store: StoreName, scope: string): Promise<T[]>;
  /** Deletes every row of a store whose `scope` equals the given value. */
  clearScope(store: StoreName, scope: string): Promise<void>;
}

class IdbStore implements KeyValueStore {
  private dbPromise: Promise<IDBDatabase> | null = null;

  constructor(private readonly factory: IDBFactory) {}

  private db(): Promise<IDBDatabase> {
    this.dbPromise ??= new Promise<IDBDatabase>((resolve, reject) => {
      const request = this.factory.open(DB_NAME, DB_VERSION);
      request.onupgradeneeded = () => {
        const db = request.result;
        for (const name of Object.keys(KEY_PATHS) as StoreName[]) {
          if (!db.objectStoreNames.contains(name)) {
            const store = db.createObjectStore(name, { keyPath: KEY_PATHS[name] });
            store.createIndex('scope', 'scope', { unique: false });
          }
        }
      };
      request.onsuccess = () => {
        const db = request.result;
        // Another tab upgraded the schema: drop our handle so the next call reopens.
        db.onversionchange = () => {
          db.close();
          this.dbPromise = null;
        };
        resolve(db);
      };
      request.onerror = () => {
        this.dbPromise = null;
        reject(request.error ?? new Error('IndexedDB unavailable'));
      };
      request.onblocked = () => reject(new Error('IndexedDB upgrade blocked'));
    });
    return this.dbPromise;
  }

  private async run<T>(store: StoreName, mode: IDBTransactionMode, action: (s: IDBObjectStore) => IDBRequest | void): Promise<T> {
    const db = await this.db();
    return new Promise<T>((resolve, reject) => {
      const tx = db.transaction(store, mode);
      const request = action(tx.objectStore(store));
      let result: T;
      if (request) request.onsuccess = () => (result = request.result as T);
      tx.oncomplete = () => resolve(result);
      tx.onerror = () => reject(tx.error ?? new Error('IndexedDB transaction failed'));
      tx.onabort = () => reject(tx.error ?? new Error('IndexedDB transaction aborted'));
    });
  }

  get<T>(store: StoreName, key: string): Promise<T | undefined> {
    return this.run<T | undefined>(store, 'readonly', (s) => s.get(key));
  }

  async put<T extends object>(store: StoreName, value: T): Promise<void> {
    await this.run<unknown>(store, 'readwrite', (s) => s.put(value));
  }

  async delete(store: StoreName, key: string): Promise<void> {
    await this.run<unknown>(store, 'readwrite', (s) => s.delete(key));
  }

  byScope<T>(store: StoreName, scope: string): Promise<T[]> {
    return this.run<T[]>(store, 'readonly', (s) => s.index('scope').getAll(IDBKeyRange.only(scope)));
  }

  async clearScope(store: StoreName, scope: string): Promise<void> {
    await this.run<unknown>(store, 'readwrite', (s) => {
      const cursorRequest = s.index('scope').openKeyCursor(IDBKeyRange.only(scope));
      cursorRequest.onsuccess = () => {
        const cursor = cursorRequest.result;
        if (!cursor) return;
        s.delete(cursor.primaryKey);
        cursor.continue();
      };
    });
  }
}

/** Session-only fallback with the same contract. */
class MemoryStore implements KeyValueStore {
  private readonly stores = new Map<StoreName, Map<string, Record<string, unknown>>>();

  private map(store: StoreName): Map<string, Record<string, unknown>> {
    let m = this.stores.get(store);
    if (!m) this.stores.set(store, (m = new Map()));
    return m;
  }

  async get<T>(store: StoreName, key: string): Promise<T | undefined> {
    return this.map(store).get(key) as T | undefined;
  }

  async put<T extends object>(store: StoreName, value: T): Promise<void> {
    const row = value as Record<string, unknown>;
    this.map(store).set(String(row[KEY_PATHS[store]]), row);
  }

  async delete(store: StoreName, key: string): Promise<void> {
    this.map(store).delete(key);
  }

  async byScope<T>(store: StoreName, scope: string): Promise<T[]> {
    return [...this.map(store).values()].filter((r) => r['scope'] === scope) as T[];
  }

  async clearScope(store: StoreName, scope: string): Promise<void> {
    const m = this.map(store);
    for (const [key, row] of m) if (row['scope'] === scope) m.delete(key);
  }
}

/**
 * Opens the IndexedDB store, falling back to memory if it cannot be opened (checked once, lazily).
 * Every method resolves — callers treat storage as best effort.
 */
export class OfflineDatabase implements KeyValueStore {
  private backend: KeyValueStore;
  private readonly memory = new MemoryStore();
  private failed = false;

  constructor() {
    const factory = typeof indexedDB === 'undefined' ? null : indexedDB;
    this.backend = factory ? new IdbStore(factory) : this.memory;
  }

  /** True when data only lives in memory (no IndexedDB). */
  get volatile(): boolean {
    return this.failed || this.backend === this.memory;
  }

  private async safe<T>(action: (store: KeyValueStore) => Promise<T>): Promise<T> {
    try {
      return await action(this.backend);
    } catch {
      if (!this.failed) {
        this.failed = true;
        this.backend = this.memory;
      }
      return action(this.memory);
    }
  }

  get<T>(store: StoreName, key: string): Promise<T | undefined> {
    return this.safe((s) => s.get<T>(store, key));
  }

  put<T extends object>(store: StoreName, value: T): Promise<void> {
    return this.safe((s) => s.put(store, value));
  }

  delete(store: StoreName, key: string): Promise<void> {
    return this.safe((s) => s.delete(store, key));
  }

  byScope<T>(store: StoreName, scope: string): Promise<T[]> {
    return this.safe((s) => s.byScope<T>(store, scope));
  }

  clearScope(store: StoreName, scope: string): Promise<void> {
    return this.safe((s) => s.clearScope(store, scope));
  }
}
