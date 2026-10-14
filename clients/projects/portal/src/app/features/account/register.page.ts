import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiError, PortalAuthService } from '@sd/core';
import { SdButton, SdFormField, SdIcon, SdInput, ToastService } from '@sd/ui';
import { BrandService } from '../../brand.service';
import { errorMessage } from '../../shared/portal-utils';

/**
 * Self-registration (brands with `selfRegistration`): name, e-mail, company, phone and password. "ok"
 * signs the new contact in; "exists" suggests signing in with an e-mailed code instead.
 */
@Component({
  selector: 'portal-register-page',
  imports: [RouterLink, SdButton, SdIcon, SdFormField, SdInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="portal-container wrap">
      <div class="card">
        <h1>Create your account</h1>
        <p class="lead">Submit tickets, follow their progress and join the community.</p>

        @if (exists()) {
          <div class="portal-callout portal-callout--info exists" role="status">
            <sd-icon name="info" [size]="16" />
            <div>
              <p>{{ exists() }}</p>
              <a sdButton variant="primary" size="sm" icon="mail" [routerLink]="brand.base() + '/login'" [queryParams]="{ method: 'otp', returnUrl: returnUrl() ?? null }">
                Sign in with an e-mail code
              </a>
            </div>
          </div>
        }
        @if (error()) {
          <p class="portal-callout portal-callout--danger" role="alert"><sd-icon name="circle-alert" [size]="15" />{{ error() }}</p>
        }

        <form class="form" (submit)="$event.preventDefault(); submit()" novalidate>
          <sd-form-field label="Full name" required [error]="errors()['fullName']">
            <input sdInput name="fullName" autocomplete="name" maxlength="200" [value]="fullName()" (input)="fullName.set($any($event.target).value)" />
          </sd-form-field>
          <sd-form-field label="Work e-mail" required [error]="errors()['email']">
            <input sdInput type="email" name="email" autocomplete="email" maxlength="254" [value]="email()" (input)="email.set($any($event.target).value)" />
          </sd-form-field>
          <div class="pair">
            <sd-form-field label="Company" optional [error]="errors()['company']">
              <input sdInput name="company" autocomplete="organization" maxlength="200" [value]="company()" (input)="company.set($any($event.target).value)" />
            </sd-form-field>
            <sd-form-field label="Phone" optional [error]="errors()['phone']">
              <input sdInput type="tel" name="phone" autocomplete="tel" maxlength="40" [value]="phone()" (input)="phone.set($any($event.target).value)" />
            </sd-form-field>
          </div>
          <sd-form-field label="Password" required [error]="errors()['password']" hint="At least 8 characters.">
            <div class="pw">
              <input
                sdInput
                [type]="showPassword() ? 'text' : 'password'"
                name="password"
                autocomplete="new-password"
                [value]="password()"
                (input)="password.set($any($event.target).value)"
              />
              <button type="button" class="reveal" [attr.aria-pressed]="showPassword()" (click)="showPassword.set(!showPassword())">
                <sd-icon [name]="showPassword() ? 'eye-off' : 'eye'" [size]="16" /><span class="sd-visually-hidden">Show password</span>
              </button>
            </div>
          </sd-form-field>
          <button sdButton variant="primary" type="submit" size="lg" block [loading]="busy()">Create account</button>
        </form>
        <p class="foot">Already registered? <a [routerLink]="brand.base() + '/login'" [queryParams]="returnUrl() ? { returnUrl: returnUrl() } : {}">Sign in</a></p>
      </div>
    </div>
  `,
  styles: `
    .wrap {
      display: flex;
      justify-content: center;
      padding: 40px 0;
    }
    .card {
      width: min(520px, 100%);
      padding: clamp(20px, 5vw, 30px);
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
    .exists,
    .card > .portal-callout {
      margin-bottom: 14px;
    }
    .exists p {
      margin: 0 0 8px;
    }
    .form {
      display: grid;
      gap: 14px;
    }
    .pair {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 14px;
    }
    .pw {
      position: relative;
    }
    .pw input {
      padding-right: 42px;
    }
    .reveal {
      position: absolute;
      top: 50%;
      right: 6px;
      display: inline-flex;
      padding: 6px;
      border: 0;
      border-radius: var(--sd-radius-sm);
      background: transparent;
      color: var(--sd-text-3);
      cursor: pointer;
      transform: translateY(-50%);
    }
    .reveal:hover {
      color: var(--sd-text);
    }
    .reveal:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .foot {
      margin: 18px 0 0;
      color: var(--sd-text-2);
      text-align: center;
    }
    @media (max-width: 480px) {
      .pair {
        grid-template-columns: minmax(0, 1fr);
      }
    }
  `,
})
export class RegisterPage {
  readonly returnUrl = input<string | undefined>();

  protected readonly brand = inject(BrandService);
  private readonly auth = inject(PortalAuthService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  protected readonly fullName = signal('');
  protected readonly email = signal('');
  protected readonly company = signal('');
  protected readonly phone = signal('');
  protected readonly password = signal('');
  protected readonly showPassword = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly exists = signal<string | null>(null);
  protected readonly errors = signal<Record<string, string | null>>({});

  protected async submit(): Promise<void> {
    const slug = this.brand.slug();
    if (!slug || this.busy()) return;
    const errors: Record<string, string | null> = {
      fullName: this.fullName().trim() ? null : 'Enter your name.',
      email: /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(this.email().trim()) ? null : 'Enter a valid e-mail address.',
      password: this.password().length >= 8 ? null : 'Use at least 8 characters.',
    };
    this.errors.set(errors);
    this.error.set(null);
    this.exists.set(null);
    if (Object.values(errors).some(Boolean)) return;
    this.busy.set(true);
    try {
      const res = await this.auth.register(slug, {
        fullName: this.fullName().trim(),
        email: this.email().trim(),
        password: this.password(),
        company: this.company().trim() || null,
        phone: this.phone().trim() || null,
      });
      if (res.status === 'ok') {
        this.toast.success(`Welcome, ${res.contact?.fullName ?? this.fullName().trim()}!`, 'Your account is ready.');
        const url = this.returnUrl();
        await this.router.navigateByUrl(url && url.startsWith(this.brand.base()) ? url : this.brand.base());
      } else if (res.status === 'exists') {
        this.exists.set(res.message || 'An account already exists for this e-mail address. Sign in with a code sent to your inbox.');
      } else {
        this.error.set(res.message || 'Your account could not be created.');
      }
    } catch (err) {
      const e = ApiError.from(err);
      const fields: Record<string, string | null> = {
        fullName: e.fieldError('fullName'),
        email: e.fieldError('email'),
        password: e.fieldError('password'),
        company: e.fieldError('company'),
        phone: e.fieldError('phone'),
      };
      this.errors.set(fields);
      if (!Object.values(fields).some(Boolean)) this.error.set(errorMessage(err, 'Your account could not be created. Please try again.'));
    } finally {
      this.busy.set(false);
    }
  }
}
