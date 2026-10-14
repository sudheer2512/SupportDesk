import { ChangeDetectionStrategy, Component, booleanAttribute, computed, input } from '@angular/core';

/**
 * `<sd-skeleton width="60%" />`, `<sd-skeleton circle width="32px" height="32px" />`,
 * `<sd-skeleton [lines]="3" />` — loading placeholders with a gentle shimmer (static under reduced motion).
 * Hidden from assistive tech; mark the loading region with aria-busy instead.
 */
@Component({
  selector: 'sd-skeleton',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @for (line of lineArray(); track line; let last = $last) {
      <span
        class="bone"
        [style.width]="lines() > 1 && last ? '62%' : width()"
        [style.height]="height()"
        [style.border-radius]="circle() ? '50%' : radius()"
      ></span>
    }
  `,
  host: { 'aria-hidden': 'true' },
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: 8px;
      width: 100%;
    }
    .bone {
      display: block;
      max-width: 100%;
      background: linear-gradient(90deg, var(--sd-surface-3) 25%, var(--sd-surface-2) 37%, var(--sd-surface-3) 63%);
      background-size: 400% 100%;
      animation: sd-shimmer 1.4s ease infinite;
    }
    @keyframes sd-shimmer {
      0% {
        background-position: 100% 50%;
      }
      100% {
        background-position: 0 50%;
      }
    }
  `,
})
export class SdSkeleton {
  readonly width = input('100%');
  readonly height = input('12px');
  readonly radius = input('var(--sd-radius-xs)');
  readonly circle = input(false, { transform: booleanAttribute });
  readonly lines = input(1);
  protected readonly lineArray = computed(() => Array.from({ length: Math.max(1, this.lines()) }, (_, i) => i));
}
