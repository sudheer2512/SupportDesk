import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiError, PlatformApi, PlatformAuditEntryDto, PlatformOverviewDto, PlatformTenantDto } from '@sd/core';
import { SdBadge, SdButton, SdCard, SdDateTimePipe, SdEmptyState, SdKpiTile, SdPageHeader, SdRelativeTimePipe, SdSkeleton } from '@sd/ui';
import { SAAS_CSS, money, tenantTone } from '../subscription/saas-ui';

/** Platform overview: tenant counts, revenue and receivables, trials ending soon and recent platform activity. */
@Component({
  selector: 'desk-platform-overview-page',
  imports: [RouterLink, SdBadge, SdButton, SdCard, SdDateTimePipe, SdEmptyState, SdKpiTile, SdPageHeader, SdRelativeTimePipe, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Overview" icon="dashboard" subtitle="Tenants, revenue and what needs attention.">
        <button sdButton sdPageActions variant="ghost" icon="refresh" [loading]="loading()" (click)="load()">Refresh</button>
      </sd-page-header>
      <div class="desk-page__body">
        @if (error(); as e) {
          <sd-empty-state icon="circle-alert" title="The overview could not be loaded" [message]="e"><button sdButton (click)="load()">Try again</button></sd-empty-state>
        } @else {
          <div class="kpis">
            <sd-kpi-tile label="Tenants" icon="building" [value]="o()?.tenants ?? null" [loading]="!o()" interactive (activate)="go('/platform/tenants')" />
            <sd-kpi-tile label="On trial" icon="hourglass" [value]="o()?.trials ?? null" [loading]="!o()" interactive (activate)="go('/platform/tenants', 'Trial')" />
            <sd-kpi-tile label="Active" icon="circle-check" [value]="o()?.active ?? null" [loading]="!o()" interactive (activate)="go('/platform/tenants', 'Active')" />
            <sd-kpi-tile label="Suspended" icon="octagon-alert" [value]="o()?.suspended ?? null" [loading]="!o()" interactive (activate)="go('/platform/tenants', 'Suspended')" />
            <sd-kpi-tile label="Monthly recurring revenue" icon="trending-up" [value]="o() ? money(o()!.monthlyRecurringRevenue, o()!.currency) : null" [loading]="!o()" />
            <sd-kpi-tile label="Open receivables" icon="wallet" [value]="o() ? money(o()!.openReceivables, o()!.currency) : null" [loading]="!o()" interactive (activate)="go('/platform/invoices')" />
            <sd-kpi-tile label="Overdue invoices" icon="receipt" [value]="o()?.overdueInvoices ?? null" [loading]="!o()" interactive (activate)="go('/platform/invoices')" />
            <sd-kpi-tile label="Support requests pending" icon="life-buoy" [value]="o()?.pendingSupportRequests ?? null" [loading]="!o()" interactive (activate)="go('/platform/support')" />
          </div>

          <div class="grid">
            <sd-card title="Trials" subtitle="Newest trial workspaces">
              @if (trials() === null) {
                <sd-skeleton [lines]="4" />
              } @else if (trials()!.length === 0) {
                <sd-empty-state compact icon="hourglass" title="No trials right now" />
              } @else {
                <ul class="list">
                  @for (t of trials(); track t.id) {
                    <li>
                      <a [routerLink]="['/platform/tenants', t.id]">{{ t.name }}</a>
                      <span class="muted small">{{ t.slug }}</span>
                      <span class="sd-spacer"></span>
                      @if (t.trialEndsAt) {
                        <span class="small">ends {{ t.trialEndsAt | sdRelativeTime }}</span>
                      }
                      <sd-badge size="sm" [tone]="tone(t.subscriptionStatus)">{{ t.subscriptionStatus }}</sd-badge>
                    </li>
                  }
                </ul>
              }
            </sd-card>
            <sd-card title="Recent platform activity">
              <div sdCardActions><a sdButton size="sm" variant="ghost" routerLink="/platform/audit">Open audit</a></div>
              @if (activity() === null) {
                <sd-skeleton [lines]="4" />
              } @else if (activity()!.length === 0) {
                <sd-empty-state compact icon="shield-check" title="No activity yet" />
              } @else {
                <ul class="list">
                  @for (a of activity(); track a.id) {
                    <li class="act">
                      <span class="small">{{ a.summary || a.action }}</span>
                      <span class="sd-spacer"></span>
                      <span class="muted small" [title]="a.occurredAt | sdDateTime">{{ a.occurredAt | sdRelativeTime }}</span>
                    </li>
                  }
                </ul>
              }
            </sd-card>
          </div>
        }
      </div>
    </div>
  `,
  styles: `
    ${SAAS_CSS}
    .kpis { display: grid; gap: var(--sd-space-4); grid-template-columns: repeat(auto-fill, minmax(min(100%, 210px), 1fr)); margin-bottom: var(--sd-space-5); }
    .grid { display: grid; gap: var(--sd-space-4); grid-template-columns: repeat(auto-fit, minmax(min(100%, 420px), 1fr)); }
    .list { list-style: none; margin: 0; padding: 0; display: grid; }
    .list li { display: flex; align-items: center; gap: 8px; padding: 8px 0; border-bottom: 1px solid var(--sd-divider); min-width: 0; }
    .list li:last-child { border-bottom: 0; }
    .list a { color: var(--sd-text); font-weight: var(--sd-weight-medium); text-decoration: none; }
    .list a:hover { text-decoration: underline; }
    .act .small { overflow: hidden; text-overflow: ellipsis; }
  `,
})
export class PlatformOverviewPage implements OnInit {
  private readonly api = inject(PlatformApi);
  private readonly router = inject(Router);
  protected readonly o = signal<PlatformOverviewDto | null>(null);
  protected readonly trials = signal<PlatformTenantDto[] | null>(null);
  protected readonly activity = signal<PlatformAuditEntryDto[] | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly money = money;
  protected readonly tone = tenantTone;

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.overview().subscribe({
      next: (o) => {
        this.o.set(o);
        this.loading.set(false);
      },
      error: (e: ApiError) => {
        this.error.set(e.userMessage);
        this.loading.set(false);
      },
    });
    this.api.tenants({ status: 'Trial', limit: 8 }).subscribe({ next: (p) => this.trials.set(p.items), error: () => this.trials.set([]) });
    this.api.audit({ limit: 8 }).subscribe({ next: (p) => this.activity.set(p.items), error: () => this.activity.set([]) });
  }

  protected go(path: string, status?: string): void {
    void this.router.navigate([path], status ? { queryParams: { status } } : {});
  }
}
