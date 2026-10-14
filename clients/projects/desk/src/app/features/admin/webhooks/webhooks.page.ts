import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError, PageResult, SaveWebhookRequest, WebhookDeliveryDto, WebhookDto, WebhooksApi } from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCheckbox,
  SdDateTimePipe,
  SdDialogService,
  SdDrawer,
  SdDropdownMenu,
  SdEmptyState,
  SdFormField,
  SdIcon,
  SdInput,
  SdJsonViewer,
  SdMenuItem,
  SdPageHeader,
  SdRelativeTimePipe,
  SdSkeleton,
  SdTone,
  SdToggle,
  ToastService,
} from '@sd/ui';

interface EditorState {
  id: string | null;
  name: string;
  url: string;
  events: string[];
  isActive: boolean;
  secret: string;
}

/**
 * Outbound webhook subscriptions (admin.integrations): create/edit with an event picker, signing secret shown once
 * (create/rotate), enable/disable, delivery log with payload and response excerpts, redelivery and test ping.
 */
@Component({
  selector: 'desk-webhooks-page',
  imports: [
    FormsModule,
    SdPageHeader,
    SdButton,
    SdBadge,
    SdIcon,
    SdDrawer,
    SdFormField,
    SdInput,
    SdCheckbox,
    SdToggle,
    SdEmptyState,
    SdSkeleton,
    SdDropdownMenu,
    SdJsonViewer,
    SdDateTimePipe,
    SdRelativeTimePipe,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Webhooks" icon="webhook" subtitle="Signed HTTP callbacks for ticket, customer and knowledge events." [breadcrumbs]="crumbs">
        <button sdButton sdPageActions variant="primary" icon="plus" (click)="edit(null)">New webhook</button>
      </sd-page-header>
      <div class="desk-page__body">
        @if (revealed(); as r) {
          <div class="secret" role="status">
            <sd-icon name="key" [size]="16" />
            <div>
              <strong>Signing secret for “{{ r.name }}” — copy it now, it is shown only once.</strong>
              <p class="sd-muted small">Verify each request: <code>X-SupportDesk-Signature: sha256=HMAC_SHA256(secret, raw body)</code>.</p>
              <code class="value">{{ r.secret }}</code>
            </div>
            <span class="sd-spacer"></span>
            <button sdButton size="sm" icon="copy" (click)="copy(r.secret)">Copy</button>
            <button sdButton size="sm" variant="ghost" icon="x" iconOnly aria-label="Dismiss" (click)="revealed.set(null)"></button>
          </div>
        }

        @if (loading()) {
          <sd-skeleton [lines]="4" height="48px" />
        } @else if (hooks().length === 0) {
          <sd-empty-state icon="webhook" title="No webhooks yet" message="Send SupportDesk events to your ERP, chat tools or data warehouse — every delivery is signed and retried.">
            <button sdButton variant="primary" icon="plus" (click)="edit(null)">Add a webhook</button>
          </sd-empty-state>
        } @else {
          <ul class="hooks">
            @for (h of hooks(); track h.id) {
              <li class="hook">
                <div class="main">
                  <div class="title">
                    <strong>{{ h.name }}</strong>
                    <sd-badge size="sm" [tone]="statusTone(h.status)" [dot]="true">{{ h.status }}</sd-badge>
                    @if (h.consecutiveFailures > 0) {
                      <sd-badge size="sm" tone="danger" variant="outline">{{ h.consecutiveFailures }} failed in a row</sd-badge>
                    }
                  </div>
                  <span class="url sd-truncate">{{ h.url }}</span>
                  <div class="events">
                    @for (e of h.events.slice(0, 5); track e) {
                      <sd-badge size="sm" variant="outline">{{ e === '*' ? 'All events' : e }}</sd-badge>
                    }
                    @if (h.events.length > 5) {
                      <span class="sd-muted small">+{{ h.events.length - 5 }} more</span>
                    }
                  </div>
                </div>
                <div class="last sd-muted small">
                  @if (h.lastDeliveryAt) {
                    last delivery {{ h.lastDeliveryAt | sdRelativeTime }}{{ h.lastStatusCode ? ' · HTTP ' + h.lastStatusCode : '' }}
                  } @else {
                    no deliveries yet
                  }
                </div>
                <sd-toggle [value]="h.status !== 'Disabled'" [ariaLabel]="(h.status === 'Disabled' ? 'Enable ' : 'Disable ') + h.name" (valueChange)="setEnabled(h, $event)" />
                <button sdButton size="sm" icon="list" (click)="openDeliveries(h)">Deliveries</button>
                <sd-dropdown-menu iconOnly icon="ellipsis" variant="ghost" size="sm" align="end" [ariaLabel]="'More actions for ' + h.name" [items]="menu" (select)="onMenu(h, $event)" />
              </li>
            }
          </ul>
        }
      </div>
    </div>

    <!-- Editor -->
    <sd-drawer [(open)]="editorOpen" [title]="editor().id ? 'Edit webhook' : 'New webhook'" width="520px">
      <form class="form" id="webhook-form" (ngSubmit)="save()">
        <sd-form-field label="Name" required [error]="errors()['name']">
          <input sdInput name="name" [ngModel]="editor().name" (ngModelChange)="patch({ name: $event })" maxlength="200" />
        </sd-form-field>
        <sd-form-field label="Payload URL" required hint="Receives POST requests with a JSON envelope {id, type, occurredAt, tenantId, data}." [error]="errors()['url']">
          <input sdInput name="url" type="url" placeholder="https://erp.example.com/hooks/supportdesk" [ngModel]="editor().url" (ngModelChange)="patch({ url: $event })" />
        </sd-form-field>
        <fieldset class="events-pick" [attr.aria-invalid]="!!errors()['events']">
          <legend>Events</legend>
          <sd-checkbox name="all" [value]="editor().events.includes('*')" (valueChange)="setAll($event)">All events (current and future)</sd-checkbox>
          @if (!editor().events.includes('*')) {
            @for (group of eventGroups(); track group.name) {
              <div class="egroup">
                <span class="gname">{{ group.name }}</span>
                @for (e of group.events; track e) {
                  <sd-checkbox [value]="editor().events.includes(e)" (valueChange)="toggleEvent(e, $event)">{{ e }}</sd-checkbox>
                }
              </div>
            }
          }
          @if (errors()['events']; as e) {
            <p class="err">{{ e }}</p>
          }
        </fieldset>
        <sd-form-field
          [label]="editor().id ? 'New signing secret' : 'Signing secret'"
          [hint]="editor().id ? 'Leave empty to keep the current secret (or use Rotate secret).' : 'Leave empty to generate one (at least 16 characters).'"
          optional
          [error]="errors()['secret']"
        >
          <input sdInput name="secret" type="password" autocomplete="new-password" [ngModel]="editor().secret" (ngModelChange)="patch({ secret: $event })" />
        </sd-form-field>
        <sd-toggle name="active" [value]="editor().isActive" (valueChange)="patch({ isActive: $event })">Enabled</sd-toggle>
        @if (formError()) {
          <p class="err" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDrawerFooter class="footer">
        <button sdButton type="button" variant="ghost" (click)="editorOpen.set(false)">Cancel</button>
        <button sdButton type="submit" form="webhook-form" variant="primary" [loading]="saving()">{{ editor().id ? 'Save' : 'Create webhook' }}</button>
      </div>
    </sd-drawer>

    <!-- Delivery log -->
    <sd-drawer [(open)]="logOpen" [title]="'Deliveries — ' + (logHook()?.name ?? '')" width="640px">
      @if (logHook(); as h) {
        <div class="log-tools">
          <select sdInput aria-label="Status" [ngModel]="logStatus()" (ngModelChange)="logStatus.set($event); loadDeliveries(1)">
            <option value="">All statuses</option>
            <option value="Succeeded">Succeeded</option>
            <option value="Failed">Failed (retrying)</option>
            <option value="Pending">Pending</option>
            <option value="DeadLetter">Dead letter</option>
          </select>
          <button sdButton size="sm" variant="ghost" icon="refresh" (click)="loadDeliveries(deliveries()?.page ?? 1)">Refresh</button>
          <span class="sd-spacer"></span>
          <button sdButton size="sm" icon="send" [loading]="pinging()" (click)="ping(h)">Send test ping</button>
        </div>
        @if (deliveries(); as page) {
          @if (page.items.length === 0) {
            <sd-empty-state compact icon="inbox" title="No deliveries" message="Deliveries appear when subscribed events happen." />
          } @else {
            <ul class="deliveries">
              @for (d of page.items; track d.id) {
                <li class="delivery" [class.open]="expanded() === d.id">
                  <button type="button" class="drow" [attr.aria-expanded]="expanded() === d.id" (click)="expand(h, d)">
                    <sd-badge size="sm" [tone]="deliveryTone(d.status)">{{ d.status }}</sd-badge>
                    <span class="etype">{{ d.eventType }}</span>
                    <span class="sd-muted small">{{ d.responseCode ? 'HTTP ' + d.responseCode : '' }}{{ d.durationMs !== null ? ' · ' + d.durationMs + ' ms' : '' }}</span>
                    <span class="sd-spacer"></span>
                    <span class="sd-muted small">{{ d.createdAt | sdRelativeTime }}</span>
                  </button>
                  @if (expanded() === d.id) {
                    <div class="ddetail">
                      <dl>
                        <dt>Attempts</dt>
                        <dd>{{ d.attempts }}</dd>
                        <dt>Created</dt>
                        <dd>{{ d.createdAt | sdDateTime }}</dd>
                        @if (d.deliveredAt) {
                          <dt>Delivered</dt>
                          <dd>{{ d.deliveredAt | sdDateTime }}</dd>
                        } @else if (d.status === 'Failed') {
                          <dt>Next attempt</dt>
                          <dd>{{ d.nextAttemptAt | sdDateTime }}</dd>
                        }
                        <dt>Event id</dt>
                        <dd class="mono">{{ d.eventId }}</dd>
                      </dl>
                      @if (d.responseExcerpt) {
                        <h4>Response</h4>
                        <pre class="excerpt">{{ d.responseExcerpt }}</pre>
                      }
                      <h4>Payload</h4>
                      @if (detail(); as full) {
                        <sd-json-viewer [value]="full.payload" [expandDepth]="2" ariaLabel="Delivery payload" />
                      } @else {
                        <sd-skeleton [lines]="3" />
                      }
                      <button sdButton size="sm" icon="refresh" [disabled]="h.status === 'Disabled'" (click)="redeliver(h, d)">Redeliver</button>
                    </div>
                  }
                </li>
              }
            </ul>
            <div class="pager">
              <span class="sd-muted small">{{ page.total }} deliveries</span>
              <span class="sd-spacer"></span>
              <button sdButton size="sm" icon="chevron-left" [disabled]="page.page <= 1" (click)="loadDeliveries(page.page - 1)">Previous</button>
              <button sdButton size="sm" iconRight="chevron-right" [disabled]="page.page * page.pageSize >= page.total" (click)="loadDeliveries(page.page + 1)">Next</button>
            </div>
          }
        } @else {
          <sd-skeleton [lines]="5" />
        }
      }
    </sd-drawer>
  `,
  styles: `
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
    .secret p {
      margin: 2px 0 6px;
    }
    .value {
      font-family: var(--sd-font-mono);
      word-break: break-all;
    }
    .small {
      font-size: var(--sd-text-xs);
    }
    .hooks {
      display: flex;
      flex-direction: column;
      margin: 0;
      padding: 0;
      list-style: none;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
    }
    .hook {
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 12px;
      border-bottom: 1px solid var(--sd-divider);
    }
    .hook:last-child {
      border-bottom: 0;
    }
    .main {
      display: flex;
      flex-direction: column;
      gap: 4px;
      flex: 1;
      min-width: 0;
    }
    .title {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
    }
    .url {
      font-family: var(--sd-font-mono);
      font-size: var(--sd-text-xs);
      color: var(--sd-text-2);
    }
    .events {
      display: flex;
      flex-wrap: wrap;
      gap: 4px;
    }
    .last {
      min-width: 150px;
      text-align: right;
    }
    @media (max-width: 760px) {
      .hook {
        flex-wrap: wrap;
      }
      .last {
        text-align: left;
      }
    }
    .form {
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
    .events-pick {
      display: flex;
      flex-direction: column;
      gap: 6px;
      margin: 0;
      padding: 10px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      max-height: 340px;
      overflow: auto;
    }
    .events-pick[aria-invalid='true'] {
      border-color: var(--sd-danger-border);
    }
    legend {
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-semibold);
    }
    .egroup {
      display: flex;
      flex-direction: column;
      gap: 4px;
      padding-top: 6px;
    }
    .gname {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      text-transform: uppercase;
      letter-spacing: 0.04em;
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
    .log-tools {
      display: flex;
      align-items: center;
      gap: 8px;
      margin-bottom: 12px;
    }
    .log-tools select {
      width: 170px;
    }
    .deliveries {
      display: flex;
      flex-direction: column;
      gap: 6px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .delivery {
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-sm);
    }
    .delivery.open {
      border-color: var(--sd-primary-border);
    }
    .drow {
      display: flex;
      align-items: center;
      gap: 8px;
      width: 100%;
      padding: 7px 9px;
      border: 0;
      background: transparent;
      color: var(--sd-text);
      text-align: left;
      cursor: pointer;
    }
    .drow:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .etype {
      font-family: var(--sd-font-mono);
      font-size: var(--sd-text-sm);
    }
    .ddetail {
      display: flex;
      flex-direction: column;
      gap: 8px;
      padding: 4px 10px 10px;
      font-size: var(--sd-text-sm);
    }
    .ddetail dl {
      display: grid;
      grid-template-columns: 110px 1fr;
      gap: 3px 10px;
      margin: 0;
    }
    .ddetail dt {
      color: var(--sd-text-3);
    }
    .ddetail dd {
      margin: 0;
    }
    .mono {
      font-family: var(--sd-font-mono);
      font-size: var(--sd-text-xs);
    }
    h4 {
      margin: 4px 0 0;
      font-size: var(--sd-text-sm);
    }
    .excerpt {
      max-height: 160px;
      margin: 0;
      padding: 6px;
      overflow: auto;
      border-radius: var(--sd-radius-sm);
      background: var(--sd-surface-sunken);
      font-family: var(--sd-font-mono);
      font-size: var(--sd-text-xs);
      white-space: pre-wrap;
      word-break: break-word;
    }
    .pager {
      display: flex;
      align-items: center;
      gap: 8px;
      margin-top: 10px;
    }
  `,
})
export class WebhooksPage implements OnInit {
  private readonly api = inject(WebhooksApi);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(SdDialogService);

  protected readonly crumbs = [{ label: 'Administration', link: '/admin' }, { label: 'Webhooks' }];
  protected readonly menu: SdMenuItem[] = [
    { id: 'edit', label: 'Edit', icon: 'pencil' },
    { id: 'ping', label: 'Send test ping', icon: 'send' },
    { id: 'rotate', label: 'Rotate secret', icon: 'key' },
    { id: 'divider', divider: true },
    { id: 'delete', label: 'Delete', icon: 'trash', danger: true },
  ];

  protected readonly hooks = signal<WebhookDto[]>([]);
  protected readonly eventTypes = signal<string[]>([]);
  protected readonly loading = signal(true);
  protected readonly revealed = signal<{ name: string; secret: string } | null>(null);

  protected readonly editorOpen = signal(false);
  protected readonly editor = signal<EditorState>({ id: null, name: '', url: '', events: [], isActive: true, secret: '' });
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);

  protected readonly logOpen = signal(false);
  protected readonly logHook = signal<WebhookDto | null>(null);
  protected readonly logStatus = signal('');
  protected readonly deliveries = signal<PageResult<WebhookDeliveryDto> | null>(null);
  protected readonly expanded = signal<string | null>(null);
  protected readonly detail = signal<WebhookDeliveryDto | null>(null);
  protected readonly pinging = signal(false);

  protected readonly eventGroups = computed(() => {
    const groups = new Map<string, string[]>();
    for (const e of this.eventTypes()) {
      const g = e.split('.')[0];
      groups.set(g, [...(groups.get(g) ?? []), e]);
    }
    return [...groups.entries()].map(([name, events]) => ({ name, events }));
  });

  ngOnInit(): void {
    this.api.eventTypes().subscribe((r) => this.eventTypes.set(r.eventTypes));
    this.reload();
  }

  private reload(): void {
    this.api.list().subscribe({
      next: (list) => {
        this.hooks.set(list);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  protected statusTone(status: string): SdTone {
    return status === 'Active' ? 'success' : status === 'Failing' ? 'warning' : 'neutral';
  }

  protected deliveryTone(status: string): SdTone {
    return status === 'Succeeded' ? 'success' : status === 'Failed' ? 'warning' : status === 'DeadLetter' ? 'danger' : 'info';
  }

  // ───── Editor ─────

  protected edit(h: WebhookDto | null): void {
    this.editor.set(
      h
        ? { id: h.id, name: h.name, url: h.url, events: [...h.events], isActive: h.status !== 'Disabled', secret: '' }
        : { id: null, name: '', url: '', events: ['ticket.created'], isActive: true, secret: '' },
    );
    this.errors.set({});
    this.formError.set(null);
    this.editorOpen.set(true);
  }

  protected patch(change: Partial<EditorState>): void {
    this.editor.update((e) => ({ ...e, ...change }));
  }

  protected setAll(all: boolean): void {
    this.patch({ events: all ? ['*'] : [] });
  }

  protected toggleEvent(event: string, on: boolean): void {
    this.patch({ events: on ? [...this.editor().events.filter((e) => e !== event), event] : this.editor().events.filter((e) => e !== event) });
  }

  protected save(): void {
    const e = this.editor();
    const request: SaveWebhookRequest = { name: e.name.trim(), url: e.url.trim(), events: e.events, isActive: e.isActive, secret: e.secret.trim() || null };
    this.saving.set(true);
    const call = e.id ? this.api.update(e.id, request) : this.api.create(request);
    call.subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.editorOpen.set(false);
        if (saved.secret) this.revealed.set({ name: saved.name, secret: saved.secret });
        this.toast.success(e.id ? 'Webhook saved' : 'Webhook created', saved.name);
        this.reload();
      },
      error: (err: unknown) => {
        this.saving.set(false);
        if (err instanceof ApiError) {
          const map: Record<string, string> = {};
          for (const [k, v] of Object.entries(err.errors as Record<string, unknown>)) map[k] = Array.isArray(v) ? String(v[0]) : String(v);
          this.errors.set(map);
          this.formError.set(Object.keys(map).length ? null : err.userMessage);
        } else this.formError.set('The webhook could not be saved.');
      },
    });
  }

  // ───── Row actions ─────

  protected setEnabled(h: WebhookDto, enabled: boolean): void {
    this.api.setEnabled(h.id, enabled).subscribe({
      next: (updated) => this.hooks.update((list) => list.map((x) => (x.id === h.id ? updated : x))),
      error: () => this.hooks.update((list) => [...list]),
    });
  }

  protected async onMenu(h: WebhookDto, item: SdMenuItem): Promise<void> {
    switch (item.id) {
      case 'edit':
        this.edit(h);
        break;
      case 'ping':
        this.ping(h);
        break;
      case 'rotate': {
        const ok = await this.dialogs.confirm({ title: 'Rotate the signing secret?', message: `“${h.name}” must be updated with the new secret; deliveries signed with the old one will fail verification.`, confirmLabel: 'Rotate' });
        if (ok)
          this.api.rotateSecret(h.id).subscribe((r) => {
            if (r.secret) this.revealed.set({ name: r.name, secret: r.secret });
          });
        break;
      }
      case 'delete': {
        const ok = await this.dialogs.confirm({ title: `Delete “${h.name}”?`, message: 'Its delivery log is deleted too.', danger: true, confirmLabel: 'Delete' });
        if (ok)
          this.api.delete(h.id).subscribe(() => {
            this.toast.success('Webhook deleted');
            this.reload();
          });
        break;
      }
    }
  }

  protected ping(h: WebhookDto): void {
    this.pinging.set(true);
    this.api.ping(h.id).subscribe({
      next: (d) => {
        this.pinging.set(false);
        if (d.status === 'Succeeded') this.toast.success('Ping delivered', `HTTP ${d.responseCode} in ${d.durationMs} ms`);
        else this.toast.warning('Ping failed', d.responseExcerpt ?? `HTTP ${d.responseCode ?? '—'}`);
        if (this.logOpen() && this.logHook()?.id === h.id) this.loadDeliveries(1);
        this.reload();
      },
      error: (err: unknown) => {
        this.pinging.set(false);
        this.toast.error('Ping failed', err instanceof ApiError ? err.userMessage : undefined);
      },
    });
  }

  // ───── Delivery log ─────

  protected openDeliveries(h: WebhookDto): void {
    this.logHook.set(h);
    this.logStatus.set('');
    this.deliveries.set(null);
    this.expanded.set(null);
    this.logOpen.set(true);
    this.loadDeliveries(1);
  }

  protected loadDeliveries(page: number): void {
    const h = this.logHook();
    if (!h) return;
    this.api.deliveries(h.id, { status: this.logStatus() || null, page, pageSize: 25 }).subscribe((p) => this.deliveries.set(p));
  }

  protected expand(h: WebhookDto, d: WebhookDeliveryDto): void {
    if (this.expanded() === d.id) {
      this.expanded.set(null);
      return;
    }
    this.expanded.set(d.id);
    this.detail.set(null);
    this.api.delivery(h.id, d.id).subscribe((full) => {
      if (this.expanded() === d.id) this.detail.set(full);
    });
  }

  protected redeliver(h: WebhookDto, d: WebhookDeliveryDto): void {
    this.api.redeliver(h.id, d.id).subscribe({
      next: () => {
        this.toast.success('Queued for redelivery', d.eventType);
        this.loadDeliveries(this.deliveries()?.page ?? 1);
      },
      error: (err: unknown) => this.toast.error('Could not redeliver', err instanceof ApiError ? err.userMessage : undefined),
    });
  }

  protected copy(text: string): void {
    void navigator.clipboard?.writeText(text).then(() => this.toast.success('Copied'));
  }
}
