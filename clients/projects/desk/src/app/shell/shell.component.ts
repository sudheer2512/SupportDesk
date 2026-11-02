import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { RealtimeService, STORAGE_KEYS, safeStorage } from '@sd/core';
import { SdDrawer, SdIcon, SdLogo } from '@sd/ui';
import { filter } from 'rxjs';
import { registerDeskCommands } from '../core/desk-commands';
import { NavListComponent } from './nav-list.component';
import { TopBarComponent } from './top-bar.component';

/**
 * Authenticated layout: collapsible left nav rail (desktop/tablet), drawer navigation on phones,
 * top bar, and the routed module content in <main>.
 */
@Component({
  selector: 'desk-shell',
  imports: [RouterOutlet, NavListComponent, TopBarComponent, SdDrawer, SdIcon, SdLogo],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <a class="sd-skip-link" href="#desk-main" (click)="focusMain($event)">Skip to content</a>
    <div class="shell" [class.is-collapsed]="collapsed()">
      <nav class="rail" aria-label="Main">
        <div class="brand">
          <sd-logo [size]="28" [wordmark]="!collapsed()" />
        </div>
        <desk-nav-list class="rail-list" [collapsed]="collapsed()" />
        <button
          type="button"
          class="collapse"
          [attr.aria-label]="collapsed() ? 'Expand navigation' : 'Collapse navigation'"
          [attr.aria-expanded]="!collapsed()"
          (click)="toggleCollapsed()"
        >
          <sd-icon class="collapse-icon" [class.flip]="collapsed()" name="chevrons-left" [size]="16" />
          @if (!collapsed()) {
            <span>Collapse</span>
          }
        </button>
      </nav>
      <div class="column">
        <desk-top-bar (openNav)="navOpen.set(true)" />
        @if (realtime.state() === 'reconnecting' || (realtime.state() === 'disconnected' && everConnected())) {
          <div class="offline" role="status">Live updates paused — reconnecting…</div>
        }
        <main id="desk-main" tabindex="-1">
          <router-outlet />
        </main>
      </div>
    </div>
    <sd-drawer side="left" width="290px" [(open)]="navOpen" ariaLabel="Navigation" flush>
      <div class="drawer-brand"><sd-logo [size]="28" wordmark /></div>
      <desk-nav-list (navigated)="navOpen.set(false)" />
    </sd-drawer>
  `,
  styles: `
    :host {
      display: block;
      height: 100%;
    }
    .shell {
      display: flex;
      height: 100dvh;
    }
    .rail {
      display: flex;
      flex-direction: column;
      width: var(--sd-nav-width);
      flex: none;
      background: var(--sd-surface);
      border-right: 1px solid var(--sd-border);
      transition: width var(--sd-dur-slow) var(--sd-ease);
      z-index: var(--sd-z-nav);
    }
    .is-collapsed .rail {
      width: var(--sd-nav-width-collapsed);
    }
    .brand {
      display: flex;
      align-items: center;
      height: var(--sd-topbar-h);
      padding: 0 16px;
      flex: none;
      overflow: hidden;
    }
    .rail-list {
      flex: 1;
      min-height: 0;
      overflow-y: auto;
      overflow-x: hidden;
    }
    .collapse {
      display: flex;
      align-items: center;
      gap: 10px;
      height: 40px;
      margin: 8px;
      padding: 0 12px;
      border: 0;
      border-radius: var(--sd-radius-md);
      background: transparent;
      color: var(--sd-text-3);
      cursor: pointer;
      font-size: var(--sd-text-sm);
    }
    .collapse:hover {
      background: var(--sd-surface-3);
      color: var(--sd-text);
    }
    .collapse-icon {
      display: inline-block;
      width: 16px;
      font-size: 18px;
      line-height: 1;
      text-align: center;
      transition: transform var(--sd-dur) var(--sd-ease);
    }
    .collapse-icon.flip {
      transform: rotate(180deg);
    }
    .column {
      display: flex;
      flex-direction: column;
      flex: 1;
      min-width: 0;
    }
    main {
      /* Containing block for absolutely positioned content (visually hidden captions): without it they are laid out
         against the document and make the whole page scroll as well as main. */
      position: relative;
      flex: 1;
      min-height: 0;
      overflow: auto;
      outline: none;
    }
    .offline {
      padding: 4px 16px;
      background: var(--sd-warning-bg);
      color: var(--sd-warning);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-medium);
      text-align: center;
    }
    .drawer-brand {
      display: flex;
      align-items: center;
      height: 52px;
      padding: 0 16px;
      border-bottom: 1px solid var(--sd-divider);
    }
    @media (max-width: 767px) {
      .rail {
        display: none;
      }
    }
    @media (min-width: 768px) and (max-width: 1100px) {
      .rail {
        width: var(--sd-nav-width-collapsed);
      }
    }
  `,
})
export class ShellComponent {
  protected readonly realtime = inject(RealtimeService);
  protected readonly navOpen = signal(false);
  protected readonly collapsed = signal(safeStorage.get(STORAGE_KEYS.navCollapsed) === '1');
  protected readonly everConnected = signal(false);

  constructor() {
    registerDeskCommands();
    const router = inject(Router);
    const sub = router.events.pipe(filter((e) => e instanceof NavigationEnd)).subscribe(() => this.navOpen.set(false));
    inject(DestroyRef).onDestroy(() => sub.unsubscribe());
    // Only warn about lost live updates after a connection was established once (not while the API is down at start-up).
    effect(() => {
      if (this.realtime.connected()) this.everConnected.set(true);
    });
  }

  protected toggleCollapsed(): void {
    this.collapsed.update((c) => !c);
    safeStorage.set(STORAGE_KEYS.navCollapsed, this.collapsed() ? '1' : '0');
  }

  protected focusMain(event: Event): void {
    event.preventDefault();
    document.getElementById('desk-main')?.focus();
  }
}
