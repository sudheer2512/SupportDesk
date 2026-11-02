import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ApiError, AuthApi, MeApi, MeStore, MfaSetupDto, ToastBridge, canonicalTimeZone, timeZoneOptions } from '@sd/core';
import {
  SdAvatar,
  SdBadge,
  SdButton,
  SdCard,
  SdDialogService,
  SdFormField,
  SdInput,
  SdPageHeader,
  SdQrCode,
  SdRichTextEditor,
  SdTab,
  SdTabs,
  SdLabelPipe,
} from '@sd/ui';

const COMMON_LOCALES = ['en-US', 'en-GB', 'en-IN', 'en-AU', 'de-DE', 'fr-FR', 'es-ES', 'pt-BR', 'it-IT', 'nl-NL', 'ja-JP', 'zh-CN', 'ar-AE', 'hi-IN'];

/** /me — profile (name, time zone, locale, signature), password change and two-factor setup. */
@Component({
  selector: 'desk-profile-page',
  imports: [FormsModule, SdPageHeader, SdTabs, SdTab, SdCard, SdFormField, SdInput, SdButton, SdBadge, SdAvatar, SdQrCode, SdRichTextEditor, SdLabelPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="My profile" subtitle="Your details, sign-in security and preferences." icon="user" />
      <div class="desk-page__body">
        <sd-tabs [selected]="tab()" (selectedChange)="selectTab($event)" ariaLabel="Profile sections">
          <sd-tab id="profile" label="Profile" icon="user">
            <sd-card class="section">
              <div class="identity">
                <sd-avatar [name]="me.displayName()" [src]="me.user()?.avatarUrl ?? null" size="xl" [presence]="me.availability()" />
                <div>
                  <h2>{{ me.displayName() }}</h2>
                  <p class="sd-muted">{{ me.user()?.email }} · {{ me.user()?.tenantName }}</p>
                  <div class="badges">
                    @for (r of me.user()?.roles ?? []; track r) {
                      <sd-badge tone="primary">{{ r }}</sd-badge>
                    } @empty {
                      <sd-badge tone="primary">{{ me.user()?.kind | sdLabel }}</sd-badge>
                    }
                    @if (me.user()?.isLightAgent) {
                      <sd-badge>Light agent</sd-badge>
                    }
                    @for (d of me.user()?.departments ?? []; track d.id) {
                      <sd-badge variant="outline">{{ d.name }}</sd-badge>
                    }
                  </div>
                </div>
              </div>
              <form class="form" (ngSubmit)="saveProfile()" novalidate>
                <sd-form-field label="Display name" required [error]="profileErrors()['displayName']">
                  <input sdInput name="displayName" [(ngModel)]="displayName" required maxlength="120" />
                </sd-form-field>
                <div class="two">
                  <sd-form-field label="Time zone" hint="Dates and SLA times are shown in this zone.">
                    <select sdInput name="timeZone" [(ngModel)]="timeZone">
                      @for (z of zones; track z) {
                        <option [value]="z">{{ z }}</option>
                      }
                    </select>
                  </sd-form-field>
                  <sd-form-field label="Language & format">
                    <select sdInput name="locale" [(ngModel)]="locale">
                      @for (l of locales(); track l) {
                        <option [value]="l">{{ localeName(l) }}</option>
                      }
                    </select>
                  </sd-form-field>
                </div>
                <sd-form-field label="E-mail signature" hint="Appended to your replies.">
                  <sd-rich-text-editor name="signature" toolbar="compact" minHeight="90px" placeholder="Best regards, …" [(ngModel)]="signature" />
                </sd-form-field>
                <div class="actions">
                  <button sdButton variant="primary" type="submit" [loading]="savingProfile()">Save profile</button>
                </div>
              </form>
            </sd-card>
          </sd-tab>

          <sd-tab id="security" label="Password" icon="key">
            <sd-card class="section" title="Change password" subtitle="Use at least 12 characters; a passphrase works well.">
              <form class="form narrow" (ngSubmit)="changePassword()" novalidate>
                <input type="text" name="username" autocomplete="username" [value]="me.user()?.email ?? ''" hidden />
                <sd-form-field label="Current password" required [error]="pwErrors()['currentPassword']">
                  <input sdInput type="password" name="currentPassword" autocomplete="current-password" [(ngModel)]="currentPassword" />
                </sd-form-field>
                <sd-form-field label="New password" required [error]="pwErrors()['newPassword']">
                  <input sdInput type="password" name="newPassword" autocomplete="new-password" [(ngModel)]="newPassword" />
                </sd-form-field>
                <sd-form-field label="Confirm new password" required [error]="pwErrors()['confirm']">
                  <input sdInput type="password" name="confirmPassword" autocomplete="new-password" [(ngModel)]="confirmPassword" />
                </sd-form-field>
                <div class="actions">
                  <button sdButton variant="primary" type="submit" [loading]="savingPassword()">Change password</button>
                </div>
              </form>
            </sd-card>
          </sd-tab>

          <sd-tab id="mfa" label="Two-factor" icon="shield-check">
            <sd-card class="section" title="Two-factor authentication" subtitle="Protect your account with a code from an authenticator app.">
              <div class="status">
                <span>Status</span>
                @if (me.user()?.mfaEnabled) {
                  <sd-badge tone="success" icon="shield-check">On</sd-badge>
                } @else {
                  <sd-badge tone="warning" icon="shield-alert">Off</sd-badge>
                }
              </div>
              @if (me.user()?.mfaEnabled) {
                <p class="sd-secondary">You will be asked for a code each time you sign in on a new device.</p>
                <div class="actions start">
                  <button sdButton variant="danger" type="button" [loading]="mfaBusy()" (click)="disableMfa()">Turn off two-factor</button>
                </div>
              } @else if (setup(); as s) {
                <ol class="steps">
                  <li>
                    <p>Scan this QR code with an authenticator app (Microsoft Authenticator, Google Authenticator, 1Password…).</p>
                    <div class="qr">
                      <sd-qr-code [value]="s.otpAuthUri" [size]="176" label="Authenticator setup QR code" />
                      <div class="secret">
                        <span class="sd-muted sd-small">Can't scan? Enter this key manually:</span>
                        <code class="key">{{ groupedSecret() }}</code>
                      </div>
                    </div>
                  </li>
                  <li>
                    <p>Enter the 6-digit code the app shows to finish.</p>
                    <form class="code-row" (ngSubmit)="enableMfa()" novalidate>
                      <sd-form-field label="Verification code" [error]="mfaError()">
                        <input sdInput name="mfaCode" inputmode="numeric" autocomplete="one-time-code" maxlength="8" [(ngModel)]="mfaCode" />
                      </sd-form-field>
                      <button sdButton variant="primary" type="submit" [loading]="mfaBusy()">Turn on</button>
                      <button sdButton variant="ghost" type="button" (click)="setup.set(null)">Cancel</button>
                    </form>
                  </li>
                </ol>
              } @else {
                <p class="sd-secondary">Two-factor authentication is off. Turning it on adds a second step at sign-in.</p>
                <div class="actions start">
                  <button sdButton variant="primary" type="button" icon="qr-code" [loading]="mfaBusy()" (click)="startMfa()">Set up two-factor</button>
                </div>
              }
            </sd-card>
          </sd-tab>
        </sd-tabs>
      </div>
    </div>
  `,
  styles: `
    .section {
      max-width: 820px;
    }
    .identity {
      display: flex;
      align-items: center;
      gap: 16px;
      padding-bottom: 18px;
      margin-bottom: 18px;
      border-bottom: 1px solid var(--sd-divider);
    }
    .identity h2 {
      font-size: var(--sd-text-xl);
    }
    .badges {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      margin-top: 8px;
    }
    .form {
      display: grid;
      gap: 16px;
    }
    .form.narrow {
      max-width: 420px;
    }
    .two {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 16px;
    }
    @media (max-width: 640px) {
      .two {
        grid-template-columns: 1fr;
      }
    }
    .actions {
      display: flex;
      justify-content: flex-end;
      gap: 8px;
    }
    .actions.start {
      justify-content: flex-start;
      margin-top: 12px;
    }
    .status {
      display: flex;
      align-items: center;
      gap: 10px;
      margin-bottom: 10px;
      font-weight: var(--sd-weight-medium);
    }
    .steps {
      display: grid;
      gap: 18px;
      margin: 12px 0 0;
      padding-left: 20px;
    }
    .qr {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 18px;
      margin-top: 10px;
    }
    .secret {
      display: grid;
      gap: 6px;
    }
    .key {
      padding: 6px 10px;
      font-size: 1rem;
      letter-spacing: 0.08em;
      word-break: break-all;
    }
    .code-row {
      display: flex;
      flex-wrap: wrap;
      align-items: flex-end;
      gap: 8px;
      margin-top: 8px;
    }
  `,
})
export class ProfilePage {
  /** ?section=profile|security|mfa (component input binding). */
  readonly section = input<string | undefined>();

  protected readonly me = inject(MeStore);
  private readonly meApi = inject(MeApi);
  private readonly authApi = inject(AuthApi);
  private readonly toasts = inject(ToastBridge);
  private readonly dialogs = inject(SdDialogService);
  private readonly router = inject(Router);

  protected readonly zones = timeZoneOptions();
  protected readonly tab = signal('profile');

  protected displayName = '';
  protected timeZone = 'UTC';
  protected locale = 'en-US';
  protected signature = '';
  protected readonly savingProfile = signal(false);
  protected readonly profileErrors = signal<Record<string, string | null>>({});
  protected readonly locales = computed(() => {
    const current = this.me.user()?.locale;
    return current && !COMMON_LOCALES.includes(current) ? [current, ...COMMON_LOCALES] : COMMON_LOCALES;
  });

  protected currentPassword = '';
  protected newPassword = '';
  protected confirmPassword = '';
  protected readonly savingPassword = signal(false);
  protected readonly pwErrors = signal<Record<string, string | null>>({});

  protected readonly setup = signal<MfaSetupDto | null>(null);
  protected readonly mfaBusy = signal(false);
  protected readonly mfaError = signal<string | null>(null);
  protected mfaCode = '';
  protected readonly groupedSecret = computed(() => (this.setup()?.secret ?? '').replace(/(.{4})/g, '$1 ').trim());

  constructor() {
    effect(() => {
      const s = this.section();
      untracked(() => this.tab.set(s === 'security' || s === 'mfa' ? s : 'profile'));
    });
    effect(() => {
      const user = this.me.user();
      untracked(() => {
        if (!user) return;
        this.displayName = user.displayName;
        this.timeZone = canonicalTimeZone(user.timeZone || 'UTC');
        this.locale = user.locale || 'en-US';
        this.signature = user.signature ?? '';
      });
    });
  }

  protected selectTab(id: string | null): void {
    const section = id ?? 'profile';
    this.tab.set(section);
    void this.router.navigate([], { queryParams: { section: section === 'profile' ? null : section }, replaceUrl: true });
  }

  protected localeName(tag: string): string {
    try {
      return `${new Intl.DisplayNames([tag], { type: 'language' }).of(tag) ?? tag} (${tag})`;
    } catch {
      return tag;
    }
  }

  protected saveProfile(): void {
    if (!this.displayName.trim()) {
      this.profileErrors.set({ displayName: 'Enter your name.' });
      return;
    }
    this.profileErrors.set({});
    this.savingProfile.set(true);
    this.meApi
      .updateProfile({ displayName: this.displayName.trim(), timeZone: this.timeZone, locale: this.locale, signature: this.signature || null })
      .subscribe({
        next: () => {
          this.savingProfile.set(false);
          this.toasts.success('Profile saved');
        },
        error: (err: unknown) => {
          this.savingProfile.set(false);
          const e = ApiError.from(err);
          this.profileErrors.set({ displayName: e.fieldError('displayName') });
          if (!e.isNetworkError && !e.isServerError) this.toasts.error('Could not save profile', e.userMessage);
        },
      });
  }

  protected changePassword(): void {
    const errors: Record<string, string | null> = {
      currentPassword: this.currentPassword ? null : 'Enter your current password.',
      newPassword: this.newPassword.length >= 12 ? null : 'Use at least 12 characters.',
      confirm: this.newPassword === this.confirmPassword ? null : 'The passwords do not match.',
    };
    this.pwErrors.set(errors);
    if (Object.values(errors).some(Boolean)) return;
    this.savingPassword.set(true);
    this.authApi.changePassword({ currentPassword: this.currentPassword, newPassword: this.newPassword }).subscribe({
      next: () => {
        this.savingPassword.set(false);
        this.currentPassword = this.newPassword = this.confirmPassword = '';
        this.toasts.success('Password changed');
      },
      error: (err: unknown) => {
        this.savingPassword.set(false);
        const e = ApiError.from(err);
        this.pwErrors.set({
          currentPassword: e.fieldError('currentPassword'),
          newPassword: e.fieldError('newPassword'),
          confirm: null,
        });
        if (!e.isNetworkError && !e.isServerError) this.toasts.error('Password not changed', e.userMessage);
      },
    });
  }

  protected startMfa(): void {
    this.mfaBusy.set(true);
    this.mfaError.set(null);
    this.authApi.mfaSetup().subscribe({
      next: (s) => {
        this.setup.set(s);
        this.mfaCode = '';
        this.mfaBusy.set(false);
      },
      error: () => this.mfaBusy.set(false),
    });
  }

  protected enableMfa(): void {
    if (!/^\d{6,8}$/.test(this.mfaCode.trim())) {
      this.mfaError.set('Enter the 6-digit code from your app.');
      return;
    }
    this.mfaBusy.set(true);
    this.authApi.mfaEnable(this.mfaCode.trim()).subscribe({
      next: () => {
        this.mfaBusy.set(false);
        this.setup.set(null);
        this.me.patch({ mfaEnabled: true });
        this.toasts.success('Two-factor authentication is on');
      },
      error: (err: unknown) => {
        this.mfaBusy.set(false);
        this.mfaError.set(ApiError.from(err).status === 400 ? 'That code did not match. Try the current code.' : ApiError.from(err).userMessage);
      },
    });
  }

  protected async disableMfa(): Promise<void> {
    const ok = await this.dialogs.confirm({
      title: 'Turn off two-factor authentication?',
      message: 'Your account will be protected by your password only.',
      confirmLabel: 'Turn off',
      danger: true,
    });
    if (!ok) return;
    this.mfaBusy.set(true);
    this.authApi.mfaDisable().subscribe({
      next: () => {
        this.mfaBusy.set(false);
        this.me.patch({ mfaEnabled: false });
        this.toasts.info('Two-factor authentication is off');
      },
      error: () => this.mfaBusy.set(false),
    });
  }
}
