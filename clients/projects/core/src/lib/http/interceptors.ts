import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, from, switchMap, throwError } from 'rxjs';
import { SD_CONFIG, SdCoreConfig } from '../config';
import { ApiError } from './api-error';
import { SD_AUTH_SESSION } from './auth-session';
import { AUTH_RETRIED, SILENT_ERRORS, SKIP_AUTH } from './http-context';
import { ToastBridge } from '../services/toast-bridge';

function isApiRequest(req: HttpRequest<unknown>, config: SdCoreConfig): boolean {
  const base = config.apiBaseUrl.replace(/\/$/, '');
  return req.url === base || req.url.startsWith(`${base}/`);
}

/**
 * Attaches `Authorization: Bearer <token>` (and `X-Client: mobile` for the mobile app) to API calls.
 * On a 401 it refreshes once (single-flight in the session) and retries; if that fails the session is
 * expired and the user is sent to the login page.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const config = inject(SD_CONFIG);
  const session = inject(SD_AUTH_SESSION, { optional: true });
  if (!isApiRequest(req, config)) return next(req);

  const base = config.client === 'mobile' ? req.clone({ setHeaders: { 'X-Client': 'mobile' } }) : req;
  if (!session || base.context.get(SKIP_AUTH)) return next(base);

  const withToken = (r: HttpRequest<unknown>) => {
    const token = session.accessToken();
    return token ? r.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : r;
  };

  return next(withToken(base)).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401 || base.context.get(AUTH_RETRIED)) {
        return throwError(() => error);
      }
      return from(session.refreshAccessToken()).pipe(
        switchMap((ok) => {
          if (!ok) {
            session.handleSessionExpired();
            return throwError(() => error);
          }
          const retry = base.clone({ context: base.context.set(AUTH_RETRIED, true) });
          return next(withToken(retry));
        }),
      );
    }),
  );
};

/**
 * Global feedback for failures nobody can fix locally: network errors, 5xx, 403 and 429 raise a toast
 * (unless the request opted out with `silent`). 4xx validation/conflict errors are left to the caller.
 */
export const errorToastInterceptor: HttpInterceptorFn = (req, next) => {
  const config = inject(SD_CONFIG);
  const toasts = inject(ToastBridge);
  if (!isApiRequest(req, config) || req.context.get(SILENT_ERRORS)) return next(req);

  return next(req).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse) {
        const api = ApiError.from(error);
        if (api.status === 0) {
          toasts.show({ kind: 'danger', title: 'Connection problem', message: api.userMessage, key: 'network' });
        } else if (api.status >= 500) {
          toasts.show({
            kind: 'danger',
            title: 'Something went wrong on the server',
            message: api.traceId ? `${api.userMessage} (trace ${api.traceId})` : api.userMessage,
            key: `5xx:${api.title}`,
          });
        } else if (api.status === 403 && !api.is('tenant_suspended')) {
          toasts.show({ kind: 'warning', title: 'Not allowed', message: api.detail ?? 'You do not have permission to do that.', key: '403' });
        } else if (api.is('tenant_suspended')) {
          toasts.show({ kind: 'danger', title: 'Account suspended', message: api.userMessage, key: 'suspended' });
        } else if (api.status === 429) {
          toasts.show({ kind: 'warning', title: 'Slow down', message: 'Too many requests — please wait a moment and try again.', key: '429' });
        }
      }
      return throwError(() => error);
    }),
  );
};
