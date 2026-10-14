import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CustomersApi, DepartmentDto, Guid, MetaApi, OrganisationApi, PRIORITIES, RoutingPreviewDto, TeamDto, saveErrorMessage } from '@sd/core';
import { SdBadge, SdButton, SdCard, SdCombobox, SdFormField, SdInput, SdOption, SdSkeleton } from '@sd/ui';
import { map } from 'rxjs';
import { TABLE_STYLES } from '../customers/shared/styles';
import { FORM_STYLES } from './shared/org-ui';

/** Test-route preview: who would receive a sample ticket now, and why each member is (in)eligible. Nothing is saved. */
@Component({
  selector: 'desk-routing-preview-panel',
  imports: [FormsModule, SdCard, SdFormField, SdInput, SdButton, SdBadge, SdCombobox, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-card title="Test routing" subtitle="Preview the assignment of a sample ticket right now — nothing is created or assigned.">
      <form class="preview-form" (ngSubmit)="run()">
        <sd-form-field label="Department" required>
          <select sdInput name="pd" [ngModel]="dept()" (ngModelChange)="dept.set($event); team = null" required>
            <option [ngValue]="null" disabled>Choose…</option>
            @for (d of departments(); track d.id) {
              <option [ngValue]="d.id">{{ d.name }}</option>
            }
          </select>
        </sd-form-field>
        <sd-form-field label="Team" optional>
          <select sdInput name="pt" [(ngModel)]="team">
            <option [ngValue]="null">Department routing team</option>
            @for (t of teamsOf(dept()); track t.id) {
              <option [ngValue]="t.id">{{ t.name }}</option>
            }
          </select>
        </sd-form-field>
        <sd-form-field label="Customer" optional hint="Territory and dedicated-agent rules use it.">
          <sd-combobox [search]="searchCustomers" [(value)]="customer" placeholder="Search customers…" ariaLabel="Customer" />
        </sd-form-field>
        <sd-form-field label="Product" optional>
          <select sdInput name="pp" [(ngModel)]="product">
            <option [ngValue]="null">—</option>
            @for (p of products(); track p.id) {
              <option [ngValue]="p.id">{{ p.name }}</option>
            }
          </select>
        </sd-form-field>
        <sd-form-field label="Priority">
          <select sdInput name="ppr" [(ngModel)]="priority">
            @for (p of priorities; track p) {
              <option [ngValue]="p">{{ p }}</option>
            }
          </select>
        </sd-form-field>
        <sd-form-field label="Language" optional>
          <input sdInput name="pl" [(ngModel)]="language" maxlength="10" placeholder="en" />
        </sd-form-field>
        <div class="go">
          <button sdButton variant="primary" type="submit" icon="play" [loading]="running()" [disabled]="!dept()">Preview</button>
        </div>
      </form>
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      @if (running() && !result()) {
        <sd-skeleton [lines]="3" />
      }
      @if (result(); as r) {
        <div class="result" aria-live="polite">
          <p>
            @if (r.assigneeName) {
              <sd-badge tone="success" icon="user-check">{{ r.assigneeName }}</sd-badge>
            } @else {
              <sd-badge tone="warning" icon="inbox">Stays in queue</sd-badge>
            }
            <span class="small"> {{ r.teamName ?? 'No team' }} · {{ r.strategy }} — {{ r.reason }}</span>
            @if (r.usedFallback) {
              <sd-badge size="sm" icon="arrow-right">Fallback used</sd-badge>
            }
          </p>
          @if (r.candidates.length > 0) {
            <div class="dt-wrap">
              <table class="dt">
                <caption class="sd-visually-hidden">Routing candidates</caption>
                <thead>
                  <tr>
                    <th scope="col">Agent</th>
                    <th scope="col">Availability</th>
                    <th scope="col" class="num">Load</th>
                    <th scope="col">Shift</th>
                    <th scope="col">Result</th>
                    <th scope="col" class="num">Score</th>
                  </tr>
                </thead>
                <tbody>
                  @for (c of r.candidates; track c.userId + '-' + $index) {
                    <tr>
                      <td>{{ c.name }}</td>
                      <td>{{ c.availability }}</td>
                      <td class="num">{{ c.openTickets }}/{{ c.capacity }}</td>
                      <td>{{ c.onShift ? 'On shift' : 'Off shift' }}{{ c.onLeave ? ' · on leave' : '' }}</td>
                      <td>
                        @if (c.eligible) {
                          <sd-badge size="sm" tone="success">Eligible</sd-badge>
                        } @else {
                          <sd-badge size="sm" tone="neutral">{{ c.excludedReason }}</sd-badge>
                        }
                      </td>
                      <td class="num">{{ c.score ?? '—' }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </div>
      }
    </sd-card>
  `,
  styles: [
    TABLE_STYLES,
    FORM_STYLES,
    `
      :host { display: block; margin-bottom: var(--sd-space-4); }
      .preview-form { display: grid; gap: var(--sd-space-3); grid-template-columns: repeat(auto-fill, minmax(min(100%, 180px), 1fr)); align-items: end; }
      .go { display: flex; align-items: end; }
      .result { margin-top: var(--sd-space-3); display: grid; gap: var(--sd-space-2); }
      .small { font-size: var(--sd-text-sm); }
    `,
  ],
})
export class RoutingPreviewPanel {
  private readonly api = inject(OrganisationApi);
  private readonly meta = inject(MetaApi);
  private readonly customersApi = inject(CustomersApi);
  readonly departments = input<DepartmentDto[]>([]);
  readonly teams = input<TeamDto[]>([]);
  readonly departmentId = input<Guid | null>(null);
  protected readonly dept = signal<Guid | null>(null);
  protected team: Guid | null = null;
  protected customer: string | null = null;
  protected product: Guid | null = null;
  protected priority = 'Medium';
  protected language = '';
  protected readonly priorities = PRIORITIES;
  protected readonly products = computed(() => this.meta.ticketForm()?.products ?? []);
  protected readonly running = signal(false);
  protected readonly result = signal<RoutingPreviewDto | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly searchCustomers = (q: string) =>
    this.customersApi
      .list({ q, limit: 15 })
      .pipe(map((r) => r.items.map((c): SdOption => ({ value: c.id, label: c.name, description: c.serviceRegion ?? c.tier ?? null }))));

  constructor() {
    this.meta.load();
    // Default to the page's department filter (or the first department) until the user picks one.
    effect(() => {
      const preferred = this.departmentId() ?? this.departments()[0]?.id ?? null;
      if (this.dept() === null && preferred) this.dept.set(preferred);
    });
  }

  protected teamsOf(dept: Guid | null): TeamDto[] {
    return this.teams().filter((t) => t.departmentId === dept && t.isActive);
  }

  protected run(): void {
    this.running.set(true);
    this.error.set(null);
    this.api
      .routingPreview({ departmentId: this.dept(), teamId: this.team, customerId: this.customer, productId: this.product, priority: this.priority, language: this.language || null })
      .subscribe({
        next: (r) => {
          this.result.set(r);
          this.running.set(false);
        },
        error: (err: unknown) => {
          this.error.set(saveErrorMessage(err));
          this.running.set(false);
        },
      });
  }
}
