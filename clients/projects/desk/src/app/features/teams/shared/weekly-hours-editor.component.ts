import { ChangeDetectionStrategy, Component, input, model } from '@angular/core';
import { WEEK_DAYS, WeekDay, WeeklyHoursDto, WorkIntervalDto } from '@sd/core';
import { SdButton, SdCheckbox } from '@sd/ui';

/**
 * Weekly working-hours editor: `<desk-weekly-hours-editor [(value)]="weekly" />`. One row per weekday with any number
 * of HH:mm intervals (end may be 24:00), quick presets and "copy Monday to weekdays". The API validates overlaps.
 */
@Component({
  selector: 'desk-weekly-hours-editor',
  imports: [SdButton, SdCheckbox],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="presets" role="group" aria-label="Presets">
      <button sdButton type="button" size="sm" variant="ghost" [disabled]="disabled()" (click)="preset('09:00', '18:00', 5)">Mon–Fri 09–18</button>
      <button sdButton type="button" size="sm" variant="ghost" [disabled]="disabled()" (click)="preset('09:30', '18:30', 6)">Mon–Sat 09:30–18:30</button>
      <button sdButton type="button" size="sm" variant="ghost" [disabled]="disabled()" (click)="preset('00:00', '24:00', 7)">All day, every day</button>
      <button sdButton type="button" size="sm" variant="ghost" [disabled]="disabled()" (click)="copyMonday()">Copy Monday to weekdays</button>
    </div>
    <div class="days">
      @for (day of days; track day) {
        <div class="day" role="group" [attr.aria-label]="day">
          <sd-checkbox [value]="intervals(day).length > 0" (valueChange)="toggle(day, $event)" [ariaLabel]="day + ' is a working day'" [disabled]="disabled()">
            <span class="name">{{ day }}</span>
          </sd-checkbox>
          <div class="slots">
            @for (iv of intervals(day); track $index; let i = $index) {
              <span class="slot">
                <input class="sd-input sd-input-sm" type="time" [value]="iv.start" [attr.aria-label]="day + ' interval ' + (i + 1) + ' start'"
                  [disabled]="disabled()" (change)="set(day, i, 'start', $any($event.target).value)" />
                <span aria-hidden="true">–</span>
                <input class="sd-input sd-input-sm" type="text" inputmode="numeric" pattern="[0-2][0-9]:[0-5][0-9]" maxlength="5" [value]="iv.end"
                  [attr.aria-label]="day + ' interval ' + (i + 1) + ' end (HH:mm, 24:00 allowed)'" [disabled]="disabled()"
                  (change)="set(day, i, 'end', $any($event.target).value)" />
                <button sdButton type="button" size="sm" variant="ghost" icon="x" iconOnly [attr.aria-label]="'Remove ' + day + ' interval ' + (i + 1)"
                  [disabled]="disabled()" (click)="remove(day, i)"></button>
              </span>
            } @empty {
              <span class="off">Closed</span>
            }
            @if (intervals(day).length > 0 && intervals(day).length < 4) {
              <button sdButton type="button" size="sm" variant="ghost" icon="plus" [disabled]="disabled()" (click)="add(day)" [attr.aria-label]="'Add interval on ' + day">
                Split
              </button>
            }
          </div>
        </div>
      }
    </div>
  `,
  styles: `
    :host { display: grid; gap: var(--sd-space-2); }
    .presets { display: flex; flex-wrap: wrap; gap: 4px; }
    .days { display: grid; gap: 6px; }
    .day { display: grid; grid-template-columns: 140px minmax(0, 1fr); gap: var(--sd-space-2); align-items: center;
      padding: 6px 8px; border: 1px solid var(--sd-divider); border-radius: var(--sd-radius-md); background: var(--sd-surface); }
    .name { font-weight: var(--sd-weight-medium); }
    .slots { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; }
    .slot { display: inline-flex; align-items: center; gap: 4px; }
    .slot input { width: 96px; }
    .off { color: var(--sd-text-3); font-size: var(--sd-text-sm); }
    @media (max-width: 640px) { .day { grid-template-columns: 1fr; } }
  `,
})
export class WeeklyHoursEditorComponent {
  readonly value = model<WeeklyHoursDto>({});
  readonly disabled = input(false);
  protected readonly days = WEEK_DAYS;

  protected intervals(day: WeekDay): WorkIntervalDto[] {
    return this.value()[day] ?? [];
  }

  protected toggle(day: WeekDay, on: boolean): void {
    this.patch(day, on ? [{ start: '09:00', end: '18:00' }] : []);
  }

  protected add(day: WeekDay): void {
    const list = this.intervals(day);
    const last = list[list.length - 1];
    this.patch(day, [...list, { start: last?.end && last.end < '23:00' ? last.end : '18:00', end: '24:00' }]);
  }

  protected remove(day: WeekDay, index: number): void {
    this.patch(day, this.intervals(day).filter((_, i) => i !== index));
  }

  protected set(day: WeekDay, index: number, field: 'start' | 'end', raw: string): void {
    const text = (raw ?? '').trim();
    this.patch(day, this.intervals(day).map((iv, i) => (i === index ? { ...iv, [field]: text } : iv)));
  }

  protected preset(start: string, end: string, dayCount: number): void {
    const next: WeeklyHoursDto = {};
    WEEK_DAYS.forEach((d, i) => (next[d] = i < dayCount ? [{ start, end }] : []));
    this.value.set(next);
  }

  protected copyMonday(): void {
    const monday = this.intervals('Monday');
    const next: WeeklyHoursDto = { ...this.value() };
    for (const d of ['Tuesday', 'Wednesday', 'Thursday', 'Friday'] as const) next[d] = monday.map((iv) => ({ ...iv }));
    this.value.set(next);
  }

  private patch(day: WeekDay, list: WorkIntervalDto[]): void {
    this.value.set({ ...this.value(), [day]: list });
  }
}
