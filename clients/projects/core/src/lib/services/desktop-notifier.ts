import { Injectable, signal } from '@angular/core';

export type DesktopPermission = 'default' | 'granted' | 'denied' | 'unsupported';

/**
 * Thin wrapper over the browser Notification API. Permission must be requested from a user gesture
 * (e.g. the "Enable desktop alerts" button in the notifications panel).
 */
@Injectable({ providedIn: 'root' })
export class DesktopNotifier {
  readonly permission = signal<DesktopPermission>(currentPermission());

  get supported(): boolean {
    return typeof Notification !== 'undefined';
  }

  async requestPermission(): Promise<DesktopPermission> {
    if (!this.supported) return 'unsupported';
    try {
      const result = await Notification.requestPermission();
      this.permission.set(result);
      return result;
    } catch {
      this.permission.set(currentPermission());
      return this.permission();
    }
  }

  /** Shows a desktop notification when permitted; clicking focuses the tab and runs `onClick`. */
  show(options: { title: string; body?: string | null; tag?: string; onClick?: () => void }): void {
    if (!this.supported || Notification.permission !== 'granted') return;
    try {
      const n = new Notification(options.title, { body: options.body ?? undefined, tag: options.tag });
      n.onclick = () => {
        globalThis.focus?.();
        options.onClick?.();
        n.close();
      };
    } catch {
      /* some platforms only allow notifications from a service worker — ignore */
    }
  }
}

function currentPermission(): DesktopPermission {
  return typeof Notification === 'undefined' ? 'unsupported' : Notification.permission;
}
