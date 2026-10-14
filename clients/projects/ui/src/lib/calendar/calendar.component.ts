import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  model,
  output,
  signal,
  untracked,
} from '@angular/core';
import { MeStore, addDays, addMonths, isSameDay, startOfDay, startOfWeek, toDate } from '@sd/core';
import { SdButton } from '../button/button.component';

export interface SdCalendarEvent {
  id: string;
  title: string;
  start: string | Date;
  end?: string | Date | null;
  allDay?: boolean;
  /** CSS colour for the event chip (defaults to the primary colour). */
  color?: string | null;
  data?: unknown;
}

interface PlacedEvent {
  event: SdCalendarEvent;
  top: number;
  height: number;
  lane: number;
  lanes: number;
  timeLabel: string;
}

const HOUR_PX = 44;

/**
 * `<sd-calendar [events]="shifts" [(view)]="view" [(date)]="cursor" (eventClick)="open($event)"
 *    (rangeChange)="load($event.start, $event.end)" />`
 * Month grid (6 weeks) and week time-grid (all-day lane + hourly slots, overlapping events side by side).
 * Keyboard: arrows move between days, PageUp/PageDown change month, Enter selects a day.
 */
@Component({
  selector: 'sd-calendar',
  imports: [SdButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="bar">
      <button sdButton size="sm" type="button" (click)="goToday()">Today</button>
      <button sdButton size="sm" variant="ghost" icon="chevron-left" iconOnly type="button" [attr.aria-label]="view() === 'month' ? 'Previous month' : 'Previous week'" (click)="shift(-1)"></button>
      <button sdButton size="sm" variant="ghost" icon="chevron-right" iconOnly type="button" [attr.aria-label]="view() === 'month' ? 'Next month' : 'Next week'" (click)="shift(1)"></button>
      <h2 class="title" aria-live="polite">{{ title() }}</h2>
      <span class="sd-spacer"></span>
      <div class="views" role="radiogroup" aria-label="Calendar view">
        <button type="button" role="radio" [attr.aria-checked]="view() === 'month'" (click)="view.set('month')">Month</button>
        <button type="button" role="radio" [attr.aria-checked]="view() === 'week'" (click)="view.set('week')">Week</button>
      </div>
    </div>

    <div class="weekdays" aria-hidden="true" [class.weekdays--week]="view() === 'week'">
      @if (view() === 'week') {
        <span class="gutter"></span>
      }
      @for (d of weekdayHeads(); track $index) {
        <span [class.is-today]="view() === 'week' && d.today">{{ d.label }}</span>
      }
    </div>

    @if (view() === 'month') {
      <div class="month" role="grid" [attr.aria-label]="title()" (keydown)="onGridKey($event)">
        @for (week of monthWeeks(); track $index) {
          <div class="week" role="row">
            @for (day of week; track day.date.getTime()) {
              <div class="day" role="gridcell" [class.is-out]="!day.inMonth" [class.is-today]="day.today">
                <button
                  type="button"
                  class="day-num"
                  [attr.data-date]="day.key"
                  [attr.tabindex]="day.focused ? 0 : -1"
                  [attr.aria-label]="day.label"
                  [attr.aria-current]="day.today ? 'date' : null"
                  (click)="selectDay(day.date)"
                >
                  {{ day.date.getDate() }}
                </button>
                @for (ev of day.events.slice(0, maxPerDay()); track ev.id) {
                  <button type="button" class="chip" [style.--_c]="ev.color || null" (click)="eventClick.emit(ev)" [title]="ev.title">
                    @if (!ev.allDay) {
                      <span class="chip-time">{{ timeOf(ev.start) }}</span>
                    }
                    <span class="sd-truncate">{{ ev.title }}</span>
                  </button>
                }
                @if (day.events.length > maxPerDay()) {
                  <button type="button" class="more" (click)="openWeek(day.date)">+{{ day.events.length - maxPerDay() }} more</button>
                }
              </div>
            }
          </div>
        }
      </div>
    } @else {
      <div class="weekview">
        <div class="allday">
          <span class="gutter sd-xs sd-muted">all-day</span>
          @for (day of weekDays(); track day.date.getTime()) {
            <div class="allday-cell">
              @for (ev of day.allDay; track ev.id) {
                <button type="button" class="chip" [style.--_c]="ev.color || null" (click)="eventClick.emit(ev)">
                  <span class="sd-truncate">{{ ev.title }}</span>
                </button>
              }
            </div>
          }
        </div>
        <div class="timegrid" [style.--_hour.px]="hourPx">
          <div class="hours" aria-hidden="true">
            @for (h of hours(); track h) {
              <span class="hour-label">{{ hourLabel(h) }}</span>
            }
          </div>
          @for (day of weekDays(); track day.date.getTime()) {
            <div class="col" [class.is-today]="day.today" role="group" [attr.aria-label]="day.label" (dblclick)="selectDay(day.date)">
              @for (h of hours(); track h) {
                <div class="slot"></div>
              }
              @for (p of day.placed; track p.event.id) {
                <button
                  type="button"
                  class="block"
                  [style.--_c]="p.event.color || null"
                  [style.top.px]="p.top"
                  [style.height.px]="p.height"
                  [style.left]="(p.lane / p.lanes) * 100 + '%'"
                  [style.width]="'calc(' + 100 / p.lanes + '% - 3px)'"
                  [attr.aria-label]="p.event.title + ', ' + p.timeLabel"
                  (click)="eventClick.emit(p.event)"
                >
                  <span class="block-title">{{ p.event.title }}</span>
                  <span class="block-time">{{ p.timeLabel }}</span>
                </button>
              }
            </div>
          }
        </div>
      </div>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      min-height: 0;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
      overflow: hidden;
    }
    .bar {
      display: flex;
      align-items: center;
      gap: 6px;
      padding: 10px 12px;
      border-bottom: 1px solid var(--sd-divider);
      flex-wrap: wrap;
    }
    .title {
      margin-left: 6px;
      font-size: var(--sd-text-lg);
    }
    .views {
      display: inline-flex;
      padding: 2px;
      border-radius: var(--sd-radius-sm);
      background: var(--sd-surface-3);
    }
    .views button {
      height: 26px;
      padding: 0 10px;
      border: 0;
      border-radius: 4px;
      background: transparent;
      color: var(--sd-text-2);
      font-weight: var(--sd-weight-medium);
      cursor: pointer;
    }
    .views button[aria-checked='true'] {
      background: var(--sd-surface);
      color: var(--sd-text);
      box-shadow: var(--sd-shadow-xs);
    }
    .weekdays {
      display: grid;
      grid-template-columns: repeat(7, 1fr);
      border-bottom: 1px solid var(--sd-divider);
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .weekdays--week {
      grid-template-columns: 52px repeat(7, 1fr);
    }
    .weekdays span {
      padding: 6px 8px;
    }
    .weekdays span.is-today {
      color: var(--sd-primary);
    }
    .month {
      display: grid;
      grid-template-rows: repeat(6, minmax(92px, 1fr));
      flex: 1;
    }
    .week {
      display: grid;
      grid-template-columns: repeat(7, minmax(0, 1fr));
    }
    .day {
      display: flex;
      flex-direction: column;
      gap: 2px;
      min-width: 0;
      padding: 4px;
      border-right: 1px solid var(--sd-divider);
      border-bottom: 1px solid var(--sd-divider);
    }
    .day.is-out {
      background: var(--sd-surface-2);
    }
    .day.is-out .day-num {
      color: var(--sd-text-3);
    }
    .day-num {
      align-self: flex-start;
      min-width: 24px;
      height: 24px;
      padding: 0 6px;
      border: 0;
      border-radius: var(--sd-radius-pill);
      background: transparent;
      color: var(--sd-text);
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-medium);
      cursor: pointer;
    }
    .day-num:hover {
      background: var(--sd-surface-3);
    }
    .day.is-today .day-num {
      background: var(--sd-primary);
      color: var(--sd-on-primary);
    }
    .chip {
      --_c: var(--sd-primary);
      display: flex;
      align-items: center;
      gap: 4px;
      width: 100%;
      min-width: 0;
      height: 20px;
      padding: 0 6px;
      border: 0;
      border-left: 3px solid var(--_c);
      border-radius: 4px;
      background: color-mix(in oklab, var(--_c) 13%, var(--sd-surface));
      color: var(--sd-text);
      font-size: 11.5px;
      text-align: left;
      cursor: pointer;
    }
    .chip-time {
      color: var(--sd-text-2);
      font-variant-numeric: tabular-nums;
      flex: none;
    }
    .more {
      align-self: flex-start;
      border: 0;
      background: transparent;
      color: var(--sd-text-2);
      font-size: 11.5px;
      cursor: pointer;
      padding: 0 4px;
    }
    .more:hover {
      color: var(--sd-primary);
    }
    .weekview {
      display: flex;
      flex-direction: column;
      flex: 1;
      min-height: 0;
    }
    .allday {
      display: grid;
      grid-template-columns: 52px repeat(7, minmax(0, 1fr));
      border-bottom: 1px solid var(--sd-border);
    }
    .allday .gutter {
      padding: 4px;
      text-align: right;
    }
    .allday-cell {
      display: flex;
      flex-direction: column;
      gap: 2px;
      padding: 3px;
      border-left: 1px solid var(--sd-divider);
      min-height: 28px;
    }
    .timegrid {
      display: grid;
      grid-template-columns: 52px repeat(7, minmax(0, 1fr));
      overflow: auto;
      max-height: 560px;
    }
    .hours {
      display: flex;
      flex-direction: column;
    }
    .hour-label {
      height: var(--_hour);
      padding: 0 6px;
      color: var(--sd-text-3);
      font-size: 11px;
      text-align: right;
      transform: translateY(-6px);
    }
    .col {
      position: relative;
      border-left: 1px solid var(--sd-divider);
    }
    .col.is-today {
      background: color-mix(in oklab, var(--sd-primary) 4%, var(--sd-surface));
    }
    .slot {
      height: var(--_hour);
      border-bottom: 1px solid var(--sd-divider);
    }
    .block {
      --_c: var(--sd-primary);
      position: absolute;
      display: flex;
      flex-direction: column;
      gap: 1px;
      margin-left: 1px;
      padding: 2px 5px;
      overflow: hidden;
      border: 0;
      border-left: 3px solid var(--_c);
      border-radius: 4px;
      background: color-mix(in oklab, var(--_c) 16%, var(--sd-surface));
      color: var(--sd-text);
      font-size: 11.5px;
      text-align: left;
      cursor: pointer;
    }
    .block:hover {
      background: color-mix(in oklab, var(--_c) 24%, var(--sd-surface));
    }
    .block-title {
      font-weight: var(--sd-weight-semibold);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .block-time {
      color: var(--sd-text-2);
    }
    @media (max-width: 640px) {
      .month {
        grid-template-rows: repeat(6, minmax(64px, 1fr));
      }
      .chip-time {
        display: none;
      }
    }
  `,
})
export class SdCalendar {
  readonly events = input<SdCalendarEvent[]>([]);
  readonly view = model<'month' | 'week'>('month');
  readonly date = model<Date>(new Date());
  /** 0 = Sunday, 1 = Monday. */
  readonly weekStartsOn = input(1);
  readonly maxPerDay = input(3);
  readonly hourStart = input(0);
  readonly hourEnd = input(24);
  readonly eventClick = output<SdCalendarEvent>();
  readonly dayClick = output<Date>();
  readonly rangeChange = output<{ start: Date; end: Date }>();

  protected readonly hourPx = HOUR_PX;
  private readonly me = inject(MeStore);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly focused = signal<Date>(startOfDay(new Date()));
  private readonly locale = computed(() => this.me.user()?.locale || undefined);

  private readonly normalised = computed(() =>
    this.events()
      .map((e) => {
        const start = toDate(e.start);
        if (!start) return null;
        const end = toDate(e.end ?? null) ?? new Date(start.getTime() + 30 * 60_000);
        return { e, start, end };
      })
      .filter((x): x is { e: SdCalendarEvent; start: Date; end: Date } => x !== null)
      .sort((a, b) => a.start.getTime() - b.start.getTime()),
  );

  protected readonly range = computed(() => {
    const d = this.date();
    if (this.view() === 'week') {
      const start = startOfWeek(d, this.weekStartsOn());
      return { start, end: addDays(start, 7) };
    }
    const first = new Date(d.getFullYear(), d.getMonth(), 1);
    const start = startOfWeek(first, this.weekStartsOn());
    return { start, end: addDays(start, 42) };
  });

  protected readonly title = computed(() => {
    const d = this.date();
    const loc = this.locale();
    if (this.view() === 'month') return new Intl.DateTimeFormat(loc, { month: 'long', year: 'numeric' }).format(d);
    const { start } = this.range();
    const end = addDays(start, 6);
    const f = new Intl.DateTimeFormat(loc, { month: 'short', day: 'numeric' });
    return `${f.format(start)} – ${f.format(end)}, ${end.getFullYear()}`;
  });

  protected readonly weekdayHeads = computed(() => {
    const start = this.view() === 'week' ? this.range().start : startOfWeek(new Date(), this.weekStartsOn());
    const f = new Intl.DateTimeFormat(this.locale(), this.view() === 'week' ? { weekday: 'short', day: 'numeric' } : { weekday: 'short' });
    const today = new Date();
    return Array.from({ length: 7 }, (_, i) => {
      const d = addDays(start, i);
      return { label: f.format(d), today: isSameDay(d, today) };
    });
  });

  protected readonly monthWeeks = computed(() => {
    const { start } = this.range();
    const month = this.date().getMonth();
    const today = new Date();
    const focused = this.focused();
    const labelFmt = new Intl.DateTimeFormat(this.locale(), { weekday: 'long', month: 'long', day: 'numeric' });
    const weeks: {
      date: Date;
      key: string;
      inMonth: boolean;
      today: boolean;
      focused: boolean;
      label: string;
      events: SdCalendarEvent[];
    }[][] = [];
    for (let w = 0; w < 6; w++) {
      const row = [];
      for (let i = 0; i < 7; i++) {
        const date = addDays(start, w * 7 + i);
        const events = this.eventsOn(date).map((x) => x.e);
        row.push({
          date,
          key: date.toDateString(),
          inMonth: date.getMonth() === month,
          today: isSameDay(date, today),
          focused: isSameDay(date, focused),
          label: `${labelFmt.format(date)}${events.length ? `, ${events.length} events` : ''}`,
          events,
        });
      }
      weeks.push(row);
    }
    return weeks;
  });

  protected readonly hours = computed(() =>
    Array.from({ length: Math.max(1, this.hourEnd() - this.hourStart()) }, (_, i) => this.hourStart() + i),
  );

  protected readonly weekDays = computed(() => {
    const { start } = this.range();
    const today = new Date();
    const labelFmt = new Intl.DateTimeFormat(this.locale(), { weekday: 'long', month: 'long', day: 'numeric' });
    return Array.from({ length: 7 }, (_, i) => {
      const date = addDays(start, i);
      const items = this.eventsOn(date);
      const allDay = items.filter((x) => x.e.allDay || x.end.getTime() - x.start.getTime() >= 24 * 3600_000).map((x) => x.e);
      const timed = items.filter((x) => !x.e.allDay && x.end.getTime() - x.start.getTime() < 24 * 3600_000);
      return { date, today: isSameDay(date, today), label: labelFmt.format(date), allDay, placed: this.place(date, timed) };
    });
  });

  constructor() {
    effect(() => {
      const r = this.range();
      untracked(() => this.rangeChange.emit(r));
    });
  }

  protected goToday(): void {
    const today = startOfDay(new Date());
    this.focused.set(today);
    this.date.set(today);
  }

  protected shift(delta: number): void {
    const d = this.date();
    const next = this.view() === 'month' ? addMonths(d, delta) : addDays(d, delta * 7);
    this.date.set(next);
    this.focused.set(startOfDay(next));
  }

  protected selectDay(date: Date): void {
    this.focused.set(startOfDay(date));
    this.dayClick.emit(date);
  }

  protected openWeek(date: Date): void {
    this.date.set(date);
    this.view.set('week');
  }

  protected timeOf(value: string | Date): string {
    const d = toDate(value);
    return d ? new Intl.DateTimeFormat(this.locale(), { hour: 'numeric', minute: '2-digit' }).format(d) : '';
  }

  protected hourLabel(h: number): string {
    return new Intl.DateTimeFormat(this.locale(), { hour: 'numeric' }).format(new Date(2000, 0, 1, h));
  }

  protected onGridKey(event: KeyboardEvent): void {
    const deltas: Record<string, number> = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -7, ArrowDown: 7 };
    let next: Date | null = null;
    const current = this.focused();
    if (event.key in deltas) next = addDays(current, deltas[event.key]);
    else if (event.key === 'PageUp') next = new Date(current.getFullYear(), current.getMonth() - 1, current.getDate());
    else if (event.key === 'PageDown') next = new Date(current.getFullYear(), current.getMonth() + 1, current.getDate());
    else if (event.key === 'Home') next = addDays(current, -((current.getDay() - this.weekStartsOn() + 7) % 7));
    else if (event.key === 'End') next = addDays(current, 6 - ((current.getDay() - this.weekStartsOn() + 7) % 7));
    if (!next) return;
    event.preventDefault();
    this.focused.set(startOfDay(next));
    if (next.getMonth() !== this.date().getMonth()) this.date.set(next);
    queueMicrotask(() =>
      this.host.nativeElement.querySelector<HTMLButtonElement>(`[data-date="${next!.toDateString()}"]`)?.focus(),
    );
  }

  private eventsOn(day: Date) {
    const start = startOfDay(day).getTime();
    const end = start + 24 * 3600_000;
    return this.normalised().filter((x) => x.start.getTime() < end && x.end.getTime() > start);
  }

  private place(day: Date, items: { e: SdCalendarEvent; start: Date; end: Date }[]): PlacedEvent[] {
    const dayStart = startOfDay(day).getTime() + this.hourStart() * 3600_000;
    const dayEnd = startOfDay(day).getTime() + this.hourEnd() * 3600_000;
    const laneEnds: number[] = [];
    const placed: PlacedEvent[] = [];
    const fmt = new Intl.DateTimeFormat(this.locale(), { hour: 'numeric', minute: '2-digit' });
    for (const x of items) {
      const s = Math.max(x.start.getTime(), dayStart);
      const e = Math.min(x.end.getTime(), dayEnd);
      if (e <= s) continue;
      let lane = laneEnds.findIndex((end) => end <= s);
      if (lane < 0) {
        lane = laneEnds.length;
        laneEnds.push(e);
      } else laneEnds[lane] = e;
      placed.push({
        event: x.e,
        top: ((s - dayStart) / 3600_000) * HOUR_PX,
        height: Math.max(18, ((e - s) / 3600_000) * HOUR_PX - 2),
        lane,
        lanes: 1,
        timeLabel: `${fmt.format(x.start)} – ${fmt.format(x.end)}`,
      });
    }
    const lanes = Math.max(1, laneEnds.length);
    return placed.map((p) => ({ ...p, lanes }));
  }
}
