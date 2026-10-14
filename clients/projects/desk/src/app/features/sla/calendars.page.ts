import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  BusinessCalendarDto,
  BusinessCalendarListItemDto,
  BusinessTimePreviewDto,
  CalendarHolidayDto,
  ImportHolidaysResultDto,
  MeStore,
  SlaAdminApi,
  WeeklyHoursDto,
  saveErrorMessage,
  validationMessages,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCard,
  SdCheckbox,
  SdDateInput,
  SdDateTimePipe,
  SdDialog,
  SdDialogService,
  SdEmptyState,
  SdFormField,
  SdInput,
  SdPageHeader,
  SdSkeleton,
  ToastService,
} from '@sd/ui';
import { ModuleTabsComponent } from '../customers/shared/module-tabs.component';
import { SECTION_STYLES, TABLE_STYLES } from '../customers/shared/styles';
import { FORM_STYLES, PAGE_STYLES, errorText, formatMinutes, timeZoneOptions, weeklySummary } from '../teams/shared/org-ui';
import { WeeklyHoursEditorComponent } from '../teams/shared/weekly-hours-editor.component';
import { SLA_TABS } from './sla-ui';

/**
 * /sla/calendars — business calendars: weekly hours in a time zone (or 24x7), holidays (one-off / yearly, CSV import),
 * where the calendar is used, and a "business time between two instants" preview.
 */
@Component({
  selector: 'desk-sla-calendars-page',
  imports: [
    FormsModule,
    SdPageHeader,
    SdButton,
    SdBadge,
    SdCard,
    SdCheckbox,
    SdFormField,
    SdInput,
    SdDateInput,
    SdSkeleton,
    SdEmptyState,
    SdDateTimePipe,
    ModuleTabsComponent,
    WeeklyHoursEditorComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="SLA" subtitle="Business hours and holidays used by SLA clocks, departments and contracts." icon="timer">
        @if (canManage) {
          <button sdButton sdPageActions variant="primary" icon="plus" (click)="startNew()">New calendar</button>
        }
      </sd-page-header>
      <desk-module-tabs [tabs]="tabs" ariaLabel="SLA sections" />
      <div class="desk-page__body">
        @if (listError()) {
          <div class="error-box" role="alert"><span>{{ listError() }}</span><button sdButton size="sm" (click)="loadList()">Try again</button></div>
        } @else if (listLoading() && calendars().length === 0) {
          <div aria-busy="true"><sd-skeleton [lines]="6" /></div>
        } @else {
          <div class="layout">
            <nav class="clist" aria-label="Calendars">
              @if (calendars().length === 0) {
                <sd-empty-state icon="calendar" title="No calendars" compact />
              }
              @for (c of calendars(); track c.id) {
                <button type="button" class="citem" [class.is-active]="c.id === selectedId()" [attr.aria-current]="c.id === selectedId() ? 'true' : null" (click)="select(c.id)">
                  <span class="cname">{{ c.name }}</span>
                  <span class="xs muted">{{ c.is24x7 ? '24x7' : c.weeklyHours + ' h/week' }} · {{ c.timeZone }} · {{ c.holidayCount }} holidays</span>
                  <span class="pills">
                    @if (c.isDefault) {
                      <sd-badge size="sm" tone="primary">Default</sd-badge>
                    }
                    @if (c.usageCount > 0) {
                      <sd-badge size="sm">Used {{ c.usageCount }}×</sd-badge>
                    }
                  </span>
                </button>
              }
            </nav>

            <section class="detail" aria-label="Calendar details">
              @if (detailLoading()) {
                <sd-card><sd-skeleton [lines]="8" /></sd-card>
              } @else if (editing(); as e) {
                <sd-card [title]="e.id ? 'Edit calendar' : 'New calendar'">
                  <div sdCardActions>
                    @if (canManage && e.id && !e.isDefault) {
                      <button sdButton size="sm" variant="ghost" icon="trash" (click)="remove()">Delete</button>
                    }
                  </div>
                  <fieldset class="form plain" [disabled]="!canManage">
                    <div class="two">
                      <sd-form-field label="Name" required [error]="errors()['name']">
                        <input sdInput name="name" [(ngModel)]="e.name" maxlength="120" />
                      </sd-form-field>
                      <sd-form-field label="Time zone" required [error]="errors()['timeZone']">
                        <select sdInput name="tz" [(ngModel)]="e.timeZone">
                          @for (z of zones(); track z) {
                            <option [ngValue]="z">{{ z }}</option>
                          }
                        </select>
                      </sd-form-field>
                    </div>
                    <sd-form-field label="Description" optional>
                      <input sdInput name="description" [(ngModel)]="e.description" maxlength="500" />
                    </sd-form-field>
                    <div class="checks">
                      <sd-checkbox [value]="is24x7()" (valueChange)="is24x7.set($event)">Open 24x7 (no business hours)</sd-checkbox>
                      <sd-checkbox [value]="isDefault()" (valueChange)="isDefault.set($event)" [disabled]="e.isDefault">Default calendar</sd-checkbox>
                    </div>
                    @if (!is24x7()) {
                      <desk-weekly-hours-editor [value]="weekly()" (valueChange)="weekly.set($event)" [disabled]="!canManage" />
                      @if (weeklyError()) {
                        <p class="error" role="alert">{{ weeklyError() }}</p>
                      }
                      <p class="small muted">{{ summary() }}</p>
                    }
                    @if (formError()) {
                      <p class="error" role="alert">{{ formError() }}</p>
                    }
                    @if (canManage) {
                      <div class="row-end">
                        <button sdButton variant="primary" icon="check" (click)="save()" [loading]="saving()">Save calendar</button>
                      </div>
                    }
                  </fieldset>
                  @if (e.usedBy.length) {
                    <p class="small muted">Used by: {{ e.usedBy.join(' · ') }}</p>
                  }
                </sd-card>

                @if (e.id) {
                  <sd-card title="Holidays" [subtitle]="e.holidays.length + ' holidays (whole local days are excluded)'">
                    <div sdCardActions>
                      @if (canManage) {
                        <button sdButton size="sm" icon="upload" (click)="importCsv()">Import CSV</button>
                      }
                    </div>
                    @if (canManage) {
                      <form class="add-holiday" (ngSubmit)="addHoliday()">
                        <sd-form-field label="Date">
                          <sd-date-input [(value)]="holidayDate" ariaLabel="Holiday date" />
                        </sd-form-field>
                        <sd-form-field label="Name">
                          <input sdInput name="hname" [(ngModel)]="holidayName" maxlength="120" />
                        </sd-form-field>
                        <sd-checkbox [(value)]="holidayRecurring">Every year</sd-checkbox>
                        <button sdButton type="submit" icon="plus" [disabled]="!holidayDate() || !holidayName.trim()" [loading]="addingHoliday()">Add</button>
                      </form>
                      @if (holidayError()) {
                        <p class="error" role="alert">{{ holidayError() }}</p>
                      }
                    }
                    @if (e.holidays.length === 0) {
                      <p class="empty-note">No holidays.</p>
                    } @else {
                      <div class="dt-wrap">
                        <table class="dt">
                          <caption class="sd-visually-hidden">Holidays</caption>
                          <thead>
                            <tr>
                              <th scope="col">Date</th>
                              <th scope="col">Name</th>
                              <th scope="col">Repeats</th>
                              <th scope="col"><span class="sd-visually-hidden">Actions</span></th>
                            </tr>
                          </thead>
                          <tbody>
                            @for (h of e.holidays; track h.id) {
                              <tr>
                                <td class="nowrap">{{ h.recurring ? h.date.slice(5) : h.date }}</td>
                                <td>{{ h.name }}</td>
                                <td>{{ h.recurring ? 'Every year' : 'Once' }}</td>
                                <td class="row-actions">
                                  @if (canManage) {
                                    <button sdButton size="sm" variant="ghost" icon="x" iconOnly [attr.aria-label]="'Remove ' + h.name" (click)="removeHoliday(h)"></button>
                                  }
                                </td>
                              </tr>
                            }
                          </tbody>
                        </table>
                      </div>
                    }
                  </sd-card>

                  <sd-card title="Business time preview" subtitle="How many business minutes lie between two instants, and when a target would fall due.">
                    <form class="preview-form" (ngSubmit)="preview()">
                      <sd-form-field label="From">
                        <sd-date-input type="datetime" [(value)]="from" ariaLabel="Preview from" />
                      </sd-form-field>
                      <sd-form-field label="To">
                        <sd-date-input type="datetime" [(value)]="to" ariaLabel="Preview to" />
                      </sd-form-field>
                      <sd-form-field label="Add business minutes" optional>
                        <input sdInput name="add" type="number" min="0" [(ngModel)]="addMinutes" />
                      </sd-form-field>
                      <button sdButton type="submit" icon="play" [loading]="previewing()" [disabled]="!from() || !to()">Calculate</button>
                    </form>
                    @if (previewError()) {
                      <p class="error" role="alert">{{ previewError() }}</p>
                    }
                    @if (result(); as r) {
                      <div class="result" aria-live="polite">
                        <p>
                          <strong>{{ minutes(r.businessMinutes) }}</strong> business time of {{ minutes(r.calendarMinutes) }} elapsed ({{ r.timeZone }}).
                          @if (r.dueAt) {
                            {{ addMinutes }} business minutes from the start fall due <strong>{{ r.dueAt | sdDateTime }}</strong>.
                          }
                        </p>
                        @if (r.holidaysInRange.length) {
                          <p class="small">Holidays in range: {{ holidayNames(r.holidaysInRange) }}</p>
                        }
                        @if (r.intervals.length) {
                          <ul class="intervals small">
                            @for (iv of r.intervals; track iv.start) {
                              <li>{{ iv.start | sdDateTime: 'datetime' : 'short' }} → {{ iv.end | sdDateTime: 'datetime' : 'short' }} · {{ minutes(iv.minutes) }}</li>
                            }
                          </ul>
                        }
                      </div>
                    }
                  </sd-card>
                }
              } @else {
                <sd-empty-state icon="calendar" title="Choose a calendar" message="Select a calendar to see its hours, holidays and a business-time preview." />
              }
            </section>
          </div>
        }
      </div>
    </div>
  `,
  styles: [
    TABLE_STYLES,
    SECTION_STYLES,
    PAGE_STYLES,
    FORM_STYLES,
    `
      .layout { display: grid; gap: var(--sd-space-5); grid-template-columns: 300px minmax(0, 1fr); }
      @media (max-width: 900px) { .layout { grid-template-columns: 1fr; } }
      .clist { display: grid; gap: 4px; align-content: start; }
      .citem { display: grid; gap: 2px; text-align: left; padding: 10px 12px; border: 1px solid var(--sd-border); border-radius: var(--sd-radius-md);
        background: var(--sd-surface); color: var(--sd-text); cursor: pointer; font: inherit; }
      .citem:hover { background: var(--sd-row-hover); }
      .citem.is-active { border-color: var(--sd-primary); box-shadow: inset 3px 0 0 var(--sd-primary); }
      .citem:focus-visible { outline: none; box-shadow: var(--sd-focus-ring); }
      .cname { font-weight: var(--sd-weight-medium); }
      .detail { display: grid; gap: var(--sd-space-4); align-content: start; min-width: 0; }
      fieldset.plain { border: 0; padding: 0; margin: 0; min-width: 0; }
      .row-end { display: flex; justify-content: flex-end; }
      .add-holiday, .preview-form { display: flex; flex-wrap: wrap; gap: var(--sd-space-2); align-items: end; margin-bottom: var(--sd-space-3); }
      .add-holiday sd-form-field, .preview-form sd-form-field { min-width: 160px; }
      .result { display: grid; gap: var(--sd-space-2); }
      .intervals { margin: 0; padding-left: 1.1em; columns: 2 260px; }
    `,
  ],
})
export class CalendarsPage {
  private readonly api = inject(SlaAdminApi);
  private readonly dialogs = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  protected readonly canManage = inject(MeStore).canAny(['admin.sla', 'admin.organisation']);
  protected readonly tabs = SLA_TABS;
  protected readonly calendars = signal<BusinessCalendarListItemDto[]>([]);
  protected readonly listLoading = signal(true);
  protected readonly listError = signal<string | null>(null);
  protected readonly selectedId = signal<string | null>(null);
  protected readonly editing = signal<BusinessCalendarDto | null>(null);
  protected readonly detailLoading = signal(false);
  protected readonly weekly = signal<WeeklyHoursDto>({});
  protected readonly is24x7 = signal(false);
  protected readonly isDefault = signal(false);
  protected readonly zones = computed(() => timeZoneOptions(this.editing()?.timeZone));
  protected readonly summary = computed(() => weeklySummary(this.weekly()));
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly weeklyError = computed(() => Object.entries(this.errors()).find(([k]) => k.startsWith('weeklyHours'))?.[1] ?? null);
  protected readonly formError = signal<string | null>(null);
  protected readonly holidayDate = signal<string | null>(null);
  protected holidayName = '';
  protected readonly holidayRecurring = signal(false);
  protected readonly addingHoliday = signal(false);
  protected readonly holidayError = signal<string | null>(null);
  protected readonly from = signal<string | null>(atLocal(0, 17));
  protected readonly to = signal<string | null>(atLocal(3, 10));
  protected addMinutes: number | null = 120;
  protected readonly previewing = signal(false);
  protected readonly previewError = signal<string | null>(null);
  protected readonly result = signal<BusinessTimePreviewDto | null>(null);

  constructor() {
    this.loadList(true);
  }

  protected loadList(selectFirst = false): void {
    this.listLoading.set(true);
    this.listError.set(null);
    this.api.calendars().subscribe({
      next: (list) => {
        this.calendars.set(list);
        this.listLoading.set(false);
        if (selectFirst && list.length && !this.selectedId()) this.select(list[0].id);
      },
      error: (err: unknown) => {
        this.listError.set(errorText(err));
        this.listLoading.set(false);
      },
    });
  }

  protected select(id: string): void {
    this.selectedId.set(id);
    this.detailLoading.set(true);
    this.result.set(null);
    this.errors.set({});
    this.formError.set(null);
    this.api.calendar(id).subscribe({
      next: (c) => {
        this.show(c);
        this.detailLoading.set(false);
      },
      error: (err: unknown) => {
        this.formError.set(errorText(err));
        this.detailLoading.set(false);
      },
    });
  }

  private show(c: BusinessCalendarDto): void {
    this.editing.set({ ...c });
    this.weekly.set(c.weeklyHours);
    this.is24x7.set(c.is24x7);
    this.isDefault.set(c.isDefault);
  }

  protected startNew(): void {
    this.selectedId.set(null);
    this.result.set(null);
    this.errors.set({});
    this.formError.set(null);
    const tz = Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
    const weekdays: WeeklyHoursDto = {};
    for (const d of ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday']) weekdays[d] = [{ start: '09:00', end: '18:00' }];
    this.show({ id: '', name: '', description: null, timeZone: tz, is24x7: false, isDefault: false, weeklyHours: weekdays, weeklyHoursTotal: 45, holidays: [], usedBy: [], rowVersion: '' });
  }

  protected save(): void {
    const e = this.editing();
    if (!e) return;
    this.saving.set(true);
    this.formError.set(null);
    this.errors.set({});
    this.api
      .saveCalendar(e.id || null, {
        name: e.name.trim(),
        description: e.description?.trim() || null,
        timeZone: e.timeZone,
        is24x7: this.is24x7(),
        isDefault: this.isDefault(),
        weeklyHours: this.weekly(),
        rowVersion: e.rowVersion || null,
      })
      .subscribe({
        next: (c) => {
          this.saving.set(false);
          this.toast.success('Calendar saved', c.name);
          this.selectedId.set(c.id);
          this.show(c);
          this.loadList();
        },
        error: (err: unknown) => {
          this.saving.set(false);
          this.errors.set(validationMessages(err));
          this.formError.set(saveErrorMessage(err));
        },
      });
  }

  protected async remove(): Promise<void> {
    const e = this.editing();
    if (!e?.id) return;
    const ok = await this.dialogs.confirm({ title: `Delete ${e.name}?`, message: 'Calendars in use cannot be deleted.', danger: true, confirmLabel: 'Delete' });
    if (!ok) return;
    this.api.deleteCalendar(e.id).subscribe({
      next: () => {
        this.toast.success('Calendar deleted', e.name);
        this.editing.set(null);
        this.selectedId.set(null);
        this.loadList(true);
      },
      error: (err: unknown) => this.toast.error('Could not delete', saveErrorMessage(err)),
    });
  }

  protected addHoliday(): void {
    const e = this.editing();
    const date = this.holidayDate();
    if (!e?.id || !date) return;
    this.addingHoliday.set(true);
    this.holidayError.set(null);
    this.api.saveHoliday(e.id, null, { date, name: this.holidayName.trim(), recurring: this.holidayRecurring() }).subscribe({
      next: () => {
        this.addingHoliday.set(false);
        this.holidayName = '';
        this.holidayDate.set(null);
        this.select(e.id);
        this.loadList();
      },
      error: (err: unknown) => {
        this.addingHoliday.set(false);
        this.holidayError.set(saveErrorMessage(err));
      },
    });
  }

  protected removeHoliday(h: CalendarHolidayDto): void {
    const e = this.editing();
    if (!e?.id) return;
    this.api.deleteHoliday(e.id, h.id).subscribe(() => {
      this.select(e.id);
      this.loadList();
    });
  }

  protected importCsv(): void {
    const e = this.editing();
    if (!e?.id) return;
    this.dialogs.open<ImportHolidaysResultDto, string>(ImportHolidaysDialog, { data: e.id, size: 'lg' }).closed.subscribe((r) => {
      if (!r) return;
      this.toast.success('Holidays imported', `${r.added} added, ${r.updated} updated, ${r.skipped} skipped`);
      this.select(e.id);
      this.loadList();
    });
  }

  protected preview(): void {
    const e = this.editing();
    if (!e?.id || !this.from() || !this.to()) return;
    this.previewing.set(true);
    this.previewError.set(null);
    const add = this.addMinutes === null || (this.addMinutes as unknown) === '' ? null : Number(this.addMinutes);
    this.api.preview(e.id, { from: this.from()!, to: this.to()!, addMinutes: add }).subscribe({
      next: (r) => {
        this.result.set(r);
        this.previewing.set(false);
      },
      error: (err: unknown) => {
        this.previewError.set(saveErrorMessage(err));
        this.previewing.set(false);
      },
    });
  }

  protected minutes(m: number): string {
    return formatMinutes(m);
  }

  protected holidayNames(list: CalendarHolidayDto[]): string {
    return list.map((h) => `${h.name} (${h.date})`).join(', ');
  }
}

/** CSV holiday import: "date,name[,recurring]" per line; shows per-line errors. */
@Component({
  selector: 'desk-import-holidays-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdCheckbox],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Import holidays" description="One holiday per line: date,name[,recurring]. Dates as yyyy-MM-dd or dd/MM/yyyy; recurring yes/no.">
      <form id="import-form" class="form" (ngSubmit)="run()">
        <sd-form-field label="CSV" required>
          <textarea sdInput name="csv" rows="10" [ngModel]="csv()" (ngModelChange)="csv.set($event)" class="mono" placeholder="date,name,recurring&#10;2026-01-26,Republic Day,yes&#10;2026-03-04,Holi,no"></textarea>
        </sd-form-field>
        <label class="file">
          <input type="file" accept=".csv,text/csv,text/plain" (change)="readFile($event)" />
          <span class="sd-visually-hidden">Choose a CSV file</span>
        </label>
        <sd-checkbox [(value)]="replace">Replace all existing holidays</sd-checkbox>
        @if (result(); as r) {
          <div role="status">
            <p>{{ r.added }} added · {{ r.updated }} updated · {{ r.skipped }} skipped</p>
            @if (r.errors.length) {
              <ul class="errs">
                @for (e of r.errors; track $index) {
                  <li>{{ e }}</li>
                }
              </ul>
            }
          </div>
        }
        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
      </form>
      <div sdDialogFooter>
        @if (result()) {
          <button sdButton variant="primary" type="button" (click)="ref.close(result()!)">Done</button>
        } @else {
          <button sdButton type="button" (click)="ref.close()">Cancel</button>
          <button sdButton variant="primary" type="submit" form="import-form" icon="upload" [loading]="running()" [disabled]="!csv().trim()">Import</button>
        }
      </div>
    </sd-dialog>
  `,
  styles: [FORM_STYLES, `.mono { font-family: var(--sd-font-mono); font-size: var(--sd-text-sm); } .errs { color: var(--sd-danger); font-size: var(--sd-text-sm); }`],
})
export class ImportHolidaysDialog {
  private readonly calendarId = inject<string>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<ImportHolidaysResultDto>>(DialogRef);
  private readonly api = inject(SlaAdminApi);
  protected readonly csv = signal('');
  protected readonly replace = signal(false);
  protected readonly running = signal(false);
  protected readonly result = signal<ImportHolidaysResultDto | null>(null);
  protected readonly error = signal<string | null>(null);

  protected readFile(event: Event): void {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (!file) return;
    if (file.size > 512 * 1024) {
      this.error.set('The file is too large (max 512 KB).');
      return;
    }
    void file.text().then((text) => this.csv.set(text));
  }

  protected run(): void {
    this.running.set(true);
    this.error.set(null);
    this.api.importHolidays(this.calendarId, { csv: this.csv(), replaceExisting: this.replace() }).subscribe({
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

/** Local time (today + days at hour) as a UTC ISO string. */
function atLocal(days: number, hour: number): string {
  const d = new Date();
  d.setDate(d.getDate() + days);
  d.setHours(hour, 0, 0, 0);
  return d.toISOString();
}
