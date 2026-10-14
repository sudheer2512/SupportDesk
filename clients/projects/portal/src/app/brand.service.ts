import { DOCUMENT } from '@angular/common';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Title } from '@angular/platform-browser';
import { ApiError, PortalApi, PortalConfigDto, PortalFeatures } from '@sd/core';
import { firstValueFrom } from 'rxjs';

export type BrandLoadState = 'idle' | 'loading' | 'ready' | 'not-found' | 'error';

/**
 * The current brand's portal configuration (GET /portal/{brand}/config). Applies the brand colours as
 * CSS variables on <html> (with a readable on-primary colour) and the portal title as document title.
 */
@Injectable({ providedIn: 'root' })
export class BrandService {
  private readonly api = inject(PortalApi);
  private readonly document = inject(DOCUMENT);
  private readonly title = inject(Title);

  private readonly _config = signal<PortalConfigDto | null>(null);
  readonly config = this._config.asReadonly();
  readonly state = signal<BrandLoadState>('idle');
  readonly slug = signal<string | null>(null);
  readonly name = computed(() => this._config()?.portalTitle || this._config()?.name || 'Support');
  /** Router path of the brand home, e.g. "/acme" (prefix for every portal link). */
  readonly base = computed(() => `/${this.slug() ?? ''}`);
  /** Locales the brand publishes content in (first = default). */
  readonly locales = computed(() => this._config()?.locales ?? []);
  readonly features = computed<PortalFeatures>(
    () =>
      this._config()?.features ?? {
        kb: false,
        community: false,
        chat: false,
        aiAssistant: false,
        anonymousTickets: false,
        selfRegistration: false,
      },
  );

  /** Loads (once per slug) and applies the brand. Resolves when done; never throws. */
  async load(slug: string): Promise<void> {
    const key = slug.toLowerCase();
    if (this.slug() === key && (this.state() === 'ready' || this.state() === 'loading')) return;
    this.slug.set(key);
    this.state.set('loading');
    try {
      const config = await firstValueFrom(this.api.config(key));
      if (this.slug() !== key) return;
      this._config.set(config);
      this.apply(config);
      this.state.set('ready');
    } catch (err) {
      if (this.slug() !== key) return;
      this._config.set(null);
      this.clearColors();
      this.state.set(ApiError.from(err).status === 404 ? 'not-found' : 'error');
    }
  }

  setPageTitle(page: string | null): void {
    this.title.setTitle(page ? `${page} · ${this.name()}` : this.name());
  }

  private apply(config: PortalConfigDto): void {
    const root = this.document.documentElement;
    if (config.primaryColor && isColor(config.primaryColor)) {
      root.style.setProperty('--brand-primary', config.primaryColor);
      root.style.setProperty('--brand-on-primary', readableOn(config.primaryColor));
    } else {
      root.style.removeProperty('--brand-primary');
      root.style.removeProperty('--brand-on-primary');
    }
    if (config.accentColor && isColor(config.accentColor)) root.style.setProperty('--brand-accent', config.accentColor);
    else root.style.removeProperty('--brand-accent');
    const lang = config.locales?.[0];
    if (lang) root.lang = lang;
    this.setPageTitle(null);
  }

  private clearColors(): void {
    const root = this.document.documentElement;
    for (const p of ['--brand-primary', '--brand-on-primary', '--brand-accent']) root.style.removeProperty(p);
  }
}

function isColor(value: string): boolean {
  return /^#([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$/i.test(value.trim()) || /^(rgb|hsl|oklch)a?\(/i.test(value.trim());
}

/** White or near-black text, whichever contrasts better with a hex brand colour. */
function readableOn(color: string): string {
  const hex = color.trim().replace('#', '');
  if (!/^[0-9a-f]{3}([0-9a-f]{3})?([0-9a-f]{2})?$/i.test(hex)) return '#ffffff';
  const full = hex.length === 3 ? hex.split('').map((c) => c + c).join('') : hex.slice(0, 6);
  const [r, g, b] = [0, 2, 4].map((i) => {
    const c = parseInt(full.slice(i, i + 2), 16) / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  const luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
  const contrastWhite = 1.05 / (luminance + 0.05);
  const contrastDark = (luminance + 0.05) / 0.06;
  return contrastWhite >= contrastDark ? '#ffffff' : '#111318';
}
