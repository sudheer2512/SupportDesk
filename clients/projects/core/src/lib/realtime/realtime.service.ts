import { Injectable, Injector, Signal, computed, effect, inject, signal, untracked } from '@angular/core';
import { Router } from '@angular/router';
import type { HubConnection, IRetryPolicy, RetryContext } from '@microsoft/signalr';
import { Observable, Subject, filter } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { SD_CONFIG } from '../config';
import { Guid } from '../models/common';
import { Availability } from '../models/enums';
import { severityRank } from '../models/notifications';
import { MetaApi } from '../api/meta.api';
import {
  AgentAvailabilityEvent,
  ChatMessageEvent,
  ConfigChangedEvent,
  ConversationUpdatedEvent,
  NotificationEvent,
  PresenceActivity,
  PresenceViewer,
  RealtimeConnectionState,
  SlaAlertEvent,
  TicketChangedEvent,
  TicketPresenceEvent,
  TicketThreadEvent,
} from '../models/realtime';
import { DesktopNotifier } from '../services/desktop-notifier';

/** Reconnect backoff: quick first retries, then every ~30 s (with jitter) for as long as we are signed in. */
const RETRY_DELAYS_MS = [0, 2_000, 5_000, 10_000, 20_000];
const MAX_DELAY_MS = 30_000;

class BackoffPolicy implements IRetryPolicy {
  nextRetryDelayInMilliseconds(context: RetryContext): number | null {
    const base = RETRY_DELAYS_MS[context.previousRetryCount] ?? MAX_DELAY_MS;
    return base + Math.floor(Math.random() * Math.min(1_000, base / 4 + 1));
  }
}

/**
 * The agent workbench's SignalR connection (`/hubs/desk`). Connects while signed in (token from
 * AuthService, refreshed when close to expiry), reconnects with backoff, re-joins ticket groups after a
 * reconnect, and exposes server events as typed streams plus presence/availability signals.
 * Notifications with severity ≥ warning also raise a desktop notification (when permitted).
 * The @microsoft/signalr bundle is loaded lazily on first connect.
 */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private readonly auth = inject(AuthService);
  private readonly config = inject(SD_CONFIG);
  private readonly notifier = inject(DesktopNotifier);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private configRefreshTimer: ReturnType<typeof setTimeout> | null = null;

  private connection: HubConnection | null = null;
  private starting: Promise<void> | null = null;
  private restartTimer: ReturnType<typeof setTimeout> | null = null;
  private restartAttempts = 0;
  private readonly joinedTickets = new Set<Guid>();
  private readonly activities = new Map<Guid, PresenceActivity>();

  private readonly _state = signal<RealtimeConnectionState>('disconnected');
  private readonly _presence = signal<ReadonlyMap<Guid, PresenceViewer[]>>(new Map());
  private readonly _availability = signal<ReadonlyMap<Guid, Availability>>(new Map());

  readonly state = this._state.asReadonly();
  readonly connected = computed(() => this._state() === 'connected');
  /** Viewers per ticket from `ticket.presence`. */
  readonly presence = this._presence.asReadonly();
  /** Latest availability per agent from `agent.availability`. */
  readonly agentAvailability = this._availability.asReadonly();

  private readonly ticketChangedSubject = new Subject<TicketChangedEvent>();
  private readonly ticketThreadSubject = new Subject<TicketThreadEvent>();
  private readonly ticketPresenceSubject = new Subject<TicketPresenceEvent>();
  private readonly notificationSubject = new Subject<NotificationEvent>();
  private readonly availabilitySubject = new Subject<AgentAvailabilityEvent>();
  private readonly chatMessageSubject = new Subject<ChatMessageEvent>();
  private readonly conversationUpdatedSubject = new Subject<ConversationUpdatedEvent>();
  private readonly slaAlertSubject = new Subject<SlaAlertEvent>();
  private readonly reconnectedSubject = new Subject<void>();
  private readonly configChangedSubject = new Subject<ConfigChangedEvent>();

  /** Configuration changed by an administrator (cached ticket metadata is refreshed automatically). */
  readonly configChanged$: Observable<ConfigChangedEvent> = this.configChangedSubject.asObservable();
  readonly ticketChanged$: Observable<TicketChangedEvent> = this.ticketChangedSubject.asObservable();
  readonly ticketThread$: Observable<TicketThreadEvent> = this.ticketThreadSubject.asObservable();
  readonly ticketPresence$: Observable<TicketPresenceEvent> = this.ticketPresenceSubject.asObservable();
  readonly notification$: Observable<NotificationEvent> = this.notificationSubject.asObservable();
  readonly agentAvailability$: Observable<AgentAvailabilityEvent> = this.availabilitySubject.asObservable();
  readonly chatMessage$: Observable<ChatMessageEvent> = this.chatMessageSubject.asObservable();
  readonly conversationUpdated$: Observable<ConversationUpdatedEvent> = this.conversationUpdatedSubject.asObservable();
  readonly slaAlert$: Observable<SlaAlertEvent> = this.slaAlertSubject.asObservable();
  /** Fires after an automatic reconnect: refetch anything that may have been missed. */
  readonly reconnected$: Observable<void> = this.reconnectedSubject.asObservable();

  constructor() {
    effect(() => {
      const signedIn = this.auth.isAuthenticated();
      untracked(() => (signedIn ? void this.start() : void this.stop()));
    });
  }

  /** Viewers of one ticket as a signal. */
  presenceFor(ticketId: Guid): Signal<PresenceViewer[]> {
    return computed(() => this._presence().get(ticketId) ?? []);
  }

  /** `ticket.changed` + `ticket.thread` events for one ticket. */
  ticketEvents(ticketId: Guid): { changed$: Observable<TicketChangedEvent>; thread$: Observable<TicketThreadEvent> } {
    return {
      changed$: this.ticketChanged$.pipe(filter((e) => e.ticketId === ticketId)),
      thread$: this.ticketThread$.pipe(filter((e) => e.ticketId === ticketId)),
    };
  }

  /** Subscribe to a ticket's group (presence + live updates). Remembered across reconnects. */
  joinTicket(ticketId: Guid): void {
    this.joinedTickets.add(ticketId);
    this.invoke('JoinTicket', ticketId);
  }

  leaveTicket(ticketId: Guid): void {
    this.joinedTickets.delete(ticketId);
    this.activities.delete(ticketId);
    this.invoke('LeaveTicket', ticketId);
  }

  /** Tell other agents whether you are viewing, replying or writing a note on the ticket. */
  setActivity(ticketId: Guid, activity: PresenceActivity): void {
    this.activities.set(ticketId, activity);
    this.invoke('SetActivity', ticketId, activity);
  }

  setAvailability(status: Availability): void {
    this.invoke('SetAvailability', status);
  }

  async start(): Promise<void> {
    if (this.connection || this.starting) return this.starting ?? undefined;
    this.starting = this.connect().finally(() => (this.starting = null));
    return this.starting;
  }

  async stop(): Promise<void> {
    if (this.restartTimer) clearTimeout(this.restartTimer);
    this.restartTimer = null;
    this.restartAttempts = 0;
    const connection = this.connection;
    this.connection = null;
    this.joinedTickets.clear();
    this.activities.clear();
    this._presence.set(new Map());
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
      .withUrl(this.config.hubUrl, {
        accessTokenFactory: async () => {
          const expiresAt = this.auth.expiresAt();
          if (expiresAt !== null && expiresAt - Date.now() < 30_000) await this.auth.refreshAccessToken();
          return this.auth.accessToken() ?? '';
        },
        headers: this.config.client === 'mobile' ? { 'X-Client': 'mobile' } : undefined,
      })
      .withAutomaticReconnect(new BackoffPolicy())
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    connection.on('ticket.changed', (e: TicketChangedEvent) => this.ticketChangedSubject.next(e));
    connection.on('ticket.thread', (e: TicketThreadEvent) => this.ticketThreadSubject.next(e));
    connection.on('ticket.presence', (e: TicketPresenceEvent) => {
      this._presence.update((map) => new Map(map).set(e.ticketId, e.viewers ?? []));
      this.ticketPresenceSubject.next(e);
    });
    connection.on('notification', (e: NotificationEvent) => this.onNotification(e));
    connection.on('agent.availability', (e: AgentAvailabilityEvent) => {
      this._availability.update((map) => new Map(map).set(e.userId, e.availability));
      this.availabilitySubject.next(e);
    });
    connection.on('chat.message', (e: ChatMessageEvent) => this.chatMessageSubject.next(e));
    connection.on('conversation.updated', (e: ConversationUpdatedEvent) => this.conversationUpdatedSubject.next(e));
    connection.on('sla.alert', (e: SlaAlertEvent) => this.slaAlertSubject.next(e));
    connection.on('config.changed', (e: ConfigChangedEvent) => {
      this.configChangedSubject.next(e);
      // Coalesce bursts of admin changes into one metadata refresh.
      if (this.configRefreshTimer) clearTimeout(this.configRefreshTimer);
      this.configRefreshTimer = setTimeout(() => {
        this.configRefreshTimer = null;
        this.injector.get(MetaApi).refresh();
      }, 750);
    });

    connection.onreconnecting(() => this._state.set('reconnecting'));
    connection.onreconnected(() => {
      this._state.set('connected');
      this.rejoin();
      this.reconnectedSubject.next();
    });
    connection.onclose(() => {
      if (this.connection !== connection) return;
      this.connection = null;
      this._state.set('disconnected');
      this.scheduleRestart();
    });

    this.connection = connection;
    try {
      await connection.start();
      if (this.connection !== connection) {
        await connection.stop();
        return;
      }
      this.restartAttempts = 0;
      this._state.set('connected');
      this.rejoin();
    } catch {
      if (this.connection === connection) this.connection = null;
      this._state.set('disconnected');
      this.scheduleRestart();
    }
  }

  /** The initial start is not covered by automatic reconnect: retry with the same backoff while signed in. */
  private scheduleRestart(): void {
    if (!this.auth.isAuthenticated() || this.restartTimer) return;
    const delay = (RETRY_DELAYS_MS[this.restartAttempts + 1] ?? MAX_DELAY_MS) + Math.floor(Math.random() * 1_000);
    this.restartAttempts += 1;
    this.restartTimer = setTimeout(() => {
      this.restartTimer = null;
      if (this.auth.isAuthenticated()) void this.start();
    }, delay);
  }

  private rejoin(): void {
    for (const ticketId of this.joinedTickets) {
      this.invoke('JoinTicket', ticketId);
      const activity = this.activities.get(ticketId);
      if (activity) this.invoke('SetActivity', ticketId, activity);
    }
  }

  private invoke(method: string, ...args: unknown[]): void {
    const connection = this.connection;
    if (!connection || this._state() !== 'connected') return;
    connection.invoke(method, ...args).catch(() => {
      /* best effort: presence/availability are re-sent after reconnect */
    });
  }

  private onNotification(e: NotificationEvent): void {
    this.notificationSubject.next(e);
    if (severityRank(e.severity) >= severityRank('warning')) {
      this.notifier.show({
        title: e.title,
        body: e.body,
        tag: e.id,
        onClick: () => {
          if (e.link?.startsWith('/')) void this.router.navigateByUrl(e.link);
        },
      });
    }
  }
}
