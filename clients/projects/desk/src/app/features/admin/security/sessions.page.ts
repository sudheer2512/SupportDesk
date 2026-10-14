import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { AdminSecurityApi, AdminSessionDto, ApiError, MetaApi } from '@sd/core';
import { SdBadge, SdButton, SdCombobox, SdDateTimePipe, SdDialogService, SdEmptyState, SdIcon, SdOption, SdRelativeTimePipe, SdSkeleton, ToastService } from '@sd/ui';
import { ADMIN_EDITOR_STYLES } from '../admin-shared';

/** Active agent sessions across the organisation (most recent first, keyset-paged) with revoke and "sign out everywhere". */
@Component({
  selector: 'desk-admin-sessions',
  imports: [SdButton, SdBadge, SdIcon, SdCombobox, SdEmptyState, SdSkeleton, SdRelativeTimePipe, SdDateTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="toolbar">
      <sd-combobox class="user" [options]="userOptions()" [value]="userId()" (valueChange)="filterUser($event)" placeholder="All users" ariaLabel="Filter sessions by user" />
      @if (userId()) {
        <button sdButton variant="danger" icon="log-out" (click)="revokeAll()">Sign this user out everywhere</button>
      }
      <span class="sd-spacer"></span>
      <button sdButton variant="ghost" icon="refresh" (click)="reload()">Refresh</button>
    </div>
    @if (loading() && sessions().length === 0) {
      <sd-skeleton [lines]="6" height="52px" />
    } @else if (error()) {
      <sd-empty-state icon="circle-alert" title="Sessions could not be loaded" [message]="error()">
        <button sdButton icon="refresh" (click)="reload()">Try again</button>
      </sd-empty-state>
    } @else if (sessions().length === 0) {
      <sd-empty-state icon="devices" title="No active sessions" [message]="userId() ? 'This user is not signed in anywhere.' : 'Nobody is signed in right now.'" />
    } @else {
      <ul class="rows">
        @for (s of sessions(); track s.id) {
          <li>
            <span class="icon"><sd-icon [name]="s.clientKind === 'mobile' ? 'smartphone' : 'devices'" [size]="16" /></span>
            <div class="grow">
              <div class="title">
                <strong>{{ s.userName }}</strong>
                <span class="sd-muted small">{{ s.email }}</span>
                @if (s.current) {
                  <sd-badge size="sm" tone="primary">This session</sd-badge>
                }
                <sd-badge size="sm" [tone]="s.mfaSatisfied ? 'success' : 'neutral'" [icon]="s.mfaSatisfied ? 'shield-check' : null">{{ s.mfaSatisfied ? 'MFA' : 'Password' }}</sd-badge>
                <sd-badge size="sm" variant="outline">{{ s.clientKind ?? 'web' }}</sd-badge>
              </div>
              <span class="sd-muted small sd-truncate">{{ s.ipAddress ?? 'unknown address' }} · {{ s.userAgent || 'unknown device' }}</span>
              <span class="sd-muted small">Signed in {{ s.createdAt | sdDateTime }} · active {{ s.lastSeenAt | sdRelativeTime }} · expires {{ s.expiresAt | sdRelativeTime }}</span>
            </div>
            <button sdButton size="sm" variant="ghost" icon="log-out" [attr.aria-label]="'Revoke session of ' + s.userName" (click)="revoke(s)">Revoke</button>
          </li>
        }
      </ul>
      @if (nextCursor()) {
        <div class="more"><button sdButton [loading]="loading()" (click)="loadMore()">Load more</button></div>
      }
    }
  `,
  styles: `
    ${ADMIN_EDITOR_STYLES}
    .user { width: min(100%, 320px); }
    .icon { display: grid; place-items: center; width: 32px; height: 32px; flex: none; border-radius: var(--sd-radius-sm); background: var(--sd-surface-sunken); color: var(--sd-text-2); }
    .grow { display: flex; flex-direction: column; gap: 2px; }
    .title { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; }
    .more { display: flex; justify-content: center; margin-top: var(--sd-space-3); }
  `,
})
export class SessionsPage implements OnInit {
  private readonly api = inject(AdminSecurityApi);
  private readonly meta = inject(MetaApi);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(SdDialogService);

  protected readonly sessions = signal<AdminSessionDto[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly userId = signal<string | null>(null);

  protected readonly userOptions = computed<SdOption[]>(() =>
    (this.meta.ticketForm()?.agents ?? []).map((a) => ({ value: a.userId, label: a.name, description: a.email, avatar: a.name })),
  );

  ngOnInit(): void {
    this.meta.load();
    this.reload();
  }

  protected filterUser(value: string | string[] | null): void {
    this.userId.set(typeof value === 'string' ? value : null);
    this.reload();
  }

  protected reload(): void {
    this.sessions.set([]);
    this.nextCursor.set(null);
    this.fetch(null);
  }

  protected loadMore(): void {
    this.fetch(this.nextCursor());
  }

  private fetch(cursor: string | null): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.sessions({ userId: this.userId(), cursor, limit: 50 }).subscribe({
      next: (page) => {
        this.sessions.update((list) => [...list, ...page.items]);
        this.nextCursor.set(page.nextCursor);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.error.set(ApiError.from(err).userMessage);
        this.loading.set(false);
      },
    });
  }

  protected async revoke(s: AdminSessionDto): Promise<void> {
    const ok = await this.dialogs.confirm({
      title: s.current ? 'Sign yourself out?' : `Revoke ${s.userName}'s session?`,
      message: s.current ? 'This is your current session — you will be signed out.' : 'They are signed out on that device at their next request.',
      danger: true,
      confirmLabel: 'Revoke',
    });
    if (!ok) return;
    this.api.revokeSession(s.id).subscribe({
      next: () => {
        this.toast.success('Session revoked', s.userName);
        this.sessions.update((list) => list.filter((x) => x.id !== s.id));
      },
      error: (err: unknown) => this.toast.error('Could not revoke the session', ApiError.from(err).userMessage),
    });
  }

  protected async revokeAll(): Promise<void> {
    const id = this.userId();
    if (!id) return;
    const name = this.userOptions().find((o) => o.value === id)?.label ?? 'this user';
    const ok = await this.dialogs.confirm({ title: `Sign ${name} out everywhere?`, message: 'Every session on every device ends.', danger: true, confirmLabel: 'Sign out everywhere' });
    if (!ok) return;
    this.api.revokeUserSessions(id).subscribe({
      next: (r) => {
        this.toast.success('Signed out everywhere', `${r.revoked} session(s) ended`);
        this.reload();
      },
      error: (err: unknown) => this.toast.error('Could not sign the user out', ApiError.from(err).userMessage),
    });
  }
}
