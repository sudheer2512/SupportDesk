import { ChangeDetectionStrategy, Component, computed, inject, input, linkedSignal, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ApiError, PortalAuthService, PortalLoginResponse } from '@sd/core';
import { SdButton, SdFormField, SdIcon, SdInput, SdTab, SdTabs } from '@sd/ui';
import { BrandService } from '../brand.service';
import { errorMessage } from '../shared/portal-utils';

/**
 * Contact sign-in. The brand's `authMode` (C# PortalAuthMode: Local | Sso | LocalAndSso) decides the
 * methods: Local / LocalAndSso offer a password and a one-time e-mail code; Sso-only portals still offer
 * the e-mail code (contacts have no SSO account here). A `demoCode` returned by demo environments is
 * displayed, labelled as such.
 */
@Component({
  selector: 'portal-login-page',
  imports: [FormsModule, RouterLink, SdTabs, SdTab, SdFormField, SdInput, SdButton, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="portal-container wrap">
      <div class="card">
        <h1>Sign in to {{ brand.name() }}</h1>
        <p class="lead">Track your tickets and get help faster.</p>
        @if (ssoOnly()) {
          <div class="portal-callout portal-callout--info note" role="note">
            <sd-icon name="info" [size]="16" />
            <span>This portal uses your organisation's account. Sign in with a one-time code sent to your work e-mail.</span>
          </div>
        }
        @if (error()) {
          <div class="portal-callout portal-callout--danger note" role="alert"><sd-icon name="circle-alert" [size]="16" />{{ error() }}</div>
        }
        <sd-tabs [(selected)]="mode" variant="pill" ariaLabel="Sign-in method">
          @if (allowPassword()) {
            <sd-tab id="password" label="Password" icon="key">
              <form class="form" (ngSubmit)="loginWithPassword()" novalidate>
                <sd-form-field label="E-mail" required>
                  <input sdInput type="email" name="email" autocomplete="username" [(ngModel)]="email" />
                </sd-form-field>
                <sd-form-field label="Password" required>
                  <input sdInput type="password" name="password" autocomplete="current-password" [(ngModel)]="password" />
                </sd-form-field>
                <button sdButton variant="primary" type="submit" size="lg" block [loading]="busy()">Sign in</button>
                @if (allowOtp()) {
                  <button sdButton variant="link" type="button" class="switch" (click)="mode.set('otp')">Forgot your password? Sign in with an e-mail code</button>
                }
              </form>
            </sd-tab>
          }
          @if (allowOtp()) {
            <sd-tab id="otp" label="E-mail code" icon="mail">
              @if (!codeSent()) {
                <form class="form" (ngSubmit)="requestCode()" novalidate>
                  <p class="hint">We'll e-mail you a one-time sign-in code — no password needed.</p>
                  <sd-form-field label="E-mail" required>
                    <input sdInput type="email" name="otpEmail" autocomplete="email" [(ngModel)]="email" />
                  </sd-form-field>
                  <button sdButton variant="primary" type="submit" size="lg" block [loading]="busy()">Send code</button>
                </form>
              } @else {
                <form class="form" (ngSubmit)="verifyCode()" novalidate>
                  <p class="hint" aria-live="polite">{{ sentMessage() || 'Enter the code we sent to' }} <strong>{{ email }}</strong></p>
                  @if (demoCode()) {
                    <div class="portal-callout portal-callout--accent demo" role="note">
                      <sd-icon name="info" [size]="16" /><span><strong>Demo environment</strong> — your code is <code>{{ demoCode() }}</code></span>
                    </div>
                  }
                  <sd-form-field label="Code" required>
                    <input sdInput name="code" class="code" inputmode="numeric" autocomplete="one-time-code" maxlength="8" [(ngModel)]="code" />
                  </sd-form-field>
                  <button sdButton variant="primary" type="submit" size="lg" block [loading]="busy()">Sign in</button>
                  <div class="row">
                    <button sdButton variant="ghost" type="button" (click)="resetCode()">Use a different e-mail</button>
                    <button sdButton variant="ghost" type="button" [disabled]="busy()" (click)="requestCode()">Send a new code</button>
                  </div>
                </form>
              }
            </sd-tab>
          }
        </sd-tabs>
        @if (brand.features().selfRegistration) {
          <p class="foot">New here? <a [routerLink]="brand.base() + '/register'" [queryParams]="returnUrl() ? { returnUrl: returnUrl() } : {}">Create an account</a></p>
        }
        @if (brand.features().anonymousTickets) {
          <p class="foot small">No account? You can still <a [routerLink]="brand.base() + '/submit'">submit a ticket</a>.</p>
        }
      </div>
    </div>
  `,
  styles: `
    .wrap {
      display: flex;
      justify-content: center;
      padding: 48px 0;
    }
    .card {
      width: min(440px, 100%);
      padding: clamp(20px, 5vw, 28px);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-xl);
      background: var(--sd-surface);
      box-shadow: var(--sd-shadow-md);
    }
    h1 {
      font-size: var(--sd-text-2xl);
    }
    .lead {
      margin: 6px 0 18px;
      color: var(--sd-text-2);
    }
    .note {
      margin-bottom: 14px;
    }
    .form {
      display: grid;
      gap: 14px;
    }
    .hint {
      margin: 0;
      color: var(--sd-text-2);
    }
    .code {
      font-size: 1.3rem;
      letter-spacing: 0.3em;
      text-align: center;
    }
    .demo code {
      font-weight: var(--sd-weight-bold);
      letter-spacing: 0.12em;
    }
    .row {
      display: flex;
      flex-wrap: wrap;
      justify-content: space-between;
      gap: 8px;
    }
    .switch {
      justify-self: center;
      white-space: normal;
    }
    .foot {
      margin: 18px 0 0;
      color: var(--sd-text-2);
      text-align: center;
    }
    .foot.small {
      margin-top: 8px;
      font-size: var(--sd-text-sm);
    }
  `,
})
export class PortalLoginPage {
  readonly returnUrl = input<string | undefined>();
  /** `?method=otp` preselects the e-mail-code tab (e.g. from registration when the account exists). */
  readonly method = input<string | undefined>();

  protected readonly brand = inject(BrandService);
  private readonly auth = inject(PortalAuthService);
  private readonly router = inject(Router);

  private readonly authMode = computed(() => (this.brand.config()?.authMode ?? 'Local').toLowerCase());
  /** Local / LocalAndSso (and legacy password/both values) allow passwords. */
  protected readonly allowPassword = computed(() => ['local', 'localandsso', 'password', 'both'].includes(this.authMode()));
  /** Every mode except a password-only legacy value offers the e-mail code. */
  protected readonly allowOtp = computed(() => this.authMode() !== 'password');
  protected readonly ssoOnly = computed(() => this.authMode() === 'sso');
  protected readonly mode = linkedSignal<string | null>(() => (this.method() === 'otp' ? 'otp' : null));
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly codeSent = signal(false);
  protected readonly sentMessage = signal<string | null>(null);
  protected readonly demoCode = signal<string | null>(null);
  protected email = this.auth.rememberedEmail(this.brand.slug() ?? '');
  protected password = '';
  protected code = '';

  constructor() {
    if (this.auth.isAuthenticated()) void this.router.navigateByUrl(this.target());
  }

  protected async loginWithPassword(): Promise<void> {
    if (!this.email.trim() || !this.password) {
      this.error.set('Enter your e-mail and password.');
      return;
    }
    await this.run(() => this.auth.login(this.slug(), this.email, this.password), true);
  }

  protected async requestCode(): Promise<void> {
    if (!/^\S+@\S+\.\S+$/.test(this.email.trim())) {
      this.error.set('Enter a valid e-mail address.');
      return;
    }
    await this.run(async () => {
      const res = await this.auth.requestOtp(this.slug(), this.email);
      this.demoCode.set(res.demoCode ?? null);
      this.sentMessage.set(res.message ? `${res.message} Enter the code sent to` : null);
      this.code = '';
      this.codeSent.set(true);
      return res;
    }, false);
  }

  protected async verifyCode(): Promise<void> {
    if (!this.code.trim()) {
      this.error.set('Enter the code from the e-mail.');
      return;
    }
    await this.run(() => this.auth.verifyOtp(this.slug(), this.email, this.code), true);
  }

  protected resetCode(): void {
    this.codeSent.set(false);
    this.demoCode.set(null);
    this.code = '';
    this.error.set(null);
  }

  private slug(): string {
    return this.brand.slug() ?? '';
  }

  private target(): string {
    const url = this.returnUrl();
    return url && url.startsWith(`/${this.slug()}`) && !url.includes('/login') ? url : `/${this.slug()}`;
  }

  private async run(action: () => Promise<PortalLoginResponse>, expectSession: boolean): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      const res = await action();
      if (expectSession) {
        if (res.status === 'ok') await this.router.navigateByUrl(this.target());
        else this.error.set(res.message || 'Sign-in could not be completed.');
      }
    } catch (err) {
      const e = ApiError.from(err);
      this.error.set(
        e.status === 401
          ? (e.extension<string>('message') ?? 'The e-mail, password or code is not correct.')
          : errorMessage(err, 'Sign-in could not be completed. Please try again.'),
      );
    } finally {
      this.busy.set(false);
    }
  }
}
