import { ChangeDetectionStrategy, Component, computed, effect, inject, input, linkedSignal, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { PortalAccountOverviewDto, PortalAuthService, PortalTicketsApi } from '@sd/core';
import { SdAvatar, SdBadge, SdSkeleton, SdTab, SdTabs } from '@sd/ui';
import { BrandService } from '../../brand.service';
import { BreadcrumbComponent } from '../../shared/breadcrumb.component';
import { ErrorStateComponent } from '../../shared/error-state.component';
import { SupportContactsComponent } from '../../shared/support-contacts.component';
import { scopeLabel } from './account-utils';
import { InstallationsPanelComponent, LicencesPanelComponent, MaintenancePanelComponent } from './assets-panels.component';
import { ContractsPanelComponent } from './contracts-panel.component';
import { NotificationsTabComponent } from './notifications-tab.component';
import { PasswordTabComponent } from './password-tab.component';
import { ProfileTabComponent } from './profile-tab.component';

const OVERVIEW_TABS = new Set(['contracts', 'products', 'installations', 'maintenance']);

/**
 * The signed-in contact's account: profile, notification preferences, password, support contacts and —
 * for contacts allowed to see them — contracts, products & licences, installations and the maintenance
 * schedule (one overview request, loaded the first time one of those tabs opens). `?tab=` deep-links.
 */
@Component({
  selector: 'portal-account-page',
  imports: [
    SdTabs,
    SdTab,
    SdAvatar,
    SdBadge,
    SdSkeleton,
    BreadcrumbComponent,
    ErrorStateComponent,
    SupportContactsComponent,
    ProfileTabComponent,
    NotificationsTabComponent,
    PasswordTabComponent,
    ContractsPanelComponent,
    LicencesPanelComponent,
    InstallationsPanelComponent,
    MaintenancePanelComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="portal-container portal-page">
      <portal-breadcrumb [items]="[{ label: 'Home', link: brand.base() }, { label: 'Account' }]" />
      @if (auth.contact(); as c) {
        <header class="head">
          <sd-avatar [name]="c.fullName" size="lg" />
          <div class="who">
            <h1>{{ c.fullName }}</h1>
            <p class="muted">{{ c.email }}</p>
            <p class="tags">
              @if (c.customerName) {
                <sd-badge icon="building">{{ c.customerName }}</sd-badge>
              }
              @if (c.locationName) {
                <sd-badge icon="map-pin">{{ c.locationName }}</sd-badge>
              }
              <sd-badge tone="primary" icon="ticket">{{ access(c.accessScope) }}</sd-badge>
            </p>
          </div>
        </header>

        <sd-tabs [selected]="tab()" (selectedChange)="select($event)" ariaLabel="Account sections">
          <sd-tab id="profile" label="Profile" icon="user"><portal-profile-tab /></sd-tab>
          <sd-tab id="notifications" label="Notifications" icon="bell"><portal-notifications-tab /></sd-tab>
          <sd-tab id="password" label="Password" icon="key"><portal-password-tab /></sd-tab>
          @if (c.canViewContracts) {
            <sd-tab id="contracts" label="Contracts" icon="file-text">
              @if (overviewRes.error()) {
                <portal-error-state [error]="overviewRes.error()" compact (retry)="overviewRes.reload()" />
              } @else if (overview(); as o) {
                <portal-contracts-panel [contracts]="o.contracts" />
              } @else {
                <sd-skeleton height="220px" radius="var(--sd-radius-lg)" />
              }
            </sd-tab>
            <sd-tab id="products" label="Products & licences" icon="package">
              @if (overviewRes.error()) {
                <portal-error-state [error]="overviewRes.error()" compact (retry)="overviewRes.reload()" />
              } @else if (overview(); as o) {
                <portal-licences-panel [licences]="o.licences" />
              } @else {
                <sd-skeleton height="180px" radius="var(--sd-radius-lg)" />
              }
            </sd-tab>
            <sd-tab id="installations" label="Installations" icon="server">
              @if (overviewRes.error()) {
                <portal-error-state [error]="overviewRes.error()" compact (retry)="overviewRes.reload()" />
              } @else if (overview(); as o) {
                <portal-installations-panel [installations]="o.installations" />
              } @else {
                <sd-skeleton height="180px" radius="var(--sd-radius-lg)" />
              }
            </sd-tab>
            <sd-tab id="maintenance" label="Maintenance" icon="calendar-clock">
              @if (overviewRes.error()) {
                <portal-error-state [error]="overviewRes.error()" compact (retry)="overviewRes.reload()" />
              } @else if (overview(); as o) {
                <portal-maintenance-panel [activities]="o.activities" />
              } @else {
                <sd-skeleton height="180px" radius="var(--sd-radius-lg)" />
              }
            </sd-tab>
          }
          <sd-tab id="support" label="Support contacts" icon="headphones"><portal-support-contacts /></sd-tab>
        </sd-tabs>
      }
    </div>
  `,
  styles: `
    .head {
      display: flex;
      align-items: center;
      gap: 16px;
      margin: 14px 0 24px;
    }
    .who {
      min-width: 0;
    }
    h1 {
      font-size: clamp(1.4rem, 3vw, 1.8rem);
      font-weight: var(--sd-weight-bold);
      overflow-wrap: anywhere;
    }
    .muted {
      margin: 2px 0 0;
      color: var(--sd-text-2);
    }
    .tags {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      margin: 8px 0 0;
    }
  `,
})
export class AccountPage {
  /** `?tab=contracts` etc. */
  readonly tabParam = input<string | undefined>(undefined, { alias: 'tab' });

  protected readonly brand = inject(BrandService);
  protected readonly auth = inject(PortalAuthService);
  private readonly api = inject(PortalTicketsApi);
  private readonly router = inject(Router);

  protected readonly tab = linkedSignal(() => this.tabParam() || 'profile');
  private readonly overviewWanted = signal(false);
  protected readonly overviewRes = rxResource({
    params: () => {
      const slug = this.brand.slug();
      return slug && this.overviewWanted() && this.auth.contact()?.canViewContracts ? slug : undefined;
    },
    stream: ({ params }) => this.api.overview(params),
  });
  protected readonly overview = computed<PortalAccountOverviewDto | null>(() => (this.overviewRes.hasValue() ? this.overviewRes.value() : null));

  constructor() {
    effect(() => {
      if (OVERVIEW_TABS.has(this.tab())) this.overviewWanted.set(true);
    });
  }

  protected access(scope: string): string {
    return scopeLabel(scope);
  }

  protected select(tab: string | null): void {
    const id = tab || 'profile';
    this.tab.set(id);
    void this.router.navigate([], { queryParams: { tab: id === 'profile' ? null : id }, queryParamsHandling: 'merge', replaceUrl: true });
  }
}
