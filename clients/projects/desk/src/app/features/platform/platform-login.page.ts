import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ApiError, PlatformSession, ThemeService } from '@sd/core';
import { SdButton, SdFormField, SdIcon, SdInput, SdLogo } from '@sd/ui';

/** Platform operator sign-in (separate from workspace sign-in; operators use TOTP once enrolled). */
@Component({
  selector: 'desk-platform-login-page',
  imports: [FormsModule, RouterLink, SdButton, SdFormField, SdIcon, SdInput, SdLogo],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <button type="button" class="theme" [attr.aria-label]="theme.isDark() ? 'Switch to light theme' : 'Switch to dark theme'" (click)="theme.toggle()">
        <sd-icon [name]="theme.isDark() ? 'sun' : 'moon'" [size]="18" />
      </button>
      <main class="card">
        <sd-logo [size]="32" wordmark class="logo" />
        <h1>Platform console</h1>
        <p class="lead">For SupportDesk operators. Workspace users sign in <a routerLink="/login">here</a>.</p>
        @if (expired()) {
          <div class="callout callout--info" role="status"><sd-icon name="info" [size]="16" />Your platform session ended. Sign in again.</div>
        }
        @if (error()) {
          <div class="callout callout--danger" role="alert"><sd-icon name="circle-alert" [size]="16" />{{ error() }}</div>
        }
        <form class="form" (ngSubmit)="submit()" novalidate>
          <sd-form-field label="Operator e-mail" required>
            <input sdInput type="email" name="email" autocomplete="username" [(ngModel)]="email" required />
          </sd-form-field>
          <sd-form-field label="Password" required>
            <input sdInput type="password" name="password" autocomplete="current-password" [(ngModel)]="password" required />
          </sd-form-field>
          @if (needsCode()) {
            <sd-form-field label="Authenticator code" required>
              <input sdInput name="code" inputmode="numeric" autocomplete="one-time-code" maxlength="8" [(ngModel)]="code" required />
            </sd-form-field>
          }
          <button sdButton variant="primary" size="lg" type="submit" block [loading]="busy()">Sign in</button>
        </form>
      </main>
    </div>
  `,
  styles: `
    :host { display: block; height: 100dvh; overflow: auto; background: var(--sd-bg); }
    .page { position: relative; min-height: 100%; display: flex; align-items: center; justify-content: center; padding: 48px 16px; }
    .theme { position: absolute; top: 16px; right: 16px; display: inline-flex; padding: 8px; border: 1px solid var(--sd-border); border-radius: var(--sd-radius-md);
      background: var(--sd-surface); color: var(--sd-text-2); cursor: pointer; }
    .card { width: min(420px, 100%); padding: 32px; border: 1px solid var(--sd-border); border-radius: var(--sd-radius-xl); background: var(--sd-surface); box-shadow: var(--sd-shadow-lg); }
    .logo { display: inline-flex; margin-bottom: 18px; }
    h1 { margin: 0; font-size: var(--sd-text-2xl); font-weight: var(--sd-weight-bold); }
    .lead { margin: 6px 0 20px; color: var(--sd-text-2); }
    .form { display: grid; gap: 16px; }
    .callout { display: flex; align-items: flex-start; gap: 8px; margin-bottom: 16px; padding: 10px 12px; border: 1px solid transparent; border-radius: var(--sd-radius-md); font-size: var(--sd-text-sm); }
    .callout--danger { background: var(--sd-danger-bg); border-color: var(--sd-danger-border); color: var(--sd-danger); }
    .callout--info { background: var(--sd-info-bg); border-color: var(--sd-info-border); color: var(--sd-text); }
    @media (max-width: 480px) { .card { padding: 24px 18px; box-shadow: none; } }
  `,
})
export class PlatformLoginPage {
  readonly returnUrl = input<string | undefined>();
  readonly expiredParam = input<string | undefined>(undefined, { alias: 'expired' });

  private readonly session = inject(PlatformSession);
  private readonly router = inject(Router);
  protected readonly theme = inject(ThemeService);

  protected email = '';
  protected password = '';
  protected code = '';
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly needsCode = signal(false);
  protected readonly expired = computed(() => this.expiredParam() === '1');

  protected async submit(): Promise<void> {
    if (!/^\S+@\S+\.\S+$/.test(this.email.trim()) || !this.password) {
      this.error.set('Enter your e-mail address and password.');
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      const r = await this.session.login(this.email, this.password, this.needsCode() ? this.code : null);
      if (r.status === 'ok') {
        const target = this.returnUrl();
        await this.router.navigateByUrl(target && target.startsWith('/platform') ? target : '/platform');
        return;
      }
      if (r.status === 'mfa_required') this.needsCode.set(true);
      this.error.set(r.message ?? 'Sign-in failed.');
    } catch (e) {
      this.error.set(e instanceof ApiError ? ((e.problem as { message?: string } | null)?.message ?? 'Invalid credentials.') : 'Sign-in failed.');
    } finally {
      this.busy.set(false);
    }
  }
}
