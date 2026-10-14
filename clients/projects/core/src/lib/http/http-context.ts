import { HttpContext, HttpContextToken } from '@angular/common/http';

/** Do not attach the bearer token and never try a refresh (login, refresh, public portal config…). */
export const SKIP_AUTH = new HttpContextToken<boolean>(() => false);

/** Suppress the global error toast for this request (the caller shows its own feedback). */
export const SILENT_ERRORS = new HttpContextToken<boolean>(() => false);

/** Internal: set on the retried request after a token refresh so we never loop. */
export const AUTH_RETRIED = new HttpContextToken<boolean>(() => false);

export function httpContext(options: { skipAuth?: boolean; silent?: boolean } = {}): HttpContext {
  const ctx = new HttpContext();
  if (options.skipAuth) ctx.set(SKIP_AUTH, true);
  if (options.silent) ctx.set(SILENT_ERRORS, true);
  return ctx;
}
