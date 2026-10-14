import { Guid, IsoDateTime } from './common';

/** Mirrors SupportDesk.Contracts.Mobile — push-device registration for the mobile app. */

export const DEVICE_PLATFORMS = ['Android', 'iOS', 'Web'] as const;
export type DevicePlatform = (typeof DEVICE_PLATFORMS)[number];

/** POST /me/devices and POST /portal/{brand}/devices */
export interface RegisterDeviceRequest {
  platform: DevicePlatform;
  token: string;
  appVersion?: string | null;
}

/** POST …/devices/unregister */
export interface UnregisterDeviceRequest {
  token: string;
}

/** A registered device; the token is never echoed back, only its last characters (`tokenHint`). */
export interface PushDeviceDto {
  id: Guid;
  platform: DevicePlatform | string;
  tokenHint: string;
  /** agent | customer */
  appKind: string;
  appVersion: string | null;
  isActive: boolean;
  registeredAt: IsoDateTime;
  lastSeenAt: IsoDateTime;
}
