import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { AnalyticsApi, AnalyticsScheduleDto, AnalyticsScheduleRunDto, ApiError } from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdDateTimePipe,
  SdDialogService,
  SdDrawer,
  SdDropdownMenu,
  SdEmptyState,
  SdFileSizePipe,
  SdMenuItem,
  SdRelativeTimePipe,
  SdSkeleton,
  ToastService,
} from '@sd/ui';
import { AnalyticsStore } from './analytics.store';
import { ScheduleFormData, ScheduleFormDialog } from './schedule-form.dialog';
import './analytics-format';

/** /analytics/schedules — scheduled report e-mails: create, edit, pause, run now, delete and delivery history. */
@Component({
  selector: 'desk-analytics-schedules',
  imports: [SdBadge, SdButton, SdDateTimePipe, SdDrawer, SdDropdownMenu, SdEmptyState, SdFileSizePipe, SdRelativeTimePipe, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (store.catalog() && !store.catalog()!.canSchedule) {
      <sd-empty-state icon="lock" title="Scheduling is not available" message="Ask an administrator for the “analytics.schedule” permission." />
    } @else {
      <div class="toolbar">
        <p class="sd-small sd-muted intro">Reports are rendered with the owner's data scope and e-mailed as attachments.</p>
        <button sdButton variant="primary" size="sm" icon="plus" type="button" (click)="edit(null)">New schedule</button>
      </div>
      @if (loading() && items().length === 0) {
        <div aria-busy="true"><sd-skeleton [lines]="6" /></div>
      } @else if (error()) {
        <sd-empty-state icon="circle-alert" title="Schedules could not be loaded" [message]="error()">
          <button sdButton type="button" (click)="load()">Retry</button>
        </sd-empty-state>
      } @else if (items().length === 0) {
        <sd-empty-state icon="calendar-clock" title="No scheduled reports" message="Schedule any standard report to arrive by e-mail daily, weekly or monthly.">
          <button sdButton variant="primary" icon="plus" type="button" (click)="edit(null)">New schedule</button>
        </sd-empty-state>
      } @else {
        <ul class="list">
          @for (s of items(); track s.id) {
            <li class="item">
              <div class="main">
                <div class="line1">
                  <span class="name">{{ s.name }}</span>
                  <sd-badge [tone]="s.isActive ? 'success' : 'neutral'" size="sm">{{ s.isActive ? 'Active' : 'Paused' }}</sd-badge>
                  <sd-badge size="sm" variant="outline">{{ s.format === 'Xlsx' ? 'Excel' : s.format.toUpperCase() }}</sd-badge>
                </div>
                <div class="sd-small sd-muted">
                  {{ s.reportName }} · {{ cadence(s) }} · {{ periodLabel(s) }} · {{ s.recipients.length }} recipient{{ s.recipients.length === 1 ? '' : 's' }}
                  @if (s.ownerName) {
                    · owner {{ s.ownerName }}
                  }
                </div>
                <div class="sd-xs sd-muted">
                  Next run {{ s.nextRunAt | sdDateTime }}
                  @if (s.lastRunAt) {
                    · last run {{ s.lastRunAt | sdRelativeTime }}: {{ s.lastStatus }}
                  }
                </div>
              </div>
              <div class="actions">
                <button sdButton size="sm" icon="history" type="button" (click)="history(s)">History</button>
                @if (s.canEdit) {
                  <sd-dropdown-menu size="sm" icon="ellipsis" iconOnly [ariaLabel]="'Actions for ' + s.name" align="end" [items]="menu(s)" (select)="onMenu(s, $event)" />
                }
              </div>
            </li>
          }
        </ul>
      }
    }

    <sd-drawer [(open)]="historyOpen" [title]="'Delivery history · ' + (historyFor()?.name ?? '')" ariaLabel="Delivery history" width="520px">
      @if (runsLoading()) {
        <sd-skeleton [lines]="6" />
      } @else if (runsError()) {
        <sd-empty-state icon="circle-alert" title="History could not be loaded" [message]="runsError()" compact />
      } @else if (runs().length === 0) {
        <sd-empty-state icon="history" title="No deliveries yet" message="Runs appear here once the schedule fires or you run it now." compact />
      } @else {
        <ul class="runs">
          @for (r of runs(); track r.id) {
            <li class="run">
              <div class="line1">
                <sd-badge [tone]="r.status === 'Succeeded' ? 'success' : r.status === 'Failed' ? 'danger' : 'info'" size="sm">{{ r.status }}</sd-badge>
                <span class="sd-small">{{ r.manual ? 'Run now' : 'Scheduled' }} · {{ r.scheduledFor | sdDateTime }}</span>
              </div>
              <div class="sd-xs sd-muted">
                Delivered to {{ r.delivered }} of {{ r.recipients.length }}
                @if (r.fileName) {
                  · {{ r.fileName }} ({{ r.sizeBytes | sdFileSize }}, {{ r.rowCount }} rows)
                }
              </div>
              @if (r.error) {
                <div class="sd-xs err">{{ r.error }}</div>
              }
            </li>
          }
        </ul>
      }
    </sd-drawer>
  `,
  styles: `
    :host {
      display: grid;
      grid-template-columns: minmax(0, 1fr);
      gap: var(--sd-space-3);
    }
    .toolbar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: var(--sd-space-2);
    }
    .intro {
      margin: 0;
    }
    .list,
    .runs {
      display: grid;
      gap: var(--sd-space-2);
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .item {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--sd-space-3);
      padding: 12px 14px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    .main {
      display: grid;
      flex: 1;
      gap: 3px;
      min-width: 220px;
    }
    .line1 {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
    }
    .name {
      font-weight: var(--sd-weight-semibold);
    }
    .actions {
      display: flex;
      gap: var(--sd-space-2);
    }
    .run {
      display: grid;
      gap: 3px;
      padding: 8px 0;
      border-bottom: 1px solid var(--sd-divider);
    }
    .err {
      color: var(--sd-danger);
    }
  `,
})
export class AnalyticsSchedulesPage {
  private readonly api = inject(AnalyticsApi);
  private readonly dialogs = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  protected readonly store = inject(AnalyticsStore);

  protected readonly items = signal<AnalyticsScheduleDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly historyOpen = signal(false);
  protected readonly historyFor = signal<AnalyticsScheduleDto | null>(null);
  protected readonly runs = signal<AnalyticsScheduleRunDto[]>([]);
  protected readonly runsLoading = signal(false);
  protected readonly runsError = signal<string | null>(null);

  constructor() {
    this.store.ensure();
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.schedules().subscribe({
      next: (list) => {
        this.items.set(list);
        this.loading.set(false);
      },
      error: (e: unknown) => {
        const err = ApiError.from(e);
        this.error.set(err.status === 403 ? 'You do not have permission to manage scheduled reports.' : err.userMessage);
        this.loading.set(false);
      },
    });
  }

  protected cadence(s: AnalyticsScheduleDto): string {
    const at = `${s.runAtLocal} ${s.timeZone}`;
    if (s.frequency === 'Weekly') return `Every ${s.dayOfWeek} at ${at}`;
    if (s.frequency === 'Monthly') return `Monthly on day ${s.dayOfMonth} at ${at}`;
    return `Daily at ${at}`;
  }

  protected periodLabel(s: AnalyticsScheduleDto): string {
    return this.store.periodLabel(s.parameters['period'] ?? 'last_30_days');
  }

  protected menu(s: AnalyticsScheduleDto): SdMenuItem[] {
    return [
      { id: 'run', label: 'Run now', icon: 'play' },
      { id: 'edit', label: 'Edit', icon: 'pencil' },
      { id: 'toggle', label: s.isActive ? 'Pause' : 'Resume', icon: s.isActive ? 'pause' : 'play' },
      { id: 'divider', divider: true },
      { id: 'delete', label: 'Delete', icon: 'trash', danger: true },
    ];
  }

  protected onMenu(s: AnalyticsScheduleDto, item: SdMenuItem): void {
    switch (item.id) {
      case 'run':
        this.api.runSchedule(s.id).subscribe({
          next: () => this.toast.success('Report queued', 'It will be e-mailed shortly; see History for the delivery.'),
          error: (e: unknown) => this.toast.error('Could not run the report', ApiError.from(e).userMessage),
        });
        break;
      case 'edit':
        this.edit(s);
        break;
      case 'toggle':
        this.api
          .updateSchedule(s.id, {
            name: s.name, reportKey: s.reportKey, parameters: s.parameters, format: s.format, frequency: s.frequency, runAtLocal: s.runAtLocal,
            dayOfWeek: s.dayOfWeek, dayOfMonth: s.dayOfMonth, timeZone: s.timeZone, recipients: s.recipients, isActive: !s.isActive, rowVersion: s.rowVersion,
          })
          .subscribe({
            next: (saved) => this.items.update((list) => list.map((x) => (x.id === saved.id ? saved : x))),
            error: (e: unknown) => this.toast.error('Could not update the schedule', ApiError.from(e).userMessage),
          });
        break;
      case 'delete':
        void this.remove(s);
        break;
    }
  }

  protected edit(s: AnalyticsScheduleDto | null): void {
    const ref = this.dialogs.open<AnalyticsScheduleDto, ScheduleFormData>(ScheduleFormDialog, { data: { schedule: s }, size: 'lg' });
    ref.closed.subscribe((saved) => {
      if (!saved) return;
      this.toast.success(s ? 'Schedule saved' : 'Report scheduled', saved.name);
      this.load();
    });
  }

  private async remove(s: AnalyticsScheduleDto): Promise<void> {
    const ok = await this.dialogs.confirm({ title: `Delete “${s.name}”?`, message: 'Recipients will stop receiving this report. Delivery history is removed.', danger: true, confirmLabel: 'Delete' });
    if (!ok) return;
    this.api.deleteSchedule(s.id).subscribe(() => {
      this.items.update((list) => list.filter((x) => x.id !== s.id));
      this.toast.success('Schedule deleted');
    });
  }

  protected history(s: AnalyticsScheduleDto): void {
    this.historyFor.set(s);
    this.historyOpen.set(true);
    this.runsLoading.set(true);
    this.runsError.set(null);
    this.api.scheduleRuns(s.id).subscribe({
      next: (r) => {
        this.runs.set(r);
        this.runsLoading.set(false);
      },
      error: (e: unknown) => {
        this.runsError.set(ApiError.from(e).userMessage);
        this.runsLoading.set(false);
      },
    });
  }
}
