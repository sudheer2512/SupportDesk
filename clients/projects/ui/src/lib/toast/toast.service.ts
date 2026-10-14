import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { ToastBridge, ToastKind, ToastRequest } from '@sd/core';

export interface SdToast extends ToastRequest {
  id: number;
  createdAt: number;
}

const DEFAULT_DURATION: Record<ToastKind, number> = { info: 5000, success: 4000, warning: 7000, danger: 9000 };
const MAX_VISIBLE = 5;

/**
 * Toast queue rendered by `<sd-toast-outlet />` (place it once in the app shell). Also relays toasts
 * raised by non-UI code through @sd/core's ToastBridge (interceptors, realtime, stores).
 * Toasts with the same `key` replace each other instead of stacking.
 */
@Injectable({ providedIn: 'root' })
export class ToastService {
  private readonly _toasts = signal<SdToast[]>([]);
  private readonly timers = new Map<number, ReturnType<typeof setTimeout>>();
  private nextId = 1;

  readonly toasts = this._toasts.asReadonly();

  constructor() {
    const sub = inject(ToastBridge).requests$.subscribe((r) => this.show(r));
    inject(DestroyRef).onDestroy(() => sub.unsubscribe());
  }

  show(request: ToastRequest): number {
    const id = this.nextId++;
    const toast: SdToast = { ...request, id, createdAt: Date.now() };
    this._toasts.update((list) => {
      const filtered = request.key ? list.filter((t) => t.key !== request.key) : list;
      for (const t of list) if (!filtered.includes(t)) this.clearTimer(t.id);
      return [...filtered, toast].slice(-MAX_VISIBLE);
    });
    this.startTimer(toast);
    return id;
  }

  info(title: string, message?: string): number {
    return this.show({ kind: 'info', title, message });
  }

  success(title: string, message?: string): number {
    return this.show({ kind: 'success', title, message });
  }

  warning(title: string, message?: string): number {
    return this.show({ kind: 'warning', title, message });
  }

  error(title: string, message?: string): number {
    return this.show({ kind: 'danger', title, message });
  }

  dismiss(id: number): void {
    this.clearTimer(id);
    this._toasts.update((list) => list.filter((t) => t.id !== id));
  }

  /** Pause auto-dismiss while hovered/focused (WCAG 2.2.1). */
  pause(id: number): void {
    this.clearTimer(id);
  }

  resume(id: number): void {
    const toast = this._toasts().find((t) => t.id === id);
    if (toast) this.startTimer(toast, 2500);
  }

  private startTimer(toast: SdToast, override?: number): void {
    const duration = override ?? toast.durationMs ?? DEFAULT_DURATION[toast.kind];
    if (duration <= 0) return;
    this.clearTimer(toast.id);
    this.timers.set(
      toast.id,
      setTimeout(() => this.dismiss(toast.id), duration),
    );
  }

  private clearTimer(id: number): void {
    const t = this.timers.get(id);
    if (t) clearTimeout(t);
    this.timers.delete(id);
  }
}
