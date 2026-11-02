import { ChangeDetectionStrategy, Component, booleanAttribute, computed, input } from '@angular/core';
import { SdIcon } from '../icon/icon.component';

export type SdTone = 'neutral' | 'primary' | 'accent' | 'success' | 'warning' | 'danger' | 'info';

/**
 * `<sd-badge tone="success" variant="soft" icon="check">Resolved</sd-badge>` — compact pill/label.
 * `dot` renders a leading status dot; `count` mode is just a badge with a number.
 */
@Component({
  selector: 'sd-badge',
  imports: [SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (dot()) {
      <span class="dot" aria-hidden="true"></span>
    }
    @if (icon()) {
      <sd-icon [name]="icon()!" [size]="12" />
    }
    <ng-content />
  `,
  host: {
    '[class]': '"sd-badge sd-badge--" + tone() + " sd-badge--" + variant() + (size() === "sm" ? " sd-badge--sm" : "")',
  },
  styles: `
    :host {
      --_fg: var(--sd-text-2);
      --_bg: var(--sd-surface-3);
      --_border: var(--sd-border);
      display: inline-flex;
      align-items: center;
      gap: 4px;
      max-width: 100%;
      height: 22px;
      padding: 0 8px;
      border-radius: var(--sd-radius-pill);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-medium);
      line-height: 1;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      vertical-align: middle;
      color: var(--_fg);
      background: var(--_bg);
      border: 1px solid transparent;
    }
    :host(.sd-badge--sm) {
      height: 18px;
      padding: 0 6px;
      font-size: 11px;
    }
    :host(.sd-badge--outline) {
      background: transparent;
      border-color: var(--_border);
    }
    :host(.sd-badge--solid) {
      background: var(--_fg);
      color: var(--sd-surface);
    }
    :host(.sd-badge--primary) {
      --_fg: var(--sd-primary);
      --_bg: var(--sd-primary-soft);
      --_border: var(--sd-primary-border);
    }
    :host(.sd-badge--accent) {
      --_fg: var(--sd-accent);
      --_bg: var(--sd-accent-soft);
      --_border: color-mix(in oklab, var(--sd-accent) 40%, var(--sd-surface));
    }
    :host(.sd-badge--success) {
      --_fg: var(--sd-success);
      --_bg: var(--sd-success-bg);
      --_border: var(--sd-success-border);
    }
    :host(.sd-badge--warning) {
      --_fg: var(--sd-warning);
      --_bg: var(--sd-warning-bg);
      --_border: var(--sd-warning-border);
    }
    :host(.sd-badge--danger) {
      --_fg: var(--sd-danger);
      --_bg: var(--sd-danger-bg);
      --_border: var(--sd-danger-border);
    }
    :host(.sd-badge--info) {
      --_fg: var(--sd-info);
      --_bg: var(--sd-info-bg);
      --_border: var(--sd-info-border);
    }
    .dot {
      width: 6px;
      height: 6px;
      border-radius: 50%;
      background: currentColor;
      flex: none;
    }
  `,
})
export class SdBadge {
  readonly tone = input<SdTone>('neutral');
  readonly variant = input<'soft' | 'outline' | 'solid'>('soft');
  readonly size = input<'sm' | 'md'>('md');
  readonly icon = input<string | null>(null);
  readonly dot = input(false, { transform: booleanAttribute });
}

/** `<sd-priority-badge priority="High" />` — priority with its semantic colour (Low/Medium/High/Critical). */
@Component({
  selector: 'sd-priority-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="bars" aria-hidden="true"><i></i><i></i><i></i></span>{{ priority() }}`,
  host: {
    '[class]': '"sd-priority sd-priority--" + key()',
    '[attr.title]': '"Priority: " + priority()',
  },
  styles: `
    :host {
      --_c: var(--sd-priority-low);
      --_bg: var(--sd-priority-low-bg);
      display: inline-flex;
      align-items: center;
      gap: 5px;
      height: 22px;
      padding: 0 8px 0 6px;
      border-radius: var(--sd-radius-pill);
      background: var(--_bg);
      color: var(--_c);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      white-space: nowrap;
    }
    :host(.sd-priority--medium) {
      --_c: var(--sd-priority-medium);
      --_bg: var(--sd-priority-medium-bg);
    }
    :host(.sd-priority--high) {
      --_c: var(--sd-priority-high);
      --_bg: var(--sd-priority-high-bg);
    }
    :host(.sd-priority--critical) {
      --_c: var(--sd-priority-critical);
      --_bg: var(--sd-priority-critical-bg);
    }
    .bars {
      display: inline-flex;
      align-items: flex-end;
      gap: 1.5px;
      height: 10px;
    }
    .bars i {
      width: 2.5px;
      border-radius: 1px;
      background: currentColor;
      opacity: 0.28;
    }
    .bars i:nth-child(1) { height: 4px; }
    .bars i:nth-child(2) { height: 7px; }
    .bars i:nth-child(3) { height: 10px; }
    :host(.sd-priority--low) .bars i:nth-child(1),
    :host(.sd-priority--medium) .bars i:nth-child(-n + 2),
    :host(.sd-priority--high) .bars i,
    :host(.sd-priority--critical) .bars i {
      opacity: 1;
    }
  `,
})
export class SdPriorityBadge {
  readonly priority = input.required<string>();
  protected readonly key = computed(() => this.priority().toLowerCase());
}

/**
 * `<sd-status-pill [label]="t.status" [color]="t.statusColor" />` — ticket status using the admin-defined
 * colour as a dot (text stays on the neutral surface for contrast).
 */
@Component({
  selector: 'sd-status-pill',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="dot" aria-hidden="true" [style.background]="color() || 'var(--sd-text-3)'"></span><span class="text">{{ label() }}</span>`,
  // Long names ("Waiting on Third Party") truncate inside narrow cells instead of spilling into the next column.
  host: { '[attr.title]': 'label()' },
  styles: `
    :host {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      max-width: 100%;
      min-width: 0;
      height: 22px;
      padding: 0 8px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface);
      color: var(--sd-text);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-medium);
      white-space: nowrap;
    }
    .dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      flex: none;
    }
    .text {
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
    }
  `,
})
export class SdStatusPill {
  readonly label = input.required<string>();
  readonly color = input<string | null>(null);
}
