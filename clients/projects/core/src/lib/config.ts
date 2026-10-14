import { EnvironmentProviders, InjectionToken, makeEnvironmentProviders } from '@angular/core';

/** Runtime configuration of @sd/core. Apps override it through provideSdCore({...}). */
export interface SdCoreConfig {
  /** Base URL of the REST API, absolute or same-origin relative (default `/api/v1`). */
  apiBaseUrl: string;
  /** SignalR hub URL for the agent workbench (default `/hubs/desk`). */
  hubUrl: string;
  /** SignalR hub URL for portal contacts (default `/hubs/portal`; the mobile app points it at the server origin). */
  portalHubUrl: string;
  /** `mobile` adds `X-Client: mobile` to API calls and keeps the refresh token in the body (see RefreshTokenStore). */
  client: 'web' | 'mobile';
  /** Route of the agent login page (used after a failed refresh). */
  loginPath: string;
  /** Route of the friendly 403 page used by permissionGuard. */
  forbiddenPath: string;
}

export const DEFAULT_SD_CONFIG: SdCoreConfig = {
  apiBaseUrl: '/api/v1',
  hubUrl: '/hubs/desk',
  portalHubUrl: '/hubs/portal',
  client: 'web',
  loginPath: '/login',
  forbiddenPath: '/forbidden',
};

export const SD_CONFIG = new InjectionToken<SdCoreConfig>('SD_CONFIG', {
  providedIn: 'root',
  factory: () => DEFAULT_SD_CONFIG,
});

/**
 * Persists the refresh token for clients that cannot use the httpOnly cookie (mobile: Capacitor Preferences).
 * Web apps do not provide it — the server keeps the refresh token in a cookie scoped to /api/v1/auth.
 */
export interface RefreshTokenStore {
  get(): Promise<string | null>;
  set(token: string | null): Promise<void>;
}

export const SD_REFRESH_TOKEN_STORE = new InjectionToken<RefreshTokenStore>('SD_REFRESH_TOKEN_STORE');

/** Registers @sd/core configuration. Call once in the app's `providers`. */
export function provideSdCore(config: Partial<SdCoreConfig> = {}): EnvironmentProviders {
  return makeEnvironmentProviders([{ provide: SD_CONFIG, useValue: { ...DEFAULT_SD_CONFIG, ...config } }]);
}

/** Joins the API base with a relative path ("tickets/1" or "/tickets/1"). Absolute URLs pass through. */
export function joinUrl(base: string, path: string): string {
  if (/^https?:\/\//i.test(path)) return path;
  const b = base.endsWith('/') ? base.slice(0, -1) : base;
  const p = path.startsWith('/') ? path : `/${path}`;
  return `${b}${p}`;
}
