import { ChangeDetectionStrategy, Component, computed, inject, input, linkedSignal, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import {
  ContractDetailDto,
  ContractListItemDto,
  ContractsApi,
  EligibilityPreviewDto,
  EntitlementDto,
  Guid,
  InstallationListItemDto,
  MeStore,
  saveErrorMessage,
} from '@sd/core';
import { SdBadge, SdButton, SdCard, SdDialogService, SdEmptyState, SdIcon, SdInput, ToastService } from '@sd/ui';
import { ContractFormData, ContractFormDialog } from '../products/contract-form.dialog';
import { DateOnlyPipe, HumanizePipe, MoneyPipe, statusTone, urgencyTone } from './shared/customer-ui';
import { SECTION_STYLES, TABLE_STYLES } from './shared/styles';

/** Contracts, warranties and entitlements (with consumption) plus an eligibility preview — Customer 360 tab. */
@Component({
  selector: 'desk-c360-contracts',
  imports: [FormsModule, RouterLink, SdBadge, SdButton, SdCard, SdEmptyState, SdIcon, SdInput, DateOnlyPipe, HumanizePipe, MoneyPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-card title="Check support eligibility" subtitle="What a new ticket for this customer would get right now." class="elig">
      <div class="elig-row">
        <select sdInput size="sm" aria-label="Installation" [ngModel]="installationId()" (ngModelChange)="installationId.set($event)">
          <option [ngValue]="null">No specific product (ticket without product)</option>
          @for (i of installations(); track i.id) {
            <option [ngValue]="i.id">{{ i.name }} — {{ i.productName }} {{ i.version }}</option>
          }
        </select>
        <button sdButton size="sm" icon="shield-check" [loading]="checking()" (click)="check()">Check</button>
      </div>
      @if (eligibility(); as e) {
        <div class="elig-result" role="status">
          <sd-badge [tone]="e.status === 'Entitled' ? 'success' : e.status === 'Expired' ? 'danger' : 'warning'" [icon]="e.status === 'Entitled' ? 'circle-check' : 'circle-alert'">
            {{ e.status | deskHumanize }}
          </sd-badge>
          <span>{{ e.note }}</span>
          @if (e.slaPolicyName) {
            <span class="sd-small sd-muted">SLA: {{ e.slaPolicyName }}</span>
          }
          @if (e.isBillable) {
            <sd-badge tone="info" size="sm">Billable</sd-badge>
          }
        </div>
      }
    </sd-card>

    <div class="section-head">
      <h2>Contracts &amp; warranties</h2>
      <span class="sd-spacer"></span>
      @if (me.can('contracts.manage')) {
        <button sdButton size="sm" icon="plus" (click)="create()">New contract</button>
      }
    </div>
    @if (contracts().length === 0) {
      <sd-empty-state icon="file-text" title="No contracts" message="Without an active contract or warranty, tickets are marked not entitled." compact />
    } @else {
      <div class="dt-wrap">
        <table class="dt">
          <thead>
            <tr>
              <th scope="col">Contract</th>
              <th scope="col">Type</th>
              <th scope="col">Status</th>
              <th scope="col">Period</th>
              <th scope="col">Expires</th>
              <th scope="col" class="num">Value</th>
              <th scope="col">Prepaid hours</th>
            </tr>
          </thead>
          <tbody>
            @for (c of contracts(); track c.id) {
              <tr>
                <td>
                  <a [routerLink]="['/products/contracts', c.id]">{{ c.number }}</a>
                  <div class="sd-xs sd-muted">{{ c.name }}</div>
                </td>
                <td>{{ c.type | deskHumanize }}</td>
                <td><sd-badge [tone]="statusTone(c.status)" size="sm" dot>{{ c.status }}</sd-badge></td>
                <td class="nowrap sd-small">{{ c.startDate | deskDate }} – {{ c.endDate | deskDate }}</td>
                <td class="nowrap">
                  @if (c.status === 'Active') {
                    <sd-badge [tone]="urgencyTone(c.daysToExpiry)" size="sm">{{ c.daysToExpiry }} days</sd-badge>
                  } @else {
                    <span class="sd-muted">—</span>
                  }
                  @if (c.autoRenew) {
                    <sd-icon name="refresh" [size]="13" class="renew" aria-label="Auto-renews" />
                  }
                </td>
                <td class="num">{{ c.contractValue | deskMoney: c.currency }}</td>
                <td>
                  @if (c.prepaidHours) {
                    <div class="usage">
                      <div class="bar" [class.is-warn]="pct(c.consumedHours, c.prepaidHours) > 75" [class.is-danger]="pct(c.consumedHours, c.prepaidHours) >= 100">
                        <span [style.width.%]="min(100, pct(c.consumedHours, c.prepaidHours))"></span>
                      </div>
                      <span class="sd-xs sd-muted">{{ c.consumedHours }} / {{ c.prepaidHours }} h</span>
                    </div>
                  } @else {
                    <span class="sd-muted">—</span>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }

    <div class="section-head">
      <h2>Entitlements</h2>
    </div>
    @if (entitlements().length === 0) {
      <p class="empty-note">No explicit entitlements — active contracts cover the customer generally.</p>
    } @else {
      <div class="dt-wrap">
        <table class="dt">
          <thead>
            <tr>
              <th scope="col">Entitlement</th>
              <th scope="col">Contract</th>
              <th scope="col">Covers</th>
              <th scope="col">Valid</th>
              <th scope="col">Consumption</th>
            </tr>
          </thead>
          <tbody>
            @for (e of entitlements(); track e.id) {
              <tr [class.inactive]="!e.isActive">
                <td>
                  {{ e.name }}
                  <div class="sd-xs sd-muted">{{ e.type | deskHumanize }}</div>
                </td>
                <td><a [routerLink]="['/products/contracts', e.contractId]">{{ e.contractNumber }}</a></td>
                <td class="sd-small">{{ covers(e) }}</td>
                <td class="nowrap sd-small">{{ e.startDate | deskDate }} – {{ e.endDate | deskDate }}</td>
                <td>
                  @if (e.usagePercent !== null) {
                    <div class="usage">
                      <div class="bar" [class.is-warn]="e.usagePercent > 75" [class.is-danger]="e.exhausted">
                        <span [style.width.%]="min(100, e.usagePercent)"></span>
                      </div>
                      <span class="sd-xs sd-muted">
                        @if (e.hoursAllowed !== null) {
                          {{ e.hoursUsed }} / {{ e.hoursAllowed }} h
                        } @else {
                          {{ e.incidentsUsed }} / {{ e.incidentsAllowed }} incidents
                        }
                      </span>
                    </div>
                  } @else {
                    <span class="sd-small sd-muted">Unlimited</span>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styles: [
    TABLE_STYLES,
    SECTION_STYLES,
    `
      .elig-row {
        display: flex;
        gap: var(--sd-space-2);
        flex-wrap: wrap;
      }
      .elig-row select {
        max-width: 420px;
      }
      .elig-result {
        display: flex;
        align-items: center;
        gap: var(--sd-space-2);
        flex-wrap: wrap;
        margin-top: var(--sd-space-3);
      }
      .usage {
        display: grid;
        gap: 2px;
        min-width: 120px;
      }
      .renew {
        margin-left: 6px;
        color: var(--sd-text-3);
      }
      tr.inactive td {
        opacity: 0.6;
      }
    `,
  ],
})
export class C360ContractsComponent {
  protected readonly me = inject(MeStore);
  private readonly api = inject(ContractsApi);
  private readonly dialog = inject(SdDialogService);
  private readonly toast = inject(ToastService);

  readonly customerId = input.required<Guid>();
  readonly customerName = input<string>('');
  readonly contracts = input<ContractListItemDto[]>([]);
  readonly entitlements = input<EntitlementDto[]>([]);
  readonly installations = input<InstallationListItemDto[]>([]);
  readonly changed = output<void>();

  protected readonly statusTone = statusTone;
  protected readonly urgencyTone = urgencyTone;
  protected readonly installationId = linkedSignal<Guid | null>(() => this.installations().find((i) => i.environment === 'Production')?.id ?? null);
  protected readonly eligibility = signal<EligibilityPreviewDto | null>(null);
  protected readonly checking = signal(false);
  protected readonly min = Math.min;

  protected readonly selectedInstallation = computed(() => this.installations().find((i) => i.id === this.installationId()) ?? null);

  protected pct(used: number, allowed: number | null): number {
    return allowed ? Math.round((used / allowed) * 100) : 0;
  }

  protected covers(e: EntitlementDto): string {
    return [e.productName, e.installationName, e.assetName].filter(Boolean).join(' · ') || 'Customer-wide';
  }

  protected check(): void {
    const inst = this.selectedInstallation();
    this.checking.set(true);
    this.api.eligibility(this.customerId(), inst?.productId ?? null, inst?.id ?? null, null).subscribe({
      next: (e) => {
        this.eligibility.set(e);
        this.checking.set(false);
      },
      error: (err: unknown) => {
        this.checking.set(false);
        this.toast.error('Eligibility check failed', saveErrorMessage(err));
      },
    });
  }

  protected create(): void {
    this.dialog
      .open<ContractDetailDto, ContractFormData>(ContractFormDialog, {
        data: { customerId: this.customerId(), customerName: this.customerName() },
        size: 'lg',
        disableClose: true,
      })
      .closed.subscribe((saved) => {
        if (!saved) return;
        this.toast.success('Contract created', `${saved.summary.number} — open tickets are being re-evaluated.`);
        this.changed.emit();
      });
  }
}
