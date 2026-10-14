import { Injectable, inject } from '@angular/core';
import { Observable, catchError, shareReplay, throwError } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { PortalConfigDto, PortalLoginResponse, PortalRegisterRequest } from '../models/portal';
import { CSRF_HEADER } from '../api/auth.api';

const enc = encodeURIComponent;

/**
 * Public + auth endpoints of the customer portal (`/portal/{brand}/…`). The brand config is cached per
 * slug for the page lifetime. Use PortalAuthService for the session lifecycle.
 */
@Injectable({ providedIn: 'root' })
export class PortalApi {
  private readonly api = inject(ApiClient);
  private readonly configCache = new Map<string, Observable<PortalConfigDto>>();

  config(brand: string): Observable<PortalConfigDto> {
    const key = brand.toLowerCase();
    let cached = this.configCache.get(key);
    if (!cached) {
      cached = this.api.get<PortalConfigDto>(`portal/${enc(key)}/config`, { skipAuth: true, silent: true }).pipe(
        catchError((err: unknown) => {
          this.configCache.delete(key);
          return throwError(() => err);
        }),
        shareReplay({ bufferSize: 1, refCount: false }),
      );
      this.configCache.set(key, cached);
    }
    return cached;
  }

  login(brand: string, email: string, password: string): Observable<PortalLoginResponse> {
    return this.authPost(brand, 'login', { email, password });
  }

  requestOtp(brand: string, email: string): Observable<PortalLoginResponse> {
    return this.authPost(brand, 'otp/request', { email });
  }

  verifyOtp(brand: string, email: string, code: string): Observable<PortalLoginResponse> {
    return this.authPost(brand, 'otp/verify', { email, code });
  }

  /** Self-registration (only when the brand allows it). */
  register(brand: string, body: PortalRegisterRequest): Observable<PortalLoginResponse> {
    return this.authPost(brand, 'register', body);
  }

  refresh(brand: string, refreshToken: string | null): Observable<PortalLoginResponse> {
    return this.api.post<PortalLoginResponse>(`portal/${enc(brand)}/auth/refresh`, refreshToken ? { refreshToken } : {}, {
      skipAuth: true,
      silent: true,
      withCredentials: true,
      headers: { ...CSRF_HEADER },
    });
  }

  logout(brand: string): Observable<void> {
    return this.api.post<void>(`portal/${enc(brand)}/auth/logout`, {}, { silent: true, withCredentials: true, headers: { ...CSRF_HEADER } });
  }

  private authPost(brand: string, path: string, body: unknown): Observable<PortalLoginResponse> {
    return this.api.post<PortalLoginResponse>(`portal/${enc(brand)}/auth/${path}`, body, {
      skipAuth: true,
      silent: true,
      withCredentials: true,
    });
  }
}
