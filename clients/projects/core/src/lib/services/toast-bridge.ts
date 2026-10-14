import { Injectable } from '@angular/core';
import { Observable, Subject } from 'rxjs';

export type ToastKind = 'info' | 'success' | 'warning' | 'danger';

export interface ToastRequest {
  kind: ToastKind;
  title: string;
  message?: string | null;
  /** Auto-dismiss after this many ms; 0 keeps it until dismissed. Defaults by kind in the UI. */
  durationMs?: number;
  /** Optional single action button. */
  action?: { label: string; run: () => void };
  /** De-duplication key: a toast with the same key replaces a visible one instead of stacking. */
  key?: string;
}

/**
 * Lets non-UI code (interceptors, stores, realtime) raise toasts without depending on @sd/ui.
 * The UI ToastService subscribes to `requests$` and renders them in the toast outlet.
 */
@Injectable({ providedIn: 'root' })
export class ToastBridge {
  private readonly subject = new Subject<ToastRequest>();
  readonly requests$: Observable<ToastRequest> = this.subject.asObservable();

  show(request: ToastRequest): void {
    this.subject.next(request);
  }

  info(title: string, message?: string): void {
    this.show({ kind: 'info', title, message });
  }

  success(title: string, message?: string): void {
    this.show({ kind: 'success', title, message });
  }

  warning(title: string, message?: string): void {
    this.show({ kind: 'warning', title, message });
  }

  error(title: string, message?: string): void {
    this.show({ kind: 'danger', title, message });
  }
}
