import { Guid, IsoDateTime } from './common';

/** Customer-portal contracts (GET /portal/{brand}/config, /portal/{brand}/auth/*). */

export interface PortalFeatures {
  kb: boolean;
  community: boolean;
  chat: boolean;
  aiAssistant: boolean;
  anonymousTickets: boolean;
  selfRegistration: boolean;
}

/** How contacts sign in to this brand's portal. */
export type PortalAuthMode = 'password' | 'otp' | 'both' | 'sso' | string;

export interface PortalConfigDto {
  brandSlug: string;
  name: string;
  logoUrl: string | null;
  primaryColor: string | null;
  accentColor: string | null;
  portalTitle: string | null;
  portalWelcome: string | null;
  /** Server-sanitised HTML; rendered through Angular's sanitizer as well. */
  footerHtml: string | null;
  supportEmail: string | null;
  supportPhone: string | null;
  locales: string[];
  features: PortalFeatures;
  authMode: PortalAuthMode;
}

export interface PortalLoginRequest {
  email: string;
  password: string;
}

export interface PortalOtpRequest {
  email: string;
}

export interface PortalOtpVerifyRequest {
  email: string;
  code: string;
}

export interface PortalRegisterRequest {
  fullName: string;
  email: string;
  password: string;
  company: string | null;
  phone: string | null;
}

export interface PortalMeDto {
  id: Guid;
  tenantId: Guid;
  brandSlug: string;
  fullName: string;
  email: string | null;
  customerId: Guid | null;
  customerName: string | null;
  locationId: Guid | null;
  locationName: string | null;
  accessScope: string;
  preferredLanguage: string;
  canViewContracts: boolean;
}

export interface PortalLoginResponse {
  /** "ok" on success; "otp_sent" after /auth/otp/request; other values carry `message`. */
  status: 'ok' | 'otp_sent' | string;
  accessToken: string | null;
  expiresAt: IsoDateTime | null;
  refreshToken: string | null;
  contact: PortalMeDto | null;
  demoCode?: string | null;
  message?: string | null;
}
