import { DestroyRef, Injectable, effect, inject, signal, untracked } from '@angular/core';
import { Router } from '@angular/router';
import { Capacitor } from '@capacitor/core';
import { ApiError, DevicePlatform, DevicesApi } from '@sd/core';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { mobileRouteFor } from './links';
import { ModeService } from './mobile-session';
import { PushService } from './push.service';

/** Maps Capacitor's platform name to the API's DevicePlatform. */
export function devicePlatform(): DevicePlatform {
  const p = Capacitor.getPlatform();
  return p === 'ios' ? 'iOS' : p === 'android' ? 'Android' : 'Web';
}

/**
 * Keeps the server's device registration in step with the OS push token: registers after sign-in (when permission
 * was granted before), re-registers when the OS rotates the token, unregisters before sign-out, and routes
 * notification taps to the matching screen. Agents use `/me/devices`, customers `/portal/{brand}/devices`.
 */
@Injectable({ providedIn: 'root' })
export class PushRegistrar {
  private readonly push = inject(PushService);
  private readonly devices = inject(DevicesApi);
  private readonly mode = inject(ModeService);
  private readonly router = inject(Router);
  private registered: { token: string; mode: string } | null = null;
  private live = false;

  readonly supported = this.push.supported;
  readonly permission = this.push.permission;
  readonly lastError = signal<string | null>(null);
  readonly registeredOnServer = signal(false);

  constructor() {
    const sub = this.push.taps$.subscribe((tap) => {
      const route = mobileRouteFor(tap.link ?? tap.data['link'] ?? null, this.mode.mode());
      if (route) void this.router.navigateByUrl(route);
    });
    inject(DestroyRef).onDestroy(() => sub.unsubscribe());
    // The OS rotated the token (or registration finished asynchronously): tell the server.
    effect(() => {
      const token = this.push.token();
      untracked(() => {
        if (token && this.live) void this.register(token);
      });
    });
  }

  /** Called whenever the session becomes live (sign-in, restore, resume): registers silently if permitted. */
  async sync(live: boolean): Promise<void> {
    this.live = live;
    if (!this.push.supported || !live) return;
    await this.push.refresh();
    if (this.push.permission() !== 'granted') return;
    const token = this.push.token() ?? (await this.push.enable());
    if (token) await this.register(token);
  }

  /** User turned push on (Profile): prompts for permission and registers. */
  async enable(): Promise<boolean> {
    if (!this.push.supported) return false;
    const token = await this.push.enable();
    if (!token) {
      this.lastError.set(this.push.permission() === 'denied' ? 'Notifications are blocked in the system settings.' : 'Push could not be enabled.');
      return false;
    }
    await this.register(token);
    return this.registeredOnServer();
  }

  /** User turned push off: stop server pushes, then the OS registration. */
  async disable(): Promise<void> {
    await this.unregister();
    await this.push.disable();
  }

  /** Before sign-out (while the session is still valid). */
  async unregister(): Promise<void> {
    const token = this.registered?.token ?? this.push.token();
    if (!token) return;
    try {
      if (this.mode.mode() === 'agent') await firstValueFrom(this.devices.unregister(token));
      else await firstValueFrom(this.devices.unregisterForContact(this.mode.brand(), token));
    } catch {
      /* best effort — the server also drops tokens that FCM/APNs report as dead */
    }
    this.registered = null;
    this.registeredOnServer.set(false);
  }

  private async register(token: string): Promise<void> {
    const mode = this.mode.mode();
    if (!this.live || (this.registered?.token === token && this.registered.mode === mode)) return;
    const request = { platform: devicePlatform(), token, appVersion: environment.appVersion };
    try {
      if (mode === 'agent') await firstValueFrom(this.devices.register(request));
      else await firstValueFrom(this.devices.registerForContact(this.mode.brand(), request));
      this.registered = { token, mode };
      this.registeredOnServer.set(true);
      this.lastError.set(null);
    } catch (e) {
      this.lastError.set(ApiError.from(e).userMessage);
    }
  }
}
