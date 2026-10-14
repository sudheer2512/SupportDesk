import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiError, PlatformApi, PlatformAuditEntryDto, PlatformChainVerificationDto } from '@sd/core';
import { SdButton, SdDateTimePipe, SdEmptyState, SdFormField, SdIcon, SdInput, SdPageHeader, SdSkeleton } from '@sd/ui';
import { SAAS_CSS, humanize } from '../subscription/saas-ui';

/** Platform audit: operator actions, sign-ups, billing automation and suspensions; hash-chain verification. */
@Component({
  selector: 'desk-platform-audit-page',
  imports: [FormsModule, RouterLink, SdButton, SdDateTimePipe, SdEmptyState, SdFormField, SdIcon, SdInput, SdPageHeader, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Platform audit" icon="shield-check" subtitle="Tamper-evident trail of platform operations.">
        <button sdButton sdPageActions icon="shield-check" [loading]="verifying()" (click)="verify()">Verify chain</button>
      </sd-page-header>
      <div class="desk-page__body">
        @if (verification(); as v) {
          <div class="callout" [class]="v.valid ? 'callout callout--success' : 'callout callout--danger'" role="status">
            <sd-icon [name]="v.valid ? 'circle-check' : 'octagon-alert'" [size]="16" />
            <div class="grow">
              <strong>{{ v.valid ? 'Chain intact' : 'Chain broken' }}</strong> — {{ v.message }} ({{ v.checked }} sealed entries checked{{ v.brokenAtSequence ? ', first broken link #' + v.brokenAtSequence : '' }}).
            </div>
          </div>
        }
        <form class="toolbar" role="search" (ngSubmit)="load(false)">
          <sd-form-field label="Search"><input sdInput name="q" placeholder="Summary or actor" [(ngModel)]="search" /></sd-form-field>
          <sd-form-field label="Action">
            <select sdInput name="action" [ngModel]="action()" (ngModelChange)="action.set($event); load(false)">
              <option value="">All actions</option>
              @for (a of actions(); track a) {
                <option [value]="a">{{ a }}</option>
              }
            </select>
          </sd-form-field>
          <button sdButton type="submit" icon="search">Search</button>
        </form>
        @if (error(); as e) {
          <sd-empty-state icon="circle-alert" title="The audit trail could not be loaded" [message]="e"><button sdButton (click)="load(false)">Try again</button></sd-empty-state>
        } @else if (rows() === null) {
          <sd-skeleton [lines]="10" height="30px" />
        } @else if (rows()!.length === 0) {
          <sd-empty-state icon="shield-check" title="No audit entries match" />
        } @else {
          <div class="table-wrap">
            <table class="grid">
              <caption class="sd-visually-hidden">Platform audit entries</caption>
              <thead><tr><th scope="col">When</th><th scope="col">Actor</th><th scope="col">Action</th><th scope="col" class="hide-sm">Tenant</th><th scope="col">Summary</th><th scope="col" class="num hide-sm">#</th></tr></thead>
              <tbody>
                @for (a of rows(); track a.id) {
                  <tr>
                    <td class="nowrap">{{ a.occurredAt | sdDateTime }}</td>
                    <td>{{ a.actorName || a.actorType }}<div class="muted small">{{ a.actorType }}{{ a.ipAddress ? ' · ' + a.ipAddress : '' }}</div></td>
                    <td><code>{{ a.action }}</code></td>
                    <td class="hide-sm">
                      @if (a.tenantId || a.entityType === 'Tenant') {
                        <a [routerLink]="['/platform/tenants', a.tenantId ?? a.entityId]">{{ a.tenantName || 'tenant' }}</a>
                      } @else {
                        <span class="muted">—</span>
                      }
                    </td>
                    <td>{{ a.summary || humanize(a.action) }}</td>
                    <td class="num hide-sm">{{ a.sequence || '…' }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          @if (cursor()) {
            <div class="more"><button sdButton size="sm" [loading]="loading()" (click)="load(true)">Load more</button></div>
          }
        }
      </div>
    </div>
  `,
  styles: `
    ${SAAS_CSS}
    .nowrap { white-space: nowrap; }
    code { font-family: var(--sd-font-mono); font-size: var(--sd-text-xs); }
    a { color: var(--sd-text); }
  `,
})
export class PlatformAuditPage implements OnInit {
  private readonly api = inject(PlatformApi);
  protected search = '';
  protected readonly action = signal('');
  protected readonly actions = signal<string[]>([]);
  protected readonly rows = signal<PlatformAuditEntryDto[] | null>(null);
  protected readonly cursor = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly verifying = signal(false);
  protected readonly verification = signal<PlatformChainVerificationDto | null>(null);
  protected readonly humanize = humanize;

  ngOnInit(): void {
    this.load(false);
    this.api.auditActions().subscribe({ next: (a) => this.actions.set(a), error: () => undefined });
  }

  protected load(more: boolean): void {
    this.loading.set(true);
    this.error.set(null);
    if (!more) this.rows.set(null);
    this.api.audit({ search: this.search, action: this.action() || null, cursor: more ? this.cursor() : null, limit: 50 }).subscribe({
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

  protected verify(): void {
    this.verifying.set(true);
    this.api.verifyAudit().subscribe({
      next: (v) => {
        this.verification.set(v);
        this.verifying.set(false);
      },
      error: () => this.verifying.set(false),
    });
  }
}
