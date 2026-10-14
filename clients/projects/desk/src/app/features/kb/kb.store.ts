import { Injectable, computed, inject, signal } from '@angular/core';
import { KbCategoryDto, KbOptionsDto, KnowledgeApi, MeStore } from '@sd/core';
import { SdOption } from '@sd/ui';
import { Observable, finalize, shareReplay, tap } from 'rxjs';
import { EMPTY_KB_OPTIONS, categoryOptions, errorMessage, flattenCategories } from './kb-common';

/**
 * Cached knowledge-base reference data shared by the KB screens: picker options (`/kb/options`) and the
 * category list. Loaded on first use and re-fetched when the signed-in user (or tenant) changes.
 */
@Injectable({ providedIn: 'root' })
export class KbStore {
  private readonly api = inject(KnowledgeApi);
  private readonly me = inject(MeStore);

  private readonly _options = signal<KbOptionsDto | null>(null);
  private readonly _categories = signal<KbCategoryDto[] | null>(null);
  readonly optionsError = signal<string | null>(null);
  readonly categoriesError = signal<string | null>(null);
  readonly categoriesLoading = signal(false);

  private optionsKey: string | null = null;
  private categoriesKey: string | null = null;
  private categories$: Observable<KbCategoryDto[]> | null = null;

  /** Picker options (empty lists until loaded). */
  readonly options = computed(() => this._options() ?? EMPTY_KB_OPTIONS);
  readonly optionsLoaded = computed(() => this._options() !== null);
  readonly categories = computed(() => this._categories() ?? []);
  readonly categoriesLoaded = computed(() => this._categories() !== null);
  readonly categoryTree = computed(() => flattenCategories(this.categories()));
  readonly categoryOptions = computed(() => categoryOptions(this.categoryTree()));

  readonly ownerOptions = computed<SdOption[]>(() => this.options().owners.map((o) => ({ value: o.id, label: o.name, avatar: o.name })));
  readonly productOptions = computed<SdOption[]>(() => this.options().products.map((p) => ({ value: p.id, label: p.name, icon: 'package' })));
  readonly brandOptions = computed<SdOption[]>(() => this.options().brands.map((b) => ({ value: b.id, label: b.name })));
  readonly groupOptions = computed<SdOption[]>(() => this.options().customerGroups.map((g) => ({ value: g.id, label: g.name, icon: 'users' })));
  readonly tagOptions = computed<SdOption[]>(() => this.options().tags.map((t) => ({ value: t, label: t, icon: 'tag' })));

  private key(): string {
    const u = this.me.user();
    return u ? `${u.tenantId}:${u.id}` : '';
  }

  /** Loads the picker options once per signed-in user. */
  ensureOptions(): void {
    const key = this.key();
    if (this.optionsKey === key) return;
    this.optionsKey = key;
    this._options.set(null);
    this.optionsError.set(null);
    this.api.options().subscribe({
      next: (o) => {
        if (this.optionsKey === key) this._options.set(o);
      },
      error: (err: unknown) => {
        if (this.optionsKey !== key) return;
        this.optionsKey = null; // allow a retry
        this.optionsError.set(errorMessage(err));
      },
    });
  }

  /** Loads the categories once per signed-in user. */
  ensureCategories(): void {
    if (this.categoriesKey === this.key()) return;
    this.refreshCategories().subscribe({ error: () => undefined });
  }

  /** Re-fetches the categories (after create/edit/delete or an import). */
  refreshCategories(): Observable<KbCategoryDto[]> {
    const key = this.key();
    if (this.categories$ && this.categoriesKey === key) return this.categories$;
    this.categoriesKey = key;
    this.categoriesLoading.set(true);
    this.categoriesError.set(null);
    const request = this.api.categories().pipe(
      tap({
        next: (list) => {
          if (this.categoriesKey === key) this._categories.set(list);
        },
        error: (err: unknown) => {
          if (this.categoriesKey !== key) return;
          this.categoriesKey = null;
          this.categoriesError.set(errorMessage(err));
        },
      }),
      finalize(() => {
        this.categoriesLoading.set(false);
        this.categories$ = null;
      }),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    this.categories$ = request;
    return request;
  }

  /** Forces both caches to reload on next use (e.g. after an import created categories). */
  invalidate(): void {
    this.optionsKey = null;
    this.categoriesKey = null;
  }

  /** Locally patches the cached category list (after a save) so pickers update immediately. */
  upsertCategory(category: KbCategoryDto): void {
    this._categories.update((list) => {
      const current = list ?? [];
      const index = current.findIndex((c) => c.id === category.id);
      return index >= 0 ? current.map((c) => (c.id === category.id ? category : c)) : [...current, category];
    });
  }

  removeCategory(id: string): void {
    this._categories.update((list) => (list ?? []).filter((c) => c.id !== id));
  }
}
