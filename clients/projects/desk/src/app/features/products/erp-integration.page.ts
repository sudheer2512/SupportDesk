import { ChangeDetectionStrategy, Component, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ErpApi,
  ErpAuthType,
  ErpConnectionTestDto,
  ErpIntegrationDto,
  ErpMode,
  ErpSyncResultDto,
  ErpSystem,
  IntegrationLogDto,
  saveErrorMessage,
  validationMessages,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCard,
  SdDateTimePipe,
  SdEmptyState,
  SdFormField,
  SdIcon,
  SdInput,
  SdJsonViewer,
  SdPageHeader,
  SdRelativeTimePipe,
  SdSkeleton,
  SdTab,
  SdTabs,
  SdToggle,
  ToastService,
} from '@sd/ui';
import { ModuleTabsComponent, PRODUCT_TABS } from '../customers/shared/module-tabs.component';
import { SECTION_STYLES, TABLE_STYLES } from '../customers/shared/styles';

/**
 * /products/erp — Wings ERP / Wings Books connection (simulator or HTTP API with OAuth2 / API key), connection test,
 * "sync now" with per-entity results, and the integration log (sync history and API calls). Needs admin.integrations.
 */
@Component({
  selector: 'desk-erp-integration-page',
  imports: [
    FormsModule,
    SdPageHeader,
    SdTabs,
    SdTab,
    SdCard,
    SdFormField,
    SdInput,
    SdButton,
    SdBadge,
    SdIcon,
    SdToggle,
    SdSkeleton,
    SdEmptyState,
    SdJsonViewer,
    SdRelativeTimePipe,
    SdDateTimePipe,
    ModuleTabsComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="ERP integration" subtitle="Customer masters, installations, licences and AMC contracts from Wings ERP — over its API, never its database." icon="plug" />
      <desk-module-tabs [tabs]="tabs" ariaLabel="Products & Assets sections" />
      <div class="desk-page__body">
        <sd-tabs [selected]="system()" (selectedChange)="system.set(($event ?? 'WingsErp') === 'WingsBooks' ? 'WingsBooks' : 'WingsErp')" ariaLabel="ERP system" variant="pill">
          <sd-tab id="WingsErp" label="Wings ERP" icon="database" />
          <sd-tab id="WingsBooks" label="Wings Books" icon="book-open" />
        </sd-tabs>

        @if (!settings()) {
          <sd-skeleton [lines]="10" />
        } @else {
          @let s = settings()!;
          <div class="status">
            <sd-badge [tone]="!s.configured ? 'neutral' : s.status === 'Error' ? 'danger' : s.isActive ? 'success' : 'neutral'" dot>
              {{ !s.configured ? 'Not configured' : !s.isActive ? 'Disabled' : s.status }}
            </sd-badge>
            @if (s.lastSyncAt) {
              <span class="sd-small sd-muted">Last sync {{ s.lastSyncAt | sdRelativeTime }}</span>
            }
            @if (s.lastError) {
              <span class="sd-small err">{{ s.lastError }}</span>
            }
            <span class="sd-spacer"></span>
            @if (s.configured) {
              <button sdButton size="sm" icon="activity" [loading]="testing()" (click)="test()">Test connection</button>
              <button sdButton size="sm" variant="primary" icon="refresh" [loading]="syncing()" [disabled]="!s.isActive" (click)="sync()">Sync now</button>
            }
          </div>

          @if (testResult(); as t) {
            <p class="banner" [class.ok]="t.success" role="status">
              <sd-icon [name]="t.success ? 'circle-check' : 'circle-alert'" [size]="16" />
              {{ t.message }} ({{ t.durationMs }} ms@if (t.serverVersion) { · {{ t.serverVersion }} })
            </p>
          }
          @if (syncResult(); as r) {
            <sd-card [title]="r.success ? 'Sync completed' : 'Sync failed'" [subtitle]="(r.finishedAt | sdDateTime) + ' · ' + r.trigger" class="result">
              @if (r.error) {
                <p class="err sd-small">{{ r.error }}</p>
              }
              <div class="dt-wrap">
                <table class="dt">
                  <thead>
                    <tr>
                      <th scope="col">Entity</th>
                      <th scope="col" class="num">Received</th>
                      <th scope="col" class="num">Created</th>
                      <th scope="col" class="num">Updated</th>
                      <th scope="col" class="num">Unchanged</th>
                      <th scope="col" class="num">Skipped</th>
                      <th scope="col">Warnings</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (e of r.entities; track e.entity) {
                      <tr>
                        <td>{{ e.entity }}</td>
                        <td class="num">{{ e.received }}</td>
                        <td class="num">{{ e.created }}</td>
                        <td class="num">{{ e.updated }}</td>
                        <td class="num">{{ e.unchanged }}</td>
                        <td class="num">{{ e.skipped }}</td>
                        <td class="sd-small">{{ e.warnings.join(' · ') || '—' }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            </sd-card>
          }

          <form class="form" (ngSubmit)="save()" novalidate>
            <sd-card title="Connection">
              <div class="grid">
                <sd-form-field label="Name">
                  <input sdInput name="name" [(ngModel)]="name" maxlength="200" />
                </sd-form-field>
                <sd-form-field label="Mode" hint="The simulator generates deterministic demo data from your customers.">
                  <select sdInput name="mode" [(ngModel)]="mode">
                    <option value="simulator">Simulator (development / demo)</option>
                    <option value="http">Wings API (HTTP)</option>
                  </select>
                </sd-form-field>
                <sd-toggle class="toggle" [(value)]="active">Integration enabled</sd-toggle>
              </div>
              @if (mode === 'http') {
                <div class="grid">
                  <sd-form-field label="API base URL" required [error]="errors()['baseUrl']">
                    <input sdInput name="baseUrl" [(ngModel)]="baseUrl" placeholder="https://erp.example.com" />
                  </sd-form-field>
                  <sd-form-field label="Authentication" [error]="errors()['authType']">
                    <select sdInput name="auth" [(ngModel)]="authType">
                      <option value="OAuth2">OAuth 2.0 client credentials</option>
                      <option value="ApiKey">API key (X-Api-Key)</option>
                      <option value="None">None (trusted network)</option>
                    </select>
                  </sd-form-field>
                  <sd-form-field label="Timeout (seconds)" [error]="errors()['timeoutSeconds']">
                    <input sdInput type="number" min="5" max="300" name="timeout" [(ngModel)]="timeout" />
                  </sd-form-field>
                </div>
                @if (authType === 'OAuth2') {
                  <div class="grid">
                    <sd-form-field label="Token URL" optional [error]="errors()['tokenUrl']" hint="Defaults to {base URL}/oauth/token">
                      <input sdInput name="tokenUrl" [(ngModel)]="tokenUrl" />
                    </sd-form-field>
                    <sd-form-field label="Client id" required [error]="errors()['clientId']">
                      <input sdInput name="clientId" [(ngModel)]="clientId" autocomplete="off" />
                    </sd-form-field>
                    <sd-form-field label="Client secret" [hint]="s.hasClientSecret ? 'Stored — leave empty to keep it.' : 'Write-only; stored encrypted.'">
                      <input sdInput type="password" name="secret" [(ngModel)]="clientSecret" autocomplete="new-password" />
                    </sd-form-field>
                    <sd-form-field label="Scope" optional>
                      <input sdInput name="scope" [(ngModel)]="scope" placeholder="erp.read erp.write" />
                    </sd-form-field>
                  </div>
                }
                @if (authType === 'ApiKey') {
                  <div class="grid">
                    <sd-form-field label="API key" [hint]="s.hasApiKey ? 'Stored — leave empty to keep it.' : 'Write-only; stored encrypted.'">
                      <input sdInput type="password" name="apiKey" [(ngModel)]="apiKey" autocomplete="new-password" />
                    </sd-form-field>
                  </div>
                }
              }
            </sd-card>
            <sd-card title="Synchronisation" subtitle="ERP ids are the match key; desk-owned fields (tier, account manager, SLA, coverage) are never overwritten.">
              <div class="grid">
                <sd-form-field label="Sync every (minutes)" [error]="errors()['syncIntervalMinutes']">
                  <input sdInput type="number" min="5" max="10080" name="interval" [(ngModel)]="interval" />
                </sd-form-field>
              </div>
              <div class="toggles">
                <sd-toggle [(value)]="syncCustomers">Customer masters &amp; branches</sd-toggle>
                @if (system() === 'WingsErp') {
                  <sd-toggle [(value)]="syncInstallations">Installations</sd-toggle>
                  <sd-toggle [(value)]="syncLicences">Licences</sd-toggle>
                }
                <sd-toggle [(value)]="syncContracts">AMC contracts</sd-toggle>
                <sd-toggle [(value)]="createMissing">Create customers that exist only in the ERP</sd-toggle>
              </div>
            </sd-card>
            @if (formError()) {
              <p class="err" role="alert">{{ formError() }}</p>
            }
            <div class="actions">
              <button sdButton variant="primary" type="submit" icon="check" [loading]="saving()">{{ s.configured ? 'Save settings' : 'Connect ' + s.name }}</button>
            </div>
          </form>

          <div class="section-head">
            <h2>Integration log</h2>
            <span class="sd-spacer"></span>
            <select sdInput size="sm" class="f" aria-label="Log filter" [ngModel]="logFilter()" (ngModelChange)="logFilter.set($event)">
              <option value="">All entries</option>
              <option value="sync">Sync runs</option>
              <option value="ticket">Tickets from ERP</option>
              <option value="record">Record updates</option>
            </select>
          </div>
          @if (logs().length === 0) {
            <sd-empty-state icon="list" title="No log entries yet" compact />
          } @else {
            <div class="dt-wrap">
              <table class="dt">
                <thead>
                  <tr>
                    <th scope="col">When</th>
                    <th scope="col">Operation</th>
                    <th scope="col">Direction</th>
                    <th scope="col">Result</th>
                    <th scope="col" class="num">Duration</th>
                    <th scope="col">Details</th>
                  </tr>
                </thead>
                <tbody>
                  @for (l of logs(); track l.id) {
                    <tr class="clickable" tabindex="0" (click)="toggleLog(l.id)" (keydown.enter)="toggleLog(l.id)" [attr.aria-expanded]="openLog() === l.id">
                      <td class="nowrap sd-small">{{ l.createdAt | sdDateTime: 'datetime' : 'short' }}</td>
                      <td class="sd-mono sd-small">{{ l.operation }}</td>
                      <td>{{ l.direction }}</td>
                      <td>
                        <sd-badge [tone]="l.success ? 'success' : 'danger'" size="sm">{{ l.success ? 'OK' : 'Failed' }}@if (l.statusCode) { · {{ l.statusCode }} }</sd-badge>
                      </td>
                      <td class="num">{{ l.durationMs }} ms</td>
                      <td class="sd-small sd-truncate details">{{ l.requestExcerpt ?? l.url ?? '' }}</td>
                    </tr>
                    @if (openLog() === l.id) {
                      <tr>
                        <td colspan="6">
                          @if (parsed(l.responseExcerpt); as json) {
                            <sd-json-viewer [value]="json" [expandDepth]="2" />
                          } @else {
                            <pre class="raw">{{ l.responseExcerpt ?? 'No response recorded.' }}</pre>
                          }
                        </td>
                      </tr>
                    }
                  }
                </tbody>
              </table>
            </div>
          }
        }
      </div>
    </div>
  `,
  styles: [
    TABLE_STYLES,
    SECTION_STYLES,
    `
      sd-tabs {
        display: block;
        margin-bottom: var(--sd-space-4);
      }
      .status {
        display: flex;
        align-items: center;
        flex-wrap: wrap;
        gap: var(--sd-space-2);
        margin-bottom: var(--sd-space-3);
      }
      .err {
        color: var(--sd-danger);
      }
      .banner {
        display: flex;
        align-items: center;
        gap: 8px;
        padding: var(--sd-space-2) var(--sd-space-3);
        border-radius: var(--sd-radius-md);
        background: var(--sd-danger-bg);
        border: 1px solid var(--sd-danger-border);
        margin-bottom: var(--sd-space-3);
      }
      .banner.ok {
        background: var(--sd-success-bg);
        border-color: var(--sd-success-border);
      }
      .result {
        display: block;
        margin-bottom: var(--sd-space-4);
      }
      .form {
        display: grid;
        gap: var(--sd-space-4);
      }
      .grid {
        display: grid;
        gap: var(--sd-space-3);
        grid-template-columns: repeat(auto-fill, minmax(min(100%, 260px), 1fr));
        align-items: end;
        margin-bottom: var(--sd-space-3);
      }
      .toggle {
        padding-bottom: 8px;
      }
      .toggles {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sd-space-4);
      }
      .actions {
        display: flex;
        justify-content: flex-end;
      }
      .f {
        width: 180px;
      }
      .details {
        max-width: 420px;
      }
      .raw {
        white-space: pre-wrap;
        font-family: var(--sd-font-mono);
        font-size: var(--sd-text-xs);
        margin: 0;
      }
    `,
  ],
})
export class ErpIntegrationPage {
  private readonly api = inject(ErpApi);
  private readonly toast = inject(ToastService);
  protected readonly tabs = PRODUCT_TABS;
  protected readonly system = signal<ErpSystem>('WingsErp');
  protected readonly settings = signal<ErpIntegrationDto | null>(null);
  protected readonly logs = signal<IntegrationLogDto[]>([]);
  protected readonly logFilter = signal('');
  protected readonly openLog = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly testing = signal(false);
  protected readonly syncing = signal(false);
  protected readonly testResult = signal<ErpConnectionTestDto | null>(null);
  protected readonly syncResult = signal<ErpSyncResultDto | null>(null);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  protected name = '';
  protected mode: ErpMode = 'simulator';
  protected readonly active = signal(true);
  protected baseUrl = '';
  protected authType: ErpAuthType = 'OAuth2';
  protected tokenUrl = '';
  protected clientId = '';
  protected clientSecret = '';
  protected apiKey = '';
  protected scope = '';
  protected timeout = 30;
  protected interval = 60;
  protected readonly syncCustomers = signal(true);
  protected readonly syncInstallations = signal(true);
  protected readonly syncLicences = signal(true);
  protected readonly syncContracts = signal(true);
  protected readonly createMissing = signal(true);

  constructor() {
    effect(() => {
      const system = this.system();
      untracked(() => {
        this.settings.set(null);
        this.testResult.set(null);
        this.syncResult.set(null);
        this.api.settings(system).subscribe((s) => this.apply(s));
        this.loadLogs();
      });
    });
    effect(() => {
      this.logFilter();
      untracked(() => this.loadLogs());
    });
  }

  private apply(s: ErpIntegrationDto): void {
    this.settings.set(s);
    this.name = s.name;
    this.mode = s.mode;
    this.active.set(s.configured ? s.isActive : true);
    this.baseUrl = s.baseUrl ?? '';
    this.authType = s.authType;
    this.tokenUrl = s.tokenUrl ?? '';
    this.clientId = s.clientId ?? '';
    this.clientSecret = '';
    this.apiKey = '';
    this.scope = s.scope ?? '';
    this.timeout = s.timeoutSeconds;
    this.interval = s.syncIntervalMinutes;
    this.syncCustomers.set(s.syncCustomers);
    this.syncInstallations.set(s.syncInstallations);
    this.syncLicences.set(s.syncLicences);
    this.syncContracts.set(s.syncContracts);
    this.createMissing.set(s.createMissingCustomers);
  }

  private loadLogs(): void {
    this.api.logs(this.system(), this.logFilter() || null, 100).subscribe((l) => this.logs.set(l));
  }

  protected parsed(text: string | null): unknown {
    if (!text || !text.trim().startsWith('{')) return null;
    try {
      return JSON.parse(text);
    } catch {
      return null;
    }
  }

  protected toggleLog(id: string): void {
    this.openLog.update((cur) => (cur === id ? null : id));
  }

  protected save(): void {
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    this.api
      .saveSettings(this.system(), {
        name: this.name || null,
        isActive: this.active(),
        mode: this.mode,
        baseUrl: this.baseUrl || null,
        authType: this.authType,
        tokenUrl: this.tokenUrl || null,
        clientId: this.clientId || null,
        clientSecret: this.clientSecret ? this.clientSecret : null,
        apiKey: this.apiKey ? this.apiKey : null,
        scope: this.scope || null,
        timeoutSeconds: Number(this.timeout) || 30,
        syncIntervalMinutes: Number(this.interval) || 60,
        syncCustomers: this.syncCustomers(),
        syncInstallations: this.syncInstallations(),
        syncLicences: this.syncLicences(),
        syncContracts: this.syncContracts(),
        createMissingCustomers: this.createMissing(),
      })
      .subscribe({
        next: (s) => {
          this.saving.set(false);
          this.apply(s);
          this.toast.success('ERP settings saved');
        },
        error: (err: unknown) => {
          this.saving.set(false);
          this.errors.set(validationMessages(err));
          this.formError.set(saveErrorMessage(err));
        },
      });
  }

  protected test(): void {
    this.testing.set(true);
    this.api.test(this.system()).subscribe({
      next: (r) => {
        this.testing.set(false);
        this.testResult.set(r);
        this.api.settings(this.system()).subscribe((s) => this.settings.set(s));
      },
      error: () => this.testing.set(false),
    });
  }

  protected sync(): void {
    this.syncing.set(true);
    this.api.sync(this.system()).subscribe({
      next: (r) => {
        this.syncing.set(false);
        this.syncResult.set(r);
        this.loadLogs();
        this.api.settings(this.system()).subscribe((s) => this.settings.set(s));
        if (r.success) this.toast.success('Sync completed');
        else this.toast.error('Sync failed', r.error ?? undefined);
      },
      error: (err: unknown) => {
        this.syncing.set(false);
        this.toast.error('Sync could not run', saveErrorMessage(err));
      },
    });
  }
}
