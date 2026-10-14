import { Pipe, PipeTransform, inject } from '@angular/core';
import {
  ClockService,
  DateInput,
  MeStore,
  formatDate,
  formatDateTime,
  formatDurationMinutes,
  formatDurationMs,
  formatRelative,
} from '@sd/core';

/**
 * `{{ ticket.lastActivityAt | sdRelativeTime }}` → "5m ago", "in 2h", "just now" (dates older than a week
 * fall back to a short date). Re-evaluates on the shared 30 s clock.
 */
@Pipe({ name: 'sdRelativeTime', pure: false })
export class SdRelativeTimePipe implements PipeTransform {
  private readonly clock = inject(ClockService);
  private readonly me = inject(MeStore);

  transform(value: DateInput): string {
    return formatRelative(value, this.clock.coarseNow(), this.me.locale());
  }
}

/**
 * Durations: `{{ 95 | sdDuration }}` → "1h 35m" (minutes by default); `{{ ms | sdDuration: 'ms' }}`.
 */
@Pipe({ name: 'sdDuration' })
export class SdDurationPipe implements PipeTransform {
  transform(value: number | null | undefined, unit: 'minutes' | 'ms' | 'seconds' = 'minutes'): string {
    if (value === null || value === undefined || Number.isNaN(value)) return '—';
    if (unit === 'ms') return formatDurationMs(value);
    if (unit === 'seconds') return formatDurationMs(value * 1000, { seconds: true });
    return formatDurationMinutes(value);
  }
}

/**
 * `{{ at | sdDateTime }}` / `{{ at | sdDateTime: 'date' }}` — formatted in the signed-in user's time zone
 * and locale (MeDto), falling back to the browser's.
 */
@Pipe({ name: 'sdDateTime' })
export class SdDateTimePipe implements PipeTransform {
  private readonly me = inject(MeStore);

  transform(value: DateInput, mode: 'datetime' | 'date' = 'datetime', style: 'short' | 'medium' | 'long' = 'medium'): string {
    const options = { timeZone: this.me.user()?.timeZone, locale: this.me.user()?.locale, style };
    return mode === 'date' ? formatDate(value, options) : formatDateTime(value, options);
  }
}

/** `{{ bytes | sdFileSize }}` → "1.2 MB". */
@Pipe({ name: 'sdFileSize' })
export class SdFileSizePipe implements PipeTransform {
  transform(bytes: number | null | undefined): string {
    if (bytes === null || bytes === undefined || Number.isNaN(bytes)) return '';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let v = bytes;
    let u = 0;
    while (v >= 1024 && u < units.length - 1) {
      v /= 1024;
      u++;
    }
    return `${v >= 10 || u === 0 ? Math.round(v) : v.toFixed(1)} ${units[u]}`;
  }
}
