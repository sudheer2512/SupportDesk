import { Injectable, inject, signal } from '@angular/core';
import { DesktopNotifier, safeStorage } from '@sd/core';

const SOUND_KEY = 'sd.chat.sound';

/**
 * Attention signals for the live-chat console: a short two-tone chime (Web Audio — no audio asset to download) and
 * a desktop notification when a new chat starts waiting. Sound preference is remembered per browser.
 */
@Injectable({ providedIn: 'root' })
export class ChatAlertsService {
  private readonly notifier = inject(DesktopNotifier);
  private audio: AudioContext | null = null;

  readonly soundOn = signal(safeStorage.get(SOUND_KEY) !== 'off');
  readonly desktopPermission = this.notifier.permission;

  toggleSound(): void {
    this.soundOn.update((on) => !on);
    safeStorage.set(SOUND_KEY, this.soundOn() ? 'on' : 'off');
    if (this.soundOn()) this.chime();
  }

  enableDesktop(): Promise<unknown> {
    return this.notifier.requestPermission();
  }

  /** New waiting chat: chime + desktop notification (clicking it focuses the tab and runs `open`). */
  newChat(visitor: string, subject: string | null, open: () => void): void {
    if (this.soundOn()) this.chime();
    this.notifier.show({ title: `Chat waiting: ${visitor}`, body: subject ?? 'A visitor is waiting for an agent.', tag: `chat-${visitor}`, onClick: open });
  }

  private chime(): void {
    try {
      const Ctor = globalThis.AudioContext ?? (globalThis as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
      if (!Ctor) return;
      this.audio ??= new Ctor();
      const ctx = this.audio;
      if (ctx.state === 'suspended') void ctx.resume();
      const start = ctx.currentTime;
      for (const [offset, freq] of [
        [0, 880],
        [0.18, 1320],
      ] as const) {
        const osc = ctx.createOscillator();
        const gain = ctx.createGain();
        osc.type = 'sine';
        osc.frequency.value = freq;
        gain.gain.setValueAtTime(0.0001, start + offset);
        gain.gain.exponentialRampToValueAtTime(0.18, start + offset + 0.02);
        gain.gain.exponentialRampToValueAtTime(0.0001, start + offset + 0.25);
        osc.connect(gain).connect(ctx.destination);
        osc.start(start + offset);
        osc.stop(start + offset + 0.3);
      }
    } catch {
      /* audio blocked until the user interacts with the page — the visual alert still shows */
    }
  }
}
