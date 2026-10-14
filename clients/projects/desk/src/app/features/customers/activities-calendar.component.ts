import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { Guid, MeStore, ServiceActivitiesApi, ServiceActivityDto } from '@sd/core';
import { SdBadge, SdButton, SdCalendar, SdCalendarEvent, SdDateTimePipe, SdDialogService } from '@sd/ui';
import { ActivityFormData, ActivityFormDialog } from './activity-form.dialog';
import { humanize, statusTone } from './shared/customer-ui';
import { SECTION_STYLES, TABLE_STYLES } from './shared/styles';

const TYPE_COLORS: Record<string, string> = {
  OnsiteVisit: 'var(--sd-chart-1)',
  RemoteSession: 'var(--sd-chart-2)',
  Training: 'var(--sd-chart-3)',
  PreventiveMaintenance: 'var(--sd-chart-4)',
  Upgrade: 'var(--sd-chart-5)',
  Call: 'var(--sd-chart-6)',
  Meeting: 'var(--sd-chart-7)',
};

/**
 * Service visits & scheduled activities as a month/week calendar plus an agenda list:
 * `<desk-activities-calendar [customerId]="id" [customerName]="name" />` (omit customerId for all customers).
 */
@Component({
  selector: 'desk-activities-calendar',
  imports: [SdCalendar, SdButton, SdBadge, SdDateTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="head">
      <div class="legend" aria-label="Activity types">
        @for (t of legend; track t.type) {
          <span class="lg"><span class="sw" [style.background]="t.color"></span>{{ t.label }}</span>
        }
      </div>
      <span class="sd-spacer"></span>
      @if (canManage) {
        <button sdButton size="sm" variant="primary" icon="plus" (click)="openForm(null, null)">Schedule activity</button>
      }
    </div>
    <sd-calendar [events]="events()" [(view)]="view" [(date)]="date" (rangeChange)="range.set($event)" (eventClick)="onEvent($event)" (dayClick)="openForm(null, $event)" />

    <div class="section-head">
      <h2>Agenda</h2>
      <span class="sd-small sd-muted">{{ activities().length }} in view</span>
    </div>
    @if (activities().length === 0) {
      <p class="empty-note">Nothing scheduled in this period.</p>
    } @else {
      <div class="dt-wrap">
        <table class="dt">
          <thead>
            <tr>
              <th scope="col">When</th>
              <th scope="col">Activity</th>
              @if (!customerId()) {
                <th scope="col">Customer</th>
              }
              <th scope="col">Assigned</th>
              <th scope="col">Status</th>
            </tr>
          </thead>
          <tbody>
            @for (a of activities(); track a.id) {
              <tr class="clickable" tabindex="0" (click)="openForm(a, null)" (keydown.enter)="openForm(a, null)">
                <td class="nowrap sd-small">{{ a.scheduledStart | sdDateTime: 'datetime' : 'short' }}</td>
                <td>
                  {{ a.title }}
                  <div class="sd-xs sd-muted">{{ humanize(a.type) }}@if (a.installationName) { · {{ a.installationName }} }</div>
                </td>
                @if (!customerId()) {
                  <td>{{ a.customerName }}</td>
                }
                <td>{{ a.assignedUserName ?? '—' }}</td>
                <td><sd-badge [tone]="statusTone(a.status)" size="sm" dot>{{ humanize(a.status) }}</sd-badge></td>
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
      .head {
        display: flex;
        align-items: center;
        gap: var(--sd-space-3);
        flex-wrap: wrap;
        margin-bottom: var(--sd-space-3);
      }
      .legend {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sd-space-3);
        font-size: var(--sd-text-xs);
        color: var(--sd-text-2);
      }
      .lg {
        display: inline-flex;
        align-items: center;
        gap: 4px;
      }
      .sw {
        width: 10px;
        height: 10px;
        border-radius: 3px;
      }
    `,
  ],
})
export class ActivitiesCalendarComponent {
  private readonly api = inject(ServiceActivitiesApi);
  private readonly dialog = inject(SdDialogService);
  protected readonly canManage = inject(MeStore).can('customers.manage');

  readonly customerId = input<Guid | null>(null);
  readonly customerName = input<string | null>(null);

  protected readonly humanize = humanize;
  protected readonly statusTone = statusTone;
  protected readonly view = signal<'month' | 'week'>('month');
  protected readonly date = signal(new Date());
  protected readonly range = signal<{ start: Date; end: Date } | null>(null);
  protected readonly activities = signal<ServiceActivityDto[]>([]);
  protected readonly legend = Object.entries(TYPE_COLORS).map(([type, color]) => ({ type, color, label: humanize(type) }));

  protected readonly events = computed<SdCalendarEvent[]>(() =>
    this.activities().map((a) => ({
      id: a.id,
      title: this.customerId() ? a.title : `${a.customerName}: ${a.title}`,
      start: a.scheduledStart,
      end: a.scheduledEnd,
      color: a.status === 'Cancelled' || a.status === 'Missed' ? 'var(--sd-text-3)' : TYPE_COLORS[a.type],
      data: a,
    })),
  );

  constructor() {
    effect(() => {
      this.customerId();
      const r = this.range();
      untracked(() => this.load(r));
    });
  }

  private load(r: { start: Date; end: Date } | null): void {
    const start = r?.start ?? new Date(new Date().getFullYear(), new Date().getMonth() - 1, 20);
    const end = r?.end ?? new Date(new Date().getFullYear(), new Date().getMonth() + 1, 10);
    this.api.list({ customerId: this.customerId(), from: start.toISOString(), to: end.toISOString(), limit: 500 }).subscribe((a) => this.activities.set(a));
  }

  protected onEvent(e: SdCalendarEvent): void {
    this.openForm(e.data as ServiceActivityDto, null);
  }

  protected openForm(activity: ServiceActivityDto | null, day: Date | null): void {
    if (!activity && !this.canManage) return;
    this.dialog
      .open<ServiceActivityDto | null, ActivityFormData>(ActivityFormDialog, {
        data: { activity, customerId: this.customerId(), customerName: this.customerName(), start: day },
        size: 'lg',
      })
      .closed.subscribe((result) => {
        if (result !== undefined) this.load(this.range());
      });
  }
}
