import { ChangeDetectionStrategy, Component, computed, inject, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MeStore, ShortcutRegistry, ThemeService } from '@sd/core';
import { CommandPaletteService, SdButton, SdIcon, SdTooltip } from '@sd/ui';
import { AvailabilityMenuComponent } from './availability-menu.component';
import { NotificationsBellComponent } from './notifications-bell.component';
import { UserMenuComponent } from './user-menu.component';

/** Top bar: menu (phones), global search → command palette, New ticket, bell, availability, theme, user menu. */
@Component({
  selector: 'desk-top-bar',
  imports: [RouterLink, SdButton, SdIcon, SdTooltip, NotificationsBellComponent, AvailabilityMenuComponent, UserMenuComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="bar">
      <button type="button" class="icon-btn menu" aria-label="Open navigation" (click)="openNav.emit()">
        <sd-icon name="menu" [size]="20" />
      </button>
      <button type="button" class="search" (click)="palette.open()" aria-label="Search and commands" aria-haspopup="dialog">
        <sd-icon name="search" [size]="16" />
        <span class="search-text">Search tickets, customers, commands…</span>
        <span class="keys" aria-hidden="true"><kbd>{{ modKey() }}</kbd><kbd>K</kbd></span>
      </button>
      <span class="sd-spacer"></span>
      @if (me.can('tickets.create')) {
        <a sdButton variant="primary" icon="plus" routerLink="/tickets/new" class="new-ticket">
          <span class="new-label">New ticket</span>
        </a>
      }
      <desk-notifications-bell />
      <desk-availability-menu class="availability" />
      <button
        type="button"
        class="icon-btn"
        [attr.aria-label]="theme.isDark() ? 'Switch to light theme' : 'Switch to dark theme'"
        [sdTooltip]="theme.isDark() ? 'Light theme' : 'Dark theme'"
        tooltipPosition="bottom"
        (click)="theme.toggle()"
      >
        <sd-icon [name]="theme.isDark() ? 'sun' : 'moon'" [size]="18" />
      </button>
      <desk-user-menu />
    </header>
  `,
  styles: `
    :host {
      display: block;
      flex: none;
    }
    .bar {
      display: flex;
      align-items: center;
      gap: 8px;
      height: var(--sd-topbar-h);
      padding: 0 16px;
      background: var(--sd-surface);
      border-bottom: 1px solid var(--sd-border);
    }
    .icon-btn {
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
    .icon-btn:hover {
      background: var(--sd-surface-3);
      color: var(--sd-text);
    }
    .menu {
      display: none;
    }
    .search {
      display: flex;
      align-items: center;
      gap: 8px;
      width: min(440px, 40vw);
      height: 34px;
      padding: 0 8px 0 12px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface-2);
      color: var(--sd-text-3);
      cursor: text;
      text-align: left;
      transition: border-color var(--sd-dur-fast) var(--sd-ease), background-color var(--sd-dur-fast) var(--sd-ease);
    }
    .search:hover {
      border-color: var(--sd-border-strong);
      background: var(--sd-surface);
    }
    .search-text {
      flex: 1;
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .keys {
      display: inline-flex;
      gap: 3px;
    }
    @media (max-width: 767px) {
      .bar {
        padding: 0 8px;
        gap: 4px;
      }
      .menu {
        display: inline-flex;
      }
      .search {
        width: 36px;
        padding: 0;
        justify-content: center;
        border: 0;
        background: transparent;
      }
      .search-text,
      .keys,
      .availability {
        display: none;
      }
      .new-label {
        display: none;
      }
      .new-ticket {
        width: 36px;
        padding: 0;
      }
    }
  `,
})
export class TopBarComponent {
  readonly openNav = output<void>();
  protected readonly me = inject(MeStore);
  protected readonly theme = inject(ThemeService);
  protected readonly palette = inject(CommandPaletteService);
  private readonly shortcuts = inject(ShortcutRegistry);
  protected readonly modKey = computed(() => (this.shortcuts.isMac ? '⌘' : 'Ctrl'));
}
