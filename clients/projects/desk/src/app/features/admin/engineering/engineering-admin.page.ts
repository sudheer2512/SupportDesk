import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ApiError,
  EngineeringAdminApi,
  EngineeringDeliveryDto,
  EngineeringSettingsDto,
  EngineeringTrackerDto,
  EngineeringTrackerProvider,
  IntegrationLogDto,
  saveErrorMessage,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdDateTimePipe,
  SdDialogService,
  SdDrawer,
  SdDropdownMenu,
  SdEmptyState,
  SdFormField,
  SdIcon,
  SdInput,
  SdMenuItem,
  SdPageHeader,
  SdRelativeTimePipe,
  SdSkeleton,
  SdTab,
  SdTabs,
  SdTone,
  SdToggle,
  ToastService,
} from '@sd/ui';
import { TrackerEditorComponent } from './tracker-editor.component';

const PROVIDER_LABEL: Record<EngineeringTrackerProvider, string> = { AzureDevOps: 'Azure DevOps', GitHub: 'GitHub', Local: 'Built-in board' };

/**
 * Administration → Engineering (admin.integrations): tracker connections (Azure DevOps Services/Server, GitHub, the
 * built-in board) with test, project discovery, sync now, webhook URL + secret (shown once, rotatable), webhook
 * deliveries and API call logs; and the tenant-wide sync-back rules (notes, assignee and customer notifications,
 * escalation substatus).
 */
@Component({
  selector: 'desk-engineering-admin-page',
  imports: [
    FormsModule,
    SdPageHeader,
    SdButton,
    SdBadge,
    SdIcon,
    SdDrawer,
    SdDropdownMenu,
    SdEmptyState,
    SdSkeleton,
    SdFormField,
    SdInput,
    SdToggle,
    SdTabs,
    SdTab,
    SdDateTimePipe,
    SdRelativeTimePipe,
    TrackerEditorComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Engineering" icon="bug" subtitle="Escalate tickets to Azure DevOps, GitHub or the built-in board and sync fixes back." [breadcrumbs]="crumbs">
        <sd-dropdown-menu sdPageActions label="Add tracker" icon="plus" variant="primary" align="end" [items]="addMenu()" (select)="add($event)" />
      </sd-page-header>
      <div class="desk-page__body">
        @if (revealed(); as r) {
          <div class="secret" role="status">
            <sd-icon name="key" [size]="16" />
            <div class="secret-body">
              <strong>Webhook secret for “{{ r.name }}” — copy it now, it is shown only once.</strong>
              <p class="sd-muted small">
                {{ r.provider === 'GitHub' ? 'Paste it as the webhook secret (signature X-Hub-Signature-256).' : 'Send it as the X-SupportDesk-Secret header or as the Basic authentication password of the service hook.' }}
              </p>
              <code class="value">{{ r.secret }}</code>
            </div>
            <button sdButton size="sm" icon="copy" (click)="copy(r.secret)">Copy</button>
            <button sdButton size="sm" variant="ghost" icon="x" iconOnly aria-label="Dismiss the secret" (click)="revealed.set(null)"></button>
          </div>
        }

        <h2 class="section">Trackers</h2>
        @if (loading()) {
          <sd-skeleton [lines]="3" height="64px" />
        } @else if (loadError()) {
          <div class="error" role="alert">
            <sd-icon name="circle-alert" [size]="16" />
            <span>{{ loadError() }}</span>
            <button sdButton size="sm" icon="refresh" (click)="reload()">Retry</button>
          </div>
        } @else if (trackers().length === 0) {
          <sd-empty-state icon="bug" title="No tracker connected" message="Connect Azure DevOps (Services or Server/TFS) or GitHub, or enable the built-in board for teams without an external tracker.">
            <button sdButton variant="primary" icon="kanban" (click)="openEditor(null, 'Local')">Enable the built-in board</button>
          </sd-empty-state>
        } @else {
          <ul class="trackers">
            @for (t of trackers(); track t.id) {
              <li class="tracker">
                <div class="main">
                  <div class="title">
                    <sd-icon [name]="t.provider === 'GitHub' ? 'git-pull-request' : t.provider === 'Local' ? 'kanban' : 'bug'" [size]="16" />
                    <strong>{{ t.name }}</strong>
                    <sd-badge size="sm" variant="outline">{{ providerLabel(t.provider) }}</sd-badge>
                    <sd-badge size="sm" [tone]="statusTone(t)" [dot]="true">{{ t.isActive ? t.status : 'Disabled' }}</sd-badge>
                  </div>
                  <span class="sd-muted small">
                    @if (t.provider === 'AzureDevOps') {
                      {{ t.baseUrl }} · project {{ t.project }}
                    } @else if (t.provider === 'GitHub') {
                      {{ t.owner }}/{{ t.repository }} · {{ t.authType === 'App' ? 'GitHub App' : 'token' }}
                    } @else {
                      Work items and releases in SupportDesk
                    }
                    · {{ t.linkedItemCount }} linked item{{ t.linkedItemCount === 1 ? '' : 's' }}
                    @if (t.lastSyncAt) {
                      · synced {{ t.lastSyncAt | sdRelativeTime }}
                    }
                  </span>
                  @if (t.lastError) {
                    <span class="last-error small"><sd-icon name="circle-alert" [size]="12" /> {{ t.lastError }}</span>
                  }
                  @if (t.webhookUrl) {
                    <div class="webhook">
                      <span class="sd-muted small">Webhook URL</span>
                      <code class="url">{{ t.webhookUrl }}</code>
                      <button sdButton size="sm" variant="ghost" icon="copy" iconOnly [attr.aria-label]="'Copy the webhook URL of ' + t.name" (click)="copy(t.webhookUrl)"></button>
                      @if (!t.hasWebhookSecret) {
                        <sd-badge size="sm" tone="warning">No secret</sd-badge>
                      }
                    </div>
                  }
                </div>
                <div class="actions">
                  @if (t.provider !== 'Local') {
                    <button sdButton size="sm" icon="plug" [loading]="busy() === t.id + ':test'" (click)="test(t)">Test</button>
                    <button sdButton size="sm" icon="refresh" [loading]="busy() === t.id + ':sync'" [disabled]="!t.isActive" (click)="sync(t)">Sync now</button>
                  }
                  <button sdButton size="sm" icon="pencil" (click)="openEditor(t, t.provider)">Edit</button>
                  <sd-dropdown-menu iconOnly icon="ellipsis" variant="ghost" size="sm" align="end" [ariaLabel]="'More actions for ' + t.name" [items]="rowMenu(t)" (select)="onRow(t, $event)" />
                </div>
              </li>
            }
          </ul>
        }

        <h2 class="section">Sync-back rules</h2>
        @if (settingsLoading()) {
          <sd-skeleton [lines]="4" height="28px" />
        } @else if (settingsError()) {
          <div class="error" role="alert">
            <sd-icon name="circle-alert" [size]="16" />
            <span>{{ settingsError() }}</span>
            <button sdButton size="sm" icon="refresh" (click)="loadSettings()">Retry</button>
          </div>
        } @else if (settings(); as s) {
          <form class="settings" (ngSubmit)="saveSettings()" aria-label="Sync-back rules">
            <div class="toggles">
              <sd-toggle [value]="s.addNoteOnResolved" (valueChange)="patchSettings({ addNoteOnResolved: $event })">Add an internal note when a linked item is resolved</sd-toggle>
              <sd-toggle [value]="s.addNoteOnReleased" (valueChange)="patchSettings({ addNoteOnReleased: $event })">Add an internal note when the fix is released</sd-toggle>
              <sd-toggle [value]="s.notifyAssignee" (valueChange)="patchSettings({ notifyAssignee: $event })">Notify the ticket assignee (resolved, released, deleted)</sd-toggle>
              <sd-toggle [value]="s.notifyCustomerOnRelease" (valueChange)="patchSettings({ notifyCustomerOnRelease: $event })">Tell the customer when the fix is released (“fix released in version X”)</sd-toggle>
              <sd-toggle [value]="s.setEngineeringSubstatus" (valueChange)="patchSettings({ setEngineeringSubstatus: $event })">Set the ticket substatus “Engineering” on escalation</sd-toggle>
            </div>
            <div class="grid">
              <sd-form-field label="Customer reply template" optional [hint]="templateHint">
                <select sdInput name="template" [disabled]="!s.notifyCustomerOnRelease" [ngModel]="s.releaseReplyTemplateId" (ngModelChange)="patchSettings({ releaseReplyTemplateId: $event })">
                  <option [ngValue]="null">Built-in message</option>
                  @for (t of s.replyTemplates; track t.id) {
                    <option [ngValue]="t.id">{{ t.name }}</option>
                  }
                </select>
              </sd-form-field>
              <sd-form-field label="Escalation status" optional hint="Used when the ticket's status has no “Engineering” substatus.">
                <select sdInput name="status" [disabled]="!s.setEngineeringSubstatus" [ngModel]="s.escalationStatusId" (ngModelChange)="patchSettings({ escalationStatusId: $event })">
                  <option [ngValue]="null">Keep the current status</option>
                  @for (st of s.engineeringStatuses; track st.id) {
                    <option [ngValue]="st.id">{{ st.name }}</option>
                  }
                </select>
              </sd-form-field>
              <sd-form-field label="Default tracker" optional>
                <select sdInput name="default" [ngModel]="s.defaultTrackerId" (ngModelChange)="patchSettings({ defaultTrackerId: $event })">
                  <option [ngValue]="null">First active tracker</option>
                  @for (t of trackers(); track t.id) {
                    <option [ngValue]="t.id">{{ t.name }}</option>
                  }
                </select>
              </sd-form-field>
            </div>
            <p class="sd-muted small">Automation can react too: outbound webhooks receive <code>engineering.item_updated</code>, and rules can trigger on the internal notes added here.</p>
            <div class="footer">
              <button sdButton type="submit" variant="primary" [loading]="savingSettings()">Save rules</button>
            </div>
          </form>
        }
      </div>
    </div>

    <desk-tracker-editor [(open)]="editorOpen" [tracker]="editing()" [newProvider]="newProvider()" (saved)="onSaved($event)" />

    <sd-drawer [(open)]="logOpen" [title]="'Activity — ' + (logTracker()?.name ?? '')" width="680px">
      <sd-tabs [selected]="logTab()" (selectedChange)="logTab.set($event ?? 'deliveries')" ariaLabel="Tracker activity">
        <sd-tab id="deliveries" label="Webhook deliveries" icon="webhook">
          @if (deliveries(); as list) {
            @if (list.length === 0) {
              <sd-empty-state compact icon="webhook" title="No deliveries yet" message="Configure the service hook / webhook with the URL and secret shown on the tracker." />
            } @else {
              <ul class="log">
                @for (d of list; track d.id) {
                  <li>
                    <sd-badge size="sm" [tone]="d.status === 'Processed' ? 'success' : d.status === 'Failed' ? 'danger' : 'neutral'">{{ d.status }}</sd-badge>
                    <span class="mono">{{ d.eventType }}</span>
                    <span class="sd-muted small grow">{{ d.detail }}</span>
                    <span class="sd-muted small">{{ d.receivedAt | sdDateTime }}</span>
                  </li>
                }
              </ul>
            }
          } @else {
            <sd-skeleton [lines]="4" />
          }
        </sd-tab>
        <sd-tab id="calls" label="API calls" icon="list">
          @if (logs(); as list) {
            @if (list.length === 0) {
              <sd-empty-state compact icon="list" title="No calls yet" message="Calls appear after a test, an escalation or a sync." />
            } @else {
              <ul class="log">
                @for (l of list; track l.id) {
                  <li>
                    <sd-badge size="sm" [tone]="l.success ? 'success' : 'danger'">{{ l.statusCode ?? '—' }}</sd-badge>
                    <span class="mono">{{ l.operation }}</span>
                    <span class="sd-muted small grow sd-truncate">{{ l.success ? l.url : l.responseExcerpt }}</span>
                    <span class="sd-muted small">{{ l.durationMs }} ms · {{ l.createdAt | sdRelativeTime }}</span>
                  </li>
                }
              </ul>
            }
          } @else {
            <sd-skeleton [lines]="4" />
          }
        </sd-tab>
      </sd-tabs>
    </sd-drawer>
  `,
  styles: `
    .section {
      margin: 8px 0 10px;
      font-size: var(--sd-text-md, 1rem);
      font-weight: var(--sd-weight-semibold);
    }
    .small {
      font-size: var(--sd-text-xs);
    }
    .secret {
      display: flex;
      align-items: flex-start;
      gap: 10px;
      margin-bottom: 14px;
      padding: 12px;
      border: 1px solid var(--sd-warning-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-warning-bg);
    }
    .secret-body {
      flex: 1;
      min-width: 0;
    }
    .secret p {
      margin: 2px 0 6px;
    }
    .value,
    .url {
      font-family: var(--sd-font-mono);
      font-size: var(--sd-text-xs);
      word-break: break-all;
    }
    .error {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 12px;
      border: 1px solid var(--sd-danger-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-danger-bg);
      color: var(--sd-danger);
    }
    .trackers {
      display: flex;
      flex-direction: column;
      margin: 0 0 24px;
      padding: 0;
      list-style: none;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
    }
    .tracker {
      display: flex;
      flex-wrap: wrap;
      align-items: flex-start;
      gap: 12px;
      padding: 12px;
      border-bottom: 1px solid var(--sd-divider);
    }
    .tracker:last-child {
      border-bottom: 0;
    }
    .main {
      display: flex;
      flex: 1 1 360px;
      flex-direction: column;
      gap: 4px;
      min-width: 0;
    }
    .title {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
    }
    .last-error {
      display: flex;
      align-items: center;
      gap: 4px;
      color: var(--sd-danger);
    }
    .webhook {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
    }
    .settings {
      display: flex;
      flex-direction: column;
      gap: 14px;
      padding: 14px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
    }
    .toggles {
      display: flex;
      flex-direction: column;
      gap: 10px;
    }
    .grid {
      display: grid;
      grid-template-columns: repeat(3, minmax(0, 1fr));
      gap: 12px;
    }
    @media (max-width: 900px) {
      .grid {
        grid-template-columns: 1fr;
      }
    }
    .footer {
      display: flex;
      justify-content: flex-end;
    }
    .log {
      display: flex;
      flex-direction: column;
      gap: 6px;
      margin: 8px 0 0;
      padding: 0;
      list-style: none;
    }
    .log li {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      padding: 6px 8px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-sm);
    }
    .mono {
      font-family: var(--sd-font-mono);
      font-size: var(--sd-text-xs);
    }
    .grow {
      flex: 1;
      min-width: 0;
    }
  `,
})
export class EngineeringAdminPage implements OnInit {
  private readonly api = inject(EngineeringAdminApi);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(SdDialogService);

  protected readonly crumbs = [{ label: 'Administration', link: '/admin' }, { label: 'Engineering' }];
  protected readonly templateHint =
    "Placeholders: {{engineering.fixVersion}}, {{engineering.id}}, {{engineering.title}}, {{ticket.number}}, {{contact.name}}. Empty uses the built-in text.";

  protected readonly trackers = signal<EngineeringTrackerDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);
  protected readonly busy = signal<string | null>(null);
  protected readonly revealed = signal<{ name: string; provider: string; secret: string } | null>(null);

  protected readonly editorOpen = signal(false);
  protected readonly editing = signal<EngineeringTrackerDto | null>(null);
  protected readonly newProvider = signal<EngineeringTrackerProvider>('AzureDevOps');

  protected readonly settings = signal<EngineeringSettingsDto | null>(null);
  protected readonly settingsLoading = signal(true);
  protected readonly settingsError = signal<string | null>(null);
  protected readonly savingSettings = signal(false);

  protected readonly logOpen = signal(false);
  protected readonly logTracker = signal<EngineeringTrackerDto | null>(null);
  protected readonly logTab = signal<string>('deliveries');
  protected readonly deliveries = signal<EngineeringDeliveryDto[] | null>(null);
  protected readonly logs = signal<IntegrationLogDto[] | null>(null);

  protected readonly addMenu = computed<SdMenuItem[]>(() => [
    { id: 'AzureDevOps', label: 'Azure DevOps Services / Server (TFS)', icon: 'bug' },
    { id: 'GitHub', label: 'GitHub', icon: 'git-pull-request' },
    { id: 'Local', label: 'Built-in engineering board', icon: 'kanban', disabled: this.trackers().some((t) => t.provider === 'Local') },
  ]);

  ngOnInit(): void {
    this.reload();
    this.loadSettings();
  }

  protected reload(): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.api.trackers().subscribe({
      next: (list) => {
        this.trackers.set(list);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.loadError.set(err instanceof ApiError ? err.userMessage : 'Trackers could not be loaded.');
      },
    });
  }

  protected loadSettings(): void {
    this.settingsLoading.set(true);
    this.settingsError.set(null);
    this.api.settings().subscribe({
      next: (s) => {
        this.settings.set(s);
        this.settingsLoading.set(false);
      },
      error: (err: unknown) => {
        this.settingsLoading.set(false);
        this.settingsError.set(err instanceof ApiError ? err.userMessage : 'Settings could not be loaded.');
      },
    });
  }

  protected providerLabel(p: EngineeringTrackerProvider): string {
    return PROVIDER_LABEL[p] ?? p;
  }

  protected statusTone(t: EngineeringTrackerDto): SdTone {
    return !t.isActive ? 'neutral' : t.status === 'Error' ? 'danger' : 'success';
  }

  protected add(item: SdMenuItem): void {
    this.openEditor(null, item.id as EngineeringTrackerProvider);
  }

  protected openEditor(t: EngineeringTrackerDto | null, provider: EngineeringTrackerProvider): void {
    this.editing.set(t);
    this.newProvider.set(provider);
    this.editorOpen.set(true);
  }

  protected onSaved(saved: EngineeringTrackerDto): void {
    this.toast.success('Tracker saved', saved.name);
    if (saved.webhookSecret) this.revealed.set({ name: saved.name, provider: saved.provider, secret: saved.webhookSecret });
    this.reload();
    this.loadSettings();
  }

  protected rowMenu(t: EngineeringTrackerDto): SdMenuItem[] {
    const items: SdMenuItem[] = [];
    if (t.provider !== 'Local') {
      items.push({ id: 'logs', label: 'Deliveries & call log', icon: 'list' });
      items.push({ id: 'rotate', label: 'Rotate webhook secret', icon: 'key' });
      items.push({ id: 'divider', divider: true });
    }
    items.push({ id: 'delete', label: 'Delete', icon: 'trash', danger: true, disabled: t.linkedItemCount > 0 });
    return items;
  }

  protected async onRow(t: EngineeringTrackerDto, item: SdMenuItem): Promise<void> {
    switch (item.id) {
      case 'logs':
        this.openLogs(t);
        break;
      case 'rotate': {
        const ok = await this.dialogs.confirm({
          title: 'Rotate the webhook secret?',
          message: `Update the ${PROVIDER_LABEL[t.provider]} webhook with the new secret; deliveries signed with the old one are rejected.`,
          confirmLabel: 'Rotate',
        });
        if (ok)
          this.api.rotateWebhookSecret(t.id).subscribe((r) => {
            if (r.webhookSecret) this.revealed.set({ name: r.name, provider: r.provider, secret: r.webhookSecret });
            this.reload();
          });
        break;
      }
      case 'delete': {
        const ok = await this.dialogs.confirm({ title: `Delete “${t.name}”?`, message: 'Its sync and delivery logs are removed too.', danger: true, confirmLabel: 'Delete' });
        if (ok)
          this.api.delete(t.id).subscribe({
            next: () => {
              this.toast.success('Tracker deleted', t.name);
              this.reload();
            },
            error: (err: unknown) => this.toast.error('Could not delete', err instanceof ApiError ? err.userMessage : undefined),
          });
        break;
      }
    }
  }

  protected test(t: EngineeringTrackerDto): void {
    this.busy.set(t.id + ':test');
    this.api.test(t.id).subscribe({
      next: (r) => {
        this.busy.set(null);
        if (r.ok) this.toast.success('Connection OK', `${r.message} (${r.durationMs} ms)`);
        else this.toast.warning('Connection problem', r.message);
        this.reload();
      },
      error: (err: unknown) => {
        this.busy.set(null);
        this.toast.error('Test failed', err instanceof ApiError ? err.userMessage : undefined);
      },
    });
  }

  protected sync(t: EngineeringTrackerDto): void {
    this.busy.set(t.id + ':sync');
    this.api.sync(t.id).subscribe({
      next: (r) => {
        this.busy.set(null);
        if (r.error) this.toast.warning('Sync stopped', r.error);
        else this.toast.success('Sync finished', `${r.checked} checked · ${r.updated} updated · ${r.orphaned} orphaned`);
        this.reload();
      },
      error: (err: unknown) => {
        this.busy.set(null);
        this.toast.error('Sync failed', err instanceof ApiError ? err.userMessage : undefined);
      },
    });
  }

  private openLogs(t: EngineeringTrackerDto): void {
    this.logTracker.set(t);
    this.deliveries.set(null);
    this.logs.set(null);
    this.logTab.set('deliveries');
    this.logOpen.set(true);
    this.api.deliveries(t.id).subscribe({ next: (d) => this.deliveries.set(d), error: () => this.deliveries.set([]) });
    this.api.logs(t.id).subscribe({ next: (l) => this.logs.set(l), error: () => this.logs.set([]) });
  }

  protected patchSettings(change: Partial<EngineeringSettingsDto>): void {
    this.settings.update((s) => (s ? { ...s, ...change } : s));
  }

  protected saveSettings(): void {
    const s = this.settings();
    if (!s) return;
    this.savingSettings.set(true);
    this.api
      .saveSettings({
        addNoteOnResolved: s.addNoteOnResolved,
        addNoteOnReleased: s.addNoteOnReleased,
        notifyAssignee: s.notifyAssignee,
        notifyCustomerOnRelease: s.notifyCustomerOnRelease,
        releaseReplyTemplateId: s.releaseReplyTemplateId,
        setEngineeringSubstatus: s.setEngineeringSubstatus,
        escalationStatusId: s.escalationStatusId,
        defaultTrackerId: s.defaultTrackerId,
      })
      .subscribe({
        next: (saved) => {
          this.savingSettings.set(false);
          this.settings.set(saved);
          this.toast.success('Sync-back rules saved');
        },
        error: (err: unknown) => {
          this.savingSettings.set(false);
          this.toast.error('Could not save', saveErrorMessage(err));
        },
      });
  }

  protected copy(text: string | null): void {
    if (text) void navigator.clipboard?.writeText(text).then(() => this.toast.success('Copied'));
  }
}
