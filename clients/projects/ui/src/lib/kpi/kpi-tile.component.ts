import { ChangeDetectionStrategy, Component, booleanAttribute, computed, input, output } from '@angular/core';
import { uniqueId } from '@sd/core';
import { SdIcon } from '../icon/icon.component';
import { SdSkeleton } from '../skeleton/skeleton.component';

/**
 * `<sd-kpi-tile label="Open tickets" [value]="412" [delta]="-6.2" deltaLabel="vs last week"
 *    [sparkline]="[…]" [positiveIsGood]="false" icon="ticket" (activate)="drill()" />`
 * Value + label + signed delta (coloured by whether up is good) + inline SVG sparkline.
 * When `interactive`, the tile is a button (keyboard accessible) emitting `activate`.
 */
@Component({
  selector: 'sd-kpi-tile',
  imports: [SdIcon, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div
      class="tile"
      [attr.role]="interactive() ? 'button' : null"
      [attr.tabindex]="interactive() ? 0 : null"
      [class.is-interactive]="interactive()"
      (click)="interactive() && activate.emit()"
      (keydown)="onKey($event)"
    >
      <div class="top">
        <span class="label">{{ label() }}</span>
        @if (icon()) {
          <span class="icon" aria-hidden="true"><sd-icon [name]="icon()!" [size]="15" /></span>
        }
      </div>
      @if (loading()) {
        <sd-skeleton width="55%" height="28px" />
        <sd-skeleton width="35%" height="10px" />
      } @else {
        <div class="value sd-num">
          {{ value() ?? '—' }}@if (unit()) {<span class="unit">{{ unit() }}</span>}
        </div>
        <div class="bottom">
          @if (delta() !== null && delta() !== undefined) {
            <span class="delta" [class]="'delta delta--' + deltaTone()">
              <sd-icon [name]="delta()! > 0 ? 'arrow-up' : delta()! < 0 ? 'arrow-down' : 'minus'" [size]="12" />
              {{ deltaText() }}
              <span class="sd-visually-hidden">{{ deltaTone() === 'good' ? '(improvement)' : deltaTone() === 'bad' ? '(worse)' : '' }}</span>
            </span>
          }
          @if (deltaLabel()) {
            <span class="delta-label">{{ deltaLabel() }}</span>
          }
          @if (sparkPath(); as spark) {
            <svg class="spark" viewBox="0 0 100 28" preserveAspectRatio="none" aria-hidden="true">
              <defs>
                <linearGradient [attr.id]="gradientId" x1="0" x2="0" y1="0" y2="1">
                  <stop offset="0%" stop-color="currentColor" stop-opacity="0.22" />
                  <stop offset="100%" stop-color="currentColor" stop-opacity="0" />
                </linearGradient>
              </defs>
              <path [attr.d]="spark.area" [attr.fill]="'url(#' + gradientId + ')'" />
              <path [attr.d]="spark.line" fill="none" stroke="currentColor" stroke-width="1.6" vector-effect="non-scaling-stroke" />
            </svg>
          }
        </div>
      }
    </div>
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .tile {
      display: flex;
      flex-direction: column;
      gap: 6px;
      height: 100%;
      padding: 14px 16px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
      box-shadow: var(--sd-shadow-xs);
      transition: box-shadow var(--sd-dur) var(--sd-ease), border-color var(--sd-dur) var(--sd-ease);
    }
    .tile.is-interactive {
      cursor: pointer;
    }
    .tile.is-interactive:hover {
      border-color: var(--sd-border-strong);
      box-shadow: var(--sd-shadow-md);
    }
    .tile:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .top {
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .label {
      flex: 1;
      min-width: 0;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-medium);
    }
    .icon {
      display: inline-flex;
      padding: 6px;
      border-radius: var(--sd-radius-sm);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    .value {
      font-size: var(--sd-text-3xl);
      font-weight: var(--sd-weight-bold);
      letter-spacing: -0.02em;
      line-height: 1.1;
    }
    .unit {
      margin-left: 3px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-lg);
      font-weight: var(--sd-weight-medium);
    }
    .bottom {
      display: flex;
      align-items: center;
      gap: 6px;
      min-height: 28px;
    }
    .delta {
      display: inline-flex;
      align-items: center;
      gap: 2px;
      padding: 1px 6px;
      border-radius: var(--sd-radius-pill);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
    }
    .delta--good {
      color: var(--sd-success);
      background: var(--sd-success-bg);
    }
    .delta--bad {
      color: var(--sd-danger);
      background: var(--sd-danger-bg);
    }
    .delta--flat {
      color: var(--sd-text-2);
      background: var(--sd-surface-3);
    }
    .delta-label {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .spark {
      flex: 1;
      height: 28px;
      min-width: 60px;
      margin-left: auto;
      color: var(--sd-primary);
    }
  `,
})
export class SdKpiTile {
  readonly label = input.required<string>();
  readonly value = input<string | number | null>(null);
  readonly unit = input<string | null>(null);
  /** Percentage change; sign decides the arrow. */
  readonly delta = input<number | null>(null);
  readonly deltaLabel = input<string | null>(null);
  readonly positiveIsGood = input(true, { transform: booleanAttribute });
  readonly sparkline = input<readonly number[] | null>(null);
  readonly icon = input<string | null>(null);
  readonly loading = input(false, { transform: booleanAttribute });
  readonly interactive = input(false, { transform: booleanAttribute });
  readonly activate = output<void>();

  protected readonly gradientId = uniqueId('sd-spark');

  protected onKey(event: KeyboardEvent): void {
    if (!this.interactive() || (event.key !== 'Enter' && event.key !== ' ')) return;
    event.preventDefault();
    this.activate.emit();
  }

  protected readonly deltaTone = computed(() => {
    const d = this.delta() ?? 0;
    if (d === 0) return 'flat';
    return d > 0 === this.positiveIsGood() ? 'good' : 'bad';
  });
  protected readonly deltaText = computed(() => {
    const d = this.delta() ?? 0;
    return `${Math.abs(d).toFixed(Math.abs(d) < 10 ? 1 : 0)}%`;
  });
  protected readonly sparkPath = computed(() => {
    const data = this.sparkline();
    if (!data || data.length < 2) return null;
    const min = Math.min(...data);
    const max = Math.max(...data);
    const range = max - min || 1;
    const pts = data.map((v, i) => [(i / (data.length - 1)) * 100, 26 - ((v - min) / range) * 24] as const);
    const line = pts.map(([x, y], i) => `${i ? 'L' : 'M'}${x.toFixed(2)},${y.toFixed(2)}`).join(' ');
    return { line, area: `${line} L100,28 L0,28 Z` };
  });
}
