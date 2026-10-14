import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AcceptInvitationResultDto, AgentsAdminApi, saveErrorMessage, validationMessages } from '@sd/core';
import { SdButton, SdFormField, SdIcon, SdInput, SdLogo } from '@sd/ui';

/**
 * /accept-invite?tenant=…&email=… (public) — an invited agent enters the e-mailed code and chooses a password.
 * The API never reveals whether an account exists; errors are generic.
 */
@Component({
  selector: 'desk-accept-invite-page',
  imports: [FormsModule, RouterLink, SdButton, SdFormField, SdInput, SdIcon, SdLogo],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="page">
      <div class="card">
        <sd-logo [size]="32" wordmark />
        @if (done(); as d) {
          <h1>You're all set, {{ d.name }}</h1>
          <p class="lead">Your password is saved. Sign in to the <strong>{{ d.tenant }}</strong> workspace with {{ d.email }}.</p>
          <a sdButton variant="primary" routerLink="/login" icon="log-in" block>Go to sign in</a>
        } @else {
          <h1>Join your team</h1>
          <p class="lead">Enter the invitation code from your e-mail and choose a password.</p>
          @if (error()) {
            <div class="callout" role="alert"><sd-icon name="circle-alert" [size]="16" />{{ error() }}</div>
          }
          <form class="form" (ngSubmit)="submit()" novalidate>
            <sd-form-field label="Workspace" required>
              <input sdInput name="tenant" [(ngModel)]="workspace" required autocomplete="organization" />
            </sd-form-field>
            <sd-form-field label="Work e-mail" required>
              <input sdInput name="email" type="email" [(ngModel)]="mail" required autocomplete="username" />
            </sd-form-field>
            <sd-form-field label="Invitation code" required hint="Format XXXX-XXXX (letters and digits).">
              <input sdInput name="code" [(ngModel)]="code" required autocomplete="one-time-code" class="code" maxlength="12" />
            </sd-form-field>
            <sd-form-field label="Your name" optional hint="Leave empty to keep the name your administrator entered.">
              <input sdInput name="name" [(ngModel)]="name" maxlength="120" autocomplete="name" />
            </sd-form-field>
            <sd-form-field label="New password" required [error]="fieldErrors()['password']"
              hint="At least 10 characters with upper- and lower-case letters and a digit (your organisation may require more).">
              <input sdInput name="password" type="password" [(ngModel)]="password" required autocomplete="new-password" />
            </sd-form-field>
            <sd-form-field label="Confirm password" required [error]="mismatch() ? 'The passwords do not match.' : null">
              <input sdInput name="confirm" type="password" [(ngModel)]="confirm" required autocomplete="new-password" (ngModelChange)="confirmValue.set($event)" />
            </sd-form-field>
            <button sdButton variant="primary" type="submit" block [loading]="saving()" [disabled]="!canSubmit()">Set password</button>
          </form>
          <p class="help">Already have a password? <a routerLink="/login">Sign in</a></p>
        }
      </div>
    </main>
  `,
  styles: `
    :host { display: block; min-height: 100vh; background: var(--sd-bg); color: var(--sd-text); }
    .page { min-height: 100vh; display: grid; place-items: center; padding: var(--sd-space-6) var(--sd-space-4); }
    .card { width: min(100%, 440px); display: grid; gap: var(--sd-space-4); padding: var(--sd-space-7) var(--sd-space-6); background: var(--sd-surface);
      border: 1px solid var(--sd-border); border-radius: var(--sd-radius-xl); box-shadow: var(--sd-shadow-md); }
    @media (max-width: 480px) { .card { padding: var(--sd-space-5) var(--sd-space-4); } }
    h1 { font-size: var(--sd-text-xl); font-weight: var(--sd-weight-semibold); margin: 0; }
    .lead { color: var(--sd-text-2); margin: 0; }
    .form { display: grid; gap: var(--sd-space-3); }
    .code { font-family: var(--sd-font-mono); letter-spacing: 2px; text-transform: uppercase; }
    .callout { display: flex; gap: var(--sd-space-2); align-items: center; padding: var(--sd-space-3); border-radius: var(--sd-radius-md);
      background: var(--sd-danger-bg); border: 1px solid var(--sd-danger-border); color: var(--sd-danger); font-size: var(--sd-text-sm); }
    .help { font-size: var(--sd-text-sm); color: var(--sd-text-3); margin: 0; }
    a { color: var(--sd-primary); }
  `,
})
export class AcceptInvitePage {
  private readonly api = inject(AgentsAdminApi);
  /** Query parameters from the invitation link. */
  readonly tenant = input<string | null>(null);
  readonly email = input<string | null>(null);
  protected workspace = '';
  protected mail = '';
  protected code = '';
  protected name = '';
  protected password = '';
  protected confirm = '';
  protected readonly confirmValue = signal('');
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly fieldErrors = signal<Record<string, string>>({});
  protected readonly done = signal<AcceptInvitationResultDto | null>(null);
  protected readonly mismatch = computed(() => this.confirmValue().length > 0 && this.confirmValue() !== this.password);

  constructor() {
    effect(() => {
      if (this.tenant() && !this.workspace) this.workspace = this.tenant()!;
      if (this.email() && !this.mail) this.mail = this.email()!;
    });
  }

  protected canSubmit(): boolean {
    return !!this.workspace.trim() && !!this.mail.trim() && !!this.code.trim() && this.password.length > 0 && this.password === this.confirm;
  }

  protected submit(): void {
    if (!this.canSubmit()) return;
    this.saving.set(true);
    this.error.set(null);
    this.fieldErrors.set({});
    this.api
      .acceptInvitation({ tenant: this.workspace.trim(), email: this.mail.trim(), code: this.code.trim(), password: this.password, displayName: this.name.trim() || null })
      .subscribe({
        next: (r) => {
          this.saving.set(false);
          this.done.set(r);
        },
        error: (err: unknown) => {
          this.saving.set(false);
          this.fieldErrors.set(validationMessages(err));
          this.error.set(saveErrorMessage(err));
        },
      });
  }
}
