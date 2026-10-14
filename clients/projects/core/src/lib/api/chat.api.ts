import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import type { HubConnection } from '@microsoft/signalr';
import { Observable, Subject, filter } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { SD_CONFIG } from '../config';
import { ApiClient } from '../http/api-client';
import { Guid } from '../models/common';
import { ChatMessageDto, ChatStatusEvent, ChatTypingEvent, ChatWidgetConfigDto } from '../models/communication';
import { RealtimeConnectionState } from '../models/realtime';

/** Public live-chat endpoints used by agents for previews (`/chat/{widgetKey}/config`). */
@Injectable({ providedIn: 'root' })
export class ChatApi {
  private readonly api = inject(ApiClient);

  /** The widget configuration exactly as visitors receive it (brand colours, greeting, bot mode, agents online). */
  widgetConfig(widgetKey: string): Observable<ChatWidgetConfigDto> {
    return this.api.get<ChatWidgetConfigDto>(`chat/${encodeURIComponent(widgetKey)}/config`, { skipAuth: true, silent: true });
  }

  /** Absolute URL of the embeddable script and of the demo page served by the API host. */
  widgetScriptUrl(): string {
    return new URL('/widget/widget.js', globalThis.location?.origin ?? 'http://localhost').toString();
  }

  embedSnippet(widgetKey: string): string {
    return `<script src="${this.widgetScriptUrl()}" data-widget-key="${widgetKey}" async></script>`;
  }
}

/** A chat message pushed on `/hubs/chat`, tagged with its conversation. */
export interface ChatHubMessage {
  conversationId: Guid;
  message: ChatMessageDto;
}

/**
 * Agent connection to the live-chat hub (`/hubs/chat`): join a conversation's group (after the server's
 * visibility check) to receive `chat.message`, `chat.status` and `chat.typing`, and send typing indicators.
 * Connects lazily on the first `join()`, reconnects automatically and re-joins its conversations; disconnects
 * on sign-out. The @microsoft/signalr bundle is loaded on demand.
 */
@Injectable({ providedIn: 'root' })
export class ChatHubService {
  private readonly auth = inject(AuthService);
  private readonly config = inject(SD_CONFIG);

  private connection: HubConnection | null = null;
  private starting: Promise<void> | null = null;
  private readonly joined = new Set<Guid>();
  private readonly _state = signal<RealtimeConnectionState>('disconnected');
  private readonly messageSubject = new Subject<ChatHubMessage>();
  private readonly statusSubject = new Subject<ChatStatusEvent>();
  private readonly typingSubject = new Subject<ChatTypingEvent>();

  readonly state = this._state.asReadonly();
  readonly connected = computed(() => this._state() === 'connected');
  readonly message$: Observable<ChatHubMessage> = this.messageSubject.asObservable();
  readonly status$: Observable<ChatStatusEvent> = this.statusSubject.asObservable();
  readonly typing$: Observable<ChatTypingEvent> = this.typingSubject.asObservable();

  constructor() {
    effect(() => {
      if (!this.auth.isAuthenticated()) untracked(() => void this.stop());
    });
  }

  /** Events of one conversation. */
  forConversation(id: Guid): { message$: Observable<ChatMessageDto>; status$: Observable<ChatStatusEvent>; typing$: Observable<ChatTypingEvent> } {
    return {
      message$: new Observable<ChatMessageDto>((sub) => {
        const s = this.message$.pipe(filter((m) => m.conversationId === id)).subscribe((m) => sub.next(m.message));
        return () => s.unsubscribe();
      }),
      status$: this.status$.pipe(filter((s) => s.conversationId === id)),
      typing$: this.typing$.pipe(filter((t) => t.conversationId === id)),
    };
  }

  async join(conversationId: Guid): Promise<void> {
    this.joined.add(conversationId);
    await this.ensureStarted();
    await this.invoke('JoinConversation', conversationId);
  }

  async leave(conversationId: Guid): Promise<void> {
    this.joined.delete(conversationId);
    await this.invoke('LeaveConversation', conversationId);
  }

  /** Best-effort typing indicator to the visitor. */
  typing(conversationId: Guid, isTyping: boolean): void {
    void this.invoke('AgentTyping', conversationId, isTyping);
  }

  async stop(): Promise<void> {
    const connection = this.connection;
    this.connection = null;
    this.joined.clear();
    this._state.set('disconnected');
    if (connection) {
      try {
        await connection.stop();
      } catch {
        /* already closed */
      }
    }
  }

  private hubUrl(): string {
    return this.config.hubUrl.replace(/\/hubs\/[^/]+$/, '/hubs/chat');
  }

  private ensureStarted(): Promise<void> {
    if (this.connection && this._state() === 'connected') return Promise.resolve();
    if (!this.starting) this.starting = this.connect().finally(() => (this.starting = null));
    return this.starting;
  }

  private async connect(): Promise<void> {
    if (this.connection) return;
    this._state.set('connecting');
    const signalR = await import('@microsoft/signalr');
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(this.hubUrl(), {
        accessTokenFactory: async () => {
          const expiresAt = this.auth.expiresAt();
          if (expiresAt !== null && expiresAt - Date.now() < 30_000) await this.auth.refreshAccessToken();
          return this.auth.accessToken() ?? '';
        },
      })
      .withAutomaticReconnect([0, 2_000, 5_000, 10_000, 30_000])
      .configureLogging(signalR.LogLevel.Warning)
      .build();
    // Arguments: the visitor-facing message, then the conversation id.
    connection.on('chat.message', (m: ChatMessageDto, conversationId?: Guid) => {
      if (conversationId) this.route(conversationId, m);
    });
    connection.on('chat.status', (s: ChatStatusEvent) => this.statusSubject.next(s));
    connection.on('chat.typing', (t: ChatTypingEvent) => this.typingSubject.next(t));
    connection.onreconnecting(() => this._state.set('reconnecting'));
    connection.onreconnected(() => {
      this._state.set('connected');
      for (const id of this.joined) void this.invoke('JoinConversation', id);
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
      this._state.set('connected');
    } catch {
      this.connection = null;
      this._state.set('disconnected');
    }
  }

  /** The same message can arrive twice (direct push + outbox relay): forward each id once. */
  private readonly seenIds = new Set<Guid>();

  private route(conversationId: Guid, message: ChatMessageDto): void {
    if (this.seenIds.has(message.id)) return;
    this.seenIds.add(message.id);
    if (this.seenIds.size > 2000) this.seenIds.clear();
    this.messageSubject.next({ conversationId, message });
  }

  private async invoke(method: string, ...args: unknown[]): Promise<void> {
    const connection = this.connection;
    if (!connection || this._state() !== 'connected') return;
    try {
      await connection.invoke(method, ...args);
    } catch {
      /* best effort (the REST API is the source of truth) */
    }
  }
}
