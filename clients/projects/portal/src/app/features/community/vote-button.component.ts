import { ChangeDetectionStrategy, Component, booleanAttribute, input, output } from '@angular/core';
import { SdIcon, SdSpinner } from '@sd/ui';

/**
 * Up-vote toggle with the current score (one vote per person; pressing again removes it).
 * `<portal-vote [score]="t.voteScore" [voted]="t.myVote > 0" [busy]="voting()" (toggle)="vote()" />`
 */
@Component({
  selector: 'portal-vote',
  imports: [SdIcon, SdSpinner],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      type="button"
      class="vote"
      [class.is-voted]="voted()"
      [class.is-row]="layout() === 'row'"
      [attr.aria-pressed]="voted()"
      [attr.aria-label]="(voted() ? 'Remove your vote' : 'Vote up') + ' — ' + score() + (score() === 1 ? ' vote' : ' votes')"
      [disabled]="disabled() || busy()"
      (click)="toggle.emit()"
    >
      @if (busy()) {
        <sd-spinner [size]="16" />
      } @else {
        <sd-icon name="arrow-big-up" [size]="18" />
      }
      <span class="score" aria-hidden="true">{{ score() }}</span>
    </button>
  `,
  styles: `
    .vote {
      display: inline-flex;
      flex-direction: column;
      align-items: center;
      gap: 2px;
      min-width: 46px;
      padding: 6px 8px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      color: var(--sd-text-2);
      font: inherit;
      cursor: pointer;
      transition: border-color var(--sd-dur-fast) var(--sd-ease), background-color var(--sd-dur-fast) var(--sd-ease), color var(--sd-dur-fast) var(--sd-ease);
    }
    .vote.is-row {
      flex-direction: row;
      min-width: 0;
      padding: 3px 10px;
      gap: 6px;
    }
    .vote:hover:not(:disabled) {
      border-color: var(--sd-primary-border);
      color: var(--sd-primary);
    }
    .vote:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .vote.is-voted {
      border-color: var(--sd-primary);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    .vote.is-voted ::ng-deep svg {
      fill: currentColor;
    }
    .vote:disabled {
      cursor: default;
      opacity: 0.7;
    }
    .score {
      font-weight: var(--sd-weight-semibold);
      font-variant-numeric: tabular-nums;
      color: var(--sd-text);
    }
    .is-voted .score {
      color: var(--sd-primary);
    }
  `,
})
export class VoteButtonComponent {
  readonly score = input.required<number>();
  readonly voted = input(false, { transform: booleanAttribute });
  readonly busy = input(false, { transform: booleanAttribute });
  readonly disabled = input(false, { transform: booleanAttribute });
  readonly layout = input<'column' | 'row'>('column');
  readonly toggle = output<void>();
}
