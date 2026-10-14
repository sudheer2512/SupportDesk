import { DestroyRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { AuthService } from '../auth/auth.service';
import { NotificationsApi } from '../api/notifications.api';
import { Guid } from '../models/common';
import { NotificationDto, severityRank } from '../models/notifications';
import { RealtimeService } from '../realtime/realtime.service';
import { ToastBridge } from './toast-bridge';

/**
 * The bell: latest notifications (GET /notifications?limit=20) kept live from the realtime
 * `notification` event. Warning+ notifications also raise an in-app toast.
 */
@Injectable({ providedIn: 'root' })
export class NotificationStore {
  private readonly api = inject(NotificationsApi);
  private readonly auth = inject(AuthService);
  private readonly realtime = inject(RealtimeService);
  private readonly toasts = inject(ToastBridge);

  private readonly _items = signal<NotificationDto[]>([]);
  readonly items = this._items.asReadonly();
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly unreadCount = computed(() => this._items().filter((n) => !n.readAt).length);
  readonly pendingAckCount = computed(() => this._items().filter((n) => n.requiresAck && !n.acknowledgedAt).length);

  constructor() {
    effect(() => {
      const signedIn = this.auth.isAuthenticated();
      untracked(() => (signedIn ? this.refresh() : this._items.set([])));
    });
    const sub = this.realtime.notification$.subscribe((e) => {
      const item: NotificationDto = { readAt: null, acknowledgedAt: null, ...e };
      this._items.update((items) => [item, ...items.filter((n) => n.id !== item.id)].slice(0, 50));
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
  }

  markRead(id: Guid): void {
    const now = new Date().toISOString();
    this.patch(id, { readAt: now });
    this.api.markRead(id).subscribe({ error: () => this.patch(id, { readAt: null }) });
  }

  acknowledge(id: Guid): void {
    const now = new Date().toISOString();
    this.patch(id, { acknowledgedAt: now, readAt: now });
    this.api.acknowledge(id).subscribe({ error: () => this.patch(id, { acknowledgedAt: null }) });
  }

  markAllRead(): void {
    const now = new Date().toISOString();
    const before = this._items();
    this._items.update((items) => items.map((n) => (n.readAt ? n : { ...n, readAt: now })));
    this.api.markAllRead().subscribe({ error: () => this._items.set(before) });
  }

  private patch(id: Guid, changes: Partial<NotificationDto>): void {
    this._items.update((items) => items.map((n) => (n.id === id ? { ...n, ...changes } : n)));
  }
}
