import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, effect, inject } from '@angular/core';
import { Capacitor } from '@capacitor/core';
import { StatusBar, Style } from '@capacitor/status-bar';
import { ToastController } from '@ionic/angular/standalone';
import { ThemeService, ToastBridge, ToastKind } from '@sd/core';

/**
 * Light/dark for Ionic: follows the system by default, or the explicit choice from Profile (ThemeService persists it
 * and sets `data-theme` for the @sd tokens). Ionic's palette is switched with the `ion-palette-dark` class
 * (dark.class.css), and on devices the status-bar text colour follows.
 */
@Injectable({ providedIn: 'root' })
export class MobileThemeService {
  readonly theme = inject(ThemeService);
  private readonly document = inject(DOCUMENT);

  private readonly apply = effect(() => {
    const dark = this.theme.isDark();
    this.document.documentElement.classList.toggle('ion-palette-dark', dark);
    this.document.querySelector('meta[name="theme-color"]')?.setAttribute('content', dark ? '#12141c' : '#3343cf');
    if (Capacitor.isNativePlatform()) {
      void StatusBar.setStyle({ style: dark ? Style.Dark : Style.Light }).catch(() => undefined);
    }
  });
}

const COLORS: Record<ToastKind, string> = { info: 'medium', success: 'success', warning: 'warning', danger: 'danger' };

/** Ionic toasts for the app, plus the bridge for toasts raised by @sd/core services. */
@Injectable({ providedIn: 'root' })
export class MobileToasts {
  private readonly controller = inject(ToastController);
  private current: HTMLIonToastElement | null = null;

  constructor() {
    const sub = inject(ToastBridge).requests$.subscribe((t) => void this.show(t.message ? `${t.title}: ${t.message}` : t.title, t.kind));
    inject(DestroyRef).onDestroy(() => sub.unsubscribe());
  }

  async show(message: string, kind: ToastKind = 'info', durationMs = 3000): Promise<void> {
    try {
      await this.current?.dismiss();
    } catch {
      /* already gone */
    }
    const toast = await this.controller.create({
      message,
      color: COLORS[kind],
      duration: durationMs,
      // Top: never covers the tab bar or a composer.
      position: 'top',
      buttons: [{ text: 'Dismiss', role: 'cancel' }],
      cssClass: 'm-toast',
    });
    this.current = toast;
    await toast.present();
  }

  success(message: string): Promise<void> {
    return this.show(message, 'success');
  }

  error(message: string): Promise<void> {
    return this.show(message, 'danger', 5000);
  }
}
