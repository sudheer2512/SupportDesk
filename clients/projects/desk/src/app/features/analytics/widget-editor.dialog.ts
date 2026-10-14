import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AnalyticsWidgetDto, AnalyticsWidgetType } from '@sd/core';
import { SdButton, SdCombobox, SdDialog, SdFormField, SdIcon, SdInput, SdOption, SdToggle } from '@sd/ui';
import { AnalyticsStore } from './analytics.store';
import { AnalyticsWidgetComponent } from './widget.component';

export interface WidgetEditorData {
  widget: AnalyticsWidgetDto | null;
  /** Dashboard period for the live preview. */
  period: string;
  /** Grid row where a new widget is placed. */
  nextY: number;
}

const TYPES: { value: AnalyticsWidgetType; label: string; icon: string }[] = [
  { value: 'kpi', label: 'KPI', icon: 'gauge' },
  { value: 'line', label: 'Line', icon: 'chart-line' },
  { value: 'bar', label: 'Bar', icon: 'chart-column' },
  { value: 'pie', label: 'Pie', icon: 'chart-pie' },
  { value: 'table', label: 'Table', icon: 'table' },
];

const WIDGET_FILTERS = ['department', 'team', 'assignee', 'channel', 'priority', 'product', 'tier'] as const;

/** Add or edit a dashboard widget from catalogue metrics, with a live preview. */
@Component({
  selector: 'desk-analytics-widget-editor',
  imports: [FormsModule, SdButton, SdCombobox, SdDialog, SdFormField, SdIcon, SdInput, SdToggle, AnalyticsWidgetComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="data.widget ? 'Edit widget' : 'Add widget'" description="Widgets show catalogue metrics; numbers match reports and exports.">
      <form id="widget-form" class="form" (ngSubmit)="save()" novalidate>
        <fieldset class="types">
          <legend>Visualisation</legend>
          <div class="type-row" role="radiogroup" aria-label="Visualisation">
            @for (t of types; track t.value) {
              <button type="button" role="radio" class="type" [class.is-on]="type() === t.value" [attr.aria-checked]="type() === t.value" [attr.aria-label]="t.label + ' widget'" (click)="setType(t.value)">
                <sd-icon [name]="t.icon" [size]="18" />
                <span>{{ t.label }}</span>
              </button>
            }
          </div>
        </fieldset>
        <div class="two">
          <sd-form-field label="Metric" required [hint]="metricHint()">
            <sd-combobox [options]="store.metricOptions()" [value]="metric()" (valueChange)="setMetric($event)" placeholder="Choose a metric" ariaLabel="Metric" [clearable]="false" />
          </sd-form-field>
          <sd-form-field label="Title" required [error]="errors()['title']">
            <input sdInput name="title" [(ngModel)]="title" maxlength="120" />
          </sd-form-field>
        </div>
        @if (type() !== 'kpi') {
          <div class="two">
            <sd-form-field label="Break down by" required>
              <select sdInput name="dimension" [ngModel]="dimension()" (ngModelChange)="dimension.set($event)">
                @for (d of dimensionOptions(); track d.value) {
                  <option [value]="d.value">{{ d.label }}</option>
                }
              </select>
            </sd-form-field>
            @if (type() !== 'line') {
              <sd-form-field label="Show top" hint="Rows or slices (1–50).">
                <input sdInput type="number" name="limit" min="1" max="50" [(ngModel)]="limit" />
              </sd-form-field>
            }
          </div>
        }
        @if (type() === 'table') {
          <sd-form-field label="More columns" optional hint="Other metrics shown side by side (up to 7).">
            <sd-combobox multiple [options]="extraOptions()" [value]="extra()" (valueChange)="setExtra($event)" placeholder="Add metrics" ariaLabel="More columns" />
          </sd-form-field>
        }
        <div class="two">
          <sd-form-field label="Period" hint="Leave on the dashboard period to follow the page selector.">
            <select sdInput name="period" [(ngModel)]="period">
              <option value="">Dashboard period</option>
              @for (p of periods(); track p.key) {
                <option [value]="p.key">{{ p.label }}</option>
              }
            </select>
          </sd-form-field>
          @if (type() === 'kpi') {
            <div class="toggle-cell">
              <sd-toggle [(value)]="compare">Compare with the previous period</sd-toggle>
            </div>
          }
        </div>
        <fieldset class="filters">
          <legend>Widget filters <span class="sd-muted sd-xs">(added to the page filters)</span></legend>
          <div class="three">
            @for (f of filterFields(); track f.key) {
              <sd-form-field [label]="f.label">
                <sd-combobox multiple [options]="f.options" [value]="filterValues(f.key)" (valueChange)="setFilter(f.key, $event)" [placeholder]="'Any'" [ariaLabel]="f.label" />
              </sd-form-field>
            }
          </div>
        </fieldset>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
        <div class="preview" aria-label="Preview">
          <desk-analytics-widget [widget]="preview()" [period]="data.period" />
        </div>
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="widget-form">{{ data.widget ? 'Apply' : 'Add widget' }}</button>
      </div>
    </sd-dialog>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      min-height: 0;
      max-height: calc(100dvh - 48px);
    }
    .form {
      display: grid;
      gap: var(--sd-space-3);
    }
    .two,
    .three {
      display: grid;
      gap: var(--sd-space-3);
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }
    .three {
      grid-template-columns: repeat(3, minmax(0, 1fr));
    }
    @media (max-width: 640px) {
      .two,
      .three {
        grid-template-columns: 1fr;
      }
    }
    fieldset {
      margin: 0;
      padding: 0;
      border: 0;
      min-width: 0;
    }
    legend {
      margin-bottom: 6px;
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-medium);
      color: var(--sd-text-2);
    }
    .type-row {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
    }
    .type {
      display: inline-flex;
      flex-direction: column;
      align-items: center;
      gap: 4px;
      min-width: 64px;
      padding: 8px 10px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      color: var(--sd-text-2);
      font: inherit;
      font-size: var(--sd-text-xs);
      cursor: pointer;
    }
    .type.is-on {
      border-color: var(--sd-primary);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    .type:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .toggle-cell {
      display: flex;
      align-items: flex-end;
      padding-bottom: 8px;
    }
    .preview {
      height: 260px;
    }
    .error {
      color: var(--sd-danger);
    }
  `,
})
export class WidgetEditorDialog {
  protected readonly data = inject<WidgetEditorData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<AnalyticsWidgetDto>>(DialogRef);
  protected readonly store = inject(AnalyticsStore);
  protected readonly types = TYPES;

  private readonly w = this.data.widget;
  protected readonly type = signal<AnalyticsWidgetType>(this.w?.type ?? 'kpi');
  protected readonly metric = signal<string>(this.w?.metric ?? 'ticket_count');
  protected readonly dimension = signal<string>(this.w?.dimension ?? 'day');
  protected readonly extra = signal<string[]>(this.w?.metrics ?? []);
  protected readonly filters = signal<Record<string, string>>({ ...(this.w?.filters ?? {}) });
  protected title = this.w?.title ?? this.store.metric(this.w?.metric ?? 'ticket_count')?.label ?? '';
  protected limit = this.w?.limit ?? 10;
  protected period = this.w?.period ?? '';
  protected compare = signal(this.w?.compare ?? true);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  protected readonly periods = computed(() => this.store.periods().filter((p) => p.key !== 'custom'));
  private readonly metricDef = computed(() => this.store.metric(this.metric()));
  protected readonly metricHint = computed(() => this.metricDef()?.description ?? '');

  protected readonly dimensionOptions = computed<SdOption[]>(() => {
    const m = this.metricDef();
    const dims = (this.store.catalog()?.dimensions ?? []).filter((d) => d.key !== 'none' && (!m || m.dimensions.includes(d.key)));
    const t = this.type();
    return dims
      .filter((d) => (t === 'line' ? d.isTime : t === 'pie' ? !d.isTime : true))
      .map((d) => ({ value: d.key, label: d.label }));
  });

  protected readonly extraOptions = computed<SdOption[]>(() => {
    const m = this.metricDef();
    return this.store
      .metrics()
      .filter((x) => x.key !== this.metric() && x.source === (m?.source ?? 'tickets'))
      .map((x) => ({ value: x.key, label: x.label, group: x.group }));
  });

  protected readonly filterFields = computed(() => {
    const o = this.store.options();
    const m = this.metricDef();
    const lists: Record<(typeof WIDGET_FILTERS)[number], { label: string; options: SdOption[] }> = {
      department: { label: 'Department', options: o?.departments ?? [] },
      team: { label: 'Team', options: o?.teams ?? [] },
      assignee: { label: 'Agent', options: o?.agents ?? [] },
      channel: { label: 'Channel', options: o?.channels ?? [] },
      priority: { label: 'Priority', options: o?.priorities ?? [] },
      product: { label: 'Product', options: o?.products ?? [] },
      tier: { label: 'Customer tier', options: o?.tiers ?? [] },
    };
    return WIDGET_FILTERS.filter((k) => !m || m.filters.includes(k)).map((k) => ({ key: k, ...lists[k] }));
  });

  protected readonly preview = computed<AnalyticsWidgetDto>(() => this.build());

  protected setType(t: AnalyticsWidgetType): void {
    this.type.set(t);
    const time = ['day', 'week', 'month'].includes(this.dimension());
    // Categories suit bars, pies and tables; lines need time buckets.
    if (t === 'line' && !time) this.dimension.set('day');
    if ((t === 'bar' || t === 'pie' || t === 'table') && time) this.dimension.set(this.metricDef()?.source === 'agent_daily' ? 'assignee' : 'priority');
    this.ensureDimension();
  }

  protected setMetric(v: string | string[] | null): void {
    const key = Array.isArray(v) ? v[0] : v;
    if (!key) return;
    const prevLabel = this.store.metric(this.metric())?.label;
    this.metric.set(key);
    if (!this.title || this.title === prevLabel) this.title = this.store.metric(key)?.label ?? '';
    this.extra.update((list) => list.filter((k) => k !== key));
    this.ensureDimension();
  }

  protected setExtra(v: string | string[] | null): void {
    this.extra.set((Array.isArray(v) ? v : v ? [v] : []).slice(0, 7));
  }

  protected filterValues(key: string): string[] {
    return (this.filters()[key] ?? '').split(',').filter(Boolean);
  }

  protected setFilter(key: string, v: string | string[] | null): void {
    const list = Array.isArray(v) ? v : v ? [v] : [];
    this.filters.update((f) => {
      const next = { ...f };
      if (list.length) next[key] = list.join(',');
      else delete next[key];
      return next;
    });
  }

  private ensureDimension(): void {
    const opts = this.dimensionOptions();
    if (this.type() !== 'kpi' && !opts.some((o) => o.value === this.dimension())) {
      this.dimension.set(opts.find((o) => o.value === (this.type() === 'line' ? 'day' : 'department'))?.value ?? opts[0]?.value ?? 'day');
    }
  }

  private build(): AnalyticsWidgetDto {
    const t = this.type();
    return {
      id: this.w?.id ?? '',
      title: (this.title || this.metricDef()?.label || 'Widget').trim(),
      type: t,
      metric: this.metric(),
      metrics: t === 'table' ? this.extra() : null,
      dimension: t === 'kpi' ? null : this.dimension(),
      limit: t === 'kpi' || t === 'line' ? null : Math.max(1, Math.min(50, Number(this.limit) || 10)),
      filters: Object.keys(this.filters()).length ? this.filters() : null,
      period: this.period || null,
      compare: t === 'kpi' ? this.compare() : false,
      x: this.w?.x ?? 0,
      y: this.w?.y ?? this.data.nextY,
      w: this.w?.w ?? (t === 'kpi' ? 3 : t === 'table' ? 12 : 6),
      h: this.w?.h ?? (t === 'kpi' ? 2 : 4),
    };
  }

  protected save(): void {
    const w = this.build();
    if (!w.title || w.title.length > 120) {
      this.errors.set({ title: 'Enter a title of up to 120 characters.' });
      return;
    }
    if (w.type !== 'kpi' && !this.dimensionOptions().some((o) => o.value === w.dimension)) {
      this.formError.set('This metric cannot be shown this way — choose another visualisation or breakdown.');
      return;
    }
    const agg = this.metricDef()?.aggregation;
    if (w.type === 'pie' && agg !== 'count' && agg !== 'sum') {
      this.formError.set('Pie charts need a count metric (for example “Tickets created”). Use a bar chart for averages and rates.');
      return;
    }
    this.ref.close({ ...w, id: w.id || Math.random().toString(36).slice(2, 12) });
  }
}
