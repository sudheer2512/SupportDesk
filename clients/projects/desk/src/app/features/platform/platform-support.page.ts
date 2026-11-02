import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiError, PlatformApi, SupportAccessDto, SupportDiagnosticsDto } from '@sd/core';
import { SdBadge, SdButton, SdDateTimePipe, SdDrawer, SdEmptyState, SdPageHeader, SdRelativeTimePipe, SdSkeleton } from '@sd/ui';
import { SAAS_CSS, UsageMetersComponent, humanize, requestTone } from '../subscription/saas-ui';

/**
 * Support access across tenants (instead of impersonation): requests awaiting tenant approval, active time-boxed grants
 * (read-only diagnostics) and their history. Requests are created from a tenant's page.
 */
@Component({
  selector: 'desk-platform-support-page',
  imports: [RouterLink, SdBadge, SdButton, SdDateTimePipe, SdDrawer, SdEmptyState, SdPageHeader, SdRelativeTimePipe, SdSkeleton, UsageMetersComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Support access" icon="life-buoy" subtitle="Tenant-approved, time-boxed, read-only — never impersonation.">
        <button sdButton sdPageActions variant="ghost" icon="refresh" (click)="load()">Refresh</button>
      </sd-page-header>
      <div class="desk-page__body">
        @if (error(); as e) {
          <sd-empty-state icon="circle-alert" title="Support access could not be loaded" [message]="e"><button sdButton (click)="load()">Try again</button></sd-empty-state>
        } @else if (rows() === null) {
          <sd-skeleton [lines]="6" height="32px" />
        } @else if (rows()!.length === 0) {
          <sd-empty-state icon="life-buoy" title="No support access requests" message="Open a tenant and choose “Request support access” when a customer asks for help." />
        } @else {
          <div class="table-wrap">
            <table class="data">
              <caption class="sd-visually-hidden">Support access requests</caption>
              <thead><tr><th scope="col">Tenant</th><th scope="col">Operator</th><th scope="col" class="hide-sm">Reason</th><th scope="col">Status</th><th scope="col" class="hide-sm">Requested</th><th scope="col"><span class="sd-visually-hidden">Actions</span></th></tr></thead>
              <tbody>
                @for (g of rows(); track g.id) {
                  <tr>
                    <td><a [routerLink]="['/platform/tenants', g.tenantId]">{{ g.tenantName }}</a></td>
                    <td>{{ g.operatorName }}</td>
                    <td class="hide-sm">{{ g.reason }}</td>
                    <td>
                      <sd-badge size="sm" [tone]="tone(g.status)">{{ g.status }}</sd-badge>
                      @if (g.active && g.expiresAt) {
                        <div class="muted small">ends {{ g.expiresAt | sdRelativeTime }}</div>
                      } @else if (g.decidedByName) {
                        <div class="muted small">by {{ g.decidedByName }}</div>
                      }
                    </td>
                    <td class="hide-sm">{{ g.requestedAt | sdDateTime }}</td>
                    <td class="actions">
                      @if (g.active && g.isMine) {
                        <button sdButton size="sm" icon="eye" (click)="open(g)">Diagnostics</button>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </div>
    </div>

    <sd-drawer [(open)]="drawerOpen" [title]="'Diagnostics — ' + (diag()?.tenantName ?? '')" width="560px">
      @if (diagError()) {
        <p class="err" role="alert">{{ diagError() }}</p>
      } @else if (diag(); as g) {
        <p class="small muted">Access ends {{ g.accessExpiresAt | sdDateTime }}. Viewing is audited in the tenant's trail.</p>
        <desk-usage-meters [meters]="g.usage" />
        <h3 class="section-title">Configuration</h3>
        <dl class="facts">
          @for (c of counts(); track c.key) {
            <dt>{{ humanize(c.key) }}</dt>
            <dd>{{ c.value }}</dd>
          }
          <dt>Failed jobs</dt>
          <dd>{{ g.failedJobs }}</dd>
          <dt>Dead-letter events</dt>
          <dd>{{ g.deadLetterEvents }}</dd>
        </dl>
        <h3 class="section-title">Recent audit</h3>
        <ul class="list">
          @for (a of g.recentAudit; track a.id) {
            <li><span class="muted small">{{ a.occurredAt | sdRelativeTime }}</span> <span class="small">{{ a.summary || a.action }}</span></li>
          }
        </ul>
      } @else {
        <sd-skeleton [lines]="8" />
      }
    </sd-drawer>
  `,
  styles: `
    ${SAAS_CSS}
    a { color: var(--sd-text); }
    .list { list-style: none; margin: 0; padding: 0; display: grid; gap: 6px; }
  `,
})
export class PlatformSupportPage implements OnInit {
  private readonly api = inject(PlatformApi);
  protected readonly rows = signal<SupportAccessDto[] | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly drawerOpen = signal(false);
  protected readonly diag = signal<SupportDiagnosticsDto | null>(null);
  protected readonly diagError = signal<string | null>(null);
  protected readonly tone = requestTone;
  protected readonly humanize = humanize;
  protected readonly counts = computed(() => Object.entries(this.diag()?.configurationCounts ?? {}).map(([key, value]) => ({ key, value })));

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.error.set(null);
    this.api.supportAccess().subscribe({ next: (r) => this.rows.set(r), error: (e: ApiError) => this.error.set(e.userMessage) });
  }

  protected open(g: SupportAccessDto): void {
    this.diag.set(null);
    this.diagError.set(null);
    this.drawerOpen.set(true);
    this.api.diagnostics(g.id).subscribe({ next: (d) => this.diag.set(d), error: (e: ApiError) => this.diagError.set(e.userMessage) });
  }
}
