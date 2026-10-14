import { DestroyRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { Router } from '@angular/router';
import { App } from '@capacitor/app';
import {
  AuthService,
  MeStore,
  NotificationsApi,
  PortalAuthService,
  PortalRealtimeService,
  PortalTicketsApi,
  RealtimeService,
} from '@sd/core';
import { Observable, Subject, firstValueFrom, merge } from 'rxjs';
import { DraftStore } from './drafts.service';
import { AppMode, MobileRefreshTokenStore, ModeService } from './mobile-session';
import { NetworkService } from './network.service';
import { OfflineCache } from './offline-cache.service';
import { OutboxService } from './outbox.service';
import { PushRegistrar } from './push-registrar';
import { SessionSnapshotStore } from './session-snapshot';

/** How often an offline app checks whether the server is back. */
const PROBE_MS = 15_000;

/**
 * Glue between authentication, connectivity and the offline features:
 * - persists a snapshot of the signed-in identity (offline scope) and opens an "offline session" from it when the
 *   app starts without a network (read-only cached views until the stored refresh token restores the session);
 * - probes the server while offline, then flushes the outbox and asks screens to refetch;
 * - on app resume (Capacitor `resume`): reconnects SignalR, re-syncs push, flushes the outbox, refetches;
 * - signs out cleanly (push unregister, local data of the identity removed).
 */
@Injectable({ providedIn: 'root' })
export class SessionCoordinator {
  private readonly mode = inject(ModeService);
  private readonly auth = inject(AuthService);
  private readonly portalAuth = inject(PortalAuthService);
  private readonly me = inject(MeStore);
  private readonly snapshot = inject(SessionSnapshotStore);
  private readonly network = inject(NetworkService);
  private readonly outbox = inject(OutboxService);
  private readonly cache = inject(OfflineCache);
  private readonly drafts = inject(DraftStore);
  private readonly refreshStore = inject(MobileRefreshTokenStore);
  private readonly push = inject(PushRegistrar);
  private readonly realtime = inject(RealtimeService);
  private readonly portalRealtime = inject(PortalRealtimeService);
  private readonly notificationsApi = inject(NotificationsApi);
  private readonly portalTickets = inject(PortalTicketsApi);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  private readonly resumedSubject = new Subject<void>();
  private readonly restoredSubject = new Subject<void>();
  private probeTimer: ReturnType<typeof setInterval> | null = null;
  private started = false;

  /** Opened from cached data without a server session; read-only until the session is restored. */
  readonly offlineSession = signal(false);
  /** A server session exists for the current mode. */
  readonly live = computed(() => (this.mode.mode() === 'agent' ? this.auth.isAuthenticated() : this.portalAuth.isAuthenticated()));
  /** Screens disable server actions (status changes, assignment…) while this is true; composers queue instead. */
  readonly readOnly = computed(() => !this.network.online() || !this.live());
  /** Refetch triggers for screens: app resumed, connection back, session restored. */
  readonly refresh$: Observable<void> = merge(this.resumedSubject, this.network.cameOnline$, this.restoredSubject);

  /** Session became live (sign-in / restore) or ended: snapshot, outbox gate, push registration. */
  private readonly sessionEffect = effect(() => {
    const live = this.live();
    const user = this.me.user();
    const contact = this.portalAuth.contact();
    untracked(() => {
      if (live) {
        const wasOffline = this.offlineSession();
        this.offlineSession.set(false);
        if (this.mode.mode() === 'agent' && user) void this.snapshot.saveAgent(user);
        if (this.mode.mode() === 'customer' && contact) void this.snapshot.saveCustomer(this.mode.brand(), contact);
        if (wasOffline) this.restoredSubject.next();
      }
      this.outbox.setSessionReady(live);
      void this.push.sync(live);
    });
  });

  /** Called once by the root component: app lifecycle listeners and the offline probe. */
  start(): void {
    if (this.started) return;
    this.started = true;

    const resume = App.addListener('resume', () => this.onResume());
    const online = this.network.cameOnline$.subscribe(() => void this.probe());
    this.probeTimer = setInterval(() => {
      if (!this.network.online() || this.offlineSession()) void this.probe();
    }, PROBE_MS);
    this.destroyRef.onDestroy(() => {
      void resume.then((h) => h.remove());
      online.unsubscribe();
      if (this.probeTimer) clearInterval(this.probeTimer);
    });
  }

  /**
   * Route guard logic: a live session, or an offline session from the identity's snapshot when the server cannot
   * be reached (the refresh token is still stored — it is only deleted when the server rejects it).
   */
  async canEnter(mode: AppMode): Promise<boolean> {
    if (this.mode.mode() !== mode) return false;
    await this.snapshot.load();
    if (mode === 'agent') await this.auth.ready();
    else {
      if (!this.mode.brand()) return false;
      await this.portalAuth.ready(this.mode.brand());
    }
    if (this.live()) return true;
    const snap = this.snapshot.current();
    const matches = snap?.kind === mode && (mode === 'agent' || (snap.kind === 'customer' && snap.brand === this.mode.brand()));
    if (matches && (await this.refreshStore.get())) {
      this.offlineSession.set(true);
      this.network.markUnreachable();
      return true;
    }
    return false;
  }

  /** Ends the session: push unregister (while still authorised), the identity's local data, then the server session. */
  async signOut(): Promise<void> {
    await this.push.unregister();
    const scope = this.snapshot.scope();
    // Forget the identity first so late writes (realtime events, debounced drafts) cannot recreate its data.
    await this.snapshot.clear();
    if (scope) await Promise.all([this.cache.clearScope(scope), this.drafts.clearScope(scope), this.outbox.clearScope(scope)]);
    this.offlineSession.set(false);
    if (this.mode.mode() === 'agent') {
      await this.auth.logout();
    } else {
      await this.portalAuth.logout();
      await this.router.navigateByUrl('/login', { replaceUrl: true });
    }
  }

  private onResume(): void {
    this.resumedSubject.next();
    if (this.live()) {
      if (this.mode.mode() === 'agent') {
        if (this.realtime.state() === 'disconnected') void this.realtime.start();
      } else if (this.portalRealtime.state() === 'disconnected') {
        void this.portalRealtime.start();
      }
      void this.push.sync(true);
      this.outbox.kick();
    } else {
      void this.probe();
    }
  }

  /** Is the server back? Offline session → try to restore it; live session → any cheap authenticated GET. */
  private async probe(): Promise<void> {
    try {
      if (this.offlineSession()) {
        const ok = this.mode.mode() === 'agent' ? await this.auth.refreshAccessToken() : await this.portalAuth.refreshAccessToken();
        if (!ok && !(await this.refreshStore.get())) {
          // The server rejected the stored session (revoked/expired): sign in again.
          this.offlineSession.set(false);
          await this.snapshot.clear();
          await this.router.navigateByUrl('/login', { replaceUrl: true });
        }
        return;
      }
      if (!this.live()) return;
      if (this.mode.mode() === 'agent') await firstValueFrom(this.notificationsApi.unreadCount());
      else await firstValueFrom(this.portalTickets.stats(this.mode.brand()));
    } catch {
      /* still offline — NetworkService was updated by the interceptor */
    }
  }
}
