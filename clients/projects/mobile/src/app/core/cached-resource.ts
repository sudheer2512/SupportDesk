import { DestroyRef, Signal, computed, inject, signal } from '@angular/core';
import { ApiError } from '@sd/core';
import { Observable, Subscription } from 'rxjs';
import { NetworkService, isUnreachable } from './network.service';
import { CacheKind, OfflineCache } from './offline-cache.service';

export interface CachedResource<T> {
  /** Latest value (live, or from the offline cache). */
  readonly value: Signal<T | null>;
  /** A request is in flight. */
  readonly loading: Signal<boolean>;
  /** Last failure (only meaningful while `value` is null, or for a toast). */
  readonly error: Signal<ApiError | null>;
  /** When the value shown came from the offline cache: the time it was saved; null when it is live. */
  readonly staleSince: Signal<number | null>;
  /** Nothing to show yet and nothing in flight because we are offline with no cached copy. */
  readonly offlineEmpty: Signal<boolean>;
  /** Fetches (showing the cached copy first when there is nothing on screen). Resolves when settled. */
  load(): Promise<void>;
  /** Replaces the value locally (optimistic/realtime patch) and refreshes the cached copy. */
  set(value: T): void;
}

/**
 * Network-first data with an offline fallback: shows the cached copy immediately when the screen is empty,
 * refreshes from the API, and keeps (and marks stale) the cached copy when the server cannot be reached.
 * Call inside an injection context (a component field initializer).
 */
export function cachedResource<T>(options: {
  /** Cache key (null = do not cache, e.g. while a route param is missing). */
  key: () => string | null;
  kind: CacheKind;
  fetch: () => Observable<T>;
}): CachedResource<T> {
  const cache = inject(OfflineCache);
  const network = inject(NetworkService);
  const value = signal<T | null>(null);
  const loading = signal(false);
  const error = signal<ApiError | null>(null);
  const staleSince = signal<number | null>(null);
  let generation = 0;
  let subscription: Subscription | null = null;
  let loadedKey: string | null = null;
  let settlePrevious: (() => void) | null = null;
  inject(DestroyRef).onDestroy(() => subscription?.unsubscribe());

  const offlineEmpty = computed(() => value() === null && !loading() && !network.online());

  async function load(): Promise<void> {
    const gen = ++generation;
    const key = options.key();
    if (key !== loadedKey) {
      // A different record: never show the previous one's data.
      value.set(null);
      staleSince.set(null);
      loadedKey = key;
    }
    error.set(null);
    if (value() === null && key) {
      const cached = await cache.read<T>(key);
      if (gen !== generation) return;
      if (cached && value() === null) {
        value.set(cached.value);
        staleSince.set(cached.savedAt);
      }
    }
    loading.set(true);
    subscription?.unsubscribe();
    settlePrevious?.(); // a superseded load still resolves (pull-to-refresh spinners must stop)
    await new Promise<void>((resolve) => {
      settlePrevious = resolve;
      subscription = options.fetch().subscribe({
        next: (v) => {
          if (gen !== generation) return;
          value.set(v);
          staleSince.set(null);
          if (key) void cache.write(key, options.kind, v);
        },
        error: (e: unknown) => {
          if (gen === generation) {
            const err = ApiError.from(e);
            error.set(err);
            if (isUnreachable(err)) network.markUnreachable();
            loading.set(false);
          }
          resolve();
        },
        complete: () => {
          if (gen === generation) loading.set(false);
          resolve();
        },
      });
    });
  }

  function set(v: T): void {
    value.set(v);
    const key = options.key();
    if (key) void cache.write(key, options.kind, v);
  }

  return {
    value: value.asReadonly(),
    loading: loading.asReadonly(),
    error: error.asReadonly(),
    staleSince: staleSince.asReadonly(),
    offlineEmpty,
    load,
    set,
  };
}
