import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminMfaPolicy, AdminSecurityApi, ApiError, SaveSecurityPolicyRequest, SecurityPolicyDto } from '@sd/core';
import { SdButton, SdEmptyState, SdFormField, SdIcon, SdInput, SdRadio, SdRadioGroup, SdSkeleton, SdToggle, ToastService } from '@sd/ui';
import { ADMIN_EDITOR_STYLES, AdminLeaveGuarded, sameValue, saveErrors, warnOnUnload } from '../admin-shared';

const MFA_OPTIONS: { value: AdminMfaPolicy; label: string; description: string }[] = [
  { value: 'Optional', label: 'Optional', description: 'Each agent decides whether to use an authenticator app.' },
  { value: 'RequiredForAdmins', label: 'Required for administrators', description: 'Users who can manage roles or security cannot switch MFA off; without an authenticator they get an e-mailed code at sign-in.' },
  { value: 'RequiredForAll', label: 'Required for everyone', description: 'Every agent signs in with a second factor (authenticator app or e-mailed code).' },
];

/** MFA policy, password and session policies, and the agent IP allow-list (with an anti-lockout check on the server). */
@Component({
  selector: 'desk-admin-security-policies',
  imports: [FormsModule, SdButton, SdIcon, SdFormField, SdInput, SdToggle, SdRadioGroup, SdRadio, SdEmptyState, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading()) {
      <sd-skeleton [lines]="10" height="36px" />
    } @else if (loadError()) {
      <sd-empty-state icon="circle-alert" title="Security policies could not be loaded" [message]="loadError()">
        <button sdButton icon="refresh" (click)="load()">Try again</button>
      </sd-empty-state>
    } @else if (form(); as f) {
      @if (formError()) {
        <div class="callout danger" role="alert"><sd-icon name="circle-alert" [size]="16" /><div>{{ formError() }}</div></div>
      }
      <section class="card" aria-labelledby="mfa-h">
        <h2 id="mfa-h">Multi-factor authentication</h2>
        <p class="sd-muted">Single sign-on users are verified by your identity provider.</p>
        <sd-radio-group [value]="f.mfaPolicy" (valueChange)="patch({ mfaPolicy: $any($event) })" ariaLabel="MFA policy">
          @for (o of mfaOptions; track o.value) {
            <sd-radio [value]="o.value" [description]="o.description">{{ o.label }}</sd-radio>
          }
        </sd-radio-group>
      </section>

      <section class="card" aria-labelledby="pw-h">
        <h2 id="pw-h">Passwords</h2>
        <p class="sd-muted">Applies to new and changed passwords; lockout protects against guessing.</p>
        <div class="grid3">
          <sd-form-field label="Minimum length" [error]="errors()['password.minLength']">
            <input sdInput type="number" min="8" max="128" name="minLength" [ngModel]="f.password.minLength" (ngModelChange)="patchPassword({ minLength: +$event })" />
          </sd-form-field>
          <sd-form-field label="Remember previous passwords" hint="0 = allow reuse" [error]="errors()['password.historyCount']">
            <input sdInput type="number" min="0" max="24" name="history" [ngModel]="f.password.historyCount" (ngModelChange)="patchPassword({ historyCount: +$event })" />
          </sd-form-field>
          <sd-form-field label="Expire after (days)" hint="0 = never" [error]="errors()['password.expiryDays']">
            <input sdInput type="number" min="0" max="365" name="expiry" [ngModel]="f.password.expiryDays" (ngModelChange)="patchPassword({ expiryDays: +$event })" />
          </sd-form-field>
          <sd-form-field label="Lock after failed attempts" [error]="errors()['password.maxFailedAttempts']">
            <input sdInput type="number" min="3" max="20" name="attempts" [ngModel]="f.password.maxFailedAttempts" (ngModelChange)="patchPassword({ maxFailedAttempts: +$event })" />
          </sd-form-field>
          <sd-form-field label="Lockout duration (minutes)" [error]="errors()['password.lockoutMinutes']">
            <input sdInput type="number" min="1" max="1440" name="lockout" [ngModel]="f.password.lockoutMinutes" (ngModelChange)="patchPassword({ lockoutMinutes: +$event })" />
          </sd-form-field>
        </div>
        <div class="checks">
          <sd-toggle [value]="f.password.requireUpper" (valueChange)="patchPassword({ requireUpper: $event })">Upper-case letter</sd-toggle>
          <sd-toggle [value]="f.password.requireLower" (valueChange)="patchPassword({ requireLower: $event })">Lower-case letter</sd-toggle>
          <sd-toggle [value]="f.password.requireDigit" (valueChange)="patchPassword({ requireDigit: $event })">Digit</sd-toggle>
          <sd-toggle [value]="f.password.requireSymbol" (valueChange)="patchPassword({ requireSymbol: $event })">Symbol</sd-toggle>
        </div>
      </section>

      <section class="card" aria-labelledby="ss-h">
        <h2 id="ss-h">Sessions</h2>
        <p class="sd-muted">Signed-in agents are signed out after the idle timeout or the absolute lifetime, whichever comes first.</p>
        <div class="grid2">
          <sd-form-field label="Access token lifetime (minutes)" hint="Short tokens limit the impact of a leaked token." [error]="errors()['session.accessTokenMinutes']">
            <input sdInput type="number" min="5" max="60" name="access" [ngModel]="f.session.accessTokenMinutes" (ngModelChange)="patchSession({ accessTokenMinutes: +$event })" />
          </sd-form-field>
          <sd-form-field label="Idle timeout (minutes)" [error]="errors()['session.idleTimeoutMinutes']">
            <input sdInput type="number" min="5" max="10080" name="idle" [ngModel]="f.session.idleTimeoutMinutes" (ngModelChange)="patchSession({ idleTimeoutMinutes: +$event })" />
          </sd-form-field>
          <sd-form-field label="Absolute session lifetime (hours)" [error]="errors()['session.absoluteLifetimeHours']">
            <input sdInput type="number" min="1" max="720" name="absolute" [ngModel]="f.session.absoluteLifetimeHours" (ngModelChange)="patchSession({ absoluteLifetimeHours: +$event })" />
          </sd-form-field>
          <sd-form-field label="Concurrent sessions per user" hint="The oldest session ends when a new one exceeds the limit." [error]="errors()['session.maxConcurrentSessions']">
            <input sdInput type="number" min="1" max="50" name="concurrent" [ngModel]="f.session.maxConcurrentSessions" (ngModelChange)="patchSession({ maxConcurrentSessions: +$event })" />
          </sd-form-field>
        </div>
      </section>

      <section class="card" aria-labelledby="ip-h">
        <h2 id="ip-h">Agent IP allow-list</h2>
        <p class="sd-muted">Agents can only sign in and use SupportDesk from these addresses. Customers, API clients and the platform operator are not affected.</p>
        <sd-toggle [value]="f.ipAllowList.enabled" (valueChange)="patchIp({ enabled: $event })">Restrict agent access to these networks</sd-toggle>
        <div class="ip">
          <sd-form-field label="Addresses and ranges" hint="One per line — single addresses (203.0.113.7) or CIDR ranges (10.0.0.0/8, 2001:db8::/32)." [error]="errors()['ipAllowList']">
            <textarea sdInput name="ranges" class="mono" rows="5" [ngModel]="rangesText()" (ngModelChange)="setRanges($event)"></textarea>
          </sd-form-field>
          <div class="mine">
            <span class="sd-muted small">Your current address</span>
            <strong class="mono">{{ policy()?.currentIp ?? 'unknown' }}</strong>
            @if (policy()?.currentIp && !hasMyIp()) {
              <button sdButton size="sm" icon="plus" (click)="addMyIp()">Add my address</button>
            }
          </div>
        </div>
        @if (f.ipAllowList.enabled && !hasMyIp()) {
          <div class="callout warn" role="status"><sd-icon name="triangle-alert" [size]="16" /><div>Your own address is not in the list — the server will refuse to save so you do not lock yourself out.</div></div>
        }
      </section>

      <div class="savebar">
        @if (dirty()) {
          <span class="sd-muted small"><sd-icon name="circle-dot" [size]="12" /> Unsaved changes</span>
        } @else {
          <span class="sd-muted small">All changes saved</span>
        }
        <span class="sd-spacer"></span>
        <button sdButton variant="ghost" [disabled]="!dirty() || saving()" (click)="discard()">Discard</button>
        <button sdButton variant="primary" icon="check" [loading]="saving()" [disabled]="!dirty()" (click)="save()">Save policies</button>
      </div>
    }
  `,
  styles: `
    ${ADMIN_EDITOR_STYLES}
    .checks { display: flex; flex-wrap: wrap; gap: var(--sd-space-4); margin-top: var(--sd-space-3); }
    .ip { display: grid; grid-template-columns: minmax(0, 1fr) 220px; gap: var(--sd-space-4); margin-top: var(--sd-space-3); align-items: start; }
    .mine { display: flex; flex-direction: column; gap: 4px; padding: 10px 12px; border-radius: var(--sd-radius-md); background: var(--sd-surface-sunken); }
    .callout { margin-top: var(--sd-space-3); }
    @media (max-width: 760px) { .ip { grid-template-columns: minmax(0, 1fr); } }
  `,
})
export class SecurityPoliciesPage implements OnInit, AdminLeaveGuarded {
  private readonly api = inject(AdminSecurityApi);
  private readonly toast = inject(ToastService);

  protected readonly mfaOptions = MFA_OPTIONS;
  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);
  protected readonly policy = signal<SecurityPolicyDto | null>(null);
  protected readonly form = signal<SaveSecurityPolicyRequest | null>(null);
  private readonly original = signal<SaveSecurityPolicyRequest | null>(null);
  protected readonly rangesText = signal('');
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);

  protected readonly dirty = computed(() => !sameValue(this.form(), this.original()));
  protected readonly hasMyIp = computed(() => {
    const ip = this.policy()?.currentIp;
    return !ip || (this.form()?.ipAllowList.ranges ?? []).some((r) => r === ip || r.startsWith(ip + '/'));
  });

  constructor() {
    warnOnUnload(this.dirty);
  }

  ngOnInit(): void {
    this.load();
  }

  hasUnsavedChanges(): boolean {
    return this.dirty() && !this.saving();
  }

  protected load(): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.api.policy().subscribe({
      next: (p) => {
        this.apply(p);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loadError.set(ApiError.from(err).userMessage);
        this.loading.set(false);
      },
    });
  }

  protected patch(change: Partial<SaveSecurityPolicyRequest>): void {
    this.form.update((f) => (f ? { ...f, ...change } : f));
  }

  protected patchPassword(change: Partial<SaveSecurityPolicyRequest['password']>): void {
    this.form.update((f) => (f ? { ...f, password: { ...f.password, ...change } } : f));
  }

  protected patchSession(change: Partial<SaveSecurityPolicyRequest['session']>): void {
    this.form.update((f) => (f ? { ...f, session: { ...f.session, ...change } } : f));
  }

  protected patchIp(change: Partial<SaveSecurityPolicyRequest['ipAllowList']>): void {
    this.form.update((f) => (f ? { ...f, ipAllowList: { ...f.ipAllowList, ...change } } : f));
  }

  protected setRanges(text: string): void {
    this.rangesText.set(text);
    this.patchIp({ ranges: text.split(/[\n,;]+/).map((r) => r.trim()).filter(Boolean) });
  }

  protected addMyIp(): void {
    const ip = this.policy()?.currentIp;
    if (!ip) return;
    const text = this.rangesText().trim();
    this.setRanges(text ? `${text}\n${ip}` : ip);
  }

  protected discard(): void {
    const o = this.original();
    if (o) {
      this.form.set(structuredClone(o));
      this.rangesText.set(o.ipAllowList.ranges.join('\n'));
    }
    this.errors.set({});
    this.formError.set(null);
  }

  protected save(): void {
    const f = this.form();
    if (!f) return;
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    this.api.savePolicy(f).subscribe({
      next: (p) => {
        this.saving.set(false);
        this.apply(p);
        this.toast.success('Security policies saved');
      },
      error: (err: unknown) => {
        this.saving.set(false);
        const { fields, message } = saveErrors(err, 'The policies could not be saved.');
        this.errors.set(fields);
        this.formError.set(message);
      },
    });
  }

  private apply(p: SecurityPolicyDto): void {
    this.policy.set(p);
    const f: SaveSecurityPolicyRequest = { mfaPolicy: p.mfaPolicy, password: { ...p.password }, session: { ...p.session }, ipAllowList: { enabled: p.ipAllowList.enabled, ranges: [...p.ipAllowList.ranges] } };
    this.form.set(f);
    this.original.set(structuredClone(f));
    this.rangesText.set(f.ipAllowList.ranges.join('\n'));
  }
}
