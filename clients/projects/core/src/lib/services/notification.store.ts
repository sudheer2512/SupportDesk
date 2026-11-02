import { DestroyRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { AuthService } from '../auth/auth.service';
import { NotificationsApi } from '../api/notifications.api';
import { Guid } from '../models/common';
import { NotificationDto, severityRank } from '../models/notifications';
import { RealtimeService } from '../realtime/realtime.service';
import { ToastBridge } from './toast-bridge';

/**
 * The bell: latest notifications (GET /notifications?limit=20) kept live from the realtime
 * `notification` event, plus the server's unread total (GET /notifications/unread-count) — the loaded
 * page only holds the latest few. Warning+ notifications also raise an in-app toast.
 */
@Injectable({ providedIn: 'root' })
export class NotificationStore {
  private readonly api = inject(NotificationsApi);
  private readonly auth = inject(AuthService);
  private readonly realtime = inject(RealtimeService);
  private readonly toasts = inject(ToastBridge);

  private readonly _items = signal<NotificationDto[]>([]);
  private readonly _unreadTotal = signal<number | null>(null);
  readonly items = this._items.asReadonly();
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly unreadCount = computed(() => this._unreadTotal() ?? this._items().filter((n) => !n.readAt).length);
  readonly pendingAckCount = computed(() => this._items().filter((n) => n.requiresAck && !n.acknowledgedAt).length);

  constructor() {
    effect(() => {
      const signedIn = this.auth.isAuthenticated();
      untracked(() => {
        if (signedIn) {
          this.refresh();
        } else {
          this._items.set([]);
          this._unreadTotal.set(null);
        }
      });
    });
    const sub = this.realtime.notification$.subscribe((e) => {
      const item: NotificationDto = { readAt: null, acknowledgedAt: null, ...e };
      const isNew = !this._items().some((n) => n.id === item.id);
      this._items.update((items) => [item, ...items.filter((n) => n.id !== item.id)].slice(0, 50));
      if (isNew) this.adjustTotal(1);
      if (severityRank(e.severity) >= severityRank('warning')) {
        this.toasts.show({
          kind: severityRank(e.severity) >= severityRank('error') ? 'danger' : 'warning',
          title: e.title,
          message: e.body,
          key: `notification:${e.id}`,
        });
      }
    });
    const resync = this.realtime.reconnected$.subscribe(() => this.refresh());
    inject(DestroyRef).onDestroy(() => {
      sub.unsubscribe();
      resync.unsubscribe();
    });
  }

  refresh(limit = 20): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.list(limit).subscribe({
      next: (items) => {
        this._items.set(items);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('Notifications could not be loaded.');
        this.loading.set(false);
      },
    });
    this.syncUnreadTotal();
  }

  markRead(id: Guid): void {
    const now = new Date().toISOString();
    if (this._items().some((n) => n.id === id && !n.readAt)) this.adjustTotal(-1);
    this.patch(id, { readAt: now });
    this.api.markRead(id).subscribe({
      next: () => this.syncUnreadTotal(),
      error: () => {
        this.patch(id, { readAt: null });
        this.syncUnreadTotal();
      },
    });
  }

  acknowledge(id: Guid): void {
    const now = new Date().toISOString();
    if (this._items().some((n) => n.id === id && !n.readAt)) this.adjustTotal(-1);
    this.patch(id, { acknowledgedAt: now, readAt: now });
    this.api.acknowledge(id).subscribe({
      next: () => this.syncUnreadTotal(),
      error: () => {
        this.patch(id, { acknowledgedAt: null });
        this.syncUnreadTotal();
      },
    });
  }

  /** Same rule as the server: notifications that need acknowledgement stay unread until acknowledged. */
  markAllRead(): void {
    const now = new Date().toISOString();
    const before = this._items();
    this._items.update((items) => items.map((n) => (n.readAt || n.requiresAck ? n : { ...n, readAt: now })));
    this._unreadTotal.set(this._items().filter((n) => !n.readAt).length);
    this.api.markAllRead().subscribe({
      next: () => this.syncUnreadTotal(),
      error: () => {
        this._items.set(before);
        this.syncUnreadTotal();
      },
    });
  }

  private syncUnreadTotal(): void {
    this.api.unreadCount().subscribe({ next: (r) => this._unreadTotal.set(r.count), error: () => undefined });
  }

  private adjustTotal(delta: number): void {
    this._unreadTotal.update((total) => (total === null ? null : Math.max(0, total + delta)));
  }

  private patch(id: Guid, changes: Partial<NotificationDto>): void {
    this._items.update((items) => items.map((n) => (n.id === id ? { ...n, ...changes } : n)));
  }
}
