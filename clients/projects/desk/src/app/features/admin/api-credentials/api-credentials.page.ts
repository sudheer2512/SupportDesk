import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { forkJoin } from 'rxjs';
import { AdminApiKeyDto, AdminOAuthClientDto, ApiCredentialsApi, ApiError, ApiScopeDto, MetaApi } from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCheckbox,
  SdCombobox,
  SdDialogService,
  SdDrawer,
  SdEmptyState,
  SdFormField,
  SdIcon,
  SdInput,
  SdOption,
  SdPageHeader,
  SdRelativeTimePipe,
  SdSkeleton,
  SdTab,
  SdTabs,
  SdTone,
  SdTooltip,
  ToastService,
} from '@sd/ui';
import { ADMIN_EDITOR_STYLES, AdminLeaveGuarded, saveErrors } from '../admin-shared';

interface CredentialForm {
  kind: 'key' | 'client';
  name: string;
  scopes: string[];
  actAsUserId: string | null;
  expiresOn: string;
}

interface Revealed {
  title: string;
  lines: { label: string; value: string }[];
}

/**
 * API credentials (admin.integrations): static API keys (X-Api-Key) and OAuth 2.0 client-credentials apps, both
 * limited to scopes. Secrets are shown exactly once; keys and clients can be revoked, client secrets rotated.
 */
@Component({
  selector: 'desk-admin-api-credentials',
  imports: [
    FormsModule,
    SdPageHeader,
    SdTabs,
    SdTab,
    SdButton,
    SdBadge,
    SdIcon,
    SdDrawer,
    SdFormField,
    SdInput,
    SdCheckbox,
    SdCombobox,
    SdEmptyState,
    SdSkeleton,
    SdTooltip,
    SdRelativeTimePipe,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="API keys & OAuth clients" icon="key" [breadcrumbs]="crumbs" subtitle="Credentials for integrations calling the SupportDesk API. Each is limited to the scopes you grant.">
        <button sdButton sdPageActions variant="primary" icon="plus" (click)="create(tab() === 'clients' ? 'client' : 'key')">{{ tab() === 'clients' ? 'New OAuth client' : 'New API key' }}</button>
      </sd-page-header>
      <div class="desk-page__body">
        @if (revealed(); as r) {
          <div class="callout warn secret" role="status">
            <sd-icon name="key" [size]="16" />
            <div class="grow">
              <strong>{{ r.title }} — copy it now, it is shown only once.</strong>
              @for (l of r.lines; track l.label) {
                <div class="sline"><span class="sd-muted small">{{ l.label }}</span><code class="mono">{{ l.value }}</code>
                  <button sdButton size="sm" variant="ghost" iconOnly icon="copy" [attr.aria-label]="'Copy ' + l.label" (click)="copy(l.value)"></button></div>
              }
            </div>
            <button sdButton size="sm" variant="ghost" iconOnly icon="x" aria-label="Dismiss" (click)="revealed.set(null)"></button>
          </div>
        }

        @if (loading()) {
          <sd-skeleton [lines]="5" height="56px" />
        } @else if (error()) {
          <sd-empty-state icon="circle-alert" title="Credentials could not be loaded" [message]="error()">
            <button sdButton icon="refresh" (click)="load()">Try again</button>
          </sd-empty-state>
        } @else {
          <sd-tabs [(selected)]="tab" ariaLabel="Credential types">
            <sd-tab id="keys" label="API keys" icon="key" [badge]="activeKeys()">
              <p class="sd-muted small intro">Send the key in the <code>X-Api-Key</code> header. Keys acting as a user never exceed that user's permissions.</p>
              @if (keys().length === 0) {
                <sd-empty-state icon="key" title="No API keys yet" message="Create a key for scripts and simple integrations.">
                  <button sdButton variant="primary" icon="plus" (click)="create('key')">Create an API key</button>
                </sd-empty-state>
              } @else {
                <ul class="rows">
                  @for (k of keys(); track k.id) {
                    <li [class.dim]="k.status !== 'Active'">
                      <div class="grow">
                        <div class="title">
                          <strong>{{ k.name }}</strong>
                          <code class="mono sd-muted">sdk_{{ k.prefix }}_…</code>
                          <sd-badge size="sm" [tone]="tone(k.status)" dot>{{ k.status }}</sd-badge>
                        </div>
                        <div class="scopes">
                          @for (s of k.scopes; track s) {
                            <sd-badge size="sm" variant="outline">{{ s }}</sd-badge>
                          }
                        </div>
                        <span class="sd-muted small">
                          {{ k.actAsUserName ? 'Acts as ' + k.actAsUserName + ' · ' : '' }}created {{ k.createdAt | sdRelativeTime }}{{ k.createdBy ? ' by ' + k.createdBy : '' }}
                          · {{ k.lastUsedAt ? 'last used ' + (k.lastUsedAt | sdRelativeTime) : 'never used' }}
                          · {{ k.expiresAt ? 'expires ' + (k.expiresAt | sdRelativeTime) : 'no expiry' }}
                        </span>
                      </div>
                      @if (k.status === 'Active') {
                        <button sdButton size="sm" variant="ghost" icon="circle-x" [attr.aria-label]="'Revoke ' + k.name" (click)="revokeKey(k)">Revoke</button>
                      }
                    </li>
                  }
                </ul>
              }
            </sd-tab>
            <sd-tab id="clients" label="OAuth clients" icon="plug" [badge]="activeClients()">
              <p class="sd-muted small intro">Clients exchange their id and secret for a 1-hour bearer token at the token endpoint (<code>grant_type=client_credentials</code>).</p>
              @if (clients().length === 0) {
                <sd-empty-state icon="plug" title="No OAuth clients yet" message="Create a client for server-to-server integrations such as your ERP or CRM.">
                  <button sdButton variant="primary" icon="plus" (click)="create('client')">Create an OAuth client</button>
                </sd-empty-state>
              } @else {
                <ul class="rows">
                  @for (c of clients(); track c.id) {
                    <li [class.dim]="c.status !== 'Active'">
                      <div class="grow">
                        <div class="title">
                          <strong>{{ c.name }}</strong>
                          <code class="mono sd-muted">{{ c.clientId }}</code>
                          <sd-badge size="sm" [tone]="tone(c.status)" dot>{{ c.status }}</sd-badge>
                        </div>
                        <div class="scopes">
                          @for (s of c.scopes; track s) {
                            <sd-badge size="sm" variant="outline">{{ s }}</sd-badge>
                          }
                        </div>
                        <span class="sd-muted small">
                          created {{ c.createdAt | sdRelativeTime }}{{ c.createdBy ? ' by ' + c.createdBy : '' }}
                          · {{ c.lastUsedAt ? 'last token ' + (c.lastUsedAt | sdRelativeTime) : 'never used' }}
                          · {{ c.expiresAt ? 'expires ' + (c.expiresAt | sdRelativeTime) : 'no expiry' }}
                        </span>
                      </div>
                      @if (c.status === 'Active') {
                        <button sdButton size="sm" variant="ghost" icon="refresh" [attr.aria-label]="'Rotate the secret of ' + c.name" (click)="rotate(c)">Rotate secret</button>
                        <button sdButton size="sm" variant="ghost" icon="circle-x" [attr.aria-label]="'Revoke ' + c.name" (click)="revokeClient(c)">Revoke</button>
                      }
                    </li>
                  }
                </ul>
              }
            </sd-tab>
          </sd-tabs>
        }
      </div>
    </div>

    <sd-drawer [(open)]="editorOpen" [title]="form().kind === 'key' ? 'New API key' : 'New OAuth client'" width="560px">
      <form class="form" id="cred-form" (ngSubmit)="save()">
        @if (formError()) {
          <div class="callout danger" role="alert"><sd-icon name="circle-alert" [size]="16" /><div>{{ formError() }}</div></div>
        }
        <sd-form-field label="Name" required hint="Where it is used, e.g. “Wings ERP sync”." [error]="errors()['name']">
          <input sdInput name="name" [ngModel]="form().name" (ngModelChange)="patch({ name: $event })" maxlength="100" />
        </sd-form-field>
        <fieldset [attr.aria-invalid]="!!errors()['scopes']">
          <legend>Scopes</legend>
          @for (s of scopes(); track s.scope) {
            <div class="scope" [class.locked]="!s.grantable">
              <sd-checkbox [value]="form().scopes.includes(s.scope)" [disabled]="!s.grantable" (valueChange)="toggleScope(s.scope, $event)">
                <code class="mono">{{ s.scope }}</code> — {{ s.description }}
              </sd-checkbox>
              @if (!s.grantable) {
                <sd-icon name="lock" [size]="13" [sdTooltip]="'It grants permissions you do not hold.'" />
              }
            </div>
          }
          @if (errors()['scopes']) {
            <p class="err">{{ errors()['scopes'] }}</p>
          }
        </fieldset>
        @if (form().kind === 'key') {
          <sd-form-field label="Act as user" optional hint="The key's permissions are then limited to this user's permissions too." [error]="errors()['actAsUserId']">
            <sd-combobox [options]="userOptions()" [value]="form().actAsUserId" (valueChange)="patch({ actAsUserId: asId($event) })" placeholder="No user (scopes only)" ariaLabel="Act as user" />
          </sd-form-field>
        }
        <sd-form-field label="Expires on" optional hint="Recommended. Leave empty for no expiry." [error]="errors()['expiresAt']">
          <input sdInput type="date" name="expires" [min]="minDate" [ngModel]="form().expiresOn" (ngModelChange)="patch({ expiresOn: $event })" />
        </sd-form-field>
      </form>
      <div sdDrawerFooter class="footer">
        <button sdButton type="button" variant="ghost" (click)="editorOpen.set(false)">Cancel</button>
        <button sdButton type="submit" form="cred-form" variant="primary" [loading]="saving()" [disabled]="form().scopes.length === 0">
          {{ form().kind === 'key' ? 'Create key' : 'Create client' }}
        </button>
      </div>
    </sd-drawer>
  `,
  styles: `
    ${ADMIN_EDITOR_STYLES}
    .secret { margin-bottom: var(--sd-space-4); }
    .sline { display: grid; grid-template-columns: 110px minmax(0, 1fr) auto; align-items: center; gap: 8px; margin-top: 6px; }
    .sline code { word-break: break-all; }
    .intro { margin: var(--sd-space-3) 0; }
    .title { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
    .scopes { display: flex; flex-wrap: wrap; gap: 4px; margin: 4px 0; }
    .grow { display: flex; flex-direction: column; }
    li.dim strong { color: var(--sd-text-3); }
    .scope { display: flex; align-items: center; gap: 6px; padding: 3px 0; }
    .scope.locked { opacity: 0.7; }
    @media (max-width: 640px) { .sline { grid-template-columns: minmax(0, 1fr) auto; } .sline span { grid-column: 1 / -1; } }
  `,
})
export class ApiCredentialsPage implements OnInit, AdminLeaveGuarded {
  private readonly api = inject(ApiCredentialsApi);
  private readonly meta = inject(MetaApi);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(SdDialogService);

  protected readonly crumbs = [{ label: 'Administration', link: '/admin' }, { label: 'API keys & OAuth clients' }];
  protected readonly minDate = new Date(Date.now() + 86_400_000).toISOString().slice(0, 10);
  protected readonly tab = signal<string | null>('keys');
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly keys = signal<AdminApiKeyDto[]>([]);
  protected readonly clients = signal<AdminOAuthClientDto[]>([]);
  protected readonly scopes = signal<ApiScopeDto[]>([]);
  protected readonly revealed = signal<Revealed | null>(null);

  protected readonly editorOpen = signal(false);
  protected readonly form = signal<CredentialForm>({ kind: 'key', name: '', scopes: [], actAsUserId: null, expiresOn: '' });
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);

  protected readonly activeKeys = computed(() => this.keys().filter((k) => k.status === 'Active').length || null);
  protected readonly activeClients = computed(() => this.clients().filter((c) => c.status === 'Active').length || null);
  protected readonly userOptions = computed<SdOption[]>(() =>
    (this.meta.ticketForm()?.agents ?? []).map((a) => ({ value: a.userId, label: a.name, description: a.email, avatar: a.name })),
  );

  ngOnInit(): void {
    this.meta.load();
    this.load();
  }

  /** A revealed secret that was not dismissed counts as "unsaved" — leaving loses it for good. */
  hasUnsavedChanges(): boolean {
    return !!this.revealed() || (this.editorOpen() && !!this.form().name.trim());
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    forkJoin({ keys: this.api.keys(), clients: this.api.clients(), scopes: this.api.scopes() }).subscribe({
      next: ({ keys, clients, scopes }) => {
        this.keys.set(keys);
        this.clients.set(clients);
        this.scopes.set(scopes);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.error.set(ApiError.from(err).userMessage);
        this.loading.set(false);
      },
    });
  }

  protected tone(status: string): SdTone {
    return status === 'Active' ? 'success' : status === 'Expired' ? 'warning' : 'neutral';
  }

  protected asId(v: string | string[] | null): string | null {
    return typeof v === 'string' ? v : null;
  }

  protected create(kind: 'key' | 'client'): void {
    this.tab.set(kind === 'key' ? 'keys' : 'clients');
    this.form.set({ kind, name: '', scopes: [], actAsUserId: null, expiresOn: '' });
    this.errors.set({});
    this.formError.set(null);
    this.editorOpen.set(true);
  }

  protected patch(change: Partial<CredentialForm>): void {
    this.form.update((f) => ({ ...f, ...change }));
  }

  protected toggleScope(scope: string, on: boolean): void {
    const rest = this.form().scopes.filter((s) => s !== scope);
    this.patch({ scopes: on ? [...rest, scope] : rest });
  }

  protected save(): void {
    const f = this.form();
    const expiresAt = f.expiresOn ? new Date(`${f.expiresOn}T23:59:59Z`).toISOString() : null;
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    const done = () => {
      this.saving.set(false);
      this.editorOpen.set(false);
      this.load();
    };
    const fail = (err: unknown) => {
      this.saving.set(false);
      const { fields, message } = saveErrors(err, 'The credential could not be created.');
      this.errors.set(fields);
      this.formError.set(message);
    };
    if (f.kind === 'key')
      this.api.createKey({ name: f.name.trim(), scopes: f.scopes, actAsUserId: f.actAsUserId, expiresAt }).subscribe({
        next: (r) => {
          done();
          this.revealed.set({ title: `API key “${r.key.name}”`, lines: [{ label: 'API key', value: r.secret }] });
          this.toast.success('API key created', r.key.name);
        },
        error: fail,
      });
    else
      this.api.createClient({ name: f.name.trim(), scopes: f.scopes, expiresAt }).subscribe({
        next: (r) => {
          done();
          this.revealed.set({
            title: `OAuth client “${r.client.name}”`,
            lines: [
              { label: 'Client id', value: r.client.clientId },
              { label: 'Client secret', value: r.clientSecret },
              { label: 'Token endpoint', value: r.tokenEndpoint },
            ],
          });
          this.toast.success('OAuth client created', r.client.name);
        },
        error: fail,
      });
  }

  protected async revokeKey(k: AdminApiKeyDto): Promise<void> {
    if (!(await this.dialogs.confirm({ title: `Revoke “${k.name}”?`, message: 'Integrations using this key stop working immediately.', danger: true, confirmLabel: 'Revoke key' }))) return;
    this.api.revokeKey(k.id).subscribe({
      next: () => {
        this.toast.success('API key revoked', k.name);
        this.load();
      },
      error: (err: unknown) => this.toast.error('Could not revoke the key', ApiError.from(err).userMessage),
    });
  }

  protected async rotate(c: AdminOAuthClientDto): Promise<void> {
    if (!(await this.dialogs.confirm({ title: `Rotate the secret of “${c.name}”?`, message: 'The old secret stops working; update the integration with the new one.', confirmLabel: 'Rotate secret' }))) return;
    this.api.rotateClientSecret(c.id).subscribe({
      next: (r) => {
        this.revealed.set({ title: `New secret for “${r.client.name}”`, lines: [{ label: 'Client id', value: r.client.clientId }, { label: 'Client secret', value: r.clientSecret }] });
        this.load();
      },
      error: (err: unknown) => this.toast.error('Could not rotate the secret', ApiError.from(err).userMessage),
    });
  }

  protected async revokeClient(c: AdminOAuthClientDto): Promise<void> {
    if (!(await this.dialogs.confirm({ title: `Revoke “${c.name}”?`, message: 'New tokens are refused and issued tokens stop working.', danger: true, confirmLabel: 'Revoke client' }))) return;
    this.api.revokeClient(c.id).subscribe({
      next: () => {
        this.toast.success('OAuth client revoked', c.name);
        this.load();
      },
      error: (err: unknown) => this.toast.error('Could not revoke the client', ApiError.from(err).userMessage),
    });
  }

  protected copy(text: string): void {
    void navigator.clipboard?.writeText(text).then(() => this.toast.success('Copied'));
  }
}
