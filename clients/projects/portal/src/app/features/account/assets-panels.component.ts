import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { PortalInstallationDto, PortalLicenceDto, PortalServiceActivityDto } from '@sd/core';
import { SdBadge, SdDateTimePipe, SdEmptyState, SdIcon } from '@sd/ui';
import { PortalDatePipe } from '../../shared/portal-utils';
import { humanize, recordTone } from './account-utils';

/** Products & licences the customer holds (keys are masked by the server). */
@Component({
  selector: 'portal-licences-panel',
  imports: [SdBadge, SdEmptyState, PortalDatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (licences().length) {
      <div class="table-wrap">
        <table>
          <caption class="sd-visually-hidden">
            Products and licences
          </caption>
          <thead>
            <tr>
              <th scope="col">Product</th>
              <th scope="col">Seats</th>
              <th scope="col">Licence key</th>
              <th scope="col">Issued</th>
              <th scope="col">Expires</th>
              <th scope="col">Status</th>
            </tr>
          </thead>
          <tbody>
            @for (l of licences(); track l.id) {
              <tr>
                <td>
                  <span class="strong">{{ l.productName }}</span>
                  @if (l.edition || l.installationName) {
                    <span class="muted">{{ l.edition }}{{ l.edition && l.installationName ? ' · ' : '' }}{{ l.installationName }}</span>
                  }
                </td>
                <td class="num">{{ l.seats }}</td>
                <td><code>{{ l.keyMasked }}</code></td>
                <td class="nowrap">{{ l.issuedOn | portalDate }}</td>
                <td class="nowrap">{{ l.expiresOn ? (l.expiresOn | portalDate) : 'Perpetual' }}</td>
                <td><sd-badge size="sm" [tone]="tone(l.status)" dot>{{ humanize(l.status) }}</sd-badge></td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    } @else {
      <sd-empty-state icon="package" title="No licences" message="No product licences are registered for your organisation." compact />
    }
  `,
  styles: `
    .table-wrap {
      overflow-x: auto;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    table {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--sd-text-sm);
    }
    th {
      padding: 10px 14px;
      border-bottom: 1px solid var(--sd-border);
      background: var(--sd-surface-2);
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      text-align: left;
      white-space: nowrap;
    }
    td {
      padding: 11px 14px;
      border-bottom: 1px solid var(--sd-divider);
      vertical-align: top;
    }
    tbody tr:last-child td {
      border-bottom: 0;
    }
    .strong {
      display: block;
      font-weight: var(--sd-weight-medium);
    }
    .muted {
      display: block;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    code {
      font-family: var(--sd-font-mono);
      font-size: var(--sd-text-xs);
      white-space: nowrap;
    }
    .num {
      font-variant-numeric: tabular-nums;
    }
    .nowrap {
      white-space: nowrap;
    }
  `,
})
export class LicencesPanelComponent {
  readonly licences = input.required<PortalLicenceDto[]>();
  protected readonly humanize = humanize;
  protected tone(status: string): ReturnType<typeof recordTone> {
    return recordTone(status);
  }
}

/** The customer's installations (product, version, environment, location, go-live). */
@Component({
  selector: 'portal-installations-panel',
  imports: [SdBadge, SdIcon, SdEmptyState, PortalDatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (installations().length) {
      <ul class="grid">
        @for (i of installations(); track i.id) {
          <li class="card">
            <div class="head">
              <span class="ic" aria-hidden="true"><sd-icon name="server" [size]="18" /></span>
              <div class="titles">
                <h3>{{ i.name }}</h3>
                <p class="muted">{{ i.productName }}{{ i.version ? ' ' + i.version : '' }}{{ i.code ? ' · ' + i.code : '' }}</p>
              </div>
              <sd-badge size="sm" [tone]="tone(i.status)" dot>{{ humanize(i.status) }}</sd-badge>
            </div>
            <dl class="facts">
              <dt>Environment</dt>
              <dd>{{ humanize(i.environment) }}</dd>
              <dt>Deployment</dt>
              <dd>{{ humanize(i.deploymentMode) }}</dd>
              @if (i.locationName) {
                <dt>Location</dt>
                <dd>{{ i.locationName }}</dd>
              }
              @if (i.goLiveOn) {
                <dt>Live since</dt>
                <dd>{{ i.goLiveOn | portalDate }}</dd>
              }
              @if (i.userCount !== null) {
                <dt>Users</dt>
                <dd>{{ i.userCount }}</dd>
              }
            </dl>
          </li>
        }
      </ul>
    } @else {
      <sd-empty-state icon="server" title="No installations" message="No installations are registered for your organisation." compact />
    }
  `,
  styles: `
    .grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 300px), 1fr));
      gap: 14px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .card {
      display: grid;
      gap: 12px;
      padding: 16px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    .head {
      display: flex;
      align-items: flex-start;
      gap: 10px;
    }
    .ic {
      display: inline-flex;
      padding: 8px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    .titles {
      flex: 1;
      min-width: 0;
    }
    h3 {
      font-size: var(--sd-text-md);
      overflow-wrap: anywhere;
    }
    .muted {
      margin: 2px 0 0;
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
    .facts {
      display: grid;
      grid-template-columns: max-content minmax(0, 1fr);
      gap: 6px 14px;
      margin: 0;
      font-size: var(--sd-text-sm);
    }
    dt {
      color: var(--sd-text-3);
    }
    dd {
      margin: 0;
    }
  `,
})
export class InstallationsPanelComponent {
  readonly installations = input.required<PortalInstallationDto[]>();
  protected readonly humanize = humanize;
  protected tone(status: string): ReturnType<typeof recordTone> {
    return recordTone(status);
  }
}

/** Scheduled service activities (maintenance visits, upgrades…): upcoming first, then past with outcomes. */
@Component({
  selector: 'portal-maintenance-panel',
  imports: [SdBadge, SdIcon, SdEmptyState, SdDateTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (activities().length) {
      @for (group of groups(); track group.title) {
        @if (group.items.length) {
          <section class="group" [attr.aria-label]="group.title">
            <h3>{{ group.title }} <span class="count">{{ group.items.length }}</span></h3>
            <ol class="list">
              @for (a of group.items; track a.id) {
                <li class="item" [class.is-past]="group.past">
                  <div class="date" aria-hidden="true">
                    <span class="day">{{ day(a.scheduledStart) }}</span>
                    <span class="month">{{ month(a.scheduledStart) }}</span>
                  </div>
                  <div class="body">
                    <div class="row">
                      <span class="title">{{ a.title }}</span>
                      <sd-badge size="sm">{{ humanize(a.type) }}</sd-badge>
                      <sd-badge size="sm" [tone]="tone(a.status)" dot>{{ humanize(a.status) }}</sd-badge>
                    </div>
                    <p class="when">
                      <sd-icon name="calendar-clock" [size]="13" />{{ a.scheduledStart | sdDateTime }} – {{ a.scheduledEnd | sdDateTime }}
                    </p>
                    @if (a.locationName || a.installationName) {
                      <p class="where">
                        <sd-icon name="map-pin" [size]="13" />{{ a.installationName }}{{ a.installationName && a.locationName ? ' · ' : '' }}{{ a.locationName }}
                      </p>
                    }
                    @if (a.description) {
                      <p class="desc">{{ a.description }}</p>
                    }
                    @if (group.past && a.outcome) {
                      <p class="outcome"><strong>Outcome:</strong> {{ a.outcome }}</p>
                    }
                  </div>
                </li>
              }
            </ol>
          </section>
        }
      }
    } @else {
      <sd-empty-state icon="calendar-clock" title="No scheduled service" message="Planned maintenance and service visits will appear here." compact />
    }
  `,
  styles: `
    .group + .group {
      margin-top: 22px;
    }
    h3 {
      display: flex;
      align-items: center;
      gap: 8px;
      margin-bottom: 10px;
      font-size: var(--sd-text-md);
    }
    .count {
      padding: 0 8px;
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface-3);
      color: var(--sd-text-2);
      font-size: var(--sd-text-xs);
      line-height: 20px;
    }
    .list {
      display: grid;
      gap: 10px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .item {
      display: flex;
      gap: 14px;
      padding: 14px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    .item.is-past {
      background: var(--sd-surface-2);
    }
    .date {
      display: grid;
      justify-items: center;
      align-content: center;
      flex: none;
      width: 54px;
      height: 58px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    .is-past .date {
      background: var(--sd-surface-3);
      color: var(--sd-text-2);
    }
    .day {
      font-size: var(--sd-text-xl);
      font-weight: var(--sd-weight-bold);
      line-height: 1;
    }
    .month {
      font-size: var(--sd-text-xs);
      text-transform: uppercase;
      letter-spacing: 0.06em;
    }
    .body {
      display: grid;
      gap: 4px;
      min-width: 0;
    }
    .row {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
    }
    .title {
      font-weight: var(--sd-weight-semibold);
    }
    .when,
    .where,
    .desc,
    .outcome {
      display: flex;
      align-items: center;
      gap: 5px;
      margin: 0;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .desc,
    .outcome {
      display: block;
    }
  `,
})
export class MaintenancePanelComponent {
  readonly activities = input.required<PortalServiceActivityDto[]>();
  protected readonly humanize = humanize;

  protected readonly groups = computed(() => {
    const now = Date.now();
    const upcoming = this.activities()
      .filter((a) => Date.parse(a.scheduledEnd) >= now)
      .sort((a, b) => Date.parse(a.scheduledStart) - Date.parse(b.scheduledStart));
    const past = this.activities()
      .filter((a) => Date.parse(a.scheduledEnd) < now)
      .sort((a, b) => Date.parse(b.scheduledStart) - Date.parse(a.scheduledStart));
    return [
      { title: 'Upcoming', items: upcoming, past: false },
      { title: 'Past', items: past, past: true },
    ];
  });

  protected tone(status: string): ReturnType<typeof recordTone> {
    return recordTone(status);
  }

  protected day(iso: string): string {
    const d = new Date(iso);
    return Number.isNaN(d.getTime()) ? '' : String(d.getDate());
  }

  protected month(iso: string): string {
    const d = new Date(iso);
    return Number.isNaN(d.getTime()) ? '' : d.toLocaleString(undefined, { month: 'short' });
  }
}
