import { ChangeDetectionStrategy, Component, computed, inject, input, model, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AnalyticsFilterValues, CustomersApi } from '@sd/core';
import { SdButton, SdCombobox, SdDateInput, SdDrawer, SdFormField, SdIcon, SdInput, SdOption, SdOptionSource, SdToggle } from '@sd/ui';
import { map } from 'rxjs';
import { AnalyticsFilterState, FILTER_LABELS } from './analytics-format';
import { AnalyticsStore } from './analytics.store';

type FilterKey = keyof AnalyticsFilterValues;

const ALL_FIELDS: FilterKey[] = ['department', 'team', 'assignee', 'channel', 'priority', 'customer', 'product', 'tier'];

/**
 * Period, comparison and filter selection shared by dashboards and reports. Period and comparison are inline; the
 * entity filters live in a drawer (bottom sheet on phones) and show as removable chips.
 */
@Component({
  selector: 'desk-analytics-filter-bar',
  imports: [FormsModule, SdButton, SdCombobox, SdDateInput, SdDrawer, SdFormField, SdIcon, SdInput, SdToggle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="bar" role="group" aria-label="Period and filters">
      <select sdInput size="sm" class="period" aria-label="Period" [ngModel]="state().period" (ngModelChange)="setPeriod($event)">
        @for (p of store.periods(); track p.key) {
          <option [value]="p.key">{{ p.label }}</option>
        }
      </select>
      @if (state().period === 'custom') {
        <sd-date-input size="sm" ariaLabel="From date" [value]="state().from" (valueChange)="patch({ from: $event })" [max]="state().to" />
        <span class="sd-muted" aria-hidden="true">–</span>
        <sd-date-input size="sm" ariaLabel="To date" [value]="state().to" (valueChange)="patch({ to: $event })" [min]="state().from" />
      }
      <sd-toggle [value]="state().compare !== ''" (valueChange)="patch({ compare: $event ? 'previous' : '' })">Compare</sd-toggle>
      @if (state().compare !== '') {
        <select sdInput size="sm" class="compare" aria-label="Compare with" [ngModel]="state().compare" (ngModelChange)="patch({ compare: $event })">
          <option value="previous">Previous period</option>
          <option value="last_year">Same period last year</option>
        </select>
      }
      @if (fields().length) {
        <button sdButton size="sm" icon="filter" type="button" (click)="openDrawer()" [attr.aria-label]="'Filters' + (activeCount() ? ' (' + activeCount() + ' active)' : '')">
          Filters@if (activeCount()) {<span class="count">{{ activeCount() }}</span>}
        </button>
      }
      @for (chip of chips(); track chip.key + chip.value) {
        <span class="chip">
          <span class="chip-label">{{ chip.field }}: {{ chip.label }}</span>
          <button type="button" class="chip-x" [attr.aria-label]="'Remove filter ' + chip.field + ' ' + chip.label" (click)="removeValue(chip.key, chip.value)">
            <sd-icon name="x" [size]="12" />
          </button>
        </span>
      }
      @if (activeCount()) {
        <button sdButton size="sm" variant="link" type="button" (click)="clear()">Clear filters</button>
      }
    </div>

    <sd-drawer [(open)]="drawerOpen" title="Filters" ariaLabel="Analytics filters" [side]="phone() ? 'bottom' : 'right'" width="380px">
      <div class="fields">
        @for (f of fields(); track f) {
          <sd-form-field [label]="labels[f]">
            @if (f === 'customer') {
              <sd-combobox multiple [search]="searchCustomers" [options]="customerOptions()" [value]="draftValues(f)" (valueChange)="setDraft(f, $event)"
                placeholder="Any customer" [ariaLabel]="labels[f]" />
            } @else {
              <sd-combobox multiple [options]="optionsFor(f)" [value]="draftValues(f)" (valueChange)="setDraft(f, $event)" [placeholder]="'Any ' + labels[f].toLowerCase()"
                [ariaLabel]="labels[f]" />
            }
          </sd-form-field>
        }
      </div>
      <div sdDrawerFooter class="footer">
        <button sdButton type="button" (click)="draft.set({})">Reset</button>
        <button sdButton variant="primary" type="button" (click)="apply()">Apply filters</button>
      </div>
    </sd-drawer>
  `,
  styles: `
    .bar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--sd-space-2);
    }
    .period {
      width: auto;
      min-width: 150px;
    }
    .compare {
      width: auto;
    }
    .count {
      margin-left: 6px;
      padding: 0 6px;
      border-radius: 999px;
      background: var(--sd-primary);
      color: var(--sd-on-primary, #fff);
      font-size: var(--sd-text-xs);
    }
    .chip {
      display: inline-flex;
      align-items: center;
      gap: 2px;
      max-width: 100%;
      padding: 2px 4px 2px 10px;
      border: 1px solid var(--sd-border);
      border-radius: 999px;
      background: var(--sd-surface-2);
      font-size: var(--sd-text-xs);
    }
    .chip-label {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .chip-x {
      display: inline-flex;
      padding: 3px;
      border: 0;
      border-radius: 999px;
      background: transparent;
      color: var(--sd-text-2);
      cursor: pointer;
    }
    .chip-x:hover {
      background: var(--sd-surface-3, var(--sd-border));
    }
    .chip-x:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .fields {
      display: grid;
      gap: var(--sd-space-3);
    }
    .footer {
      display: flex;
      justify-content: flex-end;
      gap: var(--sd-space-2);
    }
  `,
})
export class AnalyticsFilterBarComponent {
  protected readonly store = inject(AnalyticsStore);
  private readonly customers = inject(CustomersApi);

  readonly state = model.required<AnalyticsFilterState>();
  readonly fields = input<FilterKey[]>(ALL_FIELDS);

  protected readonly labels = FILTER_LABELS;
  protected readonly drawerOpen = signal(false);
  protected readonly draft = signal<AnalyticsFilterValues>({});
  private readonly customerNames = signal<Record<string, string>>({});
  protected readonly phone = signal(typeof window !== 'undefined' && window.matchMedia?.('(max-width: 640px)').matches);

  protected readonly activeCount = computed(() => Object.values(this.state().filters).filter((v) => !!v).length);

  protected readonly customerOptions = computed<SdOption[]>(() =>
    Object.entries(this.customerNames()).map(([value, label]) => ({ value, label })),
  );

  protected readonly chips = computed(() => {
    const out: { key: FilterKey; field: string; value: string; label: string }[] = [];
    for (const [k, v] of Object.entries(this.state().filters) as [FilterKey, string | undefined][]) {
      if (!v) continue;
      for (const value of v.split(',')) out.push({ key: k, field: this.labels[k], value, label: this.labelFor(k, value) });
    }
    return out;
  });

  protected readonly searchCustomers: SdOptionSource = (q) =>
    this.customers.list({ q, limit: 20 }).pipe(
      map((page) => {
        const opts = page.items.map((c) => ({ value: c.id, label: c.name, description: c.tier }));
        this.customerNames.update((m) => ({ ...m, ...Object.fromEntries(opts.map((o) => [o.value, o.label])) }));
        return opts;
      }),
    );

  protected optionsFor(f: FilterKey): SdOption[] {
    const o = this.store.options();
    if (!o) return [];
    const list = {
      department: o.departments,
      team: o.teams,
      assignee: o.agents,
      channel: o.channels,
      priority: o.priorities,
      product: o.products,
      module: o.modules,
      version: o.versions,
      category: o.categories,
      tier: o.tiers,
      statusCategory: o.statusCategories,
      customer: [],
    }[f];
    return list.map((x) => ({ value: x.value, label: x.label }));
  }

  private labelFor(f: FilterKey, value: string): string {
    if (f === 'customer') return this.customerNames()[value] ?? 'Selected customer';
    return this.optionsFor(f).find((o) => o.value === value)?.label ?? value;
  }

  protected draftValues(f: FilterKey): string[] {
    return (this.draft()[f] ?? '').split(',').filter(Boolean);
  }

  protected setDraft(f: FilterKey, value: string | string[] | null): void {
    const list = Array.isArray(value) ? value : value ? [value] : [];
    this.draft.update((d) => ({ ...d, [f]: list.join(',') || undefined }));
  }

  protected openDrawer(): void {
    this.draft.set({ ...this.state().filters });
    this.drawerOpen.set(true);
  }

  protected apply(): void {
    const clean = Object.fromEntries(Object.entries(this.draft()).filter(([, v]) => !!v)) as AnalyticsFilterValues;
    this.state.update((s) => ({ ...s, filters: clean }));
    this.drawerOpen.set(false);
  }

  protected setPeriod(period: string): void {
    const s = this.state();
    if (period === 'custom' && (!s.from || !s.to)) {
      const to = new Date();
      const from = new Date(to.getTime() - 29 * 86400000);
      const iso = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
      this.state.set({ ...s, period, from: iso(from), to: iso(to) });
      return;
    }
    this.state.set({ ...s, period });
  }

  protected patch(p: Partial<AnalyticsFilterState>): void {
    this.state.update((s) => ({ ...s, ...p }));
  }

  protected removeValue(f: FilterKey, value: string): void {
    this.state.update((s) => {
      const rest = (s.filters[f] ?? '').split(',').filter((v) => v && v !== value);
      return { ...s, filters: { ...s.filters, [f]: rest.join(',') || undefined } };
    });
  }

  protected clear(): void {
    this.state.update((s) => ({ ...s, filters: {} }));
  }
}
