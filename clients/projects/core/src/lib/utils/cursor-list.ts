import { DestroyRef, Signal, computed, inject, signal } from '@angular/core';
import { Observable, Subscription } from 'rxjs';
import { ApiError } from '../http/api-error';
import { PagedResult } from '../models/common';

export type PageFetcher<T> = (cursor: string | null) => Observable<PagedResult<T>>;

/**
 * Signal-based keyset-paged list: `load()` fetches the first page, `loadMore()` appends the next one
 * using `nextCursor`. Stale responses (after a reload or fetcher change) are discarded. Supports local
 * upserts/removals so realtime events can patch the list in place.
 */
export class CursorList<T> {
  private readonly _items = signal<T[]>([]);
  private readonly _loading = signal(false);
  private readonly _loadingMore = signal(false);
  private readonly _error = signal<ApiError | null>(null);
  private readonly _nextCursor = signal<string | null>(null);
  private readonly _total = signal<number | null>(null);
  private readonly _loaded = signal(false);
  private generation = 0;
  private subscription: Subscription | null = null;

  readonly items: Signal<T[]> = this._items.asReadonly();
  /** First page in flight. */
  readonly loading: Signal<boolean> = this._loading.asReadonly();
  /** A further page in flight. */
  readonly loadingMore: Signal<boolean> = this._loadingMore.asReadonly();
  readonly error: Signal<ApiError | null> = this._error.asReadonly();
  readonly nextCursor: Signal<string | null> = this._nextCursor.asReadonly();
  readonly total: Signal<number | null> = this._total.asReadonly();
  /** At least one page has arrived (distinguishes "empty" from "not loaded yet"). */
  readonly loaded: Signal<boolean> = this._loaded.asReadonly();
  readonly hasMore = computed(() => this._nextCursor() !== null);
  readonly isEmpty = computed(() => this._loaded() && this._items().length === 0);

  constructor(
    private fetcher: PageFetcher<T>,
    private readonly keyOf: (item: T) => string = (item) => (item as { id?: string }).id ?? '',
  ) {}

  /** Swaps the query (e.g. new filters) and reloads from the first page. */
  setFetcher(fetcher: PageFetcher<T>): void {
    this.fetcher = fetcher;
    this.load();
  }

  /** (Re)loads the first page, replacing the items. */
  load(): void {
    this.fetch(null, false);
  }

  /** Appends the next page if there is one and nothing is in flight. */
  loadMore(): void {
    const cursor = this._nextCursor();
    if (cursor === null || this._loading() || this._loadingMore()) return;
    this.fetch(cursor, true);
  }

  /** Inserts or replaces an item (matched by key); new items go to the top unless `append`. */
  upsert(item: T, append = false): void {
    const key = this.keyOf(item);
    this._items.update((items) => {
      const index = items.findIndex((i) => this.keyOf(i) === key);
      if (index >= 0) {
        const copy = items.slice();
        copy[index] = item;
        return copy;
      }
      return append ? [...items, item] : [item, ...items];
    });
  }

  remove(key: string): void {
    this._items.update((items) => items.filter((i) => this.keyOf(i) !== key));
  }

  /** Cancels any request in flight. */
  destroy(): void {
    this.subscription?.unsubscribe();
    this.subscription = null;
  }

  private fetch(cursor: string | null, append: boolean): void {
    const generation = ++this.generation;
    this.subscription?.unsubscribe();
    (append ? this._loadingMore : this._loading).set(true);
    if (!append) this._loadingMore.set(false);
    this._error.set(null);

    this.subscription = this.fetcher(cursor).subscribe({
      next: (page) => {
        if (generation !== this.generation) return;
        this._items.update((items) => (append ? [...items, ...page.items] : page.items));
        this._nextCursor.set(page.nextCursor ?? null);
        if (page.total !== undefined && page.total !== null) this._total.set(page.total);
        else if (!append) this._total.set(null);
        this._loaded.set(true);
      },
      error: (err: unknown) => {
        if (generation !== this.generation) return;
        this._error.set(ApiError.from(err));
        this._loading.set(false);
        this._loadingMore.set(false);
        this._loaded.set(true);
      },
      complete: () => {
        if (generation !== this.generation) return;
        this._loading.set(false);
        this._loadingMore.set(false);
      },
    });
  }
}

/** Creates a CursorList that cancels its request when the calling component/service is destroyed. */
export function cursorList<T>(fetcher: PageFetcher<T>, keyOf?: (item: T) => string): CursorList<T> {
  const list = new CursorList<T>(fetcher, keyOf);
  inject(DestroyRef).onDestroy(() => list.destroy());
  return list;
}
