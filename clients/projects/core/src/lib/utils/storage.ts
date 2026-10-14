/**
 * localStorage wrapper that never throws (private mode, blocked storage, quota). Only convenience
 * state lives here (theme, last tenant/email, table column prefs) — never tokens.
 */
export const safeStorage = {
  get(key: string): string | null {
    try {
      return globalThis.localStorage?.getItem(key) ?? null;
    } catch {
      return null;
    }
  },
  set(key: string, value: string): void {
    try {
      globalThis.localStorage?.setItem(key, value);
    } catch {
      /* storage unavailable — ignore */
    }
  },
  remove(key: string): void {
    try {
      globalThis.localStorage?.removeItem(key);
    } catch {
      /* ignore */
    }
  },
  getJson<T>(key: string): T | null {
    const raw = safeStorage.get(key);
    if (raw === null) return null;
    try {
      return JSON.parse(raw) as T;
    } catch {
      return null;
    }
  },
  setJson(key: string, value: unknown): void {
    try {
      safeStorage.set(key, JSON.stringify(value));
    } catch {
      /* unserialisable — ignore */
    }
  },
};

/** Namespaced keys used across the apps. */
export const STORAGE_KEYS = {
  theme: 'sd.theme',
  lastTenant: 'sd.login.tenant',
  lastEmail: 'sd.login.email',
  navCollapsed: 'sd.nav.collapsed',
  tablePrefix: 'sd.table.',
  splitPrefix: 'sd.split.',
  portalEmailPrefix: 'sd.portal.email.',
} as const;
