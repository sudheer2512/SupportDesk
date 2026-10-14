import { Injectable, Signal, signal } from '@angular/core';
import { EMPTY, Observable } from 'rxjs';

export type PushPermission = 'granted' | 'denied' | 'prompt' | 'unsupported';

/** A tap on a delivered notification: `link` is the server's deep link (e.g. `/tickets/{id}`). */
export interface PushTap {
  link: string | null;
  data: Record<string, string>;
}

/**
 * Native push seam. The app talks only to this abstraction; registration of the device token with the
 * SupportDesk server is done by PushRegistrar (`POST /me/devices` or `/portal/{brand}/devices`).
 *
 * The web/dev build provides {@link WebPushService} (push unsupported — notifications still arrive in-app over
 * SignalR). A native build provides an implementation backed by `@capacitor/push-notifications`
 * (see docs/MOBILE.md → "Push notifications" for the ready-to-paste class and the provider swap in app.config.ts).
 */
export abstract class PushService {
  /** Whether this build can receive OS push notifications at all. */
  abstract readonly supported: boolean;
  /** OS permission state. */
  abstract readonly permission: Signal<PushPermission>;
  /** The OS device token once registered (FCM token on Android, APNs token on iOS); null before/after. */
  abstract readonly token: Signal<string | null>;
  /** Taps on delivered notifications (open the linked screen). */
  abstract readonly taps$: Observable<PushTap>;
  /** Asks for permission when needed and registers with the OS push service; resolves with the token (or null). */
  abstract enable(): Promise<string | null>;
  /** Stops OS push for this install (the server registration is removed by PushRegistrar). */
  abstract disable(): Promise<void>;
  /** Reads the current permission/token without prompting (app start, resume). */
  abstract refresh(): Promise<void>;
}

/** Browser / dev-server build: no OS push. Everything still works through realtime and the notifications list. */
@Injectable()
export class WebPushService extends PushService {
  readonly supported = false;
  readonly permission = signal<PushPermission>('unsupported').asReadonly();
  readonly token = signal<string | null>(null).asReadonly();
  readonly taps$: Observable<PushTap> = EMPTY;

  async enable(): Promise<string | null> {
    return null;
  }

  async disable(): Promise<void> {
    /* nothing registered */
  }

  async refresh(): Promise<void> {
    /* nothing to read */
  }
}
