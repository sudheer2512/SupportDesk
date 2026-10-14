import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, computed, effect, inject, signal } from '@angular/core';
import { STORAGE_KEYS, safeStorage } from '../utils/storage';

export type ThemePreference = 'system' | 'light' | 'dark';
export type EffectiveTheme = 'light' | 'dark';

/**
 * Light/dark theme. `system` follows prefers-color-scheme (no attribute on <html>); `light`/`dark` set
 * `data-theme` on <html>, which the @sd/ui tokens honour. The preference is remembered in localStorage.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly document = inject(DOCUMENT);
  private readonly media = this.document.defaultView?.matchMedia?.('(prefers-color-scheme: dark)') ?? null;
  private readonly systemDark = signal(this.media?.matches ?? false);

  readonly preference = signal<ThemePreference>(readPreference());
  readonly effective = computed<EffectiveTheme>(() => {
    const pref = this.preference();
    return pref === 'system' ? (this.systemDark() ? 'dark' : 'light') : pref;
  });
  readonly isDark = computed(() => this.effective() === 'dark');

  constructor() {
    const onChange = (e: MediaQueryListEvent) => this.systemDark.set(e.matches);
    this.media?.addEventListener?.('change', onChange);
    inject(DestroyRef).onDestroy(() => this.media?.removeEventListener?.('change', onChange));

    effect(() => {
      const pref = this.preference();
      const root = this.document.documentElement;
      if (pref === 'system') root.removeAttribute('data-theme');
      else root.setAttribute('data-theme', pref);
      root.style.colorScheme = this.effective();
    });
  }

  set(preference: ThemePreference): void {
    this.preference.set(preference);
    if (preference === 'system') safeStorage.remove(STORAGE_KEYS.theme);
    else safeStorage.set(STORAGE_KEYS.theme, preference);
  }

  /** Flips between light and dark (an explicit choice overrides the system preference). */
  toggle(): void {
    this.set(this.effective() === 'dark' ? 'light' : 'dark');
  }
}

function readPreference(): ThemePreference {
  const stored = safeStorage.get(STORAGE_KEYS.theme);
  return stored === 'light' || stored === 'dark' ? stored : 'system';
}
