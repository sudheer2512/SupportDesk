import { CdkMenu, CdkMenuItem, CdkMenuTrigger } from '@angular/cdk/menu';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { PortalAuthService, ThemeService } from '@sd/core';
import { SdAvatar, SdButton, SdDrawer, SdEmptyState, SdIcon, SdSkeleton } from '@sd/ui';
import { filter } from 'rxjs';
import { BrandService } from '../brand.service';

interface PortalNavItem {
  label: string;
  path: string;
  icon: string;
  exact?: boolean;
}

/**
 * Branded portal layout: header (brand logo/title, feature-aware nav, sign-in / account menu, theme
 * toggle), routed content and footer (brand footer HTML via Angular's sanitizer, support contacts).
 */
@Component({
  selector: 'portal-shell',
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    CdkMenuTrigger,
    CdkMenu,
    CdkMenuItem,
    SdButton,
    SdIcon,
    SdAvatar,
    SdDrawer,
    SdEmptyState,
    SdSkeleton,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <a class="sd-skip-link" href="#portal-main" (click)="skip($event)">Skip to content</a>
    <header class="header">
      <div class="portal-container bar">
        <a class="brand" [routerLink]="base()" [attr.aria-label]="brand.name() + ' — home'">
          @if (brand.config()?.logoUrl) {
            <img [src]="brand.config()!.logoUrl" [alt]="brand.config()!.name" class="logo" />
          } @else {
            <span class="mark" aria-hidden="true">{{ initial() }}</span>
          }
          <span class="title">{{ brand.name() }}</span>
        </a>
        <nav class="nav" aria-label="Portal">
          @for (item of nav(); track item.path) {
            <a
              class="nav-link"
              [routerLink]="base() + item.path"
              routerLinkActive="is-active"
              [routerLinkActiveOptions]="{ exact: !!item.exact }"
              ariaCurrentWhenActive="page"
              >{{ item.label }}</a
            >
          }
        </nav>
        <span class="sd-spacer"></span>
        <button
          type="button"
          class="icon-btn"
          [attr.aria-label]="theme.isDark() ? 'Switch to light theme' : 'Switch to dark theme'"
          (click)="theme.toggle()"
        >
          <sd-icon [name]="theme.isDark() ? 'sun' : 'moon'" [size]="18" />
        </button>
        @if (auth.contact(); as contact) {
          <button type="button" class="account" [cdkMenuTriggerFor]="accountMenu" [attr.aria-label]="'Account menu for ' + contact.fullName">
            <sd-avatar [name]="contact.fullName" size="sm" />
            <span class="account-name">{{ contact.fullName }}</span>
          </button>
          <ng-template #accountMenu>
            <div class="sd-menu" cdkMenu aria-label="Account">
              <div class="sd-menu-label" role="presentation">{{ contact.customerName || contact.email }}</div>
              <a class="sd-menu-item" cdkMenuItem [routerLink]="base() + '/tickets'"><sd-icon name="ticket" [size]="15" />My tickets</a>
              <a class="sd-menu-item" cdkMenuItem [routerLink]="base() + '/account'"><sd-icon name="user" [size]="15" />Account</a>
              <div class="sd-menu-divider" role="separator"></div>
              <button type="button" class="sd-menu-item" cdkMenuItem (cdkMenuItemTriggered)="auth.logout()">
                <sd-icon name="log-out" [size]="15" />Sign out
              </button>
            </div>
          </ng-template>
        } @else if (brand.state() === 'ready') {
          <a sdButton variant="primary" size="sm" [routerLink]="base() + '/login'" class="signin">Sign in</a>
        }
        <button type="button" class="icon-btn menu" aria-label="Open menu" (click)="menuOpen.set(true)">
          <sd-icon name="menu" [size]="20" />
        </button>
      </div>
    </header>

    <main id="portal-main" tabindex="-1" class="main">
      @switch (brand.state()) {
        @case ('loading') {
          <div class="portal-container loading" aria-busy="true">
            <sd-skeleton height="28px" width="40%" />
            <sd-skeleton [lines]="4" />
          </div>
        }
        @case ('not-found') {
          <sd-empty-state icon="search" title="Support portal not found" message="Check the address — this portal does not exist or is no longer available." />
        }
        @case ('error') {
          <sd-empty-state icon="circle-alert" title="The portal could not be loaded" message="Please try again in a moment.">
            <button sdButton type="button" (click)="reload()">Try again</button>
          </sd-empty-state>
        }
        @default {
          <router-outlet />
        }
      }
    </main>

    <footer class="footer">
      <div class="portal-container foot">
        @if (brand.config()?.footerHtml) {
          <div class="sd-prose footer-html" [innerHTML]="brand.config()!.footerHtml"></div>
        }
        <div class="contacts">
          @if (brand.config()?.supportEmail; as email) {
            <a [href]="'mailto:' + email"><sd-icon name="mail" [size]="14" />{{ email }}</a>
          }
          @if (brand.config()?.supportPhone; as phone) {
            <a [href]="'tel:' + phone"><sd-icon name="phone" [size]="14" />{{ phone }}</a>
          }
          <span class="powered">Powered by SupportDesk</span>
        </div>
      </div>
    </footer>

    <sd-drawer side="bottom" [(open)]="menuOpen" title="Menu">
      <nav class="sheet-nav" aria-label="Portal">
        @for (item of nav(); track item.path) {
          <a class="sheet-link" [routerLink]="base() + item.path"><sd-icon [name]="item.icon" [size]="18" />{{ item.label }}</a>
        }
        @if (!auth.contact()) {
          <a class="sheet-link" [routerLink]="base() + '/login'"><sd-icon name="log-in" [size]="18" />Sign in</a>
        } @else {
          <button type="button" class="sheet-link" (click)="auth.logout()"><sd-icon name="log-out" [size]="18" />Sign out</button>
        }
      </nav>
    </sd-drawer>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      min-height: 100dvh;
    }
    .header {
      position: sticky;
      top: 0;
      z-index: var(--sd-z-header);
      background: color-mix(in oklab, var(--sd-surface) 88%, transparent);
      backdrop-filter: saturate(1.4) blur(10px);
      border-bottom: 1px solid var(--sd-border);
    }
    .bar {
      display: flex;
      align-items: center;
      gap: 12px;
      height: 60px;
    }
    .brand {
      display: inline-flex;
      align-items: center;
      gap: 10px;
      min-width: 0;
      color: var(--sd-text);
      text-decoration: none;
    }
    .brand:hover {
      text-decoration: none;
    }
    .logo {
      height: 32px;
      width: auto;
      max-width: 160px;
      object-fit: contain;
    }
    .mark {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 32px;
      height: 32px;
      border-radius: 9px;
      background: var(--sd-primary);
      color: var(--sd-on-primary);
      font-weight: var(--sd-weight-bold);
    }
    .title {
      font-size: var(--sd-text-lg);
      font-weight: var(--sd-weight-semibold);
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .nav {
      display: flex;
      gap: 2px;
      margin-left: 16px;
    }
    .nav-link {
      padding: 8px 10px;
      border-radius: var(--sd-radius-md);
      color: var(--sd-text-2);
      font-weight: var(--sd-weight-medium);
      white-space: nowrap;
      text-decoration: none;
    }
    .nav-link:hover {
      background: var(--sd-surface-3);
      color: var(--sd-text);
      text-decoration: none;
    }
    .nav-link.is-active {
      color: var(--sd-primary);
      background: var(--sd-primary-soft);
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
    .account {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      padding: 3px 10px 3px 3px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface);
      color: var(--sd-text);
      cursor: pointer;
    }
    .account-name {
      max-width: 140px;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      font-weight: var(--sd-weight-medium);
    }
    .main {
      flex: 1;
      outline: none;
    }
    .loading {
      display: grid;
      gap: 16px;
      padding: 48px 0;
    }
    .footer {
      margin-top: 48px;
      border-top: 1px solid var(--sd-border);
      background: var(--sd-surface);
    }
    .foot {
      display: grid;
      gap: 12px;
      padding: 24px 0 32px;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .contacts {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 16px;
    }
    .contacts a {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      color: var(--sd-text-2);
    }
    .powered {
      margin-left: auto;
      color: var(--sd-text-3);
    }
    .sheet-nav {
      display: grid;
      gap: 4px;
    }
    .sheet-link {
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 12px;
      border: 0;
      border-radius: var(--sd-radius-md);
      background: transparent;
      color: var(--sd-text);
      font-size: var(--sd-text-lg);
      text-align: left;
      text-decoration: none;
      cursor: pointer;
    }
    .sheet-link:hover {
      background: var(--sd-surface-3);
      text-decoration: none;
    }
    @media (max-width: 860px) {
      .nav,
      .account-name {
        display: none;
      }
      .menu {
        display: inline-flex;
      }
    }
    @media (max-width: 480px) {
      .signin {
        display: none;
      }
    }
  `,
})
export class PortalShellComponent {
  protected readonly brand = inject(BrandService);
  protected readonly auth = inject(PortalAuthService);
  protected readonly theme = inject(ThemeService);
  protected readonly menuOpen = signal(false);
  protected readonly base = computed(() => `/${this.brand.slug() ?? ''}`);
  protected readonly initial = computed(() => (this.brand.name()[0] ?? 'S').toUpperCase());
  protected readonly nav = computed<PortalNavItem[]>(() => {
    const f = this.brand.features();
    const items: PortalNavItem[] = [{ label: 'Home', path: '', icon: 'home', exact: true }];
    if (f.kb) items.push({ label: 'Knowledge Base', path: '/kb', icon: 'book-open' });
    if (f.community) items.push({ label: 'Community', path: '/community', icon: 'messages' });
    items.push({ label: 'My Tickets', path: '/tickets', icon: 'ticket' });
    items.push({ label: 'Submit a Ticket', path: '/submit', icon: 'file-plus' });
    if (this.auth.isAuthenticated()) items.push({ label: 'Account', path: '/account', icon: 'user' });
    return items;
  });

  constructor() {
    const sub = inject(Router)
      .events.pipe(filter((e) => e instanceof NavigationEnd))
      .subscribe(() => this.menuOpen.set(false));
    inject(DestroyRef).onDestroy(() => sub.unsubscribe());
  }

  protected reload(): void {
    const slug = this.brand.slug();
    if (slug) {
      this.brand.state.set('idle');
      void this.brand.load(slug);
    }
  }

  protected skip(event: Event): void {
    event.preventDefault();
    document.getElementById('portal-main')?.focus();
  }
}
