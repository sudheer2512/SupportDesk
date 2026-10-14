import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthApi } from '../api/auth.api';
import { SD_CONFIG, SD_REFRESH_TOKEN_STORE } from '../config';
import { ApiError } from '../http/api-error';
import { AuthSession } from '../http/auth-session';
import { LoginOkResponse, LoginResponse, MfaMethod } from '../models/auth';
import { STORAGE_KEYS, safeStorage } from '../utils/storage';
import { MeStore } from './me.store';

export type AuthStatus = 'unknown' | 'anonymous' | 'authenticated';

const REFRESH_LEAD_MS = 60_000;
const RETRY_AFTER_NETWORK_ERROR_MS = 15_000;

/**
 * Agent/admin session. The access token lives in memory only (a signal); the refresh token is an
 * httpOnly cookie (web) or, for mobile, kept by the provided RefreshTokenStore. Refreshes are
 * single-flight, happen proactively ~60 s before expiry, and on any 401 via the auth interceptor.
 */
@Injectable({ providedIn: 'root' })
export class AuthService implements AuthSession {
  private readonly authApi = inject(AuthApi);
  private readonly me = inject(MeStore);
  private readonly router = inject(Router);
  private readonly config = inject(SD_CONFIG);
  private readonly refreshStore = inject(SD_REFRESH_TOKEN_STORE, { optional: true });

  private readonly token = signal<{ value: string; expiresAt: number } | null>(null);
  private readonly _status = signal<AuthStatus>('unknown');
  private refreshInFlight: Promise<boolean> | null = null;
  private refreshTimer: ReturnType<typeof setTimeout> | null = null;
  private readyPromise: Promise<void> | null = null;

  readonly status = this._status.asReadonly();
  readonly isAuthenticated = computed(() => this._status() === 'authenticated');
  readonly expiresAt = computed(() => this.token()?.expiresAt ?? null);

  accessToken(): string | null {
    return this.token()?.value ?? null;
  }

  /** Resolves once the start-up session restore (refresh from cookie) has finished. Guards await it. */
  ready(): Promise<void> {
    this.readyPromise ??= this.refreshAccessToken().then(() => undefined);
    return this.readyPromise;
  }

  /** Last tenant slug + e-mail used on this device (convenience only; never credentials). */
  rememberedLogin(): { tenant: string; email: string } {
    return { tenant: safeStorage.get(STORAGE_KEYS.lastTenant) ?? '', email: safeStorage.get(STORAGE_KEYS.lastEmail) ?? '' };
  }

  async login(tenant: string, email: string, password: string): Promise<LoginResponse> {
    safeStorage.set(STORAGE_KEYS.lastTenant, tenant.trim());
    safeStorage.set(STORAGE_KEYS.lastEmail, email.trim());
    const response = await firstValueFrom(this.authApi.login({ tenant: tenant.trim(), email: email.trim(), password }));
    if (response.status === 'ok') await this.establish(response);
    return response;
  }

  async verifyMfa(challengeId: string, code: string, method: MfaMethod): Promise<LoginResponse> {
    const response = await firstValueFrom(this.authApi.verifyMfa({ challengeId, code: code.trim(), method }));
    if (response.status === 'ok') await this.establish(response);
    return response;
  }

  refreshAccessToken(): Promise<boolean> {
    if (this.refreshInFlight) return this.refreshInFlight;
    this.refreshInFlight = this.doRefresh().finally(() => (this.refreshInFlight = null));
    return this.refreshInFlight;
  }

  async logout(): Promise<void> {
    const refreshToken = this.refreshStore ? await this.refreshStore.get() : null;
    try {
      await firstValueFrom(this.authApi.logout(refreshToken));
    } catch {
      /* the local session ends regardless */
    }
    await this.clearLocal();
    await this.router.navigateByUrl(this.config.loginPath);
  }

  handleSessionExpired(): void {
    const wasSignedIn = this._status() === 'authenticated';
    void this.clearLocal();
    const current = this.router.url;
    if (!current.startsWith(this.config.loginPath)) {
      void this.router.navigate([this.config.loginPath], {
        queryParams: { returnUrl: current === '/' ? null : current, expired: wasSignedIn ? 1 : null },
      });
    }
  }

  private async doRefresh(): Promise<boolean> {
    try {
      const stored = this.refreshStore ? await this.refreshStore.get() : null;
      if (this.config.client === 'mobile' && !stored) {
        await this.clearLocal();
        return false;
      }
      const response = await firstValueFrom(this.authApi.refresh(stored));
      if (response?.status !== 'ok' || !response.accessToken) {
        await this.clearLocal();
        return false;
      }
      await this.establish(response);
      return true;
    } catch (error) {
      const err = ApiError.from(error);
      const sessionGone = err.status >= 400 && err.status < 500 && err.status !== 429;
      if (sessionGone || this._status() !== 'authenticated') {
        await this.clearLocal();
      } else {
        // Network or server hiccup while signed in: keep the current token and try again shortly.
        this.scheduleRefresh(RETRY_AFTER_NETWORK_ERROR_MS);
      }
      return false;
    }
  }

  private async establish(ok: LoginOkResponse): Promise<void> {
    const expiresAt = Date.parse(ok.expiresAt);
    this.token.set({ value: ok.accessToken, expiresAt: Number.isNaN(expiresAt) ? Date.now() + 10 * 60_000 : expiresAt });
    if (ok.refreshToken && this.refreshStore) await this.refreshStore.set(ok.refreshToken);
    this.me.set(ok.user);
    this._status.set('authenticated');
    this.scheduleRefresh();
  }

  private scheduleRefresh(delayOverride?: number): void {
    if (this.refreshTimer) clearTimeout(this.refreshTimer);
    const expiresAt = this.token()?.expiresAt;
    if (!expiresAt) return;
    const delay = delayOverride ?? Math.max(5_000, expiresAt - Date.now() - REFRESH_LEAD_MS);
    this.refreshTimer = setTimeout(() => void this.refreshAccessToken(), delay);
  }

  private async clearLocal(): Promise<void> {
    if (this.refreshTimer) clearTimeout(this.refreshTimer);
    this.refreshTimer = null;
    this.token.set(null);
    this.me.clear();
    this._status.set('anonymous');
    if (this.refreshStore) await this.refreshStore.set(null);
  }
}
