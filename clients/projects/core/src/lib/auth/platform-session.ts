import { Injectable, computed, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { PlatformLoginResponse } from '../models/platform';

/**
 * The SaaS platform operator session (platform console at /platform). Separate from the agent session: platform tokens
 * carry no tenant and only work on /api/v1/platform/*; tenant tokens are refused there. The access token is kept in
 * memory only (30-minute lifetime, no refresh token): reloading the page or expiry means signing in again.
 */
@Injectable({ providedIn: 'root' })
export class PlatformSession {
  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);
  private readonly token = signal<string | null>(null);
  private readonly expiresAt = signal<number | null>(null);
  private timer: ReturnType<typeof setTimeout> | null = null;

  readonly displayName = signal<string | null>(null);
  readonly signedIn = computed(() => !!this.token() && (this.expiresAt() ?? 0) > Date.now());
  readonly expiry = computed(() => this.expiresAt());

  accessToken(): string | null {
    const exp = this.expiresAt();
    return this.token() && exp && exp > Date.now() ? this.token() : null;
  }

  async login(email: string, password: string, code?: string | null): Promise<PlatformLoginResponse> {
    const response = await firstValueFrom(
      this.api.post<PlatformLoginResponse>('platform/auth/login', { email: email.trim(), password, code: code ?? null }, { skipAuth: true, silent: true }),
    );
    if (response.status === 'ok' && response.accessToken) {
      this.token.set(response.accessToken);
      const exp = response.expiresAt ? Date.parse(response.expiresAt) : Date.now() + 30 * 60_000;
      this.expiresAt.set(exp);
      this.displayName.set(response.displayName);
      if (this.timer) clearTimeout(this.timer);
      this.timer = setTimeout(() => this.handleExpired(), Math.max(1000, exp - Date.now()));
    }
    return response;
  }

  logout(): void {
    this.clear();
    void this.router.navigateByUrl('/platform/login');
  }

  /** Called on 401 from a platform endpoint or when the token lifetime ends. */
  handleExpired(): void {
    if (!this.token()) return;
    this.clear();
    void this.router.navigate(['/platform/login'], { queryParams: { expired: 1 } });
  }

  private clear(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    this.token.set(null);
    this.expiresAt.set(null);
    this.displayName.set(null);
  }
}

/** Platform console routes: a valid operator session is required (otherwise → /platform/login). */
export const platformGuard: CanActivateFn = (_route, state) => {
  const session = inject(PlatformSession);
  return session.signedIn() ? true : inject(Router).createUrlTree(['/platform/login'], { queryParams: { returnUrl: state.url } });
};
