import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { Observable, Subject, tap } from 'rxjs';

/** HTTP statuses that mean "the server is not reachable" rather than "the server said no". */
export function isUnreachableStatus(status: number): boolean {
  return status === 0 || status === 502 || status === 503 || status === 504;
}

/**
 * Connectivity as the app experiences it: the browser/OS network flag AND whether the SupportDesk server answered
 * recently. A failed request (status 0 / 502–504) marks the server unreachable; any successful API response marks it
 * reachable again. `online()` drives the offline banner, read-only mode and the outbox.
 */
@Injectable({ providedIn: 'root' })
export class NetworkService {
  private readonly browserOnline = signal(typeof navigator === 'undefined' ? true : navigator.onLine !== false);
  private readonly serverReachable = signal(true);
  private readonly cameOnlineSubject = new Subject<void>();

  readonly online = computed(() => this.browserOnline() && this.serverReachable());
  /** Why we are offline, for the banner. */
  readonly reason = computed<'device' | 'server' | null>(() =>
    !this.browserOnline() ? 'device' : !this.serverReachable() ? 'server' : null,
  );
  /** Fires when connectivity is restored (flush the outbox, refetch screens). */
  readonly cameOnline$: Observable<void> = this.cameOnlineSubject.asObservable();

  constructor() {
    if (typeof window === 'undefined') return;
    const up = () => this.setBrowserOnline(true);
    const down = () => this.setBrowserOnline(false);
    window.addEventListener('online', up);
    window.addEventListener('offline', down);
    inject(DestroyRef).onDestroy(() => {
      window.removeEventListener('online', up);
      window.removeEventListener('offline', down);
    });
  }

  markReachable(): void {
    if (this.serverReachable()) return;
    const wasOnline = this.online();
    this.serverReachable.set(true);
    if (!wasOnline && this.online()) this.cameOnlineSubject.next();
  }

  markUnreachable(): void {
    this.serverReachable.set(false);
  }

  private setBrowserOnline(value: boolean): void {
    const wasOnline = this.online();
    this.browserOnline.set(value);
    // The OS says we are back: optimistically assume the server is too; the next request confirms it.
    if (value) this.serverReachable.set(true);
    if (!wasOnline && this.online()) this.cameOnlineSubject.next();
  }
}

/** Feeds every API response into NetworkService (reachable on success, unreachable on network-level failures). */
export const connectivityInterceptor: HttpInterceptorFn = (req, next) => {
  const network = inject(NetworkService);
  return next(req).pipe(
    tap({
      next: (event) => {
        if ('status' in event && typeof event.status === 'number' && event.status > 0) network.markReachable();
      },
      error: (error: unknown) => {
        if (error instanceof HttpErrorResponse) {
          if (isUnreachableStatus(error.status)) network.markUnreachable();
          else network.markReachable();
        }
      },
    }),
  );
};
