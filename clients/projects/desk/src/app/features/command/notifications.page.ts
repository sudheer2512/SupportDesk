import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import {
  ApiError,
  DesktopNotifier,
  NotificationDto,
  NotificationStore,
  NotificationsApi,
  RealtimeService,
  severityRank,
} from '@sd/core';
import { SdBadge, SdButton, SdEmptyState, SdIcon, SdPageHeader, SdRelativeTimePipe, SdSkeleton, ToastService } from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import '../tickets/shared/workbench-icons';

type Filter = 'all' | 'unread' | 'ack';

const TYPE_LABELS: Record<string, string> = {
  'ticket.assigned': 'Assignments',
  'ticket.mentioned': 'Mentions',
  'ticket.customer_replied': 'Customer replies',
  'ticket.escalated': 'Escalations',
  'sla.warning': 'SLA warnings',
  'sla.breached': 'SLA breaches',
  'approval.requested': 'Approval requests',
  'approval.decided': 'Approval decisions',
  'followup.due': 'Follow-ups',
  'ai.proposal': 'AI proposals',
  'anomaly.detected': 'Anomalies',
  'chat.waiting': 'Waiting chats',
  system: 'System',
};

/**
 * /notifications — every in-app notification (latest 200) with filters, mark read / all read and
 * acknowledgement for critical items; live from the realtime `notification` event. Desktop alerts for
 * warnings and critical items are raised by the realtime service once the browser permission is granted.
 */
@Component({
  selector: 'desk-notifications-page',
  imports: [FormsModule, SdBadge, SdButton, SdEmptyState, SdIcon, SdPageHeader, SdRelativeTimePipe, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Notifications" icon="bell" subtitle="Assignments, mentions, customer replies, SLA alerts and approvals.">
        @switch (notifier.permission()) {
          @case ('default') {
            <button sdButton sdPageActions size="sm" variant="subtle" icon="devices" type="button" (click)="notifier.requestPermission()">Enable desktop alerts</button>
          }
          @case ('denied') {
            <span sdPageActions class="sd-small sd-muted">Desktop alerts are blocked in this browser.</span>
          }
          @case ('granted') {
            <span sdPageActions class="sd-small sd-muted"><sd-icon name="check" [size]="13" /> Desktop alerts on for warnings and critical items</span>
          }
        }
        <button sdButton sdPageActions size="sm" type="button" icon="check-check" [disabled]="unreadCount() === 0" (click)="markAllRead()">Mark all read</button>
      </sd-page-header>
      <div class="desk-page__body">
        <div class="filters" role="toolbar" aria-label="Filter notifications">
          <div class="seg" role="radiogroup" aria-label="Show">
            <button type="button" role="radio" [attr.aria-checked]="filter() === 'all'" (click)="filter.set('all')">All</button>
            <button type="button" role="radio" [attr.aria-checked]="filter() === 'unread'" (click)="filter.set('unread')">Unread ({{ unreadCount() }})</button>
            <button type="button" role="radio" [attr.aria-checked]="filter() === 'ack'" (click)="filter.set('ack')">Needs acknowledgement ({{ ackCount() }})</button>
          </div>
          <label class="type">
            <span class="sd-visually-hidden">Type</span>
            <select class="sd-input sd-input-sm" [ngModel]="type()" (ngModelChange)="type.set($event)" aria-label="Notification type">
              <option value="">All types</option>
              @for (t of types(); track t) {
                <option [value]="t">{{ label(t) }}</option>
              }
            </select>
          </label>
        </div>

        @if (loading() && items().length === 0) {
          <sd-skeleton [lines]="8" />
        } @else if (error()) {
          <p class="error" role="alert">{{ error() }} <button sdButton size="sm" variant="link" type="button" (click)="load()">Retry</button></p>
        } @else if (visible().length === 0) {
          <sd-empty-state icon="bell-off" title="No notifications here" message="You're all caught up." />
        } @else {
          <ul class="list" aria-live="polite">
            @for (n of visible(); track n.id) {
              <li class="item" [class.is-unread]="!n.readAt" [class]="'item sev--' + tone(n)">
                <span class="sev" aria-hidden="true"><sd-icon [name]="icon(n)" [size]="16" /></span>
                <button type="button" class="body" (click)="open(n)">
                  <span class="title">{{ n.title }}</span>
                  @if (n.body) {
                    <span class="text">{{ n.body }}</span>
                  }
                  <span class="meta sd-xs">
                    {{ label(n.type) }} · {{ n.createdAt | sdRelativeTime }}
                    @if (!n.readAt) {
                      · <strong>unread</strong>
                    }
                  </span>
                </button>
                <div class="actions">
                  @if (n.requiresAck && !n.acknowledgedAt) {
                    <button sdButton size="sm" variant="danger" type="button" (click)="acknowledge(n)">Acknowledge</button>
                  } @else if (n.requiresAck) {
                    <sd-badge size="sm" tone="success" icon="check">Acknowledged</sd-badge>
                  }
                  @if (!n.readAt && !n.requiresAck) {
                    <button sdButton size="sm" variant="ghost" type="button" (click)="markRead(n)">Mark read</button>
                  }
                </div>
              </li>
            }
          </ul>
        }
      </div>
    </div>
  `,
  styles: `
    .filters {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      align-items: center;
      margin-bottom: var(--sd-space-4);
    }
    .seg {
      display: inline-flex;
      flex-wrap: wrap;
      gap: 2px;
      padding: 3px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface-3);
    }
    .seg button {
      height: 28px;
      padding: 0 10px;
      border: 0;
      border-radius: var(--sd-radius-sm);
      background: transparent;
      color: var(--sd-text-2);
      font: inherit;
      font-size: var(--sd-text-sm);
      cursor: pointer;
    }
    .seg button[aria-checked='true'] {
      background: var(--sd-surface);
      color: var(--sd-text);
      box-shadow: var(--sd-shadow-sm);
    }
    .list {
      display: grid;
      gap: 6px;
      max-width: 900px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .item {
      display: flex;
      align-items: flex-start;
      gap: 10px;
      padding: 10px 12px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
    }
    .item.is-unread {
      border-left: 3px solid var(--sd-primary);
    }
    .sev {
      display: inline-flex;
      padding: 6px;
      border-radius: var(--sd-radius-sm);
      background: var(--sd-surface-3);
      color: var(--sd-text-2);
    }
    .sev--warning .sev {
      background: var(--sd-warning-bg);
      color: var(--sd-warning);
    }
    .sev--danger .sev {
      background: var(--sd-danger-bg);
      color: var(--sd-danger);
    }
    .sev--success .sev {
      background: var(--sd-success-bg);
      color: var(--sd-success);
    }
    .body {
      display: grid;
      gap: 2px;
      flex: 1;
      min-width: 0;
      padding: 0;
      border: 0;
      background: transparent;
      color: var(--sd-text);
      font: inherit;
      text-align: left;
      cursor: pointer;
    }
    .title {
      font-weight: var(--sd-weight-medium);
    }
    .is-unread .title {
      font-weight: var(--sd-weight-semibold);
    }
    .text {
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
      overflow-wrap: anywhere;
    }
    .meta {
      color: var(--sd-text-3);
    }
    .actions {
      display: flex;
      align-items: center;
      gap: 6px;
      flex-wrap: wrap;
      justify-content: flex-end;
    }
    .error {
      color: var(--sd-danger);
    }
    @media (max-width: 560px) {
      .item {
        flex-wrap: wrap;
      }
      .actions {
        width: 100%;
      }
    }
  `,
})
export class NotificationsPage {
  protected readonly notifier = inject(DesktopNotifier);
  private readonly api = inject(NotificationsApi);
  private readonly store = inject(NotificationStore);
  private readonly realtime = inject(RealtimeService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  protected readonly items = signal<NotificationDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly filter = signal<Filter>('all');
  protected readonly type = signal('');

  protected readonly unreadCount = computed(() => this.items().filter((n) => !n.readAt).length);
  protected readonly ackCount = computed(() => this.items().filter((n) => n.requiresAck && !n.acknowledgedAt).length);
  protected readonly types = computed(() => [...new Set(this.items().map((n) => n.type))].sort());
  protected readonly visible = computed(() => {
    const f = this.filter();
    const t = this.type();
    return this.items().filter(
      (n) => (!t || n.type === t) && (f === 'all' || (f === 'unread' && !n.readAt) || (f === 'ack' && n.requiresAck && !n.acknowledgedAt)),
    );
  });

  constructor() {
    this.load();
    const live = this.realtime.notification$.subscribe((n) => {
      this.items.update((list) => [{ readAt: null, acknowledgedAt: null, ...n }, ...list.filter((x) => x.id !== n.id)]);
    });
    const resync = this.realtime.reconnected$.subscribe(() => this.load());
    inject(DestroyRef).onDestroy(() => {
      live.unsubscribe();
      resync.unsubscribe();
    });
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.list(200).subscribe({
      next: (list) => {
        this.items.set(list);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.error.set(ApiError.from(err).userMessage);
      },
    });
  }

  protected label(type: string): string {
    return TYPE_LABELS[type] ?? type;
  }

  protected tone(n: NotificationDto): 'neutral' | 'success' | 'warning' | 'danger' {
    const rank = severityRank(n.severity);
    return rank >= 3 ? 'danger' : rank === 2 ? 'warning' : rank === 1 ? 'success' : 'neutral';
  }

  protected icon(n: NotificationDto): string {
    if (n.type.startsWith('sla.')) return 'timer';
    if (n.type === 'ticket.mentioned') return 'at-sign';
    if (n.type === 'ticket.customer_replied') return 'reply';
    if (n.type === 'ticket.assigned') return 'user-check';
    if (n.type === 'ticket.escalated') return 'arrow-up-right';
    if (n.type.startsWith('approval.')) return 'shield-check';
    if (n.type === 'followup.due') return 'alarm-clock';
    if (n.type.startsWith('ai.')) return 'sparkles';
    return 'bell';
  }

  protected open(n: NotificationDto): void {
    if (!n.readAt && !n.requiresAck) this.markRead(n);
    if (n.link?.startsWith('/')) void this.router.navigateByUrl(n.link);
  }

  protected markRead(n: NotificationDto): void {
    const now = new Date().toISOString();
    this.patch(n.id, { readAt: now });
    this.store.markRead(n.id);
  }

  protected async acknowledge(n: NotificationDto): Promise<void> {
    const now = new Date().toISOString();
    this.patch(n.id, { acknowledgedAt: now, readAt: now });
    try {
      await firstValueFrom(this.api.acknowledge(n.id));
      this.store.refresh();
    } catch (err) {
      this.patch(n.id, { acknowledgedAt: null });
      this.toast.error('Could not acknowledge', ApiError.from(err).userMessage);
    }
  }

  protected async markAllRead(): Promise<void> {
    const before = this.items();
    const now = new Date().toISOString();
    this.items.update((list) => list.map((n) => (n.readAt || n.requiresAck ? n : { ...n, readAt: now })));
    try {
      await firstValueFrom(this.api.markAllRead());
      this.store.refresh();
    } catch (err) {
      this.items.set(before);
      this.toast.error('Could not mark all as read', ApiError.from(err).userMessage);
    }
  }

  private patch(id: string, changes: Partial<NotificationDto>): void {
    this.items.update((list) => list.map((n) => (n.id === id ? { ...n, ...changes } : n)));
  }
}
