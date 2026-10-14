import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthStatus } from '../auth/auth.service';
import { SD_REFRESH_TOKEN_STORE } from '../config';
import { ApiError } from '../http/api-error';
import { AuthSession } from '../http/auth-session';
import { PortalLoginResponse, PortalMeDto, PortalRegisterRequest } from '../models/portal';
import { STORAGE_KEYS, safeStorage } from '../utils/storage';
import { PortalApi } from './portal.api';

/**
 * Customer-portal session — a separate token store from the agent AuthService. One brand at a time:
 * switching brand drops the session. Access token in memory only; refresh via the httpOnly cookie
 * (`POST /portal/{brand}/auth/refresh` with `X-Requested-With: SupportDesk`).
 */
@Injectable({ providedIn: 'root' })
export class PortalAuthService implements AuthSession {
  private readonly api = inject(PortalApi);
  private readonly router = inject(Router);
  private readonly refreshStore = inject(SD_REFRESH_TOKEN_STORE, { optional: true });

  private readonly token = signal<{ value: string; expiresAt: number } | null>(null);
  private readonly _brand = signal<string | null>(null);
  private readonly _contact = signal<PortalMeDto | null>(null);
  private readonly _status = signal<AuthStatus>('unknown');
  private refreshInFlight: Promise<boolean> | null = null;
  private refreshTimer: ReturnType<typeof setTimeout> | null = null;
  private readonly restored = new Map<string, Promise<void>>();

  readonly brand = this._brand.asReadonly();
  readonly contact = this._contact.asReadonly();
  readonly status = this._status.asReadonly();
  readonly isAuthenticated = computed(() => this._status() === 'authenticated');

  accessToken(): string | null {
    return this.token()?.value ?? null;
  }

  /** Selects the brand and restores its session once (guards await this). */
  ready(brand: string): Promise<void> {
    const key = brand.toLowerCase();
    if (this._brand() !== key) {
      this.clearLocal();
      this._brand.set(key);
      this.restored.clear();
    }
    let pending = this.restored.get(key);
    if (!pending) {
      pending = this.refreshAccessToken().then(() => undefined);
      this.restored.set(key, pending);
    }
    return pending;
  }

  rememberedEmail(brand: string): string {
    return safeStorage.get(STORAGE_KEYS.portalEmailPrefix + brand.toLowerCase()) ?? '';
  }

  async login(brand: string, email: string, password: string): Promise<PortalLoginResponse> {
    this.selectBrand(brand);
    this.remember(brand, email);
    return this.handle(await firstValueFrom(this.api.login(brand, email.trim(), password)));
  }

  async requestOtp(brand: string, email: string): Promise<PortalLoginResponse> {
    this.selectBrand(brand);
    this.remember(brand, email);
    return firstValueFrom(this.api.requestOtp(brand, email.trim()));
  }

  async verifyOtp(brand: string, email: string, code: string): Promise<PortalLoginResponse> {
    this.selectBrand(brand);
    return this.handle(await firstValueFrom(this.api.verifyOtp(brand, email.trim(), code.trim())));
  }

  /** Creates a portal account (brands with self-registration) and signs in on success (status "ok"). */
  async register(brand: string, request: PortalRegisterRequest): Promise<PortalLoginResponse> {
    this.selectBrand(brand);
    this.remember(brand, request.email);
    return this.handle(await firstValueFrom(this.api.register(brand, { ...request, email: request.email.trim() })));
  }

  /** Replaces the cached contact after a profile change. */
  updateContact(patch: Partial<PortalMeDto>): void {
    const current = this._contact();
    if (current) this._contact.set({ ...current, ...patch });
  }

  refreshAccessToken(): Promise<boolean> {
    if (this.refreshInFlight) return this.refreshInFlight;
    this.refreshInFlight = this.doRefresh().finally(() => (this.refreshInFlight = null));
    return this.refreshInFlight;
  }

  async logout(): Promise<void> {
    const brand = this._brand();
    if (brand) {
      try {
        await firstValueFrom(this.api.logout(brand));
      } catch {
        /* ends locally regardless */
      }
    }
    this.clearLocal();
    await this.router.navigate(brand ? [`/${brand}`] : ['/']);
  }

  /** Drops the local session without calling the API or navigating (used by hosts with their own login route). */
  clearSession(): void {
    this.clearLocal();
  }

  handleSessionExpired(): void {
    const brand = this._brand();
    this.clearLocal();
    if (brand) {
      void this.router.navigate([`/${brand}/login`], { queryParams: { returnUrl: this.router.url } });
    }
  }

  private async doRefresh(): Promise<boolean> {
    const brand = this._brand();
    if (!brand) return false;
    try {
      const stored = this.refreshStore ? await this.refreshStore.get() : null;
      const response = await firstValueFrom(this.api.refresh(brand, stored));
      if (this._brand() !== brand) return false;
      return this.handle(response).status === 'ok';
    } catch (error) {
      const err = ApiError.from(error);
      if (err.status >= 400 || this._status() !== 'authenticated') this.clearLocal(true);
      return false;
    }
  }

  private handle(response: PortalLoginResponse): PortalLoginResponse {
    if (response.status === 'ok' && response.accessToken) {
      const expiresAt = response.expiresAt ? Date.parse(response.expiresAt) : Number.NaN;
      this.token.set({ value: response.accessToken, expiresAt: Number.isNaN(expiresAt) ? Date.now() + 10 * 60_000 : expiresAt });
      this._contact.set(response.contact);
      this._status.set('authenticated');
      const brand = this._brand();
      if (brand && !this.restored.has(brand)) this.restored.set(brand, Promise.resolve());
      if (response.refreshToken && this.refreshStore) void this.refreshStore.set(response.refreshToken);
      this.scheduleRefresh();
    }
    return response;
  }

  private scheduleRefresh(): void {
    if (this.refreshTimer) clearTimeout(this.refreshTimer);
    const expiresAt = this.token()?.expiresAt;
    if (!expiresAt) return;
    this.refreshTimer = setTimeout(() => void this.refreshAccessToken(), Math.max(5_000, expiresAt - Date.now() - 60_000));
  }

  private selectBrand(brand: string): void {
    const key = brand.toLowerCase();
    if (this._brand() !== key) {
      this.clearLocal();
      this._brand.set(key);
      this.restored.clear();
    }
  }

  private remember(brand: string, email: string): void {
    safeStorage.set(STORAGE_KEYS.portalEmailPrefix + brand.toLowerCase(), email.trim());
  }

  private clearLocal(keepBrand = true): void {
    if (this.refreshTimer) clearTimeout(this.refreshTimer);
    this.refreshTimer = null;
    this.token.set(null);
    this._contact.set(null);
    this._status.set('anonymous');
    if (!keepBrand) this._brand.set(null);
    if (this.refreshStore) void this.refreshStore.set(null);
  }
}
