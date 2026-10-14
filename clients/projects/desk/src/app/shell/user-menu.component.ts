import { CdkMenu, CdkMenuItem, CdkMenuTrigger } from '@angular/cdk/menu';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService, MeStore, ShortcutRegistry } from '@sd/core';
import { SdAvatar, SdIcon } from '@sd/ui';

/** Avatar button → profile, change password, two-factor setup, shortcuts, sign out. */
@Component({
  selector: 'desk-user-menu',
  imports: [CdkMenuTrigger, CdkMenu, CdkMenuItem, SdAvatar, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button type="button" class="trigger" [cdkMenuTriggerFor]="menu" [attr.aria-label]="'Account menu for ' + me.displayName()">
      <sd-avatar [name]="me.displayName()" [src]="me.user()?.avatarUrl ?? null" size="sm" [presence]="me.availability()" />
    </button>
    <ng-template #menu>
      <div class="sd-menu menu" cdkMenu aria-label="Account">
        <div class="who" role="presentation">
          <sd-avatar [name]="me.displayName()" [src]="me.user()?.avatarUrl ?? null" size="lg" />
          <div class="who-text">
            <strong class="sd-truncate">{{ me.displayName() }}</strong>
            <span class="sd-truncate sd-muted sd-small">{{ me.user()?.email }}</span>
            <span class="sd-truncate sd-muted sd-xs">{{ me.user()?.tenantName }}</span>
          </div>
        </div>
        <div class="sd-menu-divider" role="separator"></div>
        <button type="button" class="sd-menu-item" cdkMenuItem (cdkMenuItemTriggered)="go('profile')">
          <sd-icon name="user" [size]="15" />My profile
        </button>
        <button type="button" class="sd-menu-item" cdkMenuItem (cdkMenuItemTriggered)="go('security')">
          <sd-icon name="key" [size]="15" />Change password
        </button>
        <button type="button" class="sd-menu-item" cdkMenuItem (cdkMenuItemTriggered)="go('mfa')">
          <sd-icon name="shield-check" [size]="15" />Two-factor authentication
          <span class="sd-menu-hint">{{ me.user()?.mfaEnabled ? 'On' : 'Off' }}</span>
        </button>
        <button type="button" class="sd-menu-item" cdkMenuItem (cdkMenuItemTriggered)="shortcuts.helpOpen.set(true)">
          <sd-icon name="keyboard" [size]="15" />Keyboard shortcuts
          <span class="sd-menu-hint">?</span>
        </button>
        <div class="sd-menu-divider" role="separator"></div>
        <button type="button" class="sd-menu-item" cdkMenuItem (cdkMenuItemTriggered)="auth.logout()">
          <sd-icon name="log-out" [size]="15" />Sign out
        </button>
      </div>
    </ng-template>
  `,
  styles: `
    .trigger {
      display: inline-flex;
      padding: 2px;
      border: 0;
      border-radius: 50%;
      background: transparent;
      cursor: pointer;
    }
    .trigger:hover {
      box-shadow: 0 0 0 3px var(--sd-surface-3);
    }
    .menu {
      min-width: 260px;
    }
    .who {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 8px 10px;
    }
    .who-text {
      display: flex;
      flex-direction: column;
      min-width: 0;
    }
  `,
})
export class UserMenuComponent {
  protected readonly me = inject(MeStore);
  protected readonly auth = inject(AuthService);
  protected readonly shortcuts = inject(ShortcutRegistry);
  private readonly router = inject(Router);

  protected go(section: 'profile' | 'security' | 'mfa'): void {
    void this.router.navigate(['/me'], { queryParams: section === 'profile' ? {} : { section } });
  }
}
