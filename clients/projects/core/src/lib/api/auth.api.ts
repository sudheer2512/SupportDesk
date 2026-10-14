import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import {
  ChangePasswordRequest,
  LoginOkResponse,
  LoginRequest,
  LoginResponse,
  MfaSetupDto,
  MfaVerifyRequest,
} from '../models/auth';

/** Header the API requires on cookie-authenticated refresh calls (CSRF defence). */
export const CSRF_HEADER = { 'X-Requested-With': 'SupportDesk' } as const;

/**
 * Raw /auth endpoints for agents/admins. Use AuthService for the session lifecycle; this client only
 * maps HTTP calls (login/mfa/refresh skip the bearer and send cookies).
 */
@Injectable({ providedIn: 'root' })
export class AuthApi {
  private readonly api = inject(ApiClient);

  login(request: LoginRequest): Observable<LoginResponse> {
    return this.api.post<LoginResponse>('auth/login', request, { skipAuth: true, silent: true, withCredentials: true });
  }

  verifyMfa(request: MfaVerifyRequest): Observable<LoginResponse> {
    return this.api.post<LoginResponse>('auth/mfa', request, { skipAuth: true, silent: true, withCredentials: true });
  }

  /** Cookie refresh (web) or body refresh (mobile). 401 → the session is over. */
  refresh(refreshToken: string | null): Observable<LoginOkResponse> {
    return this.api.post<LoginOkResponse>('auth/refresh', refreshToken ? { refreshToken } : {}, {
      skipAuth: true,
      silent: true,
      withCredentials: true,
      headers: { ...CSRF_HEADER },
    });
  }

  logout(refreshToken: string | null): Observable<void> {
    return this.api.post<void>('auth/logout', refreshToken ? { refreshToken } : {}, {
      silent: true,
      withCredentials: true,
      headers: { ...CSRF_HEADER },
    });
  }

  changePassword(request: ChangePasswordRequest): Observable<void> {
    return this.api.post<void>('auth/change-password', request, { silent: true });
  }

  mfaSetup(): Observable<MfaSetupDto> {
    return this.api.post<MfaSetupDto>('auth/mfa/setup', {});
  }

  mfaEnable(code: string): Observable<void> {
    return this.api.post<void>('auth/mfa/enable', { code }, { silent: true });
  }

  mfaDisable(): Observable<void> {
    return this.api.post<void>('auth/mfa/disable', {});
  }
}
