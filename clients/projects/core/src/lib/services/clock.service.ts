import { DestroyRef, Injectable, inject, signal } from '@angular/core';

/**
 * Shared ticking clocks so live labels (SLA countdowns, "5m ago") update without per-component timers.
 * `now` ticks every second (countdowns); `coarseNow` every 30 s (relative times).
 */
@Injectable({ providedIn: 'root' })
export class ClockService {
  readonly now = signal(Date.now());
  readonly coarseNow = signal(Date.now());

  constructor() {
    const fine = setInterval(() => this.now.set(Date.now()), 1000);
    const coarse = setInterval(() => this.coarseNow.set(Date.now()), 30_000);
    inject(DestroyRef).onDestroy(() => {
      clearInterval(fine);
      clearInterval(coarse);
    });
  }
}
