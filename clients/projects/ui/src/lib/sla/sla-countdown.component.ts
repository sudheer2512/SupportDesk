import { ChangeDetectionStrategy, Component, booleanAttribute, computed, inject, input } from '@angular/core';
import { ClockService, MeStore, formatDateTime, slaCountdown } from '@sd/core';
import { SdIcon } from '../icon/icon.component';

const ICONS: Record<string, string> = {
  ontrack: 'timer',
  atrisk: 'triangle-alert',
  breached: 'circle-alert',
  paused: 'pause',
  met: 'circle-check',
  none: 'minus',
};

/**
 * `<sd-sla-countdown [dueAt]="t.dueAt" [state]="t.slaState" label="Resolution" />`
 * Live-ticking SLA timer ("Due in 1h 05m", "Breached 12m ago", "Paused", "Met"), coloured by state
 * (on-track green, at-risk amber within `atRiskMinutes`, breached red, paused grey, met teal). The
 * absolute due time is in the tooltip; the visible text is the accessible name (not a live region).
 */
@Component({
  selector: 'sd-sla-countdown',
  imports: [SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-icon [name]="icon()" [size]="compact() ? 12 : 13" />
    @if (label()) {
      <span class="kind">{{ label() }}:</span>
    }
    <span class="text sd-num">{{ countdown().label }}</span>
  `,
  host: {
    role: 'timer',
    'aria-live': 'off',
    '[class]': '"sd-sla sd-sla--" + countdown().tone',
    '[class.is-compact]': 'compact()',
    '[attr.title]': 'tooltip()',
  },
  styles: `
    :host {
      --_c: var(--sd-sla-ontrack);
      --_bg: var(--sd-sla-ontrack-bg);
      display: inline-flex;
      align-items: center;
      gap: 4px;
      height: 22px;
      padding: 0 8px;
      border-radius: var(--sd-radius-pill);
      background: var(--_bg);
      color: var(--_c);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      white-space: nowrap;
    }
    :host(.is-compact) {
      height: auto;
      padding: 0;
      background: transparent;
    }
    :host(.sd-sla--atrisk) {
      --_c: var(--sd-sla-atrisk);
      --_bg: var(--sd-sla-atrisk-bg);
    }
    :host(.sd-sla--breached) {
      --_c: var(--sd-sla-breached);
      --_bg: var(--sd-sla-breached-bg);
    }
    :host(.sd-sla--paused) {
      --_c: var(--sd-sla-paused);
      --_bg: var(--sd-sla-paused-bg);
    }
    :host(.sd-sla--met) {
      --_c: var(--sd-sla-met);
      --_bg: var(--sd-sla-met-bg);
    }
    :host(.sd-sla--none) {
      --_c: var(--sd-text-3);
      --_bg: var(--sd-surface-3);
    }
    .kind {
      font-weight: var(--sd-weight-medium);
      opacity: 0.85;
    }
  `,
})
export class SdSlaCountdown {
  readonly dueAt = input<string | Date | null>(null);
  /** SlaState from the API: OnTrack / AtRisk / Breached / Paused / Met / None. */
  readonly state = input<string | null>(null);
  readonly label = input<string | null>(null);
  readonly atRiskMinutes = input(30);
  readonly compact = input(false, { transform: booleanAttribute });

  private readonly clock = inject(ClockService);
  private readonly me = inject(MeStore);
  protected readonly countdown = computed(() =>
    slaCountdown(this.dueAt(), this.state(), this.clock.now(), this.atRiskMinutes() * 60_000),
  );
  protected readonly icon = computed(() => ICONS[this.countdown().tone] ?? 'timer');
  protected readonly tooltip = computed(() => {
    const due = this.dueAt();
    if (!due) return null;
    return `Due ${formatDateTime(due, { timeZone: this.me.user()?.timeZone, locale: this.me.user()?.locale })}`;
  });
}
