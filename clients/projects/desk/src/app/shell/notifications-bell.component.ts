import { CdkConnectedOverlay, CdkOverlayOrigin, ConnectedPosition } from '@angular/cdk/overlay';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { DesktopNotifier, NotificationDto, NotificationStore, severityRank } from '@sd/core';
import { SdButton, SdIcon, SdRelativeTimePipe, SdSkeleton } from '@sd/ui';

const POSITIONS: ConnectedPosition[] = [
  { originX: 'end', originY: 'bottom', overlayX: 'end', overlayY: 'top', offsetY: 6 },
  { originX: 'center', originY: 'bottom', overlayX: 'center', overlayY: 'top', offsetY: 6 },
];

/**
 * Bell with unread count and a panel of the latest notifications (GET /notifications?limit=20), kept
 * live by the realtime `notification` event. Click → mark read + follow the link; acknowledgement for
 * notifications that require it; "mark all read"; opt-in desktop alerts (requested on click).
 */
@Component({
  selector: 'desk-notifications-bell',
  imports: [CdkOverlayOrigin, CdkConnectedOverlay, SdButton, SdIcon, SdRelativeTimePipe, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      type="button"
      class="bell"
      cdkOverlayOrigin
      #origin="cdkOverlayOrigin"
      [attr.aria-label]="'Notifications' + (store.unreadCount() ? ', ' + store.unreadCount() + ' unread' : '')"
      [attr.aria-expanded]="open()"
      aria-haspopup="dialog"
      (click)="toggle()"
    >
      <sd-icon [name]="store.pendingAckCount() ? 'bell-ring' : 'bell'" [size]="18" />
      @if (store.unreadCount()) {
        <span class="count" aria-hidden="true">{{ store.unreadCount() > 99 ? '99+' : store.unreadCount() }}</span>
      }
    </button>
    <ng-template
      cdkConnectedOverlay
      [cdkConnectedOverlayOrigin]="origin"
      [cdkConnectedOverlayOpen]="open()"
      [cdkConnectedOverlayPositions]="positions"
      (overlayOutsideClick)="open.set(false)"
      (detach)="open.set(false)"
    >
      <section class="sd-popover panel" role="dialog" aria-label="Notifications" (keydown.escape)="open.set(false)">
        <header class="head">
          <h2>Notifications</h2>
          <span class="sd-spacer"></span>
          <button sdButton variant="link" size="sm" type="button" [disabled]="!store.unreadCount()" (click)="store.markAllRead()">
            Mark all read
          </button>
        </header>
        @if (notifier.permission() === 'default') {
          <div class="desktop">
            <sd-icon name="devices" [size]="15" />
            <span>Get desktop alerts for urgent notifications.</span>
            <button sdButton size="sm" variant="subtle" type="button" (click)="notifier.requestPermission()">Enable</button>
          </div>
        }
        <div class="list" role="list">
          @if (store.loading() && store.items().length === 0) {
            @for (n of [1, 2, 3]; track n) {
              <div class="item"><sd-skeleton [lines]="2" /></div>
            }
          } @else if (store.error() && store.items().length === 0) {
            <p class="empty">{{ store.error() }} <button sdButton variant="link" size="sm" type="button" (click)="store.refresh()">Retry</button></p>
          } @else {
            @for (n of store.items(); track n.id) {
              <div class="item" role="listitem" [class.is-unread]="!n.readAt">
                <span class="sev" [class]="'sev sev--' + tone(n)" aria-hidden="true"><sd-icon [name]="icon(n)" [size]="14" /></span>
                <button type="button" class="body" (click)="openItem(n)">
                  <span class="title">{{ n.title }}</span>
                  @if (n.body) {
                    <span class="text">{{ n.body }}</span>
                  }
                  <span class="time">{{ n.createdAt | sdRelativeTime }}</span>
                </button>
                @if (n.requiresAck && !n.acknowledgedAt) {
                  <button sdButton size="sm" variant="secondary" type="button" (click)="store.acknowledge(n.id)">Acknowledge</button>
                } @else if (!n.readAt) {
                  <button type="button" class="mark" aria-label="Mark as read" (click)="store.markRead(n.id)">
                    <span class="unread-dot"></span>
                  </button>
                }
              </div>
            } @empty {
              <p class="empty">You're all caught up.</p>
            }
          }
        </div>
        <footer class="foot">
          <button sdButton variant="link" size="sm" type="button" (click)="viewAll()">View all notifications</button>
        </footer>
      </section>
    </ng-template>
  `,
  styles: `
    .bell {
      position: relative;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 36px;
      height: 36px;
      border: 0;
      border-radius: var(--sd-radius-md);
      background: transparent;
      color: var(--sd-text-2);
      cursor: pointer;
    }
    .bell:hover,
    .bell[aria-expanded='true'] {
      background: var(--sd-surface-3);
      color: var(--sd-text);
    }
    .count {
      position: absolute;
      top: 3px;
      right: 2px;
      min-width: 17px;
      height: 17px;
      padding: 0 4px;
      border: 2px solid var(--sd-surface);
      border-radius: var(--sd-radius-pill);
      background: var(--sd-danger);
      color: #fff;
      font-size: 10px;
      font-weight: var(--sd-weight-bold);
      line-height: 13px;
      text-align: center;
    }
    .panel {
      width: min(400px, calc(100vw - 16px));
      max-height: min(560px, calc(100dvh - 80px));
      display: flex;
      flex-direction: column;
      overflow: hidden;
    }
    .head {
      display: flex;
      align-items: center;
      padding: 12px 14px 8px;
    }
    h2 {
      font-size: var(--sd-text-md);
    }
    .desktop {
      display: flex;
      align-items: center;
      gap: 8px;
      margin: 0 12px 8px;
      padding: 8px 10px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-info-bg);
      color: var(--sd-info);
      font-size: var(--sd-text-sm);
    }
    .desktop span {
      flex: 1;
    }
    .list {
      overflow: auto;
      padding: 0 6px 8px;
    }
    .foot {
      display: flex;
      justify-content: center;
      padding: 6px 12px 8px;
      border-top: 1px solid var(--sd-divider);
    }
    .item {
      display: flex;
      align-items: flex-start;
      gap: 10px;
      padding: 10px 8px;
      border-radius: var(--sd-radius-md);
    }
    .item:hover {
      background: var(--sd-surface-2);
    }
    .item.is-unread .title {
      font-weight: var(--sd-weight-semibold);
    }
    .sev {
      display: inline-flex;
      padding: 6px;
      border-radius: 50%;
      background: var(--sd-surface-3);
      color: var(--sd-text-2);
      flex: none;
    }
    .sev--warning {
      background: var(--sd-warning-bg);
      color: var(--sd-warning);
    }
    .sev--danger {
      background: var(--sd-danger-bg);
      color: var(--sd-danger);
    }
    .sev--success {
      background: var(--sd-success-bg);
      color: var(--sd-success);
    }
    .body {
      display: flex;
      flex-direction: column;
      gap: 2px;
      flex: 1;
      min-width: 0;
      padding: 0;
      border: 0;
      background: transparent;
      color: inherit;
      text-align: left;
      cursor: pointer;
    }
    .text {
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
      display: -webkit-box;
      -webkit-line-clamp: 2;
      -webkit-box-orient: vertical;
      overflow: hidden;
    }
    .time {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .mark {
      display: inline-flex;
      padding: 6px;
      border: 0;
      border-radius: 50%;
      background: transparent;
      cursor: pointer;
    }
    .mark:hover {
      background: var(--sd-surface-3);
    }
    .unread-dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: var(--sd-primary);
    }
    .empty {
      padding: 28px 16px;
      color: var(--sd-text-3);
      text-align: center;
    }
  `,
})
export class NotificationsBellComponent {
  protected readonly store = inject(NotificationStore);
  protected readonly notifier = inject(DesktopNotifier);
  private readonly router = inject(Router);
  protected readonly positions = POSITIONS;
  protected readonly open = signal(false);
  protected readonly hasItems = computed(() => this.store.items().length > 0);

  protected toggle(): void {
    const next = !this.open();
    this.open.set(next);
    if (next) this.store.refresh();
  }

  protected viewAll(): void {
    this.open.set(false);
    void this.router.navigateByUrl('/notifications');
  }

  protected openItem(n: NotificationDto): void {
    if (!n.readAt) this.store.markRead(n.id);
    if (n.link) {
      this.open.set(false);
      if (/^https?:\/\//i.test(n.link)) window.open(n.link, '_blank', 'noopener');
      else void this.router.navigateByUrl(n.link);
    }
  }

  protected tone(n: NotificationDto): string {
    const rank = severityRank(n.severity);
    return rank >= 3 ? 'danger' : rank === 2 ? 'warning' : rank === 1 ? 'success' : 'info';
  }

  protected icon(n: NotificationDto): string {
    const t = this.tone(n);
    return t === 'danger' ? 'circle-alert' : t === 'warning' ? 'triangle-alert' : t === 'success' ? 'circle-check' : 'bell';
  }
}
