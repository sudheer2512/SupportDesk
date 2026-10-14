import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { forkJoin } from 'rxjs';
import { AdminRoleListItemDto, AdminRolesApi, AdminSecurityApi, ApiError, SsoConfigDto, SsoTestResultDto } from '@sd/core';
import { SdBadge, SdButton, SdEmptyState, SdFormField, SdIcon, SdInput, SdSkeleton, SdToggle, ToastService } from '@sd/ui';
import { ADMIN_EDITOR_STYLES, AdminLeaveGuarded, sameValue, saveErrors, warnOnUnload } from '../admin-shared';

interface SsoForm {
  enabled: boolean;
  authority: string;
  clientId: string;
  scopes: string;
  emailClaim: string;
  nameClaim: string;
  autoProvision: boolean;
  defaultRoleId: string;
  enforceSso: boolean;
}

/**
 * OpenID Connect single sign-on: provider (authority, client id, write-only secret), claim mapping, just-in-time
 * provisioning with a default role, "SSO required" (administrators keep password sign-in as a break-glass), and a
 * connection test that loads the provider's discovery document.
 */
@Component({
  selector: 'desk-admin-sso',
  imports: [FormsModule, SdButton, SdBadge, SdIcon, SdFormField, SdInput, SdToggle, SdEmptyState, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading()) {
      <sd-skeleton [lines]="8" height="40px" />
    } @else if (loadError()) {
      <sd-empty-state icon="circle-alert" title="SSO settings could not be loaded" [message]="loadError()">
        <button sdButton icon="refresh" (click)="load()">Try again</button>
      </sd-empty-state>
    } @else if (config(); as c) {
      @if (!c.planIncludesSso) {
        <div class="callout warn" role="note"><sd-icon name="triangle-alert" [size]="16" /><div><strong>Single sign-on is not included in your plan.</strong><p>You can prepare the configuration; switching it on requires a plan with SSO.</p></div></div>
      }
      @if (formError()) {
        <div class="callout danger" role="alert"><sd-icon name="circle-alert" [size]="16" /><div>{{ formError() }}</div></div>
      }

      <section class="card" aria-labelledby="idp-h">
        <div class="hrow">
          <h2 id="idp-h">Identity provider</h2>
          <sd-badge [tone]="c.enabled ? 'success' : 'neutral'" dot>{{ c.enabled ? 'SSO on' : 'SSO off' }}</sd-badge>
        </div>
        <p class="sd-muted">Register SupportDesk in Microsoft Entra ID, Okta, Google Workspace, Keycloak… as a web application using the redirect URI below.</p>
        <div class="urls">
          <div><span class="sd-muted small">Redirect (callback) URI</span><code class="mono">{{ c.callbackUrl }}</code><button sdButton size="sm" variant="ghost" iconOnly icon="copy" aria-label="Copy the redirect URI" (click)="copy(c.callbackUrl)"></button></div>
          <div><span class="sd-muted small">Sign-in start URL</span><code class="mono">{{ c.startUrl }}</code><button sdButton size="sm" variant="ghost" iconOnly icon="copy" aria-label="Copy the sign-in URL" (click)="copy(c.startUrl)"></button></div>
        </div>
        <div class="grid2">
          <sd-form-field label="Authority (issuer)" required hint="https://login.microsoftonline.com/{tenant}/v2.0" [error]="errors()['authority']">
            <input sdInput name="authority" class="mono" [ngModel]="form().authority" (ngModelChange)="patch({ authority: $event })" maxlength="500" />
          </sd-form-field>
          <sd-form-field label="Client id" required [error]="errors()['clientId']">
            <input sdInput name="clientId" class="mono" [ngModel]="form().clientId" (ngModelChange)="patch({ clientId: $event })" maxlength="300" />
          </sd-form-field>
          <sd-form-field label="Client secret" [hint]="secretHint()" [error]="errors()['clientSecret']">
            <span class="secret">
              <input sdInput name="secret" type="password" autocomplete="new-password" [placeholder]="c.hasClientSecret && !clearSecret() ? '•••••••• stored' : ''"
                [ngModel]="secret()" (ngModelChange)="secret.set($event); clearSecret.set(false)" />
              @if (c.hasClientSecret) {
                <button sdButton type="button" size="sm" variant="ghost" [pressed]="clearSecret()" (click)="clearSecret.set(!clearSecret()); secret.set('')">{{ clearSecret() ? 'Keep' : 'Remove' }}</button>
              }
            </span>
          </sd-form-field>
          <sd-form-field label="Scopes" [error]="errors()['scopes']">
            <input sdInput name="scopes" class="mono" [ngModel]="form().scopes" (ngModelChange)="patch({ scopes: $event })" maxlength="300" />
          </sd-form-field>
        </div>
        <div class="row">
          <button sdButton icon="plug" [loading]="testing()" [disabled]="!form().authority.trim()" (click)="test()">Test connection</button>
          @if (testResult(); as t) {
            <sd-badge [tone]="t.success ? 'success' : 'danger'" dot>{{ t.success ? 'Reachable' : 'Failed' }}</sd-badge>
          }
        </div>
        @if (testResult(); as t) {
          <div class="test" role="status">
            <p>{{ t.message }}</p>
            @if (t.success) {
              <dl>
                <dt>Issuer</dt><dd class="mono">{{ t.issuer }}</dd>
                <dt>Authorization endpoint</dt><dd class="mono">{{ t.authorizationEndpoint }}</dd>
                <dt>Token endpoint</dt><dd class="mono">{{ t.tokenEndpoint }}</dd>
                <dt>Signing keys</dt><dd>{{ t.signingKeys }}</dd>
              </dl>
            }
          </div>
        }
      </section>

      <section class="card" aria-labelledby="claims-h">
        <h2 id="claims-h">Claims &amp; provisioning</h2>
        <div class="grid2">
          <sd-form-field label="E-mail claim" hint="Matches agents by e-mail address." [error]="errors()['emailClaim']">
            <input sdInput name="emailClaim" class="mono" [ngModel]="form().emailClaim" (ngModelChange)="patch({ emailClaim: $event })" maxlength="100" />
          </sd-form-field>
          <sd-form-field label="Display-name claim" [error]="errors()['nameClaim']">
            <input sdInput name="nameClaim" class="mono" [ngModel]="form().nameClaim" (ngModelChange)="patch({ nameClaim: $event })" maxlength="100" />
          </sd-form-field>
        </div>
        <div class="toggles">
          <sd-toggle [value]="form().autoProvision" (valueChange)="patch({ autoProvision: $event })">Create agents automatically at their first SSO sign-in (just-in-time)</sd-toggle>
          @if (form().autoProvision) {
            <sd-form-field label="Role for new agents" required [error]="errors()['defaultRoleId']">
              <select sdInput name="role" [ngModel]="form().defaultRoleId" (ngModelChange)="patch({ defaultRoleId: $event })">
                <option value="">Choose a role…</option>
                @for (r of roles(); track r.id) {
                  <option [value]="r.id">{{ r.name }}</option>
                }
              </select>
            </sd-form-field>
          }
          <sd-toggle [value]="form().enabled" (valueChange)="patch({ enabled: $event, enforceSso: $event && form().enforceSso })">Single sign-on is on</sd-toggle>
          <sd-toggle [value]="form().enforceSso" [disabled]="!form().enabled" (valueChange)="patch({ enforceSso: $event })">Require SSO — agents cannot sign in with a password (administrators can, as a fallback)</sd-toggle>
          @if (errors()['enforceSso']) {
            <p class="err">{{ errors()['enforceSso'] }}</p>
          }
        </div>
      </section>

      <div class="savebar">
        @if (dirty()) {
          <span class="sd-muted small"><sd-icon name="circle-dot" [size]="12" /> Unsaved changes</span>
        } @else {
          <span class="sd-muted small">All changes saved</span>
        }
        <span class="sd-spacer"></span>
        <button sdButton variant="ghost" [disabled]="!dirty() || saving()" (click)="discard()">Discard</button>
        <button sdButton variant="primary" icon="check" [loading]="saving()" [disabled]="!dirty()" (click)="save()">Save SSO settings</button>
      </div>
    }
  `,
  styles: `
    ${ADMIN_EDITOR_STYLES}
    .hrow { display: flex; align-items: center; gap: 8px; }
    .hrow h2 { margin: 0; }
    .urls { display: flex; flex-direction: column; gap: 6px; margin-bottom: var(--sd-space-4); }
    .urls > div { display: grid; grid-template-columns: 170px minmax(0, 1fr) auto; align-items: center; gap: 8px; padding: 6px 10px; border-radius: var(--sd-radius-sm); background: var(--sd-surface-sunken); }
    .urls code { word-break: break-all; }
    .secret { display: flex; gap: 6px; }
    .row { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin-top: var(--sd-space-3); }
    .test { margin-top: var(--sd-space-3); padding: 10px 12px; border: 1px solid var(--sd-border); border-radius: var(--sd-radius-md); font-size: var(--sd-text-sm); }
    .test p { margin: 0 0 6px; }
    .test dl { display: grid; grid-template-columns: 180px minmax(0, 1fr); gap: 4px 10px; margin: 0; }
    .test dt { color: var(--sd-text-3); }
    .test dd { margin: 0; word-break: break-all; }
    .toggles { display: flex; flex-direction: column; gap: 10px; margin-top: var(--sd-space-3); }
    .toggles sd-form-field { max-width: 360px; }
    @media (max-width: 760px) { .urls > div { grid-template-columns: minmax(0, 1fr) auto; } .urls span { grid-column: 1 / -1; } .test dl { grid-template-columns: minmax(0, 1fr); } }
  `,
})
export class SsoPage implements OnInit, AdminLeaveGuarded {
  private readonly api = inject(AdminSecurityApi);
  private readonly rolesApi = inject(AdminRolesApi);
  private readonly toast = inject(ToastService);

  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);
  protected readonly config = signal<SsoConfigDto | null>(null);
  protected readonly roles = signal<AdminRoleListItemDto[]>([]);
  protected readonly form = signal<SsoForm>(this.toForm(null));
  private readonly original = signal<SsoForm>(this.toForm(null));
  protected readonly secret = signal('');
  protected readonly clearSecret = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly testing = signal(false);
  protected readonly testResult = signal<SsoTestResultDto | null>(null);

  protected readonly dirty = computed(() => !sameValue(this.form(), this.original()) || !!this.secret() || this.clearSecret());
  protected readonly secretHint = computed(() =>
    this.clearSecret() ? 'The stored secret will be removed when you save.' : this.config()?.hasClientSecret ? 'Leave empty to keep the stored secret. It is never shown again.' : 'Write-only: stored encrypted.',
  );

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
    forkJoin({ config: this.api.sso(), roles: this.rolesApi.list() }).subscribe({
      next: ({ config, roles }) => {
        this.apply(config);
        this.roles.set(roles);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loadError.set(ApiError.from(err).userMessage);
        this.loading.set(false);
      },
    });
  }

  protected patch(change: Partial<SsoForm>): void {
    this.form.update((f) => ({ ...f, ...change }));
  }

  protected discard(): void {
    this.form.set({ ...this.original() });
    this.secret.set('');
    this.clearSecret.set(false);
    this.errors.set({});
    this.formError.set(null);
  }

  protected test(): void {
    this.testing.set(true);
    this.api.testSso(this.form().authority.trim() || null).subscribe({
      next: (r) => {
        this.testing.set(false);
        this.testResult.set(r);
      },
      error: (err: unknown) => {
        this.testing.set(false);
        this.toast.error('The test could not run', ApiError.from(err).userMessage);
      },
    });
  }

  protected save(): void {
    const f = this.form();
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    this.api
      .saveSso({
        enabled: f.enabled,
        authority: f.authority.trim() || null,
        clientId: f.clientId.trim() || null,
        clientSecret: this.secret() || null,
        clearClientSecret: this.clearSecret(),
        scopes: f.scopes,
        emailClaim: f.emailClaim,
        nameClaim: f.nameClaim,
        autoProvision: f.autoProvision,
        defaultRoleId: f.defaultRoleId || null,
        enforceSso: f.enabled && f.enforceSso,
      })
      .subscribe({
        next: (c) => {
          this.saving.set(false);
          this.apply(c);
          this.toast.success('SSO settings saved');
        },
        error: (err: unknown) => {
          this.saving.set(false);
          const { fields, message } = saveErrors(err, 'The SSO settings could not be saved.');
          this.errors.set(fields);
          this.formError.set(message);
        },
      });
  }

  protected copy(text: string): void {
    void navigator.clipboard?.writeText(text).then(() => this.toast.success('Copied'));
  }

  private apply(c: SsoConfigDto): void {
    this.config.set(c);
    const f = this.toForm(c);
    this.form.set(f);
    this.original.set({ ...f });
    this.secret.set('');
    this.clearSecret.set(false);
  }

  private toForm(c: SsoConfigDto | null): SsoForm {
    return {
      enabled: c?.enabled ?? false,
      authority: c?.authority ?? '',
      clientId: c?.clientId ?? '',
      scopes: c?.scopes ?? 'openid profile email',
      emailClaim: c?.emailClaim ?? 'email',
      nameClaim: c?.nameClaim ?? 'name',
      autoProvision: c?.autoProvision ?? false,
      defaultRoleId: c?.defaultRoleId ?? '',
      enforceSso: c?.enforceSso ?? false,
    };
  }
}
