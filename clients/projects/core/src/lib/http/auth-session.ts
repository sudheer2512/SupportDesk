import { InjectionToken } from '@angular/core';

/**
 * What the HTTP interceptor needs from whichever session owns the access token. The desk and mobile
 * agent mode bind it to AuthService; the customer portal binds it to PortalAuthService.
 */
export interface AuthSession {
  /** Current in-memory access token (never persisted). */
  accessToken(): string | null;
  /** Refresh once (single-flight). Resolves true when a new access token is available. */
  refreshAccessToken(): Promise<boolean>;
  /** Called after a 401 that a refresh could not fix: clear state and go to the login page. */
  handleSessionExpired(): void;
}

export const SD_AUTH_SESSION = new InjectionToken<AuthSession>('SD_AUTH_SESSION');
