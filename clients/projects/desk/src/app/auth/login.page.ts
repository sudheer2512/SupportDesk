import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ApiError, AuthService, LoginMfaRequiredResponse, LoginResponse, MfaMethod, ThemeService } from '@sd/core';
import { SdButton, SdFormField, SdIcon, SdInput, SdLogo } from '@sd/ui';

type Step = 'credentials' | 'mfa' | 'expired';

/**
 * Agent/admin sign-in: workspace (tenant slug) + e-mail + password, then an MFA code step when required.
 * A `demoCode` returned by demo environments is shown, clearly labelled. Tenant and e-mail are remembered
 * on this device (never the password).
 */
@Component({
  selector: 'desk-login-page',
  imports: [FormsModule, RouterLink, SdButton, SdFormField, SdInput, SdIcon, SdLogo],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <aside class="hero" aria-hidden="true">
        <sd-logo [size]="34" wordmark class="hero-logo" />
        <div class="hero-copy">
          <h2>Every customer conversation, one calm workspace.</h2>
          <ul>
            <li><sd-icon name="ticket" [size]="16" />Tickets from e-mail, portal, chat and phone in one queue</li>
            <li><sd-icon name="timer" [size]="16" />SLA timers that warn before anything breaches</li>
            <li><sd-icon name="sparkles" [size]="16" />AI summaries and reply drafts you approve</li>
          </ul>
        </div>
        <p class="hero-foot">SupportDesk</p>
      </aside>

      <main class="main">
        <button
          type="button"
          class="theme"
          [attr.aria-label]="theme.isDark() ? 'Switch to light theme' : 'Switch to dark theme'"
          (click)="theme.toggle()"
        >
          <sd-icon [name]="theme.isDark() ? 'sun' : 'moon'" [size]="18" />
        </button>

        <div class="card">
          <sd-logo [size]="32" class="card-logo" />
          @switch (step()) {
            @case ('credentials') {
              <h1>Sign in to SupportDesk</h1>
              <p class="lead">Use your workspace name and work e-mail.</p>
              @if (expired()) {
                <div class="callout callout--info" role="status">
                  <sd-icon name="info" [size]="16" />Your session ended. Please sign in again.
                </div>
              }
              @if (error()) {
                <div class="callout callout--danger" role="alert"><sd-icon name="circle-alert" [size]="16" />{{ error() }}</div>
              }
              <form class="form" (ngSubmit)="submitCredentials()" novalidate>
                <sd-form-field label="Workspace" hint="Your company's SupportDesk name, e.g. acme" [error]="fieldErrors()['tenant']" required>
                  <input sdInput name="tenant" autocomplete="organization" autocapitalize="none" spellcheck="false" [(ngModel)]="tenant" required />
                </sd-form-field>
                <sd-form-field label="Work e-mail" [error]="fieldErrors()['email']" required>
                  <input sdInput type="email" name="email" autocomplete="username" [(ngModel)]="email" required />
                </sd-form-field>
                <sd-form-field label="Password" [error]="fieldErrors()['password']" required>
                  <div class="password">
                    <input
                      sdInput
                      [type]="showPassword() ? 'text' : 'password'"
                      name="password"
                      autocomplete="current-password"
                      [(ngModel)]="password"
                      required
                    />
                    <button
                      type="button"
                      class="reveal"
                      [attr.aria-label]="showPassword() ? 'Hide password' : 'Show password'"
                      [attr.aria-pressed]="showPassword()"
                      (click)="showPassword.set(!showPassword())"
                    >
                      <sd-icon [name]="showPassword() ? 'eye-off' : 'eye'" [size]="16" />
                    </button>
                  </div>
                </sd-form-field>
                <button sdButton variant="primary" size="lg" type="submit" block [loading]="busy()">Sign in</button>
              </form>
              <p class="help">Forgot your password? Ask your SupportDesk administrator to reset it.</p>
              <p class="help signup">New to SupportDesk? <a routerLink="/signup">Start a free trial</a> · <a routerLink="/platform/login">Platform console</a></p>
            }
            @case ('mfa') {
              <h1>Two-step verification</h1>
              <p class="lead">
                @if (method() === 'email') {
                  Enter the 6-digit code we e-mailed to {{ email }}.
                } @else {
                  Enter the 6-digit code from your authenticator app.
                }
              </p>
              @if (challenge()?.demoCode) {
                <div class="callout callout--demo" role="note">
                  <sd-icon name="info" [size]="16" />
                  <span><strong>Demo environment</strong> — your verification code is <code>{{ challenge()?.demoCode }}</code></span>
                </div>
              }
              @if (error()) {
                <div class="callout callout--danger" role="alert"><sd-icon name="circle-alert" [size]="16" />{{ error() }}</div>
              }
              <form class="form" (ngSubmit)="submitMfa()" novalidate>
                @if (methods().length > 1) {
                  <div class="methods" role="radiogroup" aria-label="Verification method">
                    @for (m of methods(); track m) {
                      <button type="button" role="radio" [attr.aria-checked]="method() === m" (click)="method.set(m)">
                        <sd-icon [name]="m === 'email' ? 'mail' : 'smartphone'" [size]="15" />
                        {{ m === 'email' ? 'E-mail code' : 'Authenticator app' }}
                      </button>
                    }
                  </div>
                }
                <sd-form-field label="Verification code" required>
                  <input
                    sdInput
                    name="code"
                    class="code"
                    inputmode="numeric"
                    autocomplete="one-time-code"
                    maxlength="8"
                    [(ngModel)]="code"
                    required
                  />
                </sd-form-field>
                <button sdButton variant="primary" size="lg" type="submit" block [loading]="busy()">Verify</button>
                <button sdButton variant="ghost" type="button" block (click)="backToCredentials()">Use a different account</button>
              </form>
            }
            @case ('expired') {
              <h1>Password expired</h1>
              <div class="callout callout--warning" role="alert">
                <sd-icon name="triangle-alert" [size]="16" />
                {{ expiredMessage() || 'Your password has expired and must be changed before you can sign in.' }}
              </div>
              <p class="help">Ask your SupportDesk administrator to reset your password, then sign in again.</p>
              <button sdButton type="button" block (click)="backToCredentials()">Back to sign in</button>
            }
          }
        </div>
      </main>
    </div>
  `,
  styles: `
    :host {
      display: block;
      height: 100dvh;
      overflow: auto;
    }
    .page {
      display: grid;
      grid-template-columns: minmax(320px, 0.9fr) 1.1fr;
      min-height: 100%;
    }
    .hero {
      position: relative;
      display: flex;
      flex-direction: column;
      justify-content: space-between;
      padding: 40px 48px;
      color: #eef0ff;
      background:
        radial-gradient(1200px 600px at -10% 110%, color-mix(in oklab, var(--sd-accent) 55%, transparent), transparent 60%),
        radial-gradient(900px 500px at 120% -10%, rgba(255, 255, 255, 0.16), transparent 55%),
        linear-gradient(160deg, #2b38b8 0%, #1d2585 55%, #141a5c 100%);
      overflow: hidden;
    }
    .hero-logo {
      --sd-primary: #ffffff;
      --sd-on-primary: #2b38b8;
      color: #ffffff;
    }
    .hero-logo ::ng-deep .word strong {
      color: #ffffff;
    }
    .hero-copy h2 {
      max-width: 420px;
      color: #ffffff;
      font-size: 2rem;
      font-weight: 700;
      letter-spacing: -0.025em;
      line-height: 1.15;
    }
    .hero-copy ul {
      margin: 28px 0 0;
      padding: 0;
      list-style: none;
      display: grid;
      gap: 14px;
    }
    .hero-copy li {
      display: flex;
      align-items: center;
      gap: 10px;
      color: rgba(238, 240, 255, 0.88);
    }
    .hero-foot {
      color: rgba(238, 240, 255, 0.55);
      font-size: var(--sd-text-sm);
    }
    .main {
      position: relative;
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 48px 24px;
      background: var(--sd-bg);
    }
    .theme {
      position: absolute;
      top: 16px;
      right: 16px;
      display: inline-flex;
      padding: 8px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      color: var(--sd-text-2);
      cursor: pointer;
    }
    .card {
      width: min(420px, 100%);
      padding: 32px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-xl);
      background: var(--sd-surface);
      box-shadow: var(--sd-shadow-lg);
    }
    .card-logo {
      display: none;
      margin-bottom: 18px;
    }
    h1 {
      font-size: var(--sd-text-2xl);
      font-weight: var(--sd-weight-bold);
    }
    .lead {
      margin: 6px 0 20px;
      color: var(--sd-text-2);
    }
    .form {
      display: grid;
      gap: 16px;
    }
    .password {
      position: relative;
    }
    .password input {
      padding-right: 40px;
    }
    .reveal {
      position: absolute;
      top: 50%;
      right: 4px;
      transform: translateY(-50%);
      display: inline-flex;
      padding: 6px;
      border: 0;
      border-radius: var(--sd-radius-sm);
      background: transparent;
      color: var(--sd-text-3);
      cursor: pointer;
    }
    .reveal:hover {
      color: var(--sd-text);
    }
    .code {
      font-size: 1.35rem;
      letter-spacing: 0.35em;
      text-align: center;
      font-variant-numeric: tabular-nums;
    }
    .methods {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 8px;
    }
    .methods button {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      gap: 6px;
      height: 36px;
      border: 1px solid var(--sd-border-strong);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      color: var(--sd-text-2);
      cursor: pointer;
    }
    .methods button[aria-checked='true'] {
      border-color: var(--sd-primary);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
      font-weight: var(--sd-weight-medium);
    }
    .callout {
      display: flex;
      align-items: flex-start;
      gap: 8px;
      margin-bottom: 16px;
      padding: 10px 12px;
      border: 1px solid transparent;
      border-radius: var(--sd-radius-md);
      font-size: var(--sd-text-sm);
    }
    .callout sd-icon {
      margin-top: 1px;
    }
    .callout--danger {
      background: var(--sd-danger-bg);
      border-color: var(--sd-danger-border);
      color: var(--sd-danger);
    }
    .callout--warning {
      background: var(--sd-warning-bg);
      border-color: var(--sd-warning-border);
      color: var(--sd-warning);
    }
    .callout--info {
      background: var(--sd-info-bg);
      border-color: var(--sd-info-border);
      color: var(--sd-info);
    }
    .callout--demo {
      background: var(--sd-accent-soft);
      border-color: color-mix(in oklab, var(--sd-accent) 35%, var(--sd-surface));
      color: var(--sd-text);
    }
    .callout--demo code {
      font-size: 1rem;
      font-weight: var(--sd-weight-bold);
      letter-spacing: 0.12em;
    }
    .help {
      margin-top: 18px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
      text-align: center;
    }
    .help.signup {
      margin-top: 8px;
    }
    .help a {
      color: var(--sd-primary);
    }
    @media (max-width: 900px) {
      .page {
        grid-template-columns: 1fr;
      }
      .hero {
        display: none;
      }
      .card-logo {
        display: inline-flex;
      }
    }
    @media (max-width: 480px) {
      .main {
        padding: 56px 12px 24px;
        align-items: flex-start;
      }
      .card {
        padding: 24px 18px;
        box-shadow: none;
      }
    }
  `,
})
export class LoginPage {
  /** Query params (component input binding). */
  readonly returnUrl = input<string | undefined>();
  readonly expiredParam = input<string | undefined>(undefined, { alias: 'expired' });

  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected readonly theme = inject(ThemeService);

  protected tenant = this.auth.rememberedLogin().tenant;
  protected email = this.auth.rememberedLogin().email;
  protected password = '';
  protected code = '';

  protected readonly step = signal<Step>('credentials');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly fieldErrors = signal<Record<string, string | null>>({});
  protected readonly showPassword = signal(false);
  protected readonly challenge = signal<LoginMfaRequiredResponse | null>(null);
  protected readonly method = signal<MfaMethod>('totp');
  protected readonly expiredMessage = signal<string | null>(null);
  protected readonly expired = computed(() => this.expiredParam() === '1');
  protected readonly methods = computed<MfaMethod[]>(() => this.challenge()?.methods ?? ['totp']);

  protected async submitCredentials(): Promise<void> {
    const errors: Record<string, string | null> = {
      tenant: this.tenant.trim() ? null : 'Enter your workspace name.',
      email: /^\S+@\S+\.\S+$/.test(this.email.trim()) ? null : 'Enter a valid e-mail address.',
      password: this.password ? null : 'Enter your password.',
    };
    this.fieldErrors.set(errors);
    if (Object.values(errors).some(Boolean)) return;
    await this.run(() => this.auth.login(this.tenant, this.email, this.password));
  }

  protected async submitMfa(): Promise<void> {
    const challenge = this.challenge();
    if (!challenge) return;
    if (!/^\d{4,8}$/.test(this.code.trim())) {
      this.error.set('Enter the code (digits only).');
      return;
    }
    await this.run(() => this.auth.verifyMfa(challenge.challengeId, this.code, this.method()));
  }

  protected backToCredentials(): void {
    this.step.set('credentials');
    this.challenge.set(null);
    this.code = '';
    this.password = '';
    this.error.set(null);
  }

  private async run(action: () => Promise<LoginResponse>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      const result = await action();
      switch (result.status) {
        case 'ok':
          this.password = '';
          await this.router.navigateByUrl(this.safeReturnUrl());
          break;
        case 'mfa_required':
          this.challenge.set(result);
          this.method.set(result.methods?.includes('totp') ? 'totp' : (result.methods?.[0] ?? 'totp'));
          this.code = '';
          this.step.set('mfa');
          break;
        case 'password_expired':
          this.expiredMessage.set(result.message ?? null);
          this.step.set('expired');
          break;
      }
    } catch (err) {
      const e = ApiError.from(err);
      if (e.is('validation_failed')) {
        this.fieldErrors.set({ tenant: e.fieldError('tenant'), email: e.fieldError('email'), password: e.fieldError('password') });
      }
      this.error.set(this.messageFor(e));
    } finally {
      this.busy.set(false);
    }
  }

  private messageFor(e: ApiError): string {
    if (e.isNetworkError) return 'Cannot reach SupportDesk. Check your connection and try again.';
    if (e.status === 401) return this.step() === 'mfa' ? 'That code is not valid or has expired.' : 'The workspace, e-mail or password is incorrect.';
    if (e.is('tenant_suspended')) return 'This workspace is suspended. Contact your administrator.';
    if (e.is('rate_limited')) return 'Too many attempts. Wait a minute and try again.';
    if (e.status >= 500) return 'SupportDesk is having trouble right now. Please try again shortly.';
    return e.userMessage;
  }

  private safeReturnUrl(): string {
    const url = this.returnUrl();
    return url && url.startsWith('/') && !url.startsWith('//') && !url.startsWith('/login') ? url : '/';
  }
}
