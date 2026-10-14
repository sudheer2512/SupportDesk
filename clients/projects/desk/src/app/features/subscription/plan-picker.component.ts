import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError, ChangePlanResultDto, PlanChangePreviewDto, PlanDto, PlanFeatureDto, SubscriptionDto, SubscriptionsApi } from '@sd/core';
import { SdBadge, SdButton, SdFormField, SdIcon, SdInput } from '@sd/ui';
import { SAAS_CSS, bytes, compactNumber, money } from './saas-ui';

/**
 * Plan comparison and change: a comparison table (prices, limits, features), seats and billing cycle, a server-side
 * preview (pro-rata amount, effective date, usage conflicts) and confirmation. Upgrades apply now; downgrades are
 * scheduled for the end of the period and are refused when current usage does not fit.
 */
@Component({
  selector: 'desk-plan-picker',
  imports: [FormsModule, SdBadge, SdButton, SdFormField, SdIcon, SdInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="options">
      <sd-form-field label="Seats (agent licences)" hint="Billed per agent; you are never billed for fewer agents than are active.">
        <input sdInput type="number" min="1" max="100000" name="seats" [ngModel]="seats()" (ngModelChange)="seats.set(+$event || 1); preview.set(null)" />
      </sd-form-field>
      <sd-form-field label="Billing cycle">
        <select sdInput name="cycle" [ngModel]="cycle()" (ngModelChange)="cycle.set($event); preview.set(null)">
          <option value="Monthly">Monthly</option>
          <option value="Annual">Annual (2 months free)</option>
        </select>
      </sd-form-field>
    </div>

    <div class="table-wrap">
      <table class="grid compare">
        <caption class="sd-visually-hidden">Plan comparison</caption>
        <thead>
          <tr>
            <th scope="col">Plan</th>
            @for (p of plans(); track p.id) {
              <th scope="col" [class.current]="p.id === subscription().plan.id">
                {{ p.name }}
                @if (p.id === subscription().plan.id) {
                  <sd-badge size="sm" tone="primary">Current</sd-badge>
                }
              </th>
            }
          </tr>
        </thead>
        <tbody>
          <tr>
            <th scope="row">Price / agent</th>
            @for (p of plans(); track p.id) {
              <td [class.current]="p.id === subscription().plan.id">
                <strong>{{ money(cycle() === 'Annual' ? p.annualPricePerAgent : p.monthlyPricePerAgent, p.currency) }}</strong>
                <span class="muted small">/{{ cycle() === 'Annual' ? 'year' : 'month' }}</span>
              </td>
            }
          </tr>
          @for (row of limitRows; track row.label) {
            <tr>
              <th scope="row">{{ row.label }}</th>
              @for (p of plans(); track p.id) {
                <td [class.current]="p.id === subscription().plan.id" class="sd-num">{{ row.value(p) }}</td>
              }
            </tr>
          }
          @for (f of features(); track f.key) {
            <tr>
              <th scope="row">{{ f.label }}</th>
              @for (p of plans(); track p.id) {
                <td [class.current]="p.id === subscription().plan.id">
                  @if (p.features.includes(f.key)) {
                    <sd-icon name="check" [size]="16" class="yes" /><span class="sd-visually-hidden">Included</span>
                  } @else {
                    <span class="no" aria-label="Not included">—</span>
                  }
                </td>
              }
            </tr>
          }
          <tr>
            <th scope="row"><span class="sd-visually-hidden">Choose</span></th>
            @for (p of plans(); track p.id) {
              <td [class.current]="p.id === subscription().plan.id">
                <button
                  sdButton
                  size="sm"
                  [variant]="selected() === p.code ? 'primary' : 'secondary'"
                  [disabled]="p.code === 'trial' && subscription().plan.code !== 'trial'"
                  [loading]="previewing() && selected() === p.code"
                  [attr.aria-pressed]="selected() === p.code"
                  (click)="choose(p)"
                >
                  {{ p.id === subscription().plan.id ? 'Change seats' : 'Select' }}
                </button>
              </td>
            }
          </tr>
        </tbody>
      </table>
    </div>

    @if (error()) {
      <div class="callout callout--danger" role="alert"><sd-icon name="circle-alert" [size]="16" /><div class="grow">{{ error() }}</div></div>
    }

    @if (preview(); as pv) {
      <div class="preview" role="region" aria-label="Plan change preview">
        <div class="callout" [class]="pv.allowed ? 'callout callout--info' : 'callout callout--warning'">
          <sd-icon [name]="pv.allowed ? 'info' : 'triangle-alert'" [size]="16" />
          <div class="grow">
            <strong>{{ pv.fromPlan }} → {{ pv.toPlan }}</strong> ({{ pv.seats }} seats, {{ pv.billingCycle.toLowerCase() }}). {{ pv.message }}
            @if (pv.proratedAmount > 0) {
              <div>Charged now: <strong>{{ money(pv.proratedAmount, pv.currency) }}</strong> (pro rata).</div>
            }
            <div>Then {{ money(pv.newPeriodAmount, pv.currency) }} per {{ pv.billingCycle === 'Annual' ? 'year' : 'month' }}.</div>
            @if (pv.conflicts.length) {
              <ul class="conflicts">
                @for (c of pv.conflicts; track c.metric) {
                  <li>{{ c.label }}: {{ c.used }} in use, the plan allows {{ c.limit }}.</li>
                }
              </ul>
            }
          </div>
        </div>
        <div class="footer">
          <button sdButton variant="ghost" (click)="preview.set(null)">Back</button>
          <button sdButton variant="primary" [disabled]="!pv.allowed" [loading]="applying()" (click)="apply()">
            {{ pv.immediate ? (pv.direction === 'upgrade' ? 'Upgrade now' : 'Apply now') : 'Schedule change' }}
          </button>
        </div>
      </div>
    }
  `,
  styles: `
    ${SAAS_CSS}
    :host { display: grid; gap: 16px; }
    .options { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(100%, 200px), 1fr)); gap: 12px; }
    .compare th[scope='row'] { font-weight: var(--sd-weight-medium); color: var(--sd-text-2); white-space: nowrap; }
    .compare td, .compare thead th { text-align: center; }
    .compare th:first-child { text-align: left; }
    .current { background: var(--sd-primary-soft); }
    .yes { color: var(--sd-success); }
    .no { color: var(--sd-text-3); }
    .conflicts { margin: 6px 0 0; padding-left: 18px; }
    .preview .callout { margin-bottom: 12px; }
  `,
})
export class PlanPickerComponent {
  private readonly api = inject(SubscriptionsApi);

  readonly subscription = input.required<SubscriptionDto>();
  readonly plans = input<PlanDto[]>([]);
  readonly features = input<PlanFeatureDto[]>([]);
  readonly changed = output<ChangePlanResultDto>();

  protected readonly seats = signal(1);
  protected readonly cycle = signal<'Monthly' | 'Annual'>('Monthly');
  protected readonly selected = signal<string | null>(null);
  protected readonly preview = signal<PlanChangePreviewDto | null>(null);
  protected readonly previewing = signal(false);
  protected readonly applying = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly money = money;
  protected readonly current = computed(() => this.subscription().plan);

  protected readonly limitRows: { label: string; value: (p: PlanDto) => string }[] = [
    { label: 'Agents', value: (p) => this.n(p.maxAgents) },
    { label: 'Light agents', value: (p) => this.n(p.maxLightAgents) },
    { label: 'Portal users', value: (p) => this.n(p.maxPortalUsers) },
    { label: 'Storage', value: (p) => (p.storageMb === null ? 'Unlimited' : bytes(p.storageMb * 1024 * 1024)) },
    { label: 'Channels', value: (p) => this.n(p.maxChannels) },
    { label: 'AI tokens / month', value: (p) => this.n(p.aiTokensMonthly) },
    { label: 'API calls / month', value: (p) => this.n(p.apiCallsMonthly) },
  ];

  /** Called by the host when the drawer opens. */
  reset(): void {
    const sub = this.subscription();
    const agents = sub.usage.find((m) => m.metric === 'agents')?.used ?? 0;
    this.seats.set(Math.max(sub.seats, agents, 1));
    this.cycle.set(sub.billingCycle);
    this.selected.set(null);
    this.preview.set(null);
    this.error.set(null);
  }

  private n(v: number | null): string {
    return v === null ? 'Unlimited' : compactNumber(v);
  }

  protected choose(plan: PlanDto): void {
    this.selected.set(plan.code);
    this.error.set(null);
    this.previewing.set(true);
    this.api.previewChange({ planCode: plan.code, seats: this.seats(), billingCycle: this.cycle() }).subscribe({
      next: (p) => {
        this.preview.set(p);
        this.previewing.set(false);
      },
      error: (e: ApiError) => {
        this.error.set(e.fieldError('seats') ?? e.fieldError('billingCycle') ?? e.userMessage);
        this.previewing.set(false);
      },
    });
  }

  protected apply(): void {
    const code = this.selected();
    if (!code) return;
    this.applying.set(true);
    this.api.changePlan({ planCode: code, seats: this.seats(), billingCycle: this.cycle(), rowVersion: this.subscription().rowVersion }).subscribe({
      next: (r) => {
        this.applying.set(false);
        this.preview.set(null);
        this.changed.emit(r);
      },
      error: (e: ApiError) => {
        this.applying.set(false);
        this.error.set(e.userMessage);
      },
    });
  }
}
