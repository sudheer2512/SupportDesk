import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ApiError,
  AuditApi,
  AuditChainVerificationDto,
  AuditEntryDetailDto,
  AuditEntryDto,
  AuditFacetsDto,
  AuditQuery,
  saveBlob,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdDateInput,
  SdDateTimePipe,
  SdDrawer,
  SdEmptyState,
  SdFormField,
  SdIcon,
  SdInput,
  SdJsonViewer,
  SdPageHeader,
  SdSkeleton,
  ToastService,
} from '@sd/ui';
import { SAAS_CSS, humanize } from '../../subscription/saas-ui';

/**
 * Audit viewer (admin.audit): filter by actor, action, entity, date range and IP; entry detail with a before/after
 * diff; CSV export of the filtered entries; verification of the tamper-evident hash chain (first broken link).
 */
@Component({
  selector: 'desk-audit-page',
  imports: [FormsModule, SdBadge, SdButton, SdDateInput, SdDateTimePipe, SdDrawer, SdEmptyState, SdFormField, SdIcon, SdInput, SdJsonViewer, SdPageHeader, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Audit log" icon="shield-check" subtitle="Tamper-evident record of sensitive actions." [breadcrumbs]="crumbs">
        <button sdButton sdPageActions icon="download" [loading]="exporting()" (click)="exportCsv()">Export CSV</button>
        <button sdButton sdPageActions variant="primary" icon="shield-check" [loading]="verifying()" (click)="verify()">Verify chain</button>
      </sd-page-header>
      <div class="desk-page__body">
        @if (verification(); as v) {
          <div class="callout" [class]="v.valid ? 'callout callout--success' : 'callout callout--danger'" role="status">
            <sd-icon [name]="v.valid ? 'circle-check' : 'octagon-alert'" [size]="16" />
            <div class="grow">
              <strong>{{ v.valid ? 'Audit chain intact.' : 'Audit chain broken!' }}</strong> {{ v.message }}
              <div class="muted small">
                {{ v.checked }} sealed entries checked at {{ v.verifiedAt | sdDateTime }}{{ v.unsealed ? ' · ' + v.unsealed + ' awaiting sealing' : '' }}{{
                  v.anchoredAfterSequence ? ' · verified from retention anchor #' + v.anchoredAfterSequence : ''
                }}
              </div>
              @if (!v.valid && v.brokenEntryId) {
                <button sdButton size="sm" (click)="openId(v.brokenEntryId)">Open entry #{{ v.brokenAtSequence }}</button>
              }
            </div>
          </div>
        }

        <form class="filters" (ngSubmit)="search(false)" role="search" aria-label="Filter audit entries">
          <sd-form-field label="Actor"><input sdInput name="actor" placeholder="Name or id" [(ngModel)]="q.actor" /></sd-form-field>
          <sd-form-field label="Action">
            <select sdInput name="action" [(ngModel)]="q.action">
              <option value="">Any</option>
              @for (a of facets()?.actions ?? []; track a) {
                <option [value]="a">{{ a }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Entity type">
            <select sdInput name="etype" [(ngModel)]="q.entityType">
              <option value="">Any</option>
              @for (t of facets()?.entityTypes ?? []; track t) {
                <option [value]="t">{{ t }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Entity id" [error]="idError()"><input sdInput name="eid" placeholder="GUID" [(ngModel)]="q.entityId" /></sd-form-field>
          <sd-form-field label="From"><sd-date-input name="from" [(value)]="from" ariaLabel="From date" /></sd-form-field>
          <sd-form-field label="To"><sd-date-input name="to" [(value)]="to" ariaLabel="To date" /></sd-form-field>
          <sd-form-field label="IP address"><input sdInput name="ip" placeholder="e.g. 10.0." [(ngModel)]="q.ip" /></sd-form-field>
          <sd-form-field label="Summary contains"><input sdInput name="search" [(ngModel)]="q.search" /></sd-form-field>
          <div class="filter-actions">
            <button sdButton type="submit" variant="primary" icon="search">Apply</button>
            <button sdButton type="button" variant="ghost" (click)="clear()">Clear</button>
          </div>
        </form>

        @if (error(); as e) {
          <sd-empty-state icon="circle-alert" title="The audit log could not be loaded" [message]="e"><button sdButton (click)="search(false)">Try again</button></sd-empty-state>
        } @else if (rows() === null) {
          <sd-skeleton [lines]="10" height="30px" />
        } @else if (rows()!.length === 0) {
          <sd-empty-state icon="shield-check" title="No audit entries match" message="Widen the date range or clear filters." />
        } @else {
          <div class="table-wrap">
            <table class="grid">
              <caption class="sd-visually-hidden">Audit entries</caption>
              <thead>
                <tr>
                  <th scope="col">When</th><th scope="col">Actor</th><th scope="col">Action</th><th scope="col" class="hide-sm">Entity</th>
                  <th scope="col">Summary</th><th scope="col" class="hide-sm">IP</th><th scope="col" class="num">#</th>
                </tr>
              </thead>
              <tbody>
                @for (a of rows(); track a.id) {
                  <tr class="clickable" tabindex="0" (click)="open(a)" (keydown.enter)="open(a)" [attr.aria-label]="'Open audit entry ' + (a.summary || a.action)">
                    <td class="nowrap">{{ a.occurredAt | sdDateTime }}</td>
                    <td>{{ a.actorName || '—' }}<div class="muted small">{{ a.actorType }}</div></td>
                    <td><code>{{ a.action }}</code></td>
                    <td class="hide-sm">{{ a.entityType || '—' }}</td>
                    <td>{{ a.summary || humanize(a.action) }}</td>
                    <td class="hide-sm nowrap">{{ a.ipAddress || '—' }}</td>
                    <td class="num">
                      @if (a.sealed) {
                        {{ a.sequence }}
                      } @else {
                        <sd-badge size="sm" variant="outline">sealing</sd-badge>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          @if (cursor()) {
            <div class="more"><button sdButton size="sm" [loading]="loading()" (click)="search(true)">Load more</button></div>
          }
        }
      </div>
    </div>

    <sd-drawer [(open)]="detailOpen" title="Audit entry" width="640px">
      @if (detail(); as d) {
        <dl class="facts">
          <dt>When</dt>
          <dd>{{ d.entry.occurredAt | sdDateTime }}</dd>
          <dt>Actor</dt>
          <dd>{{ d.entry.actorName || '—' }} ({{ d.entry.actorType }}){{ d.entry.actorId ? ' · ' + d.entry.actorId : '' }}</dd>
          <dt>Action</dt>
          <dd><code>{{ d.entry.action }}</code></dd>
          <dt>Entity</dt>
          <dd>{{ d.entry.entityType || '—' }}{{ d.entry.entityId ? ' · ' + d.entry.entityId : '' }}</dd>
          <dt>Summary</dt>
          <dd>{{ d.entry.summary || '—' }}</dd>
          <dt>IP / client</dt>
          <dd>{{ d.entry.ipAddress || '—' }}{{ d.userAgent ? ' · ' + d.userAgent : '' }}</dd>
          <dt>Correlation</dt>
          <dd class="mono">{{ d.correlationId || '—' }}</dd>
          <dt>Sequence</dt>
          <dd>{{ d.entry.sealed ? '#' + d.entry.sequence : 'not sealed yet' }}</dd>
          <dt>Hash</dt>
          <dd class="mono">{{ d.hash || '—' }}</dd>
          <dt>Previous hash</dt>
          <dd class="mono">{{ d.prevHash || '—' }}</dd>
        </dl>
        <h3 class="section-title">Changes</h3>
        @if (d.diff.length === 0) {
          <p class="muted small">No before/after values recorded.</p>
        } @else {
          <div class="table-wrap">
            <table class="grid diff">
              <caption class="sd-visually-hidden">Before and after values</caption>
              <thead><tr><th scope="col">Field</th><th scope="col">Before</th><th scope="col">After</th></tr></thead>
              <tbody>
                @for (c of d.diff; track c.path) {
                  <tr [class]="'k-' + c.kind">
                    <td class="mono">{{ c.path }}</td>
                    <td class="before">{{ c.before ?? '—' }}</td>
                    <td class="after">{{ c.after ?? '—' }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
        @if (d.changes !== null) {
          <h3 class="section-title">Raw record</h3>
          <sd-json-viewer [value]="d.changes" [expandDepth]="2" ariaLabel="Recorded changes" />
        }
      } @else {
        <sd-skeleton [lines]="10" />
      }
    </sd-drawer>
  `,
  styles: `
    ${SAAS_CSS}
    .filters { display: grid; gap: 10px 12px; grid-template-columns: repeat(auto-fill, minmax(min(100%, 170px), 1fr)); align-items: end; margin-bottom: var(--sd-space-4); }
    .filter-actions { display: flex; gap: 8px; }
    .nowrap { white-space: nowrap; }
    code { font-family: var(--sd-font-mono); font-size: var(--sd-text-xs); }
    .diff td { vertical-align: top; overflow-wrap: anywhere; }
    .k-added .after { background: var(--sd-success-bg); }
    .k-removed .before { background: var(--sd-danger-bg); text-decoration: line-through; }
    .k-changed .before { background: var(--sd-danger-bg); }
    .k-changed .after { background: var(--sd-success-bg); }
  `,
})
export class AuditPage implements OnInit {
  private readonly api = inject(AuditApi);
  private readonly toast = inject(ToastService);

  protected readonly crumbs = [{ label: 'Administration', link: '/admin' }, { label: 'Audit log' }];
  protected q: { actor: string; action: string; entityType: string; entityId: string; ip: string; search: string } = this.empty();
  protected from: string | null = null;
  protected to: string | null = null;
  protected readonly facets = signal<AuditFacetsDto | null>(null);
  protected readonly rows = signal<AuditEntryDto[] | null>(null);
  protected readonly cursor = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly idError = signal<string | null>(null);
  protected readonly detailOpen = signal(false);
  protected readonly detail = signal<AuditEntryDetailDto | null>(null);
  protected readonly verifying = signal(false);
  protected readonly verification = signal<AuditChainVerificationDto | null>(null);
  protected readonly exporting = signal(false);
  protected readonly humanize = humanize;

  ngOnInit(): void {
    this.api.facets().subscribe({ next: (f) => this.facets.set(f), error: () => undefined });
    this.search(false);
  }

  private empty() {
    return { actor: '', action: '', entityType: '', entityId: '', ip: '', search: '' };
  }

  private query(): AuditQuery | null {
    const entityId = this.q.entityId.trim();
    if (entityId && !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(entityId)) {
      this.idError.set('Enter a GUID.');
      return null;
    }
    this.idError.set(null);
    return {
      actor: this.q.actor || null,
      action: this.q.action || null,
      entityType: this.q.entityType || null,
      entityId: entityId || null,
      ip: this.q.ip || null,
      search: this.q.search || null,
      from: this.from ? new Date(this.from + 'T00:00:00').toISOString() : null,
      to: this.to ? new Date(this.to + 'T23:59:59').toISOString() : null,
    };
  }

  protected search(more: boolean): void {
    const query = this.query();
    if (!query) return;
    this.loading.set(true);
    this.error.set(null);
    if (!more) this.rows.set(null);
    this.api.search({ ...query, cursor: more ? this.cursor() : null, limit: 50 }).subscribe({
      next: (p) => {
        this.rows.set(more ? [...(this.rows() ?? []), ...p.items] : p.items);
        this.cursor.set(p.nextCursor);
        this.loading.set(false);
      },
      error: (e: ApiError) => {
        this.error.set(e.userMessage);
        this.loading.set(false);
      },
    });
  }

  protected clear(): void {
    this.q = this.empty();
    this.from = null;
    this.to = null;
    this.search(false);
  }

  protected open(a: AuditEntryDto): void {
    this.openId(a.id);
  }

  protected openId(id: string): void {
    this.detail.set(null);
    this.detailOpen.set(true);
    this.api.get(id).subscribe({
      next: (d) => this.detail.set(d),
      error: (e: ApiError) => {
        this.detailOpen.set(false);
        this.toast.error('Entry could not be loaded', e.userMessage);
      },
    });
  }

  protected verify(): void {
    this.verifying.set(true);
    this.api.verify().subscribe({
      next: (v) => {
        this.verification.set(v);
        this.verifying.set(false);
      },
      error: () => this.verifying.set(false),
    });
  }

  protected exportCsv(): void {
    const query = this.query();
    if (!query) return;
    this.exporting.set(true);
    this.api.exportCsv(query).subscribe({
      next: (blob) => {
        saveBlob(blob, `audit-${new Date().toISOString().slice(0, 10)}.csv`);
        this.exporting.set(false);
      },
      error: (e: ApiError) => {
        this.exporting.set(false);
        this.toast.error('Export failed', e.status === 402 ? 'Audit export is not included in your plan.' : e.userMessage);
      },
    });
  }
}
