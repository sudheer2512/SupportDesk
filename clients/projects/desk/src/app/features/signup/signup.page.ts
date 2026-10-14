import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ApiError, AuthService, PlanDto, SignupApi, SignupCompletedDto, SignupStartedDto, SlugAvailabilityDto, ThemeService } from '@sd/core';
import { SdButton, SdFormField, SdIcon, SdInput, SdLogo } from '@sd/ui';
import { Subject, debounceTime, distinctUntilChanged, switchMap, of, catchError } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

type Step = 'details' | 'verify' | 'done';

/**
 * Public trial sign-up (/signup): company, workspace identifier (live availability check), administrator and plan;
 * then the e-mailed verification code (shown on screen in development, like the MFA demo code); then the workspace is
 * provisioned and the new administrator is signed in.
 */
@Component({
  selector: 'desk-signup-page',
  imports: [FormsModule, RouterLink, SdButton, SdFormField, SdIcon, SdInput, SdLogo],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <button type="button" class="theme" [attr.aria-label]="theme.isDark() ? 'Switch to light theme' : 'Switch to dark theme'" (click)="theme.toggle()">
        <sd-icon [name]="theme.isDark() ? 'sun' : 'moon'" [size]="18" />
      </button>
      <main class="card">
        <sd-logo [size]="32" wordmark class="logo" />
        @switch (step()) {
          @case ('details') {
            <h1>Start your free trial</h1>
            <p class="lead">Create a SupportDesk workspace for your team. No card required.</p>
            @if (error()) {
              <div class="callout callout--danger" role="alert"><sd-icon name="circle-alert" [size]="16" />{{ error() }}</div>
            }
            <form class="form" (ngSubmit)="submit()" novalidate>
              <sd-form-field label="Company name" required [error]="errors()['company']">
                <input sdInput name="company" autocomplete="organization" [(ngModel)]="company" (ngModelChange)="suggestSlug($event)" maxlength="120" required />
              </sd-form-field>
              <sd-form-field
                label="Workspace name"
                required
                [hint]="slugHint()"
                [error]="errors()['slug'] ?? (availability() && !availability()!.available ? availability()!.reason : null)"
              >
                <div class="slug">
                  <input sdInput name="slug" autocapitalize="none" spellcheck="false" [ngModel]="slug()" (ngModelChange)="onSlug($event)" maxlength="40" required />
                  <span class="slug-state" aria-live="polite">
                    @if (checking()) {
                      <span class="muted">checking…</span>
                    } @else if (availability()?.available) {
                      <sd-icon name="circle-check" [size]="16" class="ok" /><span class="sd-visually-hidden">available</span>
                    }
                  </span>
                </div>
              </sd-form-field>
              @if (availability()?.suggestion; as s) {
                <p class="suggest">Try <button type="button" class="linkish" (click)="onSlug(s)">{{ s }}</button></p>
              }
              <sd-form-field label="Your name" required [error]="errors()['adminName']">
                <input sdInput name="adminName" autocomplete="name" [(ngModel)]="adminName" maxlength="120" required />
              </sd-form-field>
              <sd-form-field label="Work e-mail" required [error]="errors()['adminEmail']">
                <input sdInput type="email" name="adminEmail" autocomplete="email" [(ngModel)]="adminEmail" required />
              </sd-form-field>
              <sd-form-field label="Password" required hint="At least 10 characters with upper- and lower-case letters and a digit." [error]="errors()['password']">
                <div class="password">
                  <input sdInput [type]="showPassword() ? 'text' : 'password'" name="password" autocomplete="new-password" [(ngModel)]="password" required />
                  <button type="button" class="reveal" [attr.aria-label]="showPassword() ? 'Hide password' : 'Show password'" [attr.aria-pressed]="showPassword()"
                    (click)="showPassword.set(!showPassword())">
                    <sd-icon [name]="showPassword() ? 'eye-off' : 'eye'" [size]="16" />
                  </button>
                </div>
              </sd-form-field>
              <sd-form-field label="Plan to try" [error]="errors()['planCode']" hint="Every trial includes all features of the plan; change it any time.">
                <select sdInput name="plan" [(ngModel)]="planCode">
                  @for (p of plans(); track p.code) {
                    <option [value]="p.code">{{ p.name }}{{ p.monthlyPricePerAgent > 0 ? ' — ' + p.monthlyPricePerAgent + ' ' + p.currency + '/agent/month after the trial' : '' }}</option>
                  }
                </select>
              </sd-form-field>
              <!-- Honeypot: hidden from people and assistive tech; bots fill it in. -->
              <div class="hp" aria-hidden="true">
                <label for="website">Website</label>
                <input id="website" name="website" tabindex="-1" autocomplete="off" [(ngModel)]="website" />
              </div>
              <button sdButton variant="primary" size="lg" type="submit" block [loading]="busy()">Create workspace</button>
            </form>
            <p class="help">Already have a workspace? <a routerLink="/login">Sign in</a></p>
          }
          @case ('verify') {
            <h1>Check your inbox</h1>
            <p class="lead">We sent a 6-digit code to <strong>{{ started()?.email }}</strong>. It expires in 30 minutes.</p>
            @if (started()?.demoCode) {
              <div class="callout callout--demo" role="note">
                <sd-icon name="info" [size]="16" /><span><strong>Development environment</strong> — your verification code is <code>{{ started()?.demoCode }}</code></span>
              </div>
            }
            @if (error()) {
              <div class="callout callout--danger" role="alert"><sd-icon name="circle-alert" [size]="16" />{{ error() }}</div>
            }
            <form class="form" (ngSubmit)="verify()" novalidate>
              <sd-form-field label="Verification code" required>
                <input sdInput name="code" class="code" inputmode="numeric" autocomplete="one-time-code" maxlength="8" [(ngModel)]="code" required />
              </sd-form-field>
              <button sdButton variant="primary" size="lg" type="submit" block [loading]="busy()">Verify and create workspace</button>
              <button sdButton variant="ghost" type="button" block (click)="restart()">Change details</button>
            </form>
          }
          @case ('done') {
            <div class="done">
              <sd-icon name="circle-check" [size]="40" class="ok" />
              <h1>Your workspace is ready</h1>
              <p class="lead">
                Workspace <strong>{{ completed()?.workspace }}</strong> on the {{ completed()?.planName }} trial
                @if (completed()?.trialEndsAt) {
                  until {{ completed()!.trialEndsAt!.slice(0, 10) }}
                }.
              </p>
              @if (error()) {
                <div class="callout callout--info" role="status"><sd-icon name="info" [size]="16" />{{ error() }}</div>
              }
              <a sdButton variant="primary" size="lg" block routerLink="/login">Sign in to {{ completed()?.workspace }}</a>
            </div>
          }
        }
      </main>
    </div>
  `,
  styles: `
    :host { display: block; height: 100dvh; overflow: auto; background: var(--sd-bg); }
    .page { position: relative; min-height: 100%; display: flex; align-items: flex-start; justify-content: center; padding: 48px 16px; }
    .theme { position: absolute; top: 16px; right: 16px; display: inline-flex; padding: 8px; border: 1px solid var(--sd-border); border-radius: var(--sd-radius-md);
      background: var(--sd-surface); color: var(--sd-text-2); cursor: pointer; }
    .card { width: min(480px, 100%); padding: 32px; border: 1px solid var(--sd-border); border-radius: var(--sd-radius-xl); background: var(--sd-surface); box-shadow: var(--sd-shadow-lg); }
    .logo { display: inline-flex; margin-bottom: 18px; }
    h1 { font-size: var(--sd-text-2xl); font-weight: var(--sd-weight-bold); margin: 0; }
    .lead { margin: 6px 0 20px; color: var(--sd-text-2); }
    .form { display: grid; gap: 16px; }
    .slug, .password { position: relative; }
    .slug input { padding-right: 90px; }
    .slug-state { position: absolute; top: 50%; right: 10px; transform: translateY(-50%); font-size: var(--sd-text-xs); display: inline-flex; }
    .ok { color: var(--sd-success); }
    .muted { color: var(--sd-text-3); }
    .suggest { margin: -8px 0 0; font-size: var(--sd-text-sm); color: var(--sd-text-2); }
    .linkish { border: 0; background: none; padding: 0; color: var(--sd-primary); text-decoration: underline; cursor: pointer; font: inherit; }
    .password input { padding-right: 40px; }
    .reveal { position: absolute; top: 50%; right: 4px; transform: translateY(-50%); display: inline-flex; padding: 6px; border: 0; border-radius: var(--sd-radius-sm);
      background: transparent; color: var(--sd-text-3); cursor: pointer; }
    .code { font-size: 1.35rem; letter-spacing: 0.35em; text-align: center; font-variant-numeric: tabular-nums; }
    .hp { position: absolute; left: -10000px; width: 1px; height: 1px; overflow: hidden; }
    .callout { display: flex; align-items: flex-start; gap: 8px; margin-bottom: 16px; padding: 10px 12px; border: 1px solid transparent; border-radius: var(--sd-radius-md);
      font-size: var(--sd-text-sm); }
    .callout--danger { background: var(--sd-danger-bg); border-color: var(--sd-danger-border); color: var(--sd-danger); }
    .callout--info { background: var(--sd-info-bg); border-color: var(--sd-info-border); color: var(--sd-text); }
    .callout--demo { background: var(--sd-accent-soft); border-color: color-mix(in oklab, var(--sd-accent) 35%, var(--sd-surface)); color: var(--sd-text); }
    .callout--demo code { font-size: 1rem; font-weight: var(--sd-weight-bold); letter-spacing: 0.12em; }
    .help { margin-top: 18px; color: var(--sd-text-3); font-size: var(--sd-text-sm); text-align: center; }
    .done { display: grid; gap: 8px; justify-items: center; text-align: center; }
    .done a { margin-top: 8px; }
    @media (max-width: 480px) {
      .page { padding: 56px 12px 24px; }
      .card { padding: 24px 18px; box-shadow: none; }
    }
  `,
})
export class SignupPage implements OnInit {
  private readonly api = inject(SignupApi);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly theme = inject(ThemeService);

  protected readonly step = signal<Step>('details');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly errors = signal<Record<string, string | null>>({});
  protected readonly plans = signal<PlanDto[]>([]);
  protected readonly slug = signal('');
  protected readonly availability = signal<SlugAvailabilityDto | null>(null);
  protected readonly checking = signal(false);
  protected readonly showPassword = signal(false);
  protected readonly started = signal<SignupStartedDto | null>(null);
  protected readonly completed = signal<SignupCompletedDto | null>(null);
  private slugEdited = false;
  private readonly slug$ = new Subject<string>();

  protected company = '';
  protected adminName = '';
  protected adminEmail = '';
  protected password = '';
  protected planCode = 'trial';
  protected website = '';
  protected code = '';

  protected readonly slugHint = computed(() => {
    const a = this.availability();
    if (a?.available) return `Your team signs in to workspace “${a.slug}”.`;
    return 'Letters, digits and hyphens; used when signing in.';
  });

  ngOnInit(): void {
    this.api.plans().subscribe({
      next: (p) => {
        this.plans.set(p);
        if (!p.some((x) => x.code === this.planCode) && p.length) this.planCode = p[0].code;
      },
      error: () => this.error.set('Plans could not be loaded. Refresh the page to try again.'),
    });
    this.slug$
      .pipe(
        debounceTime(350),
        distinctUntilChanged(),
        switchMap((s) => {
          if (s.trim().length < 3) {
            this.checking.set(false);
            return of(null);
          }
          this.checking.set(true);
          return this.api.slugAvailability(s).pipe(catchError(() => of(null)));
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((a) => {
        this.checking.set(false);
        this.availability.set(a);
      });
  }

  protected suggestSlug(company: string): void {
    if (this.slugEdited) return;
    const s = company.toLowerCase().normalize('NFKD').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 40);
    this.slug.set(s);
    this.slug$.next(s);
  }

  protected onSlug(value: string): void {
    this.slugEdited = true;
    this.slug.set(value);
    this.availability.set(null);
    this.slug$.next(value);
  }

  protected submit(): void {
    const e: Record<string, string | null> = {
      company: this.company.trim().length >= 2 ? null : 'Enter your company name.',
      slug: this.slug().trim().length >= 3 ? null : 'Use at least 3 letters or digits.',
      adminName: this.adminName.trim().length >= 2 ? null : 'Enter your name.',
      adminEmail: /^\S+@\S+\.\S+$/.test(this.adminEmail.trim()) ? null : 'Enter a valid e-mail address.',
      password: this.password.length >= 10 ? null : 'Use at least 10 characters.',
    };
    this.errors.set(e);
    if (Object.values(e).some(Boolean)) return;
    this.busy.set(true);
    this.error.set(null);
    this.api
      .start({
        company: this.company,
        slug: this.slug(),
        adminName: this.adminName,
        adminEmail: this.adminEmail,
        password: this.password,
        planCode: this.planCode,
        timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
        website: this.website || null,
      })
      .subscribe({
        next: (s) => {
          this.busy.set(false);
          this.started.set(s);
          this.code = '';
          this.step.set('verify');
        },
        error: (err: ApiError) => {
          this.busy.set(false);
          const fields = ['company', 'slug', 'adminName', 'adminEmail', 'password', 'planCode'];
          const mapped: Record<string, string | null> = {};
          for (const f of fields) mapped[f] = err.fieldError(f);
          this.errors.set(mapped);
          if (!fields.some((f) => mapped[f])) this.error.set(err.userMessage);
        },
      });
  }

  protected verify(): void {
    const s = this.started();
    if (!s) return;
    if (!/^\d{6}$/.test(this.code.trim())) {
      this.error.set('Enter the 6-digit code.');
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    this.api.verify(s.signupId, this.code.trim()).subscribe({
      next: async (done) => {
        this.completed.set(done);
        this.step.set('done');
        try {
          const login = await this.auth.login(done.workspace, done.adminEmail, this.password);
          this.password = '';
          if (login.status === 'ok') {
            await this.router.navigateByUrl('/subscription');
            return;
          }
          this.error.set('Sign in to continue.');
        } catch {
          this.error.set('Sign in to continue.');
        } finally {
          this.busy.set(false);
        }
      },
      error: (err: ApiError) => {
        this.busy.set(false);
        this.error.set(err.userMessage);
        if (err.status === 410) {
          this.step.set('details');
          this.started.set(null);
        }
      },
    });
  }

  protected restart(): void {
    this.step.set('details');
    this.started.set(null);
    this.error.set(null);
  }
}
