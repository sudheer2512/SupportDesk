import { Guid, IdName, IsoDateTime } from './common';
import { Availability } from './enums';

/** Mirrors SupportDesk.Contracts.Auth (agents/admins). */

export interface LoginRequest {
  tenant: string;
  email: string;
  password: string;
}

export type MfaMethod = 'totp' | 'email';

export interface LoginOkResponse {
  status: 'ok';
  accessToken: string;
  expiresAt: IsoDateTime;
  /** Only for mobile clients (header `X-Client: mobile`); web clients get an httpOnly cookie. */
  refreshToken?: string | null;
  user: MeDto;
}

export interface LoginMfaRequiredResponse {
  status: 'mfa_required';
  challengeId: Guid;
  methods: MfaMethod[];
  /** Demo/dev environments only: the code that was "sent". Display it labelled as a demo code. */
  demoCode?: string | null;
  message?: string | null;
}

export interface LoginPasswordExpiredResponse {
  status: 'password_expired';
  message?: string | null;
}

export type LoginResponse = LoginOkResponse | LoginMfaRequiredResponse | LoginPasswordExpiredResponse;

export interface MfaVerifyRequest {
  challengeId: Guid;
  code: string;
  method: MfaMethod;
}

export interface RefreshRequest {
  refreshToken: string | null;
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

export interface MfaSetupDto {
  secret: string;
  otpAuthUri: string;
}

export interface MfaEnableRequest {
  code: string;
}

export interface PasswordResetRequest {
  tenant: string;
  email: string;
}

export interface PasswordResetConfirmRequest {
  tenant: string;
  email: string;
  code: string;
  newPassword: string;
}

/** Agent/admin kinds; the API may add more. */
export type UserKind = 'Agent' | 'Admin' | 'LightAgent' | 'System' | string;

/** The signed-in agent/admin with effective permissions (the UI hides actions; the API enforces them). */
export interface MeDto {
  id: Guid;
  tenantId: Guid;
  tenantSlug: string;
  tenantName: string;
  displayName: string;
  email: string;
  avatarUrl: string | null;
  timeZone: string;
  locale: string;
  kind: UserKind;
  isLightAgent: boolean;
  permissions: string[];
  ticketScope: string;
  departments: IdName[];
  teams: IdName[];
  availability: Availability;
  planFeatures: string[];
  subscriptionStatus: string;
  mfaEnabled: boolean;
  signature: string | null;
}

export interface UpdateAvailabilityRequest {
  availability: Availability;
}

export interface UpdateProfileRequest {
  displayName: string;
  timeZone: string;
  locale: string;
  signature: string | null;
}
