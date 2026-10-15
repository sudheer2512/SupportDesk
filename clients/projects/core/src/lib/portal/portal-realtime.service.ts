import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import type { HubConnection } from '@microsoft/signalr';
import { Observable, Subject, filter } from 'rxjs';
import { SD_CONFIG, appUrl } from '../config';
import { hostedPayload } from '../http/app-root.interceptor';
import { Guid } from '../models/common';
import { PortalTicketChangedEvent, PortalTicketThreadEvent } from '../models/portal-content';
import { PortalAuthService } from './portal-auth.service';

/**
 * Live updates for the signed-in portal contact (`/hubs/portal`): new public messages and status changes on
 * the contact's own tickets. Connects while a portal session exists (token from PortalAuthService), reconnects
 * automatically, and loads the SignalR bundle lazily.
 */
@Injectable({ providedIn: 'root' })
export class PortalRealtimeService {
  private readonly auth = inject(PortalAuthService);
  private readonly config = inject(SD_CONFIG);
  private connection: HubConnection | null = null;
  private starting: Promise<void> | null = null;
  private readonly _state = signal<'disconnected' | 'connecting' | 'connected' | 'reconnecting'>('disconnected');

  readonly state = this._state.asReadonly();
  readonly connected = computed(() => this._state() === 'connected');

  private readonly threadSubject = new Subject<PortalTicketThreadEvent>();
  private readonly changedSubject = new Subject<PortalTicketChangedEvent>();
  private readonly reconnectedSubject = new Subject<void>();

  readonly ticketThread$: Observable<PortalTicketThreadEvent> = this.threadSubject.asObservable();
  readonly ticketChanged$: Observable<PortalTicketChangedEvent> = this.changedSubject.asObservable();
  readonly reconnected$: Observable<void> = this.reconnectedSubject.asObservable();

  constructor() {
    effect(() => {
      const signedIn = this.auth.isAuthenticated();
      untracked(() => (signedIn ? void this.start() : void this.stop()));
    });
  }

  /** Any change (message or status) on one ticket. */
  ticketEvents(ticketId: Guid): Observable<PortalTicketThreadEvent | PortalTicketChangedEvent> {
    return new Observable<PortalTicketThreadEvent | PortalTicketChangedEvent>((subscriber) => {
      const a = this.ticketThread$.pipe(filter((e) => e.ticketId === ticketId)).subscribe(subscriber);
      const b = this.ticketChanged$.pipe(filter((e) => e.ticketId === ticketId)).subscribe(subscriber);
      return () => {
        a.unsubscribe();
        b.unsubscribe();
      };
    });
  }

  async start(): Promise<void> {
    if (this.connection || this.starting) return this.starting ?? undefined;
    this.starting = this.connect().finally(() => (this.starting = null));
    return this.starting;
  }

  async stop(): Promise<void> {
    const connection = this.connection;
    this.connection = null;
    this._state.set('disconnected');
    if (connection) {
      try {
        await connection.stop();
      } catch {
        /* already closed */
      }
    }
  }

  private async connect(): Promise<void> {
    this._state.set('connecting');
    const signalR = await import('@microsoft/signalr');
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(this.config.portalHubUrl || appUrl('hubs/portal'), { accessTokenFactory: () => this.auth.accessToken() ?? '' })
      .withAutomaticReconnect([0, 2_000, 5_000, 10_000, 30_000, 60_000])
      .configureLogging(signalR.LogLevel.Warning)
      .build();
    connection.on('ticket.thread', (e: PortalTicketThreadEvent) => this.threadSubject.next(hostedPayload(e)));
    connection.on('ticket.changed', (e: PortalTicketChangedEvent) => this.changedSubject.next(e));
    connection.onreconnecting(() => this._state.set('reconnecting'));
    connection.onreconnected(() => {
      this._state.set('connected');
      this.reconnectedSubject.next();
    });
    connection.onclose(() => {
      if (this.connection === connection) {
        this.connection = null;
        this._state.set('disconnected');
      }
    });
    this.connection = connection;
    try {
      await connection.start();
      if (this.connection !== connection) {
        await connection.stop();
        return;
      }
      this._state.set('connected');
    } catch {
      if (this.connection === connection) this.connection = null;
      this._state.set('disconnected');
    }
  }
}
