import { ChangeDetectionStrategy, Component, booleanAttribute, computed, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ArticleFeedbackDto } from '@sd/core';
import { SdBadge, SdButton, SdCheckbox, SdDateTimePipe, SdIcon, SdRelativeTimePipe } from '@sd/ui';
import { KB_BASE } from '../kb-common';
import { KbStars } from './kb-badges.component';

/**
 * Reader feedback entries (helpful/not, stars, comment, author) with optional multi-select and a
 * per-item "Mark reviewed" action. Shared by the article Feedback tab and the feedback queue.
 */
@Component({
  selector: 'desk-kb-feedback-list',
  imports: [RouterLink, SdBadge, SdButton, SdCheckbox, SdIcon, SdRelativeTimePipe, SdDateTimePipe, KbStars],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ul class="list" [attr.aria-label]="ariaLabel()">
      @for (f of items(); track f.id) {
        <li class="item" [class.is-reviewed]="f.reviewed" [class.is-selected]="selected().includes(f.id)">
          @if (selectable()) {
            <sd-checkbox class="check" [value]="selected().includes(f.id)" [ariaLabel]="'Select feedback from ' + (f.authorName ?? 'anonymous reader')" (valueChange)="toggle(f.id, $event)" />
          }
          <span class="verdict" [class.up]="f.helpful" [class.down]="!f.helpful">
            <sd-icon [name]="f.helpful ? 'thumbs-up' : 'thumbs-down'" [size]="16" [label]="f.helpful ? 'Helpful' : 'Not helpful'" />
          </span>
          <div class="body">
            <div class="top">
              <strong>{{ f.helpful ? 'Helpful' : 'Not helpful' }}</strong>
              @if (f.rating !== null) {
                <desk-kb-stars [rating]="f.rating" />
              }
              @if (showArticle()) {
                <a class="article sd-truncate" [routerLink]="[base, 'articles', f.articleId]" [queryParams]="{ tab: 'feedback' }">{{ f.articleTitle }}</a>
              }
              @if (f.reviewed) {
                <sd-badge size="sm" tone="success" icon="check">Reviewed</sd-badge>
              } @else {
                <sd-badge size="sm" tone="warning">New</sd-badge>
              }
            </div>
            @if (f.comment) {
              <p class="comment">{{ f.comment }}</p>
            } @else {
              <p class="comment none">No comment.</p>
            }
            <div class="kb-meta">
              <span><sd-icon name="user" [size]="12" /> {{ f.authorName ?? 'Anonymous' }} · {{ f.authorType }}</span>
              <span><sd-icon name="globe" [size]="12" /> {{ f.locale }}</span>
              <span [attr.title]="f.createdAt | sdDateTime">{{ f.createdAt | sdRelativeTime }}</span>
            </div>
          </div>
          @if (canReview() && !f.reviewed) {
            <button sdButton size="sm" variant="ghost" icon="check" type="button" [loading]="busyIds().includes(f.id)" (click)="review.emit([f.id])">Mark reviewed</button>
          }
        </li>
      }
    </ul>
  `,
  styles: `
    .list {
      display: grid;
      gap: 6px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .item {
      display: flex;
      align-items: flex-start;
      gap: var(--sd-space-3);
      padding: var(--sd-space-3);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
    }
    .item.is-reviewed {
      background: var(--sd-surface-2);
    }
    .item.is-selected {
      border-color: var(--sd-primary-border);
      background: var(--sd-row-selected);
    }
    .check {
      margin-top: 4px;
    }
    .verdict {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      flex: none;
      width: 30px;
      height: 30px;
      border-radius: 50%;
    }
    .verdict.up {
      background: var(--sd-success-bg);
      color: var(--sd-success);
    }
    .verdict.down {
      background: var(--sd-danger-bg);
      color: var(--sd-danger);
    }
    .body {
      display: grid;
      gap: 4px;
      flex: 1;
      min-width: 0;
    }
    .top {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px 10px;
    }
    .article {
      max-width: 100%;
      font-weight: var(--sd-weight-medium);
    }
    .comment {
      white-space: pre-line;
      overflow-wrap: anywhere;
    }
    .comment.none {
      color: var(--sd-text-3);
      font-style: italic;
      font-size: var(--sd-text-sm);
    }
    .kb-meta {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 4px 12px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .kb-meta > span {
      display: inline-flex;
      align-items: center;
      gap: 4px;
    }
  `,
})
export class KbFeedbackList {
  readonly items = input.required<readonly ArticleFeedbackDto[]>();
  readonly showArticle = input(false, { transform: booleanAttribute });
  readonly selectable = input(false, { transform: booleanAttribute });
  readonly canReview = input(false, { transform: booleanAttribute });
  readonly selected = input<readonly string[]>([]);
  readonly busyIds = input<readonly string[]>([]);
  readonly ariaLabel = input('Feedback');
  readonly selectedChange = output<string[]>();
  readonly review = output<string[]>();

  protected readonly base = KB_BASE;
  private readonly selectedSet = computed(() => new Set(this.selected()));

  protected toggle(id: string, on: boolean): void {
    const set = new Set(this.selectedSet());
    if (on) set.add(id);
    else set.delete(id);
    this.selectedChange.emit([...set]);
  }
}
