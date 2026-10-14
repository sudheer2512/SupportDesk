import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  AnalyticsApi,
  AnalyticsReportFormat,
  AnalyticsScheduleDto,
  SaveAnalyticsScheduleRequest,
  saveErrorMessage,
  validationMessages,
} from '@sd/core';
import { SdButton, SdChipsInput, SdCombobox, SdDialog, SdFormField, SdInput, SdOption, SdToggle } from '@sd/ui';
import { AnalyticsStore } from './analytics.store';

export interface ScheduleFormData {
  schedule: AnalyticsScheduleDto | null;
  /** Prefill for a new schedule (from a report page). */
  reportKey?: string;
  parameters?: Record<string, string>;
}

const DAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/** Create or edit a scheduled report: report, rolling period, filters, format, cadence, time zone and recipients. */
@Component({
  selector: 'desk-analytics-schedule-form',
  imports: [FormsModule, SdButton, SdChipsInput, SdCombobox, SdDialog, SdFormField, SdInput, SdToggle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="s ? 'Edit scheduled report' : 'Schedule a report'" description="The report runs with your data scope and is e-mailed as an attachment.">
      <form id="schedule-form" class="form" (ngSubmit)="save()" novalidate>
        <div class="two">
          <sd-form-field label="Name" required [error]="errors()['name']">
            <input sdInput name="name" [(ngModel)]="name" maxlength="200" required />
          </sd-form-field>
          <sd-form-field label="Report" required [error]="errors()['reportKey']">
            <select sdInput name="report" [(ngModel)]="reportKey">
              @for (r of store.reports(); track r.key) {
                <option [value]="r.key">{{ r.name }}</option>
              }
            </select>
          </sd-form-field>
        </div>
        <div class="three">
          <sd-form-field label="Period covered" [error]="errors()['parameters.period']" hint="Rolling, relative to each run.">
            <select sdInput name="period" [(ngModel)]="period">
              @for (p of periods(); track p.key) {
                <option [value]="p.key">{{ p.label }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Compare with">
            <select sdInput name="compare" [(ngModel)]="compare">
              <option value="">No comparison</option>
              <option value="previous">Previous period</option>
              <option value="last_year">Same period last year</option>
            </select>
          </sd-form-field>
          <sd-form-field label="Format" [error]="errors()['format']">
            <select sdInput name="format" [(ngModel)]="format">
              <option value="Pdf">PDF</option>
              <option value="Xlsx">Excel</option>
              <option value="Csv">CSV</option>
            </select>
          </sd-form-field>
        </div>
        <div class="three">
          <sd-form-field label="Department" optional>
            <sd-combobox multiple [options]="departmentOptions()" [value]="departments()" (valueChange)="departments.set(asList($event))" placeholder="All" ariaLabel="Department" />
          </sd-form-field>
          <sd-form-field label="Priority" optional>
            <sd-combobox multiple [options]="priorityOptions()" [value]="priorities()" (valueChange)="priorities.set(asList($event))" placeholder="All" ariaLabel="Priority" />
          </sd-form-field>
          <sd-form-field label="Channel" optional>
            <sd-combobox multiple [options]="channelOptions()" [value]="channels()" (valueChange)="channels.set(asList($event))" placeholder="All" ariaLabel="Channel" />
          </sd-form-field>
        </div>
        @if (otherFilters().length) {
          <p class="sd-xs sd-muted">Also filtered by: {{ otherFilters().join(', ') }} (from the report you scheduled).</p>
        }
        <div class="three">
          <sd-form-field label="Frequency" [error]="errors()['frequency']">
            <select sdInput name="frequency" [(ngModel)]="frequency">
              <option value="Daily">Daily</option>
              <option value="Weekly">Weekly</option>
              <option value="Monthly">Monthly</option>
            </select>
          </sd-form-field>
          @if (frequency === 'Weekly') {
            <sd-form-field label="Day" [error]="errors()['dayOfWeek']">
              <select sdInput name="dow" [(ngModel)]="dayOfWeek">
                @for (d of days; track d) {
                  <option [value]="d">{{ d }}</option>
                }
              </select>
            </sd-form-field>
          } @else if (frequency === 'Monthly') {
            <sd-form-field label="Day of month" hint="1–28" [error]="errors()['dayOfMonth']">
              <input sdInput type="number" name="dom" min="1" max="28" [(ngModel)]="dayOfMonth" />
            </sd-form-field>
          }
          <sd-form-field label="Time" [error]="errors()['runAtLocal']">
            <input sdInput type="time" name="time" [(ngModel)]="runAt" />
          </sd-form-field>
        </div>
        <sd-form-field label="Time zone" [error]="errors()['timeZone']" hint="IANA name, e.g. Asia/Kolkata.">
          <input sdInput name="tz" [(ngModel)]="timeZone" maxlength="100" />
        </sd-form-field>
        <sd-form-field label="Recipients" required [error]="errors()['recipients']" hint="Press Enter after each e-mail address (up to 20).">
          <sd-chips-input [(value)]="recipients" placeholder="name@company.com" [lowercase]="true" [validate]="isEmail" ariaLabel="Recipients" />
        </sd-form-field>
        <sd-toggle [(value)]="active">Active</sd-toggle>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="schedule-form" [loading]="saving()">{{ s ? 'Save' : 'Create schedule' }}</button>
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
    .error {
      color: var(--sd-danger);
    }
  `,
})
export class ScheduleFormDialog {
  protected readonly data = inject<ScheduleFormData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<AnalyticsScheduleDto>>(DialogRef);
  protected readonly store = inject(AnalyticsStore);
  private readonly api = inject(AnalyticsApi);

  protected readonly s = this.data.schedule;
  private readonly params: Record<string, string> = { ...(this.s?.parameters ?? this.data.parameters ?? {}) };
  protected readonly days = DAYS;
  protected name = this.s?.name ?? (this.data.reportKey ? (this.store.reports().find((r) => r.key === this.data.reportKey)?.name ?? '') : '');
  protected reportKey = this.s?.reportKey ?? this.data.reportKey ?? 'volume';
  protected period = this.params['period'] && this.params['period'] !== 'custom' ? this.params['period'] : 'last_7_days';
  protected compare = this.params['compare'] ?? '';
  protected format: AnalyticsReportFormat = this.s?.format ?? 'Pdf';
  protected frequency: 'Daily' | 'Weekly' | 'Monthly' = this.s?.frequency ?? 'Weekly';
  protected dayOfWeek = this.s?.dayOfWeek ?? 'Monday';
  protected dayOfMonth = this.s?.dayOfMonth ?? 1;
  protected runAt = this.s?.runAtLocal ?? '08:00';
  protected timeZone = this.s?.timeZone ?? this.store.catalog()?.timeZone ?? 'UTC';
  protected readonly recipients = signal<string[]>(this.s?.recipients ?? []);
  protected readonly active = signal(this.s?.isActive ?? true);
  protected readonly departments = signal<string[]>(this.list('department'));
  protected readonly priorities = signal<string[]>(this.list('priority'));
  protected readonly channels = signal<string[]>(this.list('channel'));
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  protected readonly periods = computed(() => this.store.periods().filter((p) => p.key !== 'custom'));
  protected readonly departmentOptions = computed<SdOption[]>(() => this.store.options()?.departments ?? []);
  protected readonly priorityOptions = computed<SdOption[]>(() => this.store.options()?.priorities ?? []);
  protected readonly channelOptions = computed<SdOption[]>(() => this.store.options()?.channels ?? []);
  /** Filters carried over from a report that this form does not edit (kept as they are). */
  protected readonly otherFilters = computed(() =>
    Object.keys(this.params).filter((k) => !['period', 'from', 'to', 'compare', 'department', 'priority', 'channel', 'dimension'].includes(k)),
  );

  protected readonly isEmail = (v: string) => EMAIL.test(v);

  private list(key: string): string[] {
    return (this.params[key] ?? '').split(',').filter(Boolean);
  }

  protected asList(v: string | string[] | null): string[] {
    return Array.isArray(v) ? v : v ? [v] : [];
  }

  protected save(): void {
    if (this.saving()) return;
    const parameters: Record<string, string> = {};
    for (const k of this.otherFilters()) parameters[k] = this.params[k];
    parameters['period'] = this.period;
    if (this.compare) parameters['compare'] = this.compare;
    if (this.departments().length) parameters['department'] = this.departments().join(',');
    if (this.priorities().length) parameters['priority'] = this.priorities().join(',');
    if (this.channels().length) parameters['channel'] = this.channels().join(',');
    const request: SaveAnalyticsScheduleRequest = {
      name: this.name.trim(),
      reportKey: this.reportKey,
      parameters,
      format: this.format,
      frequency: this.frequency,
      runAtLocal: this.runAt,
      dayOfWeek: this.frequency === 'Weekly' ? this.dayOfWeek : null,
      dayOfMonth: this.frequency === 'Monthly' ? Number(this.dayOfMonth) : null,
      timeZone: this.timeZone.trim() || null,
      recipients: this.recipients(),
      isActive: this.active(),
      rowVersion: this.s?.rowVersion ?? null,
    };
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    const call = this.s ? this.api.updateSchedule(this.s.id, request) : this.api.createSchedule(request);
    call.subscribe({
      next: (saved) => this.ref.close(saved),
      error: (e: unknown) => {
        this.saving.set(false);
        this.errors.set(validationMessages(e));
        this.formError.set(saveErrorMessage(e));
      },
    });
  }
}
