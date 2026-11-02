import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { PlatformSession, ThemeService } from '@sd/core';
import { SdButton, SdIcon, SdLogo } from '@sd/ui';

/** Layout of the platform console: its own top bar and navigation (not the tenant nav rail). */
@Component({
  selector: 'desk-platform-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, SdButton, SdIcon, SdLogo],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <a class="sd-skip-link" href="#platform-main">Skip to content</a>
    <header class="bar">
      <div class="brand">
        <sd-logo [size]="26" />
        <span class="title">Platform console</span>
      </div>
      <nav aria-label="Platform console">
        @for (item of nav; track item.path) {
          <a [routerLink]="item.path" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: item.exact }">
            <sd-icon [name]="item.icon" [size]="16" />{{ item.label }}
          </a>
        }
      </nav>
      <div class="me">
        <span class="who">
          <span class="name">{{ session.displayName() }}</span>
          <span class="exp">session ends in {{ remaining() }}</span>
        </span>
        <button sdButton variant="ghost" size="sm" iconOnly [icon]="theme.isDark() ? 'sun' : 'moon'"
          [attr.aria-label]="theme.isDark() ? 'Switch to light theme' : 'Switch to dark theme'" (click)="theme.toggle()"></button>
        <button sdButton variant="ghost" size="sm" icon="log-out" (click)="session.logout()">Sign out</button>
      </div>
    </header>
    <main id="platform-main" class="main" tabindex="-1">
      <router-outlet />
    </main>
  `,
  styles: `
    :host { display: flex; flex-direction: column; height: 100dvh; background: var(--sd-bg); }
    .bar { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 20px; padding: 10px 20px; border-bottom: 1px solid var(--sd-border);
      background: var(--sd-surface); }
    .brand { display: flex; align-items: center; gap: 10px; }
    .title { font-weight: var(--sd-weight-semibold); }
    nav { display: flex; gap: 4px; overflow-x: auto; flex: 1 1 auto; min-width: 0; scrollbar-width: thin; }
    nav a { display: inline-flex; align-items: center; gap: 6px; padding: 6px 10px; border-radius: var(--sd-radius-md); color: var(--sd-text-2);
      text-decoration: none; white-space: nowrap; font-size: var(--sd-text-sm); }
    nav a:hover { background: var(--sd-row-hover); color: var(--sd-text); }
    nav a.active { background: var(--sd-primary-soft); color: var(--sd-primary); font-weight: var(--sd-weight-medium); }
    nav a:focus-visible { outline: none; box-shadow: var(--sd-focus-ring); }
    .me { display: flex; align-items: center; gap: 6px; }
    .who { display: grid; text-align: right; line-height: 1.2; }
    .name { font-size: var(--sd-text-sm); font-weight: var(--sd-weight-medium); }
    .exp { font-size: var(--sd-text-xs); color: var(--sd-text-3); }
    .main { position: relative; flex: 1; overflow: auto; outline: none; }
    @media (max-width: 720px) {
      .bar { padding: 8px 16px; }
      nav { order: 3; flex-basis: 100%; }
      .who { display: none; }
    }
  `,
})
export class PlatformShellComponent {
  protected readonly session = inject(PlatformSession);
  protected readonly theme = inject(ThemeService);
  private readonly now = signal(Date.now());

  protected readonly nav = [
    { path: '/platform', label: 'Overview', icon: 'dashboard', exact: true },
    { path: '/platform/tenants', label: 'Tenants', icon: 'building', exact: false },
    { path: '/platform/plans', label: 'Plans', icon: 'layers', exact: false },
    { path: '/platform/invoices', label: 'Invoices', icon: 'receipt', exact: false },
    { path: '/platform/support', label: 'Support access', icon: 'life-buoy', exact: false },
    { path: '/platform/audit', label: 'Audit', icon: 'shield-check', exact: false },
  ];

  protected readonly remaining = computed(() => {
    const exp = this.session.expiry();
    if (!exp) return '—';
    const mins = Math.max(0, Math.round((exp - this.now()) / 60_000));
    return mins >= 1 ? `${mins} min` : 'under a minute';
  });

  constructor() {
    const timer = setInterval(() => this.now.set(Date.now()), 30_000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }
}
