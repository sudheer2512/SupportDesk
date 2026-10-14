import { Injectable, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Preferences } from '@capacitor/preferences';
import { AuthService, AuthSession, PortalAuthService, RefreshTokenStore, safeStorage } from '@sd/core';

export type AppMode = 'agent' | 'customer';

const MODE_KEY = 'sd.mobile.mode';
const BRAND_KEY = 'sd.mobile.brand';

/** Which side of SupportDesk the app is signed into (agent workbench or customer portal) + the portal brand. */
@Injectable({ providedIn: 'root' })
export class ModeService {
  readonly mode = signal<AppMode>(safeStorage.get(MODE_KEY) === 'customer' ? 'customer' : 'agent');
  readonly brand = signal<string>(safeStorage.get(BRAND_KEY) ?? '');

  setMode(mode: AppMode): void {
    this.mode.set(mode);
    safeStorage.set(MODE_KEY, mode);
  }

  setBrand(brand: string): void {
    this.brand.set(brand.trim().toLowerCase());
    safeStorage.set(BRAND_KEY, this.brand());
  }
}

/**
 * Refresh tokens on mobile come back in the response body (X-Client: mobile) and are kept in Capacitor
 * Preferences, one slot per mode so agent and customer sessions never overwrite each other.
 */
@Injectable({ providedIn: 'root' })
export class MobileRefreshTokenStore implements RefreshTokenStore {
  private readonly mode = inject(ModeService);

  private key(): string {
    return `sd.mobile.refresh.${this.mode.mode()}`;
  }

  async get(): Promise<string | null> {
    try {
      return (await Preferences.get({ key: this.key() })).value;
    } catch {
      return null;
    }
  }

  async set(token: string | null): Promise<void> {
    try {
      if (token) await Preferences.set({ key: this.key(), value: token });
      else await Preferences.remove({ key: this.key() });
    } catch {
      /* storage unavailable */
    }
  }
}

/** Routes the HTTP interceptor to the agent or customer session depending on the current mode. */
@Injectable({ providedIn: 'root' })
export class MobileSession implements AuthSession {
  private readonly mode = inject(ModeService);
  private readonly agent = inject(AuthService);
  private readonly customer = inject(PortalAuthService);
  private readonly router = inject(Router);

  accessToken(): string | null {
    return this.mode.mode() === 'agent' ? this.agent.accessToken() : this.customer.accessToken();
  }

  refreshAccessToken(): Promise<boolean> {
    return this.mode.mode() === 'agent' ? this.agent.refreshAccessToken() : this.customer.refreshAccessToken();
  }

  handleSessionExpired(): void {
    if (this.mode.mode() === 'agent') {
      this.agent.handleSessionExpired();
    } else {
      this.customer.clearSession();
      void this.router.navigateByUrl('/login');
    }
  }
}
