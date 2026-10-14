import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { PortalContractDto } from '@sd/core';
import { SdBadge, SdEmptyState, SdIcon } from '@sd/ui';
import { PortalDatePipe } from '../../shared/portal-utils';
import { humanize, recordTone } from './account-utils';

/** Support contracts: status, period, days remaining, prepaid-hours consumption and entitlement usage. */
@Component({
  selector: 'portal-contracts-panel',
  imports: [SdBadge, SdIcon, SdEmptyState, PortalDatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (contracts().length) {
      <div class="list">
        @for (c of contracts(); track c.id) {
          <article class="contract" [attr.aria-labelledby]="'contract-' + c.id">
            <header class="head">
              <div class="titles">
                <h3 [id]="'contract-' + c.id">{{ c.name }}</h3>
                <p class="muted">{{ c.number }} · {{ humanize(c.type) }}</p>
              </div>
              <sd-badge [tone]="tone(c.status)" dot>{{ humanize(c.status) }}</sd-badge>
            </header>

            <dl class="facts">
              <div>
                <dt>Period</dt>
                <dd>{{ c.startDate | portalDate }} – {{ c.endDate | portalDate }}</dd>
              </div>
              <div>
                <dt>Time left</dt>
                <dd [class.warn]="c.daysRemaining >= 0 && c.daysRemaining <= 30" [class.over]="c.daysRemaining < 0">
                  @if (c.daysRemaining > 0) {
                    {{ c.daysRemaining }} {{ c.daysRemaining === 1 ? 'day' : 'days' }}
                  } @else if (c.daysRemaining === 0) {
                    Ends today
                  } @else {
                    Ended {{ -c.daysRemaining }} days ago
                  }
                </dd>
              </div>
              <div>
                <dt>Renewal</dt>
                <dd>
                  @if (c.autoRenew) {
                    Renews automatically{{ c.renewalDate ? ' on ' : '' }}{{ c.renewalDate | portalDate }}
                  } @else if (c.renewalDate) {
                    Renewal due {{ c.renewalDate | portalDate }}
                  } @else {
                    Manual renewal
                  }
                </dd>
              </div>
              <div>
                <dt>On-site support</dt>
                <dd>{{ c.onsite ? 'Included' : 'Not included' }}</dd>
              </div>
            </dl>

            @if (c.coveredChannels.length) {
              <p class="channels">
                <span class="muted">Covered channels:</span>
                @for (ch of c.coveredChannels; track ch) {
                  <sd-badge size="sm">{{ humanize(ch) }}</sd-badge>
                }
              </p>
            }

            @if (c.prepaidHours !== null) {
              <div class="usage">
                <div class="usage-head">
                  <span><sd-icon name="hourglass" [size]="14" />Prepaid hours</span>
                  <span class="num">{{ c.consumedHours }} of {{ c.prepaidHours }} h used</span>
                </div>
                <div
                  class="bar"
                  role="progressbar"
                  aria-label="Prepaid hours used"
                  aria-valuemin="0"
                  aria-valuemax="100"
                  [attr.aria-valuenow]="percent(c)"
                  [attr.aria-valuetext]="c.consumedHours + ' of ' + c.prepaidHours + ' hours used'"
                >
                  <span [style.width.%]="percent(c)" [class.warn]="percent(c) >= 80" [class.over]="percent(c) >= 100"></span>
                </div>
              </div>
            }

            @if (c.entitlements.length) {
              <div class="table-wrap">
                <table>
                  <caption>
                    Entitlements
                  </caption>
                  <thead>
                    <tr>
                      <th scope="col">Entitlement</th>
                      <th scope="col">Covers</th>
                      <th scope="col">Incidents</th>
                      <th scope="col">Hours</th>
                      <th scope="col">Valid</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (e of c.entitlements; track e.id) {
                      <tr [class.inactive]="!e.isActive">
                        <td>
                          <span class="strong">{{ e.name }}</span>
                          <span class="muted small">{{ humanize(e.type) }}{{ e.isActive ? '' : ' · inactive' }}</span>
                        </td>
                        <td>{{ e.installationName || e.productName || 'All products' }}</td>
                        <td class="num">{{ e.incidentsUsed }} / {{ e.incidentsAllowed ?? '∞' }}</td>
                        <td class="num">{{ e.hoursUsed }} / {{ e.hoursAllowed ?? '∞' }}</td>
                        <td class="nowrap">{{ e.startDate | portalDate: 'short' }} – {{ e.endDate | portalDate: 'short' }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          </article>
        }
      </div>
    } @else {
      <sd-empty-state icon="file-text" title="No contracts" message="Your organisation has no support contracts on record." compact />
    }
  `,
  styles: `
    .list {
      display: grid;
      gap: 16px;
    }
    .contract {
      display: grid;
      gap: 14px;
      padding: 18px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    .head {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: 12px;
    }
    h3 {
      font-size: var(--sd-text-lg);
    }
    .muted {
      margin: 0;
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
    .small {
      display: block;
      font-size: var(--sd-text-xs);
    }
    .facts {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 180px), 1fr));
      gap: 10px 16px;
      margin: 0;
    }
    dt {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    dd {
      margin: 2px 0 0;
      font-weight: var(--sd-weight-medium);
    }
    dd.warn {
      color: var(--sd-warning);
    }
    dd.over {
      color: var(--sd-danger);
    }
    .channels {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
      margin: 0;
    }
    .usage {
      display: grid;
      gap: 6px;
    }
    .usage-head {
      display: flex;
      justify-content: space-between;
      gap: 8px;
      font-size: var(--sd-text-sm);
    }
    .usage-head span:first-child {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      font-weight: var(--sd-weight-medium);
    }
    .bar {
      height: 8px;
      border-radius: 8px;
      background: var(--sd-surface-3);
      overflow: hidden;
    }
    .bar span {
      display: block;
      height: 100%;
      border-radius: 8px;
      background: var(--sd-primary);
    }
    .bar span.warn {
      background: var(--sd-warning);
    }
    .bar span.over {
      background: var(--sd-danger);
    }
    .table-wrap {
      overflow-x: auto;
    }
    table {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--sd-text-sm);
    }
    caption {
      padding-bottom: 6px;
      font-weight: var(--sd-weight-semibold);
      text-align: left;
    }
    th {
      padding: 8px 10px;
      border-bottom: 1px solid var(--sd-border);
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      text-align: left;
      white-space: nowrap;
    }
    td {
      padding: 9px 10px;
      border-bottom: 1px solid var(--sd-divider);
      vertical-align: top;
    }
    tr.inactive td {
      color: var(--sd-text-3);
    }
    .strong {
      font-weight: var(--sd-weight-medium);
    }
    .num {
      font-variant-numeric: tabular-nums;
      white-space: nowrap;
    }
    .nowrap {
      white-space: nowrap;
    }
  `,
})
export class ContractsPanelComponent {
  readonly contracts = input.required<PortalContractDto[]>();
  protected readonly humanize = humanize;

  protected tone(status: string): ReturnType<typeof recordTone> {
    return recordTone(status);
  }

  protected percent(c: PortalContractDto): number {
    if (c.consumedPercent !== null) return Math.max(0, Math.min(100, Math.round(c.consumedPercent)));
    return c.prepaidHours ? Math.max(0, Math.min(100, Math.round((c.consumedHours / c.prepaidHours) * 100))) : 0;
  }
}
