import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ApiError, PortalAuthService, PortalLoginResponse } from '@sd/core';
import { SdButton, SdFormField, SdIcon, SdInput, SdTab, SdTabs } from '@sd/ui';
import { BrandService } from '../brand.service';

/**
 * Contact sign-in with password or a one-time e-mail code (per the brand's authMode). A `demoCode`
 * returned by demo environments is displayed, labelled as such.
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
        @if (error()) {
          <div class="callout callout--danger" role="alert"><sd-icon name="circle-alert" [size]="16" />{{ error() }}</div>
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
                  <p class="hint">Enter the code we sent to <strong>{{ email }}</strong>.</p>
                  @if (demoCode()) {
                    <div class="callout callout--demo" role="note">
                      <sd-icon name="info" [size]="16" /><span><strong>Demo environment</strong> — your code is <code>{{ demoCode() }}</code></span>
                    </div>
                  }
                  <sd-form-field label="Code" required>
                    <input sdInput name="code" class="code" inputmode="numeric" autocomplete="one-time-code" maxlength="8" [(ngModel)]="code" />
                  </sd-form-field>
                  <button sdButton variant="primary" type="submit" size="lg" block [loading]="busy()">Sign in</button>
                  <button sdButton variant="ghost" type="button" block (click)="codeSent.set(false)">Use a different e-mail</button>
                </form>
              }
            </sd-tab>
          }
        </sd-tabs>
        @if (brand.features().selfRegistration) {
          <p class="foot">New here? <a [routerLink]="'/' + brand.slug() + '/register'">Create an account</a></p>
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
      padding: 28px;
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
    .form {
      display: grid;
      gap: 14px;
    }
    .hint {
      color: var(--sd-text-2);
    }
    .code {
      font-size: 1.3rem;
      letter-spacing: 0.3em;
      text-align: center;
    }
    .callout {
      display: flex;
      gap: 8px;
      margin-bottom: 14px;
      padding: 10px 12px;
      border: 1px solid transparent;
      border-radius: var(--sd-radius-md);
      font-size: var(--sd-text-sm);
    }
    .callout--danger {
      background: var(--sd-danger-bg);
      border-color: var(--sd-danger-border);
      color: var(--sd-danger);
    }
    .callout--demo {
      background: var(--sd-accent-soft);
      border-color: color-mix(in oklab, var(--sd-accent) 35%, var(--sd-surface));
    }
    .callout--demo code {
      font-weight: var(--sd-weight-bold);
      letter-spacing: 0.12em;
    }
    .foot {
      margin-top: 18px;
      color: var(--sd-text-2);
      text-align: center;
    }
  `,
})
export class PortalLoginPage {
  readonly returnUrl = input<string | undefined>();

  protected readonly brand = inject(BrandService);
  private readonly auth = inject(PortalAuthService);
  private readonly router = inject(Router);

  private readonly authMode = computed(() => (this.brand.config()?.authMode ?? 'both').toLowerCase());
  /** Password unless the brand is OTP-only. */
  protected readonly allowPassword = computed(() => this.authMode() !== 'otp');
  protected readonly allowOtp = computed(() => this.authMode() === 'otp' || this.authMode() === 'both');
  protected readonly mode = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly codeSent = signal(false);
  protected readonly demoCode = signal<string | null>(null);
  protected email = this.auth.rememberedEmail(this.brand.slug() ?? '');
  protected password = '';
  protected code = '';

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

  private slug(): string {
    return this.brand.slug() ?? '';
  }

  private async run(action: () => Promise<PortalLoginResponse>, expectSession: boolean): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      const res = await action();
      if (expectSession) {
        if (res.status === 'ok') {
          const url = this.returnUrl();
          await this.router.navigateByUrl(url && url.startsWith(`/${this.slug()}`) ? url : `/${this.slug()}`);
        } else {
          this.error.set(res.message || 'Sign-in could not be completed.');
        }
      }
    } catch (err) {
      const e = ApiError.from(err);
      this.error.set(
        e.status === 401
          ? 'The e-mail, password or code is not correct.'
          : e.is('rate_limited')
            ? 'Too many attempts. Wait a minute and try again.'
            : e.isNetworkError
              ? 'Cannot reach the portal. Check your connection.'
              : e.userMessage,
      );
    } finally {
      this.busy.set(false);
    }
  }
}
