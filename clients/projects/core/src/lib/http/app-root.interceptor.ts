import { HttpEventType, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { map } from 'rxjs';
import { SD_CONFIG, appRoot } from '../config';

/**
 * Hosting under a path base (IIS virtual directory or proxy sub-path, e.g. "/supportdesk/"): the API returns and stores
 * root-relative links ("/api/v1/files/…", `<img src="/api/v1/kb-assets/…">`). This interceptor maps them under the app
 * root in API responses and maps them back in request bodies, so stored content stays independent of where the app is
 * hosted. At a site root (the usual case) it does nothing.
 */
export const appRootInterceptor: HttpInterceptorFn = (req, next) => {
  const root = appRoot();
  if (root === '/') return next(req);
  const api = inject(SD_CONFIG).apiBaseUrl.replace(/\/$/, '');
  if (req.url !== api && !req.url.startsWith(`${api}/`)) return next(req);

  const outgoing = isPlainJson(req.body) ? req.clone({ body: deepMap(req.body, (s) => toStored(s, root)) }) : req;
  return next(outgoing).pipe(
    map((event) =>
      event.type === HttpEventType.Response && isPlainJson(event.body) ? event.clone({ body: deepMap(event.body, (s) => toHosted(s, root)) }) : event,
    ),
  );
};

/** Applies {@link toHosted} to a realtime (SignalR) payload, which does not pass through the HTTP interceptor. */
export function hostedPayload<T>(payload: T): T {
  const root = appRoot();
  return root === '/' ? payload : (deepMap(payload, (s) => toHosted(s, root)) as T);
}

/** "/api/v1/x" → "/supportdesk/api/v1/x"; HTML/Markdown references (`src="/api/v1/…"`, `(/api/v1/…)`) likewise. */
export function toHosted(value: string, root: string): string {
  if (!value.includes('/api/v1/')) return value;
  if (value.startsWith('/api/v1/')) return root + value.slice(1);
  return value.replace(/(["'(])\/api\/v1\//g, `$1${root}api/v1/`);
}

/** The reverse of {@link toHosted}, for content the client sends back (KB articles, templates, replies). */
export function toStored(value: string, root: string): string {
  const hosted = `${root}api/v1/`;
  if (!value.includes(hosted)) return value;
  if (value.startsWith(hosted)) return `/api/v1/${value.slice(hosted.length)}`;
  return value.split(hosted).join('/api/v1/');
}

function isPlainJson(body: unknown): body is object {
  if (body === null || typeof body !== 'object') return false;
  if (Array.isArray(body)) return true;
  const proto = Object.getPrototypeOf(body);
  return proto === Object.prototype || proto === null;
}

function deepMap(value: unknown, fn: (s: string) => string): unknown {
  if (typeof value === 'string') return fn(value);
  if (Array.isArray(value)) return value.map((v) => deepMap(v, fn));
  if (isPlainJson(value)) {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value)) out[k] = deepMap(v, fn);
    return out;
  }
  return value;
}
