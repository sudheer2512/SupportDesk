import { ChangeDetectionStrategy, Component, booleanAttribute, computed, input, model, signal } from '@angular/core';
import { uniqueId } from '@sd/core';
import { SdIcon } from '@sd/ui';

const LABELS = ['', 'Very poor', 'Poor', 'Okay', 'Good', 'Excellent'];

/**
 * 1–5 star rating built on native radios (arrow keys change the value, screen readers announce
 * "3 stars — Okay"). `<portal-star-rating [(value)]="rating" label="Rate this article" />`
 */
@Component({
  selector: 'portal-star-rating',
  imports: [SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <fieldset class="rating" [disabled]="disabled()" (mouseleave)="hover.set(null)">
      <legend [class.sd-visually-hidden]="!showLabel()">{{ label() }}</legend>
      <div class="stars">
        @for (n of stars; track n) {
          <label class="star" [class.is-on]="shown() >= n" (mouseenter)="hover.set(n)">
            <input type="radio" class="sd-visually-hidden" [name]="name" [value]="n" [checked]="value() === n" (change)="value.set(n)" />
            <sd-icon name="star" [size]="size()" />
            <span class="sd-visually-hidden">{{ n }} {{ n === 1 ? 'star' : 'stars' }} — {{ labels[n] }}</span>
          </label>
        }
        @if (shown() > 0) {
          <span class="caption" aria-hidden="true">{{ labels[shown()] }}</span>
        }
      </div>
    </fieldset>
  `,
  styles: `
    .rating {
      margin: 0;
      padding: 0;
      border: 0;
      min-width: 0;
    }
    legend {
      padding: 0;
      margin-bottom: 6px;
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-medium);
    }
    .stars {
      display: flex;
      align-items: center;
      gap: 2px;
    }
    .star {
      position: relative;
      display: inline-flex;
      padding: 4px;
      border-radius: var(--sd-radius-sm);
      color: var(--sd-border-strong);
      cursor: pointer;
      transition: color var(--sd-dur-fast) var(--sd-ease), transform var(--sd-dur-fast) var(--sd-ease);
    }
    .star:hover {
      transform: scale(1.08);
    }
    .star.is-on {
      color: var(--sd-warning);
    }
    .star.is-on ::ng-deep svg {
      fill: currentColor;
    }
    .star:has(input:focus-visible) {
      box-shadow: var(--sd-focus-ring);
    }
    fieldset:disabled .star {
      cursor: not-allowed;
      opacity: 0.6;
    }
    .caption {
      margin-left: 8px;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
  `,
})
export class StarRatingComponent {
  readonly value = model<number | null>(null);
  readonly label = input('Rating');
  readonly showLabel = input(true, { transform: booleanAttribute });
  readonly size = input(24);
  readonly disabled = input(false, { transform: booleanAttribute });

  protected readonly stars = [1, 2, 3, 4, 5];
  protected readonly labels = LABELS;
  protected readonly name = uniqueId('portal-stars');
  protected readonly hover = signal<number | null>(null);
  protected readonly shown = computed(() => this.hover() ?? this.value() ?? 0);
}
