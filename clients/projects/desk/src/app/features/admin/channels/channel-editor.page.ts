import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import {
  ApiError,
  CHANNEL_TYPES,
  ChannelAccountDto,
  ChannelProviderDto,
  ChannelTestResultDto,
  ChannelType,
  ChannelsApi,
  ChatApi,
  ChatWidgetConfigDto,
  JsonValue,
  MetaApi,
  PollResultDto,
  appUrl,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCard,
  SdDialogService,
  SdEmptyState,
  SdFormField,
  SdIcon,
  SdInput,
  SdPageHeader,
  SdRelativeTimePipe,
  SdRichTextEditor,
  SdSkeleton,
  SdToggle,
  ToastService,
} from '@sd/ui';
import { forkJoin } from 'rxjs';
import { accountStatusTone, copyText } from '../../../shared/channel-ui';
import { SECRET_LABELS, SETTING_FIELDS, SettingField, TYPE_META, extraSettingKeys } from './provider-fields';

type SettingValue = string | number | boolean | null;

/**
 * /admin/channels/new and /admin/channels/:id — create or edit a channel account with provider-specific settings
 * (secrets are write-only: they can be replaced or removed, never read back), then test the connection, poll a
 * mailbox now, copy/rotate the webhook URL, and configure the chat widget (embed snippet + live preview).
 */
@Component({
  selector: 'desk-channel-editor-page',
  imports: [
    FormsModule,
    RouterLink,
    SdPageHeader,
    SdCard,
    SdFormField,
    SdInput,
    SdToggle,
    SdButton,
    SdBadge,
    SdIcon,
    SdEmptyState,
    SdSkeleton,
    SdRichTextEditor,
    SdRelativeTimePipe,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header [title]="pageTitle()" [subtitle]="pageSubtitle()" [icon]="typeMeta().icon" [breadcrumbs]="crumbs()">
        @if (channel(); as c) {
          <a sdButton sdPageActions variant="secondary" icon="play" routerLink="/admin/channels/simulator" [queryParams]="{ channel: c.id }">Simulate</a>
        }
      </sd-page-header>
      <div class="desk-page__body">
        @if (loadError(); as e) {
          <sd-empty-state icon="circle-alert" title="This channel could not be loaded" [message]="e">
            <a sdButton routerLink="/admin/channels">Back to channels</a>
          </sd-empty-state>
        } @else if (!ready()) {
          <sd-card><sd-skeleton [lines]="8" /></sd-card>
        } @else {
          <div class="layout">
            <form class="main" (ngSubmit)="save()" novalidate>
              <sd-card title="Channel" padding="lg">
                <div class="grid">
                  <sd-form-field label="Type" required>
                    <select sdInput name="type" [ngModel]="type()" (ngModelChange)="setType($event)" [disabled]="!!channel()">
                      @for (t of types; track t) {
                        <option [value]="t">{{ typeLabel(t) }}</option>
                      }
                    </select>
                  </sd-form-field>
                  <sd-form-field label="Provider" required [hint]="providerInfo()?.description ?? null">
                    <select sdInput name="provider" [ngModel]="provider()" (ngModelChange)="setProvider($event)" [disabled]="!!channel()">
                      @for (p of providersForType(); track p.provider) {
                        <option [value]="p.provider">{{ p.provider === 'Simulator' ? 'Simulator (demo/testing)' : p.provider }}</option>
                      }
                    </select>
                  </sd-form-field>
                  <sd-form-field label="Name" required [error]="fieldErrors()['name']">
                    <input sdInput name="name" [(ngModel)]="name" maxlength="120" placeholder="Support mailbox" />
                  </sd-form-field>
                  <sd-form-field [label]="typeMeta().addressLabel" [hint]="typeMeta().addressHint" [required]="type() !== 'Chat'" [error]="fieldErrors()['address']">
                    <input sdInput name="address" [(ngModel)]="address" [placeholder]="typeMeta().addressPlaceholder" maxlength="320" />
                  </sd-form-field>
                  <sd-form-field label="Display name" hint="Shown as the sender name (defaults to the brand).">
                    <input sdInput name="displayName" [(ngModel)]="displayName" maxlength="120" />
                  </sd-form-field>
                  <sd-form-field label="Brand">
                    <select sdInput name="brandId" [(ngModel)]="brandId">
                      <option [ngValue]="null">Default brand</option>
                      @for (b of brands(); track b.id) {
                        <option [ngValue]="b.id">{{ b.name }}</option>
                      }
                    </select>
                  </sd-form-field>
                  <sd-form-field label="Department" hint="New tickets from this channel go here.">
                    <select sdInput name="departmentId" [(ngModel)]="departmentId">
                      <option [ngValue]="null">Brand default</option>
                      @for (d of departments(); track d.id) {
                        <option [ngValue]="d.id">{{ d.name }}</option>
                      }
                    </select>
                  </sd-form-field>
                </div>
                <div class="toggles">
                  <sd-toggle name="isActive" [(ngModel)]="isActive">Active</sd-toggle>
                  @if (type() === 'Email') {
                    <sd-toggle name="autoAcknowledge" [(ngModel)]="autoAcknowledge">Send automatic acknowledgements</sd-toggle>
                  }
                  @if (type() !== 'Chat') {
                    <sd-toggle name="acceptUnknownSenders" [(ngModel)]="acceptUnknownSenders">Accept messages from unknown senders</sd-toggle>
                  }
                </div>
              </sd-card>

              @if (settingKeys().length) {
                <sd-card [title]="type() === 'Chat' ? 'Widget' : 'Connection settings'" padding="lg">
                  <div class="grid">
                    @for (key of settingKeys(); track key) {
                      @let f = field(key);
                      @if (f.type === 'toggle') {
                        <div class="toggle-field">
                          <sd-toggle [name]="'s-' + key" [ngModel]="bool(key)" (ngModelChange)="setSetting(key, $event)">{{ f.label }}</sd-toggle>
                          @if (f.hint) {
                            <span class="sd-xs sd-muted">{{ f.hint }}</span>
                          }
                        </div>
                      } @else {
                        <sd-form-field [label]="f.label" [hint]="f.hint ?? null" [class.wide]="f.type === 'textarea'">
                          @switch (f.type) {
                            @case ('select') {
                              <select sdInput [name]="'s-' + key" [ngModel]="text(key)" (ngModelChange)="setSetting(key, $event)">
                                @for (o of f.options ?? []; track o.value) {
                                  <option [value]="o.value">{{ o.label }}</option>
                                }
                              </select>
                            }
                            @case ('textarea') {
                              <textarea sdInput rows="3" [name]="'s-' + key" [ngModel]="text(key)" (ngModelChange)="setSetting(key, $event)" [placeholder]="f.placeholder ?? ''"></textarea>
                            }
                            @case ('number') {
                              <input sdInput type="number" [name]="'s-' + key" [ngModel]="text(key)" (ngModelChange)="setSetting(key, $event === '' || $event === null ? null : +$event)" />
                            }
                            @default {
                              <input sdInput [type]="f.type === 'url' ? 'url' : 'text'" [name]="'s-' + key" [ngModel]="text(key)" (ngModelChange)="setSetting(key, $event)" [placeholder]="f.placeholder ?? ''" />
                            }
                          }
                        </sd-form-field>
                      }
                    }
                  </div>
                </sd-card>
              }

              @if (secretKeys().length) {
                <sd-card title="Credentials" subtitle="Stored encrypted. Saved values are never shown again — type a new value to replace one." padding="lg">
                  <div class="grid">
                    @for (key of secretKeys(); track key) {
                      <sd-form-field [label]="secretLabel(key)" [hint]="secretHint(key)">
                        <div class="secret">
                          <input
                            sdInput
                            type="password"
                            autocomplete="new-password"
                            [name]="'secret-' + key"
                            [ngModel]="secretValue(key)"
                            (ngModelChange)="setSecret(key, $event)"
                            [placeholder]="isSet(key) && !removed().has(key) ? '•••••••• (saved)' : 'Not set'"
                          />
                          @if (isSet(key)) {
                            <button sdButton type="button" size="sm" variant="ghost" [pressed]="removed().has(key)" (click)="toggleRemove(key)">
                              {{ removed().has(key) ? 'Keep' : 'Remove' }}
                            </button>
                          }
                        </div>
                      </sd-form-field>
                    }
                  </div>
                </sd-card>
              }

              @if (type() === 'Email') {
                <sd-card title="Mailbox signature" subtitle="Appended below the agent's own signature on every reply from this mailbox." padding="lg">
                  <sd-rich-text-editor name="signatureHtml" toolbar="compact" minHeight="80px" [(ngModel)]="signatureHtml" ariaLabel="Mailbox signature" />
                </sd-card>
              }

              @if (formError()) {
                <p class="form-error" role="alert">{{ formError() }}</p>
              }
              <div class="form-actions">
                <a sdButton variant="ghost" routerLink="/admin/channels">Cancel</a>
                <button sdButton variant="primary" type="submit" [loading]="saving()">{{ channel() ? 'Save changes' : 'Create channel' }}</button>
              </div>
            </form>

            @if (channel(); as c) {
              <aside class="side">
                <sd-card title="Status" padding="md">
                  <div class="status-row">
                    <sd-badge dot [tone]="statusTone(c.status)">{{ c.status }}</sd-badge>
                    @if (c.lastPolledAt) {
                      <span class="sd-small sd-muted">Polled {{ c.lastPolledAt | sdRelativeTime }}</span>
                    }
                  </div>
                  @if (c.lastError) {
                    <p class="err sd-small"><sd-icon name="circle-alert" [size]="12" /> {{ c.lastError }}</p>
                  }
                  <div class="row-actions">
                    <button sdButton size="sm" icon="zap" [loading]="testing()" (click)="test()">Test connection</button>
                    @if (providerInfo()?.polling) {
                      <button sdButton size="sm" variant="ghost" icon="refresh" [loading]="polling()" (click)="poll()">Poll now</button>
                    }
                  </div>
                  @if (testResult(); as r) {
                    <div class="result" [class.ok]="r.success" role="status">
                      <strong>{{ r.success ? 'Connected' : 'Failed' }}</strong> — {{ r.message }}
                      @if (r.details) {
                        <dl>
                          @for (d of detailEntries(r); track d[0]) {
                            <dt>{{ d[0] }}</dt>
                            <dd>{{ d[1] }}</dd>
                          }
                        </dl>
                      }
                    </div>
                  }
                  @if (pollResult(); as p) {
                    <p class="sd-small" role="status">
                      @if (p.error) {
                        Polling failed: {{ p.error }}
                      } @else {
                        Fetched {{ p.fetched }}: {{ p.processed }} processed, {{ p.duplicates }} duplicate(s), {{ p.failed }} failed.
                      }
                    </p>
                  }
                </sd-card>

                @if (c.webhookPath) {
                  <sd-card title="Webhook" subtitle="Configure this callback URL at the provider." padding="md">
                    <code class="url">{{ webhookUrl() }}</code>
                    <div class="row-actions">
                      <button sdButton size="sm" icon="copy" (click)="copy(webhookUrl()!, 'Webhook URL copied')">Copy URL</button>
                      <button sdButton size="sm" variant="ghost" icon="history" [loading]="rotating()" (click)="rotate()">Rotate key</button>
                    </div>
                    @if (c.provider === 'MetaCloud' || c.provider === 'MetaGraph') {
                      <p class="sd-xs sd-muted">Meta verifies the URL with the verify token above; signatures use the app secret.</p>
                    }
                  </sd-card>
                }

                @if (c.type === 'Chat') {
                  <sd-card title="Embed the widget" padding="md">
                    <p class="sd-small sd-secondary">Paste before &lt;/body&gt; on any page of your website.</p>
                    <code class="url">{{ embedSnippet() }}</code>
                    <div class="row-actions">
                      <button sdButton size="sm" icon="copy" (click)="copy(embedSnippet(), 'Embed code copied')">Copy embed code</button>
                      <a sdButton size="sm" variant="ghost" icon="external-link" [href]="previewUrl()" target="_blank" rel="noopener">Live preview</a>
                    </div>
                    @if (widgetConfig(); as w) {
                      <div class="preview" [style.--wp]="w.primaryColor">
                        <div class="preview-head">{{ w.title }}</div>
                        <div class="preview-body">
                          <span class="bubble">{{ w.greeting }}</span>
                          <span class="sd-xs sd-muted">
                            {{ w.botEnabled ? 'Bot first' : 'Straight to agents' }} · {{ w.agentsOnline ? 'agents online' : 'agents offline' }}
                          </span>
                        </div>
                      </div>
                    }
                  </sd-card>
                }

                <sd-card title="Danger zone" padding="md">
                  <p class="sd-small sd-secondary">Channels with message history are deactivated instead of deleted.</p>
                  <button sdButton size="sm" variant="danger" icon="trash" [loading]="deleting()" (click)="remove()">Delete channel</button>
                </sd-card>
              </aside>
            }
          </div>
        }
      </div>
    </div>
  `,
  styles: `
    .layout {
      display: grid;
      grid-template-columns: minmax(0, 1fr) 340px;
      gap: var(--sd-space-5);
      align-items: start;
    }
    @media (max-width: 1100px) {
      .layout {
        grid-template-columns: 1fr;
      }
    }
    .main {
      display: flex;
      flex-direction: column;
      gap: var(--sd-space-4);
    }
    .side {
      display: flex;
      flex-direction: column;
      gap: var(--sd-space-3);
    }
    .grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
      gap: var(--sd-space-3) var(--sd-space-4);
    }
    .wide {
      grid-column: 1 / -1;
    }
    .toggles {
      display: flex;
      flex-wrap: wrap;
      gap: var(--sd-space-4);
      margin-top: var(--sd-space-4);
    }
    .toggle-field {
      display: flex;
      flex-direction: column;
      gap: 4px;
      justify-content: center;
    }
    .secret {
      display: flex;
      gap: var(--sd-space-2);
      align-items: center;
    }
    .secret input {
      flex: 1;
    }
    .form-actions {
      display: flex;
      justify-content: flex-end;
      gap: var(--sd-space-2);
    }
    .form-error,
    .err {
      color: var(--sd-danger);
      margin: var(--sd-space-2) 0 0;
      overflow-wrap: anywhere;
    }
    .status-row {
      display: flex;
      align-items: center;
      gap: var(--sd-space-2);
      flex-wrap: wrap;
    }
    .row-actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--sd-space-2);
      margin-top: var(--sd-space-3);
    }
    .result {
      margin-top: var(--sd-space-3);
      padding: var(--sd-space-2) var(--sd-space-3);
      border-radius: var(--sd-radius-md);
      background: var(--sd-danger-bg);
      font-size: var(--sd-text-sm);
      overflow-wrap: anywhere;
    }
    .result.ok {
      background: var(--sd-success-bg);
    }
    .result dl {
      display: grid;
      grid-template-columns: max-content 1fr;
      gap: 2px var(--sd-space-2);
      margin: var(--sd-space-2) 0 0;
    }
    .result dd {
      margin: 0;
    }
    .url {
      display: block;
      padding: var(--sd-space-2);
      border-radius: var(--sd-radius-sm);
      background: var(--sd-surface-sunken);
      font-family: var(--sd-font-mono);
      font-size: var(--sd-text-xs);
      overflow-wrap: anywhere;
    }
    .preview {
      margin-top: var(--sd-space-3);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      overflow: hidden;
    }
    .preview-head {
      padding: var(--sd-space-2) var(--sd-space-3);
      background: var(--wp);
      color: #fff;
      font-weight: var(--sd-weight-semibold);
      font-size: var(--sd-text-sm);
    }
    .preview-body {
      display: flex;
      flex-direction: column;
      gap: var(--sd-space-2);
      padding: var(--sd-space-3);
      background: var(--sd-surface-sunken);
    }
    .bubble {
      align-self: flex-start;
      padding: 6px 10px;
      border-radius: 12px;
      background: var(--sd-surface);
      border: 1px solid var(--sd-border);
      font-size: var(--sd-text-sm);
    }
  `,
})
export class ChannelEditorPage {
  private readonly api = inject(ChannelsApi);
  private readonly chatApi = inject(ChatApi);
  private readonly meta = inject(MetaApi);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(SdDialogService);

  /** Route param (absent on /new). */
  readonly id = input<string | undefined>();
  /** Optional ?type= preselection on /new. */
  readonly typeParam = input<string | undefined>(undefined, { alias: 'type' });

  protected readonly types = CHANNEL_TYPES;
  protected readonly statusTone = accountStatusTone;
  protected readonly providers = signal<ChannelProviderDto[]>([]);
  protected readonly channel = signal<ChannelAccountDto | null>(null);
  protected readonly ready = signal(false);
  protected readonly loadError = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly testing = signal(false);
  protected readonly polling = signal(false);
  protected readonly rotating = signal(false);
  protected readonly deleting = signal(false);
  protected readonly testResult = signal<ChannelTestResultDto | null>(null);
  protected readonly pollResult = signal<PollResultDto | null>(null);
  protected readonly widgetConfig = signal<ChatWidgetConfigDto | null>(null);
  protected readonly fieldErrors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  protected readonly type = signal<ChannelType>('Email');
  protected readonly provider = signal<string>('FileDrop');
  protected readonly settings = signal<Record<string, SettingValue>>({});
  protected readonly secrets = signal<Record<string, string>>({});
  protected readonly removed = signal<ReadonlySet<string>>(new Set());

  // Plain form fields (ngModel)
  protected name = '';
  protected address = '';
  protected displayName = '';
  protected brandId: string | null = null;
  protected departmentId: string | null = null;
  protected isActive = true;
  protected autoAcknowledge = true;
  protected acceptUnknownSenders = true;
  protected signatureHtml = '';

  protected readonly brands = computed(() => this.meta.ticketForm()?.brands ?? []);
  protected readonly departments = computed(() => this.meta.ticketForm()?.departments ?? []);
  protected readonly typeMeta = computed(() => TYPE_META[this.type()] ?? TYPE_META['Email']);
  protected readonly providersForType = computed(() => this.providers().filter((p) => p.channelTypes.includes(this.type())));
  protected readonly providerInfo = computed(() => this.providers().find((p) => p.provider === this.provider()) ?? null);
  protected readonly settingKeys = computed(() => [...(this.providerInfo()?.settingKeys ?? []), ...extraSettingKeys(this.type())]);
  protected readonly secretKeys = computed(() => this.providerInfo()?.secretKeys ?? []);
  protected readonly pageTitle = computed(() => this.channel()?.name ?? 'New channel');
  protected readonly pageSubtitle = computed(() => {
    const c = this.channel();
    return c ? `${this.typeMeta().label} · ${c.provider} · ${c.address}` : 'Connect a mailbox, messaging number, chat widget or phone line.';
  });
  protected readonly crumbs = computed(() => [
    { label: 'Administration', link: '/admin' },
    { label: 'Channels', link: '/admin/channels' },
    { label: this.channel()?.name ?? 'New' },
  ]);
  protected readonly webhookUrl = computed(() => {
    const c = this.channel();
    return c ? this.api.webhookUrl(c) : null;
  });
  protected readonly embedSnippet = computed(() => this.chatApi.embedSnippet(this.channel()?.address ?? ''));
  protected readonly previewUrl = computed(() => appUrl(`widget/demo.html?widgetKey=${encodeURIComponent(this.channel()?.address ?? '')}`));

  constructor() {
    this.meta.ticketForm$().subscribe({ error: () => undefined });
    effect(() => {
      const id = this.id();
      const typeParam = this.typeParam();
      untracked(() => this.load(id ?? null, typeParam ?? null));
    });
  }

  private load(id: string | null, typeParam: string | null): void {
    this.ready.set(false);
    this.loadError.set(null);
    this.testResult.set(null);
    this.pollResult.set(null);
    const providers$ = this.api.providers();
    if (!id) {
      providers$.subscribe({
        next: (providers) => {
          this.providers.set(providers);
          this.channel.set(null);
          this.setType((CHANNEL_TYPES as readonly string[]).includes(typeParam ?? '') ? (typeParam as ChannelType) : 'Email');
          this.ready.set(true);
        },
        error: (e: unknown) => this.loadError.set(ApiError.from(e).userMessage),
      });
      return;
    }
    forkJoin({ providers: providers$, channel: this.api.get(id) }).subscribe({
      next: ({ providers, channel }) => {
        this.providers.set(providers);
        this.apply(channel);
        this.ready.set(true);
      },
      error: (e: unknown) => this.loadError.set(ApiError.from(e).userMessage),
    });
  }

  private apply(c: ChannelAccountDto): void {
    this.channel.set(c);
    this.type.set(c.type);
    this.provider.set(c.provider);
    this.name = c.name;
    this.address = c.address;
    this.displayName = c.displayName ?? '';
    this.brandId = c.brandId;
    this.departmentId = c.departmentId;
    this.isActive = c.isActive;
    this.autoAcknowledge = c.autoAcknowledge;
    this.acceptUnknownSenders = c.acceptUnknownSenders;
    this.signatureHtml = c.signatureHtml ?? '';
    const s: Record<string, SettingValue> = {};
    for (const [k, v] of Object.entries(c.settings ?? {})) if (v === null || ['string', 'number', 'boolean'].includes(typeof v)) s[k] = v as SettingValue;
    this.settings.set(s);
    this.secrets.set({});
    this.removed.set(new Set());
    this.widgetConfig.set(null);
    if (c.type === 'Chat' && c.isActive) this.chatApi.widgetConfig(c.address).subscribe({ next: (w) => this.widgetConfig.set(w), error: () => undefined });
  }

  protected typeLabel(t: string): string {
    return TYPE_META[t]?.label ?? t;
  }

  protected setType(t: ChannelType): void {
    this.type.set(t);
    const first = this.providersForType()[0]?.provider ?? '';
    this.setProvider(first);
  }

  protected setProvider(p: string): void {
    this.provider.set(p);
    // Defaults for the new provider's settings.
    const defaults: Record<string, SettingValue> = {};
    for (const key of this.settingKeys()) {
      const d = SETTING_FIELDS[key]?.defaultValue;
      if (d !== undefined) defaults[key] = d;
    }
    this.settings.set(defaults);
    this.secrets.set({});
  }

  protected field(key: string): SettingField {
    return SETTING_FIELDS[key] ?? { label: key, type: 'text' };
  }

  protected text(key: string): string {
    const v = this.settings()[key];
    return v === null || v === undefined ? '' : String(v);
  }

  protected bool(key: string): boolean {
    const v = this.settings()[key];
    return v === undefined ? SETTING_FIELDS[key]?.defaultValue === true : v === true || v === 'true';
  }

  protected setSetting(key: string, value: SettingValue): void {
    this.settings.update((s) => ({ ...s, [key]: value }));
  }

  protected secretLabel(key: string): string {
    return SECRET_LABELS[key]?.label ?? key;
  }

  protected secretHint(key: string): string | null {
    return SECRET_LABELS[key]?.hint ?? null;
  }

  protected secretValue(key: string): string {
    return this.secrets()[key] ?? '';
  }

  protected isSet(key: string): boolean {
    return this.channel()?.secretKeys.includes(key) ?? false;
  }

  protected setSecret(key: string, value: string): void {
    this.secrets.update((s) => ({ ...s, [key]: value }));
    if (value) this.removed.update((r) => new Set([...r].filter((k) => k !== key)));
  }

  protected toggleRemove(key: string): void {
    this.removed.update((r) => {
      const next = new Set(r);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }

  protected detailEntries(r: ChannelTestResultDto): [string, string][] {
    return Object.entries(r.details ?? {});
  }

  protected save(): void {
    this.fieldErrors.set({});
    this.formError.set(null);
    if (!this.name.trim()) {
      this.fieldErrors.set({ name: 'A name is required.' });
      return;
    }
    const settings: Record<string, JsonValue> = {};
    for (const key of this.settingKeys()) {
      const v = this.settings()[key];
      settings[key] = v === '' || v === undefined ? null : (v as JsonValue);
    }
    const secrets: Record<string, string> = {};
    for (const [k, v] of Object.entries(this.secrets())) if (v) secrets[k] = v;
    this.saving.set(true);
    const existing = this.channel();
    const request$ = existing
      ? this.api.update(
          existing.id,
          {
            rowVersion: existing.rowVersion,
            name: this.name.trim(),
            address: this.address.trim(),
            displayName: this.displayName.trim(),
            brandId: this.brandId,
            departmentId: this.departmentId,
            settings,
            secrets: { ...secrets, ...Object.fromEntries([...this.removed()].map((k) => [k, ''])) },
            signatureHtml: this.signatureHtml,
            isActive: this.isActive,
            autoAcknowledge: this.autoAcknowledge,
            acceptUnknownSenders: this.acceptUnknownSenders,
          },
          { silent: true },
        )
      : this.api.create(
          {
            type: this.type(),
            provider: this.provider(),
            name: this.name.trim(),
            address: this.address.trim() || null,
            displayName: this.displayName.trim() || null,
            brandId: this.brandId,
            departmentId: this.departmentId,
            settings: Object.fromEntries(Object.entries(settings).filter(([, v]) => v !== null)),
            secrets,
            signatureHtml: this.signatureHtml || null,
            isActive: this.isActive,
            autoAcknowledge: this.autoAcknowledge,
            acceptUnknownSenders: this.acceptUnknownSenders,
          },
          { silent: true },
        );
    request$.subscribe({
      next: (c) => {
        this.saving.set(false);
        this.toast.success(existing ? 'Channel saved' : 'Channel created', c.name);
        if (existing) this.apply(c);
        else void this.router.navigate(['/admin/channels', c.id]);
      },
      error: (e: unknown) => {
        this.saving.set(false);
        const err = ApiError.from(e);
        const errors: Record<string, string> = {};
        for (const key of ['name', 'address', 'type', 'provider', 'settings', 'brandId', 'departmentId']) {
          const msg = err.fieldError(key);
          if (msg) errors[key] = msg;
        }
        this.fieldErrors.set(errors);
        const firstSecret = Object.keys(err.errors ?? {}).find((k) => k.toLowerCase().startsWith('secrets'));
        this.formError.set(Object.keys(errors).length ? null : firstSecret ? err.fieldError(firstSecret) : err.userMessage);
      },
    });
  }

  protected test(): void {
    const c = this.channel();
    if (!c) return;
    this.testing.set(true);
    this.api.test(c.id).subscribe({
      next: (r) => {
        this.testing.set(false);
        this.testResult.set(r);
        this.refresh();
      },
      error: (e: unknown) => {
        this.testing.set(false);
        this.toast.error('Test failed', ApiError.from(e).userMessage);
      },
    });
  }

  protected poll(): void {
    const c = this.channel();
    if (!c) return;
    this.polling.set(true);
    this.api.poll(c.id).subscribe({
      next: (r) => {
        this.polling.set(false);
        this.pollResult.set(r);
        this.refresh();
      },
      error: () => this.polling.set(false),
    });
  }

  protected async rotate(): Promise<void> {
    const c = this.channel();
    if (!c) return;
    const ok = await this.dialogs.confirm({
      title: 'Rotate the webhook key?',
      message: 'The current webhook URL stops working immediately. Update the URL at the provider right after rotating.',
      confirmLabel: 'Rotate key',
      danger: true,
    });
    if (!ok) return;
    this.rotating.set(true);
    this.api.rotateWebhookKey(c.id).subscribe({
      next: (updated) => {
        this.rotating.set(false);
        this.apply(updated);
        this.toast.success('Webhook key rotated', 'Copy the new URL to the provider console.');
      },
      error: () => this.rotating.set(false),
    });
  }

  protected async remove(): Promise<void> {
    const c = this.channel();
    if (!c) return;
    const ok = await this.dialogs.confirm({
      title: `Delete ${c.name}?`,
      message: 'Unused channels are deleted; channels with conversations are deactivated so their history stays intact.',
      confirmLabel: 'Delete',
      danger: true,
    });
    if (!ok) return;
    this.deleting.set(true);
    this.api.delete(c.id).subscribe({
      next: (r) => {
        this.deleting.set(false);
        this.toast.success(r.deleted ? 'Channel deleted' : 'Channel deactivated', r.deleted ? undefined : 'It has message history, so it was deactivated instead.');
        void this.router.navigate(['/admin/channels']);
      },
      error: () => this.deleting.set(false),
    });
  }

  protected async copy(text: string, message: string): Promise<void> {
    if (await copyText(text)) this.toast.success(message);
    else this.toast.warning('Copy failed', 'Select the text and copy it manually.');
  }

  private refresh(): void {
    const c = this.channel();
    if (!c) return;
    this.api.get(c.id).subscribe({ next: (fresh) => this.channel.set(fresh), error: () => undefined });
  }
}
