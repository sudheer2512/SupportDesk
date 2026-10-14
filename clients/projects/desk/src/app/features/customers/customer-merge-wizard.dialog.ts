import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { CustomerDetailDto, CustomerListItemDto, CustomerMergeResultDto, CustomersApi, Guid, saveErrorMessage } from '@sd/core';
import { SdBadge, SdButton, SdCheckbox, SdDialog, SdIcon, SdRadio, SdRadioGroup, SdSkeleton } from '@sd/ui';
import { Subject, debounceTime, switchMap } from 'rxjs';
import { TABLE_STYLES } from './shared/styles';

export interface CustomerMergeWizardData {
  /** The customer the wizard was opened from (Customer 360). */
  customer: CustomerDetailDto;
}

interface FieldRow {
  /** Server field name (MergeableFields of CustomerMergeService) or a display-only key. */
  key: string;
  label: string;
  choosable: boolean;
  get: (c: CustomerDetailDto) => string | null;
}

const FIELDS: FieldRow[] = [
  { key: 'name', label: 'Name', choosable: true, get: (c) => c.name },
  { key: 'code', label: 'Code', choosable: true, get: (c) => c.code },
  { key: 'tier', label: 'Tier', choosable: true, get: (c) => c.tier },
  { key: 'category', label: 'Category', choosable: true, get: (c) => c.category },
  { key: 'industry', label: 'Industry', choosable: true, get: (c) => c.industry },
  { key: 'serviceRegion', label: 'Service region', choosable: true, get: (c) => c.serviceRegion },
  { key: 'accountManagerUserId', label: 'Account manager', choosable: true, get: (c) => c.accountManagerName },
  { key: 'customerGroupId', label: 'Group', choosable: true, get: (c) => c.groupName },
  { key: 'preferredLanguage', label: 'Language', choosable: true, get: (c) => c.preferredLanguage },
  { key: 'timeZone', label: 'Time zone', choosable: true, get: (c) => c.timeZone },
  { key: 'website', label: 'Website', choosable: true, get: (c) => c.website },
  { key: 'phone', label: 'Phone', choosable: true, get: (c) => c.phone },
  { key: 'email', label: 'E-mail', choosable: true, get: (c) => c.email },
  { key: 'erpCustomerId', label: 'ERP link', choosable: true, get: (c) => c.erpCode },
  { key: 'domains', label: 'E-mail domains', choosable: false, get: (c) => (c.domains.length ? c.domains.join(', ') : null) },
  { key: 'parent', label: 'Parent', choosable: false, get: (c) => c.parentName },
  { key: 'records', label: 'Records', choosable: false, get: (c) => recordSummary(c) },
];

function recordSummary(c: CustomerDetailDto): string {
  const r = c.rollup;
  return `${r.contacts} contacts · ${r.openTickets} open tickets · ${r.locations} locations · ${r.installations} installations · ${r.activeContracts} contracts`;
}

const MOVED_LABELS: Record<string, string> = {
  contacts: 'contacts',
  tickets: 'tickets',
  locations: 'locations',
  customerDepartments: 'customer departments',
  contracts: 'contracts',
  entitlements: 'entitlements',
  installations: 'installations',
  licences: 'licences',
  assets: 'assets',
  serviceActivities: 'service activities',
  subsidiaries: 'subsidiaries',
  legalHolds: 'legal holds',
  kbAccessRules: 'KB access rules',
};

/**
 * Organisation merge wizard (POST /customers/merge, customers.merge): (1) find the duplicate organisation, (2) choose
 * which one survives and which differing values to take from the other, (3) confirm. Contacts, tickets, locations,
 * departments, contracts, installations, licences, assets, service activities, subsidiaries and ERP links move to the
 * survivor; the other customer is marked merged, hidden and read-only. Closes with the merge result.
 */
@Component({
  selector: 'desk-customer-merge-wizard-dialog',
  imports: [SdDialog, SdButton, SdBadge, SdIcon, SdRadioGroup, SdRadio, SdCheckbox, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Merge customer organisations" [description]="stepText()">
      @if (result(); as r) {
        <div class="done" role="status">
          <sd-icon name="circle-check" [size]="28" />
          <div>
            <h3>{{ r.alreadyMerged ? 'These customers were already merged' : 'Customers merged' }}</h3>
            <p class="sd-small sd-muted">{{ survivor()!.name }} now holds everything that belonged to {{ merged()!.name }}.</p>
            @if (movedList().length) {
              <ul class="counts">
                @for (m of movedList(); track m.key) {
                  <li><strong>{{ m.count }}</strong> {{ m.label }}</li>
                }
              </ul>
            } @else {
              <p class="sd-small">{{ merged()!.name }} had no records to move.</p>
            }
            @if (r.fieldsCopied.length) {
              <p class="sd-small">Values taken from the merged customer: {{ r.fieldsCopied.join(', ') }}</p>
            }
          </div>
        </div>
      } @else if (step() === 1) {
        <label class="search">
          <span class="lbl">Find the duplicate organisation</span>
          <span class="box">
            <sd-icon name="search" [size]="14" />
            <input type="search" placeholder="Name, code, ERP code, domain…" [value]="query()" (input)="onQuery($any($event.target).value)" autocomplete="off" />
          </span>
        </label>
        @if (searching() && candidates().length === 0) {
          <sd-skeleton [lines]="3" />
        } @else if (query().trim().length >= 2 && candidates().length === 0) {
          <p class="sd-small sd-muted">No other customer matches “{{ query() }}”.</p>
        } @else {
          <ul class="results" aria-label="Matching customers">
            @for (c of candidates(); track c.id) {
              <li>
                <button type="button" class="result" [class.is-selected]="c.id === otherId()" [attr.aria-pressed]="c.id === otherId()" (click)="pick(c)">
                  <span class="rname">{{ c.name }} @if (c.code) {<span class="sd-xs sd-muted sd-mono">{{ c.code }}</span>}</span>
                  <span class="sd-xs sd-muted">{{ c.contactCount }} contacts · {{ c.openTickets }} open tickets{{ c.parentName ? ' · under ' + c.parentName : '' }}</span>
                </button>
              </li>
            }
          </ul>
        }
        @if (loadingOther()) {
          <sd-skeleton [lines]="2" />
        }
      } @else {
        @if (step() === 2) {
          <sd-radio-group [value]="survivorId()" (valueChange)="survivorId.set($event ?? data.customer.id)" ariaLabel="Customer that survives" direction="row">
            <sd-radio [value]="data.customer.id">Keep <strong>{{ data.customer.name }}</strong></sd-radio>
            <sd-radio [value]="other()!.id">Keep <strong>{{ other()!.name }}</strong></sd-radio>
          </sd-radio-group>
          <p class="sd-small sd-muted hint">The kept customer keeps its own values and fills blanks from the other one. Tick a value to take it instead.</p>
        }
        <div class="dt-wrap">
          <table class="dt">
            <thead>
              <tr>
                <th scope="col">Field</th>
                <th scope="col">Keeps: {{ survivor()!.name }}</th>
                <th scope="col">Merged: {{ merged()!.name }}</th>
                @if (step() === 2) {
                  <th scope="col">Use merged value</th>
                }
              </tr>
            </thead>
            <tbody>
              @for (f of fields; track f.key) {
                <tr [class.diff]="differs(f)">
                  <th scope="row">{{ f.label }}</th>
                  <td>{{ f.get(survivor()!) ?? '—' }}</td>
                  <td>{{ f.get(merged()!) ?? '—' }}</td>
                  @if (step() === 2) {
                    <td>
                      @if (f.choosable && differs(f) && f.get(merged()!)) {
                        <sd-checkbox [value]="prefer().has(f.key)" (valueChange)="toggle(f.key, $event)" [ariaLabel]="'Use merged ' + f.label" />
                      }
                    </td>
                  }
                </tr>
              }
            </tbody>
          </table>
        </div>
        @if (step() === 3) {
          <div class="warn" role="note">
            <sd-icon name="triangle-alert" [size]="16" />
            <div>
              <p>
                Everything of <strong>{{ merged()!.name }}</strong> moves to <strong>{{ survivor()!.name }}</strong>: contacts, tickets, locations,
                departments, contracts, installations, licences, assets, service activities, subsidiaries and ERP links.
              </p>
              <p class="sd-small">{{ merged()!.name }} is then marked as merged, hidden from lists and can no longer be edited or restored. The merge is recorded in the audit log.</p>
            </div>
          </div>
          @if (prefer().size) {
            <p class="sd-small">Values taken from {{ merged()!.name }}: <sd-badge size="sm">{{ preferLabels() }}</sd-badge></p>
          }
        }
      }
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      <div sdDialogFooter>
        @if (result()) {
          <button sdButton variant="primary" (click)="ref.close(result()!)">Done</button>
        } @else if (step() === 1) {
          <button sdButton type="button" (click)="ref.close()">Cancel</button>
          <button sdButton variant="primary" iconRight="arrow-right" [disabled]="!other()" (click)="step.set(2)">Next</button>
        } @else if (step() === 2) {
          <button sdButton type="button" icon="arrow-left" (click)="step.set(1)">Back</button>
          <button sdButton variant="primary" iconRight="arrow-right" (click)="step.set(3)">Review</button>
        } @else {
          <button sdButton type="button" icon="arrow-left" (click)="step.set(2)">Back</button>
          <button sdButton variant="danger" icon="merge" [loading]="merging()" (click)="merge()">Merge customers</button>
        }
      </div>
    </sd-dialog>
  `,
  styles: [
    TABLE_STYLES,
    `
      .search {
        display: grid;
        gap: 6px;
        margin-bottom: var(--sd-space-3);
      }
      .lbl {
        font-size: var(--sd-text-sm);
        font-weight: var(--sd-weight-medium);
      }
      .box {
        display: flex;
        align-items: center;
        gap: 6px;
        height: 36px;
        padding: 0 10px;
        border: 1px solid var(--sd-border);
        border-radius: var(--sd-radius-md);
        background: var(--sd-surface);
        color: var(--sd-text-3);
      }
      .box:focus-within {
        border-color: var(--sd-primary);
        box-shadow: var(--sd-focus-ring);
      }
      .box input {
        flex: 1;
        min-width: 0;
        border: 0;
        outline: none;
        background: transparent;
        color: var(--sd-text);
        font: inherit;
      }
      .results {
        display: grid;
        gap: 4px;
        margin: 0;
        padding: 0;
        list-style: none;
        max-height: 280px;
        overflow-y: auto;
      }
      .result {
        display: grid;
        gap: 2px;
        width: 100%;
        padding: 8px 10px;
        border: 1px solid var(--sd-border);
        border-radius: var(--sd-radius-md);
        background: var(--sd-surface);
        color: var(--sd-text);
        font: inherit;
        text-align: left;
        cursor: pointer;
      }
      .result:hover {
        background: var(--sd-row-hover);
      }
      .result.is-selected {
        border-color: var(--sd-primary);
        box-shadow: inset 3px 0 0 var(--sd-primary);
      }
      .result:focus-visible {
        outline: none;
        box-shadow: var(--sd-focus-ring);
      }
      .rname {
        display: flex;
        align-items: baseline;
        gap: 6px;
        font-weight: var(--sd-weight-medium);
      }
      .hint {
        margin: var(--sd-space-2) 0 var(--sd-space-3);
      }
      .dt th[scope='row'] {
        background: transparent;
        color: var(--sd-text-2);
        border-bottom: 1px solid var(--sd-divider);
      }
      tr.diff td {
        background: var(--sd-warning-bg);
      }
      .warn {
        display: flex;
        gap: 8px;
        margin-top: var(--sd-space-3);
        padding: 10px 12px;
        border: 1px solid var(--sd-warning-border);
        border-radius: var(--sd-radius-md);
        background: var(--sd-warning-bg);
      }
      .warn sd-icon {
        flex: none;
        color: var(--sd-warning);
      }
      .warn p + p {
        margin-top: 4px;
      }
      .error {
        color: var(--sd-danger);
        margin-top: var(--sd-space-2);
      }
      .done {
        display: flex;
        gap: var(--sd-space-3);
        color: var(--sd-success);
      }
      .done h3 {
        color: var(--sd-text);
        font-weight: var(--sd-weight-semibold);
      }
      .counts {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(160px, 1fr));
        gap: 4px var(--sd-space-3);
        margin: var(--sd-space-3) 0;
        padding: 0;
        list-style: none;
        color: var(--sd-text-2);
      }
    `,
  ],
})
export class CustomerMergeWizardDialog {
  protected readonly data = inject<CustomerMergeWizardData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<CustomerMergeResultDto>>(DialogRef);
  private readonly api = inject(CustomersApi);

  protected readonly fields = FIELDS;
  protected readonly step = signal<1 | 2 | 3>(1);
  protected readonly query = signal('');
  protected readonly candidates = signal<CustomerListItemDto[]>([]);
  protected readonly searching = signal(false);
  protected readonly otherId = signal<Guid | null>(null);
  protected readonly other = signal<CustomerDetailDto | null>(null);
  protected readonly loadingOther = signal(false);
  protected readonly survivorId = signal<Guid>(this.data.customer.id);
  protected readonly prefer = signal<Set<string>>(new Set());
  protected readonly merging = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly result = signal<CustomerMergeResultDto | null>(null);
  private readonly query$ = new Subject<string>();

  protected readonly survivor = computed(() => (this.survivorId() === this.other()?.id ? this.other() : this.data.customer));
  protected readonly merged = computed(() => (this.survivorId() === this.other()?.id ? this.data.customer : this.other()));
  protected readonly stepText = computed(() => {
    if (this.result()) return 'Merge complete.';
    switch (this.step()) {
      case 1:
        return `Step 1 of 3 — find the organisation that duplicates ${this.data.customer.name}.`;
      case 2:
        return 'Step 2 of 3 — choose the customer to keep and the values to carry over.';
      default:
        return 'Step 3 of 3 — review and merge.';
    }
  });
  protected readonly preferLabels = computed(() =>
    [...this.prefer()].map((k) => FIELDS.find((f) => f.key === k)?.label ?? k).join(', '),
  );
  protected readonly movedList = computed(() =>
    Object.entries(this.result()?.moved ?? {})
      .filter(([, count]) => count > 0)
      .map(([key, count]) => ({ key, count, label: MOVED_LABELS[key] ?? key })),
  );

  constructor() {
    const sub = this.query$
      .pipe(
        debounceTime(250),
        switchMap((q) => {
          this.searching.set(true);
          return this.api.list({ q, limit: 8, sort: 'name' });
        }),
      )
      .subscribe({
        next: (page) => {
          this.searching.set(false);
          this.candidates.set(page.items.filter((c) => c.id !== this.data.customer.id));
        },
        error: (err: unknown) => {
          this.searching.set(false);
          this.error.set(saveErrorMessage(err));
        },
      });
    inject(DestroyRef).onDestroy(() => sub.unsubscribe());
    // Suggest organisations with a similar name straight away.
    this.onQuery(this.data.customer.name.split(/\s+/).slice(0, 2).join(' '));
  }

  protected onQuery(value: string): void {
    this.query.set(value);
    if (value.trim().length >= 2) this.query$.next(value.trim());
    else this.candidates.set([]);
  }

  protected pick(c: CustomerListItemDto): void {
    this.otherId.set(c.id);
    this.other.set(null);
    this.prefer.set(new Set());
    this.error.set(null);
    this.loadingOther.set(true);
    this.api.get(c.id).subscribe({
      next: (detail) => {
        this.loadingOther.set(false);
        if (this.otherId() !== c.id) return;
        this.other.set(detail);
        // Default survivor: the customer with more open work and contacts, then the older one.
        const a = this.data.customer;
        const score = (x: CustomerDetailDto) => x.rollup.openTickets * 10 + x.rollup.contacts + x.rollup.installations;
        const keepOther = score(detail) > score(a) || (score(detail) === score(a) && detail.createdAt < a.createdAt);
        this.survivorId.set(keepOther ? detail.id : a.id);
      },
      error: (err: unknown) => {
        this.loadingOther.set(false);
        this.error.set(saveErrorMessage(err));
      },
    });
  }

  protected differs(f: FieldRow): boolean {
    const s = this.survivor();
    const m = this.merged();
    return !!s && !!m && (f.get(s) ?? '') !== (f.get(m) ?? '');
  }

  protected toggle(key: string, on: boolean): void {
    this.prefer.update((set) => {
      const next = new Set(set);
      if (on) next.add(key);
      else next.delete(key);
      return next;
    });
  }

  protected merge(): void {
    const s = this.survivor();
    const m = this.merged();
    if (!s || !m || this.merging()) return;
    this.merging.set(true);
    this.error.set(null);
    this.api.merge({ keepId: s.id, mergeId: m.id, preferMergedFields: [...this.prefer()] }).subscribe({
      next: (r) => {
        this.merging.set(false);
        this.result.set(r);
      },
      error: (err: unknown) => {
        this.merging.set(false);
        this.error.set(saveErrorMessage(err));
      },
    });
  }
}
