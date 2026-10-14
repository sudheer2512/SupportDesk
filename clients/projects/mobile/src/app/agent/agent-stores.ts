import { DestroyRef, Injectable, computed, inject } from '@angular/core';
import { MeApi, MeStore, NotificationDto, NotificationsApi, QueueCountsDto, RealtimeService, WorkbenchApi } from '@sd/core';
import { debounceTime, firstValueFrom, merge } from 'rxjs';
import { cachedResource } from '../core/cached-resource';
import { SessionCoordinator } from '../core/session-coordinator';

/**
 * Agent queue counts (mine, unassigned, overdue, at risk…) shared by Home and the tab bar. Cached for offline,
 * refreshed on ticket changes (debounced), SignalR reconnects and app resume.
 */
@Injectable({ providedIn: 'root' })
export class QueueCountsStore {
  private readonly api = inject(WorkbenchApi);
  private readonly realtime = inject(RealtimeService);
  private readonly session = inject(SessionCoordinator);

  readonly resource = cachedResource<QueueCountsDto>({ key: () => 'queue-counts', kind: 'meta', fetch: () => this.api.queueCounts() });
  readonly counts = this.resource.value;

  constructor() {
    const sub = merge(this.realtime.ticketChanged$.pipe(debounceTime(1500)), this.realtime.reconnected$, this.session.refresh$).subscribe(() => {
      if (this.session.live()) void this.resource.load();
    });
    inject(DestroyRef).onDestroy(() => sub.unsubscribe());
  }

  count(key: string): number | null {
    const c = this.counts();
    return c ? (c[key] ?? 0) : null;
  }
}

/** In-app notifications for the agent: list (cached), unread badge, live prepend from SignalR, read/ack. */
@Injectable({ providedIn: 'root' })
export class AgentNotificationsStore {
  private readonly api = inject(NotificationsApi);
  private readonly realtime = inject(RealtimeService);
  private readonly session = inject(SessionCoordinator);

  readonly resource = cachedResource<NotificationDto[]>({ key: () => 'notifications', kind: 'meta', fetch: () => this.api.list(50) });
  readonly items = computed(() => this.resource.value() ?? []);
  readonly unread = computed(() => this.items().filter((n) => !n.readAt).length);
  readonly needsAck = computed(() => this.items().filter((n) => n.requiresAck && !n.acknowledgedAt).length);

  constructor() {
    const live = this.realtime.notification$.subscribe((e) => {
      const n: NotificationDto = { readAt: null, acknowledgedAt: null, ...e };
      this.resource.set([n, ...this.items().filter((x) => x.id !== n.id)].slice(0, 100));
    });
    const refresh = merge(this.realtime.reconnected$, this.session.refresh$).subscribe(() => {
      if (this.session.live()) void this.resource.load();
    });
    inject(DestroyRef).onDestroy(() => {
      live.unsubscribe();
      refresh.unsubscribe();
    });
  }

  async markRead(n: NotificationDto): Promise<void> {
    if (n.readAt) return;
    this.patch(n.id, { readAt: new Date().toISOString() });
    try {
      await firstValueFrom(this.api.markRead(n.id));
    } catch {
      this.patch(n.id, { readAt: null });
    }
  }

  async acknowledge(n: NotificationDto): Promise<void> {
    const now = new Date().toISOString();
    await firstValueFrom(this.api.acknowledge(n.id));
    this.patch(n.id, { acknowledgedAt: now, readAt: n.readAt ?? now });
  }

  async markAllRead(): Promise<void> {
    await firstValueFrom(this.api.markAllRead());
    const now = new Date().toISOString();
    // Server rule: notifications that require acknowledgement stay unread until acknowledged.
    this.resource.set(this.items().map((n) => (n.readAt || (n.requiresAck && !n.acknowledgedAt) ? n : { ...n, readAt: now })));
  }

  private patch(id: string, changes: Partial<NotificationDto>): void {
    this.resource.set(this.items().map((n) => (n.id === id ? { ...n, ...changes } : n)));
  }
}

/** The signed-in agent's availability (Online / Away / Busy / Offline) with optimistic update. */
@Injectable({ providedIn: 'root' })
export class AvailabilityStore {
  private readonly me = inject(MeStore);
  private readonly meApi = inject(MeApi);
  readonly current = this.me.availability;

  async set(value: 'Online' | 'Away' | 'Busy' | 'Offline'): Promise<void> {
    const previous = this.me.availability();
    this.me.patch({ availability: value });
    try {
      // The API persists it and broadcasts agent.availability to the tenant.
      await firstValueFrom(this.meApi.setAvailability(value));
    } catch (e) {
      this.me.patch({ availability: previous });
      throw e;
    }
  }
}
