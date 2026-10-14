import { HttpClient, HttpContext, HttpEvent, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';
import { SD_CONFIG, joinUrl } from '../config';
import { ApiError } from './api-error';
import { SILENT_ERRORS, SKIP_AUTH } from './http-context';
import { QueryParams, toHttpParams } from './query-params';

export interface ApiRequestOptions {
  params?: QueryParams | object | null;
  headers?: Record<string, string>;
  /** Suppress the global error toast (the caller shows its own feedback). */
  silent?: boolean;
  /** Do not attach the bearer token / do not attempt a refresh on 401. */
  skipAuth?: boolean;
  /** Send cookies on cross-origin calls (auth endpoints). */
  withCredentials?: boolean;
  context?: HttpContext;
}

/**
 * The only class that talks to HttpClient. Every method resolves paths against `SD_CONFIG.apiBaseUrl`
 * (default `/api/v1`) and turns failures into a typed {@link ApiError} (RFC 7807 problem details).
 * Feature code uses the per-module API clients (TicketsApi, MetaApi, …), never HttpClient directly.
 */
@Injectable({ providedIn: 'root' })
export class ApiClient {
  private readonly http = inject(HttpClient);
  private readonly config = inject(SD_CONFIG);

  /** Absolute API URL for a relative path (e.g. for <a href> downloads). */
  url(path: string): string {
    return joinUrl(this.config.apiBaseUrl, path);
  }

  get<T>(path: string, options: ApiRequestOptions = {}): Observable<T> {
    return this.http.get<T>(this.url(path), this.httpOptions(options)).pipe(mapError());
  }

  post<T>(path: string, body: unknown = {}, options: ApiRequestOptions = {}): Observable<T> {
    return this.http.post<T>(this.url(path), body, this.httpOptions(options)).pipe(mapError());
  }

  put<T>(path: string, body: unknown = {}, options: ApiRequestOptions = {}): Observable<T> {
    return this.http.put<T>(this.url(path), body, this.httpOptions(options)).pipe(mapError());
  }

  patch<T>(path: string, body: unknown = {}, options: ApiRequestOptions = {}): Observable<T> {
    return this.http.patch<T>(this.url(path), body, this.httpOptions(options)).pipe(mapError());
  }

  delete<T = void>(path: string, options: ApiRequestOptions = {}): Observable<T> {
    return this.http.delete<T>(this.url(path), this.httpOptions(options)).pipe(mapError());
  }

  /** multipart/form-data upload with progress events (requires the XHR backend, i.e. no withFetch()). */
  upload<T>(path: string, form: FormData, options: ApiRequestOptions = {}): Observable<HttpEvent<T>> {
    return this.http
      .post<T>(this.url(path), form, { ...this.httpOptions(options), reportProgress: true, observe: 'events' })
      .pipe(mapError());
  }

  /** Binary download (attachments, exports) — authenticated, so it cannot be a plain link. */
  blob(path: string, options: ApiRequestOptions = {}): Observable<Blob> {
    return this.http.get(this.url(path), { ...this.httpOptions(options), responseType: 'blob' }).pipe(mapError());
  }

  private httpOptions(options: ApiRequestOptions): {
    params: ReturnType<typeof toHttpParams>;
    headers?: HttpHeaders;
    context: HttpContext;
    withCredentials?: boolean;
  } {
    const context = options.context ?? new HttpContext();
    if (options.silent) context.set(SILENT_ERRORS, true);
    if (options.skipAuth) context.set(SKIP_AUTH, true);
    return {
      params: toHttpParams(options.params),
      headers: options.headers ? new HttpHeaders(options.headers) : undefined,
      context,
      withCredentials: options.withCredentials,
    };
  }
}

function mapError<T>() {
  return (source: Observable<T>) => source.pipe(catchError((err: unknown) => throwError(() => ApiError.from(err))));
}
