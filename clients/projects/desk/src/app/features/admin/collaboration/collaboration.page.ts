import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ApiError,
  CollaborationApi,
  CollaborationCatalogDto,
  CollaborationChannelDto,
  CollaborationLogDto,
  CollaborationProviderName,
  DepartmentRefDto,
  Guid,
  MetaApi,
  saveErrorMessage,
  validationMessages,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCheckbox,
  SdDialogService,
  SdDrawer,
  SdDropdownMenu,
  SdEmptyState,
  SdFormField,
  SdIcon,
  SdInput,
  SdMenuItem,
  SdPageHeader,
  SdRadio,
  SdRadioGroup,
  SdRelativeTimePipe,
  SdSkeleton,
  SdToggle,
  ToastService,
} from '@sd/ui';

interface EditorState {
  id: Guid | null;
  provider: CollaborationProviderName;
  name: string;
  webhookUrl: string;
  events: string[];
  departmentIds: Guid[];
  minPriority: string;
  isActive: boolean;
  rowVersion: string | null;
}

/**
 * Administration → Teams & Slack (admin.integrations): incoming-webhook channels mapped to events (new urgent
 * ticket, SLA breach, escalation, mention, engineering escalation/fix/release), optional department filter, message
 * cards with deep links, send test and the delivery log. Webhook URLs are write-only secrets.
 */
@Component({
  selector: 'desk-collaboration-page',
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
    SdCheckbox,
    SdToggle,
    SdRadioGroup,
    SdRadio,
    SdRelativeTimePipe,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Teams & Slack" icon="messages" subtitle="Post urgent tickets, SLA breaches, escalations, mentions and engineering updates to team channels." [breadcrumbs]="crumbs">
        <button sdButton sdPageActions variant="primary" icon="plus" (click)="edit(null)">Add channel</button>
      </sd-page-header>
      <div class="desk-page__body">
        @if (loading()) {
          <sd-skeleton [lines]="3" height="56px" />
        } @else if (loadError()) {
          <div class="error" role="alert">
            <sd-icon name="circle-alert" [size]="16" />
            <span>{{ loadError() }}</span>
            <button sdButton size="sm" icon="refresh" (click)="reload()">Retry</button>
          </div>
        } @else if (channels().length === 0) {
          <sd-empty-state icon="messages" title="No channels yet" message="Create an incoming webhook in Microsoft Teams (Workflows or connector) or Slack, then map events to it here.">
            <button sdButton variant="primary" icon="plus" (click)="edit(null)">Add a channel</button>
          </sd-empty-state>
        } @else {
          <ul class="channels">
            @for (c of channels(); track c.id) {
              <li class="channel">
                <div class="main">
                  <div class="title">
                    <strong>{{ c.name }}</strong>
                    <sd-badge size="sm" variant="outline">{{ c.provider === 'Teams' ? 'Microsoft Teams' : 'Slack' }}</sd-badge>
                    @if (c.lastError) {
                      <sd-badge size="sm" tone="danger" [dot]="true">Failing</sd-badge>
                    }
                  </div>
                  <span class="hint sd-muted">{{ c.urlHint }}</span>
                  <div class="events">
                    @for (e of c.events; track e) {
                      <sd-badge size="sm" variant="outline">{{ eventLabel(e) }}</sd-badge>
                    }
                    @if (c.departmentIds.length > 0) {
                      <span class="sd-muted small">· {{ c.departmentIds.length }} department{{ c.departmentIds.length === 1 ? '' : 's' }}</span>
                    }
                  </div>
                  @if (c.lastError) {
                    <span class="last-error small"><sd-icon name="circle-alert" [size]="12" /> {{ c.lastError }}</span>
                  }
                </div>
                <span class="sd-muted small last">
                  @if (c.lastSentAt) {
                    last post {{ c.lastSentAt | sdRelativeTime }}{{ c.lastStatusCode ? ' · HTTP ' + c.lastStatusCode : '' }}
                  } @else {
                    nothing posted yet
                  }
                </span>
                <sd-toggle [value]="c.isActive" [ariaLabel]="(c.isActive ? 'Disable ' : 'Enable ') + c.name" (valueChange)="setActive(c, $event)" />
                <button sdButton size="sm" icon="send" [loading]="testing() === c.id" (click)="test(c)">Send test</button>
                <sd-dropdown-menu iconOnly icon="ellipsis" variant="ghost" size="sm" align="end" [ariaLabel]="'More actions for ' + c.name" [items]="menu" (select)="onMenu(c, $event)" />
              </li>
            }
          </ul>
        }
      </div>
    </div>

    <sd-drawer [(open)]="editorOpen" [title]="editor().id ? 'Edit channel' : 'Add channel'" width="560px">
      <form id="channel-form" class="form" (ngSubmit)="save()">
        <sd-form-field label="Platform" required [error]="errors()['provider']">
          <sd-radio-group direction="row" [value]="editor().provider" (valueChange)="patch({ provider: $any($event) })">
            <sd-radio value="Teams" [disabled]="!!editor().id">Microsoft Teams</sd-radio>
            <sd-radio value="Slack" [disabled]="!!editor().id">Slack</sd-radio>
          </sd-radio-group>
        </sd-form-field>
        <sd-form-field label="Name" required hint="e.g. #support-escalations" [error]="errors()['name']">
          <input sdInput name="name" maxlength="200" [ngModel]="editor().name" (ngModelChange)="patch({ name: $event })" />
        </sd-form-field>
        <sd-form-field
          label="Incoming webhook URL"
          [required]="!editor().id"
          [hint]="editor().id ? 'Stored securely — leave empty to keep the current URL.' : urlHint()"
          [error]="errors()['webhookUrl']"
        >
          <input sdInput name="webhookUrl" type="url" autocomplete="off" [ngModel]="editor().webhookUrl" (ngModelChange)="patch({ webhookUrl: $event })" />
        </sd-form-field>
        <fieldset class="pick" [attr.aria-invalid]="!!errors()['events']">
          <legend>Post these events</legend>
          @for (e of catalog()?.events ?? []; track e.key) {
            <sd-checkbox [value]="editor().events.includes(e.key)" (valueChange)="toggleEvent(e.key, $event)">
              <span class="event-label">{{ e.label }}</span>
              <span class="sd-muted small">{{ e.description }}</span>
            </sd-checkbox>
          }
          @if (errors()['events']; as e) {
            <p class="err">{{ e }}</p>
          }
        </fieldset>
        @if (editor().events.includes('ticket.urgent')) {
          <sd-form-field label="Urgent means priority at least" [error]="errors()['minPriority']">
            <select sdInput name="minPriority" [ngModel]="editor().minPriority" (ngModelChange)="patch({ minPriority: $event })">
              <option value="Critical">Critical</option>
              <option value="High">High</option>
              <option value="Medium">Medium</option>
            </select>
          </sd-form-field>
        }
        <fieldset class="pick">
          <legend>Departments <span class="sd-muted small">(none selected = all)</span></legend>
          @for (d of departments(); track d.id) {
            <sd-checkbox [value]="editor().departmentIds.includes(d.id)" (valueChange)="toggleDepartment(d.id, $event)">{{ d.name }}</sd-checkbox>
          } @empty {
            <span class="sd-muted small">Loading departments…</span>
          }
        </fieldset>
        <sd-toggle [value]="editor().isActive" (valueChange)="patch({ isActive: $event })">Active</sd-toggle>
        @if (formError()) {
          <p class="err" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDrawerFooter class="footer">
        <button sdButton type="button" variant="ghost" (click)="editorOpen.set(false)">Cancel</button>
        <button sdButton type="submit" form="channel-form" variant="primary" [loading]="saving()">{{ editor().id ? 'Save' : 'Add channel' }}</button>
      </div>
    </sd-drawer>

    <sd-drawer [(open)]="logOpen" [title]="'Posts — ' + (logChannel()?.name ?? '')" width="600px">
      @if (logs(); as list) {
        @if (list.length === 0) {
          <sd-empty-state compact icon="send" title="Nothing posted yet" message="Posts appear when mapped events happen or after a test." />
        } @else {
          <ul class="log">
            @for (l of list; track l.id) {
              <li>
                <sd-badge size="sm" [tone]="l.success ? 'success' : 'danger'">{{ l.statusCode ?? 'blocked' }}</sd-badge>
                <span class="mono">{{ l.operation }}</span>
                <span class="sd-muted small grow">{{ l.success ? '' : l.responseExcerpt }}</span>
                <span class="sd-muted small">{{ l.durationMs }} ms · {{ l.createdAt | sdRelativeTime }}</span>
              </li>
            }
          </ul>
        }
      } @else {
        <sd-skeleton [lines]="4" />
      }
    </sd-drawer>
  `,
  styles: `
    .small {
      font-size: var(--sd-text-xs);
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
    .channels {
      display: flex;
      flex-direction: column;
      margin: 0;
      padding: 0;
      list-style: none;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
    }
    .channel {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 12px;
      padding: 12px;
      border-bottom: 1px solid var(--sd-divider);
    }
    .channel:last-child {
      border-bottom: 0;
    }
    .main {
      display: flex;
      flex: 1 1 320px;
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
    .hint {
      font-family: var(--sd-font-mono);
      font-size: var(--sd-text-xs);
    }
    .events {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 4px;
    }
    .last-error {
      display: flex;
      align-items: center;
      gap: 4px;
      color: var(--sd-danger);
    }
    .last {
      min-width: 140px;
    }
    .form {
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
    .pick {
      display: flex;
      flex-direction: column;
      gap: 8px;
      margin: 0;
      padding: 10px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
    }
    .pick[aria-invalid='true'] {
      border-color: var(--sd-danger-border);
    }
    legend {
      padding: 0 4px;
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-semibold);
    }
    .event-label {
      display: block;
    }
    .err {
      margin: 0;
      color: var(--sd-danger);
      font-size: var(--sd-text-sm);
    }
    .footer {
      display: flex;
      justify-content: flex-end;
      gap: 8px;
    }
    .log {
      display: flex;
      flex-direction: column;
      gap: 6px;
      margin: 0;
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
export class CollaborationPage implements OnInit {
  private readonly api = inject(CollaborationApi);
  private readonly meta = inject(MetaApi);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(SdDialogService);

  protected readonly crumbs = [{ label: 'Administration', link: '/admin' }, { label: 'Teams & Slack' }];
  protected readonly menu: SdMenuItem[] = [
    { id: 'edit', label: 'Edit', icon: 'pencil' },
    { id: 'logs', label: 'Post log', icon: 'list' },
    { id: 'divider', divider: true },
    { id: 'delete', label: 'Delete', icon: 'trash', danger: true },
  ];

  protected readonly channels = signal<CollaborationChannelDto[]>([]);
  protected readonly catalog = signal<CollaborationCatalogDto | null>(null);
  protected readonly departments = signal<DepartmentRefDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);
  protected readonly testing = signal<Guid | null>(null);

  protected readonly editorOpen = signal(false);
  protected readonly editor = signal<EditorState>(this.blank());
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);

  protected readonly logOpen = signal(false);
  protected readonly logChannel = signal<CollaborationChannelDto | null>(null);
  protected readonly logs = signal<CollaborationLogDto[] | null>(null);

  protected readonly urlHint = computed(() =>
    this.editor().provider === 'Slack'
      ? 'Slack app → Incoming Webhooks → https://hooks.slack.com/services/…'
      : 'Teams channel → Workflows “Post to a channel when a webhook request is received” (or an incoming webhook connector).',
  );

  ngOnInit(): void {
    this.api.catalog().subscribe({ next: (c) => this.catalog.set(c), error: () => this.catalog.set(null) });
    this.meta.ticketForm$().subscribe({ next: (m) => this.departments.set(m.departments), error: () => this.departments.set([]) });
    this.reload();
  }

  protected reload(): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.api.channels().subscribe({
      next: (list) => {
        this.channels.set(list);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.loadError.set(err instanceof ApiError ? err.userMessage : 'Channels could not be loaded.');
      },
    });
  }

  private blank(): EditorState {
    return { id: null, provider: 'Teams', name: '', webhookUrl: '', events: ['ticket.urgent', 'sla.breached'], departmentIds: [], minPriority: 'Critical', isActive: true, rowVersion: null };
  }

  protected eventLabel(key: string): string {
    return this.catalog()?.events.find((e) => e.key === key)?.label ?? key;
  }

  protected edit(c: CollaborationChannelDto | null): void {
    this.editor.set(
      c
        ? {
            id: c.id,
            provider: c.provider,
            name: c.name,
            webhookUrl: '',
            events: [...c.events],
            departmentIds: [...c.departmentIds],
            minPriority: c.minPriority,
            isActive: c.isActive,
            rowVersion: c.rowVersion,
          }
        : this.blank(),
    );
    this.errors.set({});
    this.formError.set(null);
    this.editorOpen.set(true);
  }

  protected patch(change: Partial<EditorState>): void {
    this.editor.update((e) => ({ ...e, ...change }));
  }

  protected toggleEvent(key: string, on: boolean): void {
    this.patch({ events: on ? [...this.editor().events.filter((e) => e !== key), key] : this.editor().events.filter((e) => e !== key) });
  }

  protected toggleDepartment(id: Guid, on: boolean): void {
    this.patch({ departmentIds: on ? [...this.editor().departmentIds.filter((d) => d !== id), id] : this.editor().departmentIds.filter((d) => d !== id) });
  }

  protected save(): void {
    const e = this.editor();
    const request = {
      provider: e.provider,
      name: e.name.trim(),
      webhookUrl: e.webhookUrl.trim() || (e.id ? null : ''),
      events: e.events,
      departmentIds: e.departmentIds,
      minPriority: e.minPriority,
      isActive: e.isActive,
      rowVersion: e.rowVersion,
    };
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    (e.id ? this.api.update(e.id, request) : this.api.create(request)).subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.editorOpen.set(false);
        this.toast.success(e.id ? 'Channel saved' : 'Channel added', saved.name);
        this.reload();
      },
      error: (err: unknown) => {
        this.saving.set(false);
        const fields = validationMessages(err);
        this.errors.set(fields);
        this.formError.set(Object.keys(fields).length > 0 ? null : saveErrorMessage(err));
      },
    });
  }

  protected setActive(c: CollaborationChannelDto, on: boolean): void {
    this.api
      .update(c.id, { provider: c.provider, name: c.name, webhookUrl: null, events: c.events, departmentIds: c.departmentIds, minPriority: c.minPriority, isActive: on, rowVersion: c.rowVersion })
      .subscribe({
        next: (updated) => this.channels.update((list) => list.map((x) => (x.id === c.id ? updated : x))),
        error: (err: unknown) => {
          this.channels.update((list) => [...list]);
          this.toast.error('Could not change the channel', err instanceof ApiError ? err.userMessage : undefined);
        },
      });
  }

  protected test(c: CollaborationChannelDto): void {
    this.testing.set(c.id);
    this.api.test(c.id).subscribe({
      next: (r) => {
        this.testing.set(null);
        if (r.success) this.toast.success('Test message posted', `${c.name} answered HTTP ${r.statusCode} in ${r.durationMs} ms`);
        else this.toast.warning('Test failed', r.error ?? `HTTP ${r.statusCode ?? '—'}`);
        this.reload();
      },
      error: (err: unknown) => {
        this.testing.set(null);
        this.toast.error('Test failed', err instanceof ApiError ? err.userMessage : undefined);
      },
    });
  }

  protected async onMenu(c: CollaborationChannelDto, item: SdMenuItem): Promise<void> {
    switch (item.id) {
      case 'edit':
        this.edit(c);
        break;
      case 'logs':
        this.logChannel.set(c);
        this.logs.set(null);
        this.logOpen.set(true);
        this.api.logs(c.id).subscribe({ next: (l) => this.logs.set(l), error: () => this.logs.set([]) });
        break;
      case 'delete': {
        const ok = await this.dialogs.confirm({ title: `Delete “${c.name}”?`, message: 'Events stop being posted to this channel.', danger: true, confirmLabel: 'Delete' });
        if (ok)
          this.api.delete(c.id).subscribe({
            next: () => {
              this.toast.success('Channel deleted', c.name);
              this.reload();
            },
            error: (err: unknown) => this.toast.error('Could not delete', err instanceof ApiError ? err.userMessage : undefined),
          });
        break;
      }
    }
  }
}
