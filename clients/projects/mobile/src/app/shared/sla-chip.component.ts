import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { ClockService, slaCountdown } from '@sd/core';

/**
 * Live SLA countdown pill: `<m-sla-chip label="Response" [dueAt]="t.firstResponseDueAt" [state]="t.slaState" />`.
 * Ticks every second (shared ClockService); colour follows on-track / at-risk / breached / paused / met.
 */
@Component({
  selector: 'm-sla-chip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (countdown(); as c) {
      <span class="chip" [attr.data-tone]="c.tone" [attr.aria-label]="(label() ? label() + ': ' : 'SLA: ') + c.label">
        @if (label()) {
          <span class="lbl">{{ label() }}</span>
        }
        {{ compact() ? short(c.label) : c.label }}
      </span>
    }
  `,
  styles: `
    .chip {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      padding: 2px 8px;
      border-radius: 999px;
      font-size: 0.75rem;
      font-weight: 600;
      white-space: nowrap;
      background: var(--sd-sla-ontrack-bg);
      color: var(--sd-sla-ontrack);
    }
    .lbl {
      font-weight: 500;
      opacity: 0.85;
    }
    [data-tone='atrisk'] {
      background: var(--sd-sla-atrisk-bg);
      color: var(--sd-sla-atrisk);
    }
    [data-tone='breached'] {
      background: var(--sd-sla-breached-bg);
      color: var(--sd-sla-breached);
    }
    [data-tone='paused'] {
      background: var(--sd-sla-paused-bg);
      color: var(--sd-sla-paused);
    }
    [data-tone='met'] {
      background: var(--sd-sla-met-bg);
      color: var(--sd-sla-met);
    }
    [data-tone='none'] {
      background: var(--sd-surface-2);
      color: var(--sd-text-2);
    }
  `,
})
export class SlaChip {
  readonly dueAt = input<string | null>(null);
  readonly state = input<string | null>(null);
  readonly label = input('');
  /** Drop the "Due in" prefix (lists). */
  readonly compact = input(false);
  /** Hide entirely when there is no running SLA. */
  readonly hideWhenNone = input(true);

  private readonly clock = inject(ClockService);

  protected readonly countdown = computed(() => {
    const c = slaCountdown(this.dueAt(), this.state(), this.clock.now());
    return c.tone === 'none' && this.hideWhenNone() ? null : c;
  });

  protected short(label: string): string {
    return label.replace(/^Due in /, '');
  }
}
