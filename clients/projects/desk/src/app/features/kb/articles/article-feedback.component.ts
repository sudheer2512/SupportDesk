import { ChangeDetectionStrategy, Component, booleanAttribute, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { ArticleDetailDto, ArticleFeedbackDto, KnowledgeApi, PageResult, ToastBridge } from '@sd/core';
import { SdButton, SdEmptyState, SdIcon, SdSkeleton, SdToggle } from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { KbFeedbackList } from '../components/feedback-list.component';
import { KbStars } from '../components/kb-badges.component';
import { KbPager } from '../components/kb-pager.component';
import { KB_STYLES, errorMessage, formatCount, formatPercent, formatRating, helpfulPercent, remote, toastedGlobally } from '../kb-common';

const PAGE_SIZE = 20;

/** Feedback tab of the article editor: counters plus the reader feedback for this article. */
@Component({
  selector: 'desk-kb-article-feedback',
  imports: [SdButton, SdIcon, SdSkeleton, SdEmptyState, SdToggle, KbFeedbackList, KbStars, KbPager],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <dl class="counters">
      <div>
        <dt><sd-icon name="eye" [size]="14" /> Views</dt>
        <dd>{{ formatCount(article().viewCount) }}</dd>
      </div>
      <div>
        <dt><sd-icon name="thumbs-up" [size]="14" /> Helpful</dt>
        <dd>{{ formatCount(article().helpfulCount) }}</dd>
      </div>
      <div>
        <dt><sd-icon name="thumbs-down" [size]="14" /> Not helpful</dt>
        <dd>{{ formatCount(article().notHelpfulCount) }}</dd>
      </div>
      <div>
        <dt><sd-icon name="gauge" [size]="14" /> Helpful rate</dt>
        <dd>{{ helpfulRate() }}</dd>
      </div>
      <div>
        <dt><sd-icon name="star" [size]="14" /> Rating</dt>
        <dd>
          {{ formatRating(article().ratingAverage) }}
          <desk-kb-stars [rating]="article().ratingAverage" />
          <span class="sd-xs sd-muted">({{ article().ratingCount }})</span>
        </dd>
      </div>
    </dl>

    <div class="bar">
      <sd-toggle [value]="unreviewedOnly()" (valueChange)="setUnreviewed($event)">Unreviewed only</sd-toggle>
      <span class="sd-spacer"></span>
      @if (canReview() && unreviewedOnPage().length) {
        <button sdButton size="sm" icon="check-check" type="button" [loading]="busy().length > 1" (click)="markReviewed(unreviewedOnPage())">
          Mark {{ unreviewedOnPage().length }} on this page reviewed
        </button>
      }
    </div>

    @if (feedback.loading() && !feedback.value()) {
      <div class="loading" aria-busy="true" aria-label="Loading feedback">
        <sd-skeleton height="70px" radius="var(--sd-radius-md)" />
        <sd-skeleton height="70px" radius="var(--sd-radius-md)" />
      </div>
    } @else if (feedback.error(); as err) {
      <div class="kb-error" role="alert">
        <sd-icon name="circle-alert" [size]="16" />
        <p>Could not load feedback. {{ err }}</p>
        <button sdButton size="sm" type="button" icon="refresh" (click)="feedback.reload()">Retry</button>
      </div>
    } @else if (items().length === 0) {
      <sd-empty-state
        icon="thumbs-up"
        [title]="unreviewedOnly() ? 'Nothing left to review' : 'No feedback yet'"
        [message]="unreviewedOnly() ? 'All feedback for this article has been reviewed.' : 'Votes and comments from readers appear here.'"
        compact
      />
    } @else {
      <desk-kb-feedback-list [items]="items()" [canReview]="canReview()" [busyIds]="busy()" ariaLabel="Feedback for this article" (review)="markReviewed($event)" />
      @if (feedback.value(); as r) {
        <desk-kb-pager [page]="r.page" [pageSize]="r.pageSize" [total]="r.total" label="Feedback pages" (pageChange)="page.set($event)" />
      }
    }
  `,
  styles: [
    KB_STYLES,
    `
      :host {
        display: grid;
        gap: var(--sd-space-3);
      }
      .counters {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));
        gap: var(--sd-space-2);
        margin: 0;
      }
      .counters div {
        padding: var(--sd-space-3);
        border: 1px solid var(--sd-border);
        border-radius: var(--sd-radius-md);
        background: var(--sd-surface);
      }
      dt {
        display: flex;
        align-items: center;
        gap: 4px;
        color: var(--sd-text-3);
        font-size: var(--sd-text-xs);
      }
      dd {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 6px;
        margin: 4px 0 0;
        font-size: var(--sd-text-xl);
        font-weight: var(--sd-weight-semibold);
        font-variant-numeric: tabular-nums;
      }
      .bar {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sd-space-2);
      }
      .loading {
        display: grid;
        gap: 6px;
      }
    `,
  ],
})
export class ArticleFeedback {
  readonly article = input.required<ArticleDetailDto>();
  readonly canReview = input(false, { transform: booleanAttribute });

  private readonly api = inject(KnowledgeApi);
  private readonly toasts = inject(ToastBridge);
  protected readonly formatCount = formatCount;
  protected readonly formatRating = formatRating;

  protected readonly feedback = remote<PageResult<ArticleFeedbackDto>>();
  protected readonly items = computed(() => this.feedback.value()?.items ?? []);
  protected readonly unreviewedOnPage = computed(() => this.items().filter((f) => !f.reviewed).map((f) => f.id));
  protected readonly page = signal(1);
  protected readonly unreviewedOnly = signal(false);
  protected readonly busy = signal<string[]>([]);
  protected readonly helpfulRate = computed(() => formatPercent(helpfulPercent(this.article().helpfulCount, this.article().notHelpfulCount)));
  private readonly articleId = computed(() => this.article().id);

  constructor() {
    effect(() => {
      const articleId = this.articleId();
      const page = this.page();
      const reviewed = this.unreviewedOnly() ? false : null;
      untracked(() => this.feedback.load(() => this.api.feedback({ articleId, page, pageSize: PAGE_SIZE, reviewed }), { keepValue: true }));
    });
  }

  protected setUnreviewed(on: boolean): void {
    this.unreviewedOnly.set(on);
    this.page.set(1);
  }

  protected async markReviewed(ids: string[]): Promise<void> {
    if (!ids.length) return;
    this.busy.set(ids);
    try {
      await firstValueFrom(this.api.markFeedbackReviewed(ids));
      const done = new Set(ids);
      const current = this.feedback.value();
      if (current) this.feedback.set({ ...current, items: current.items.map((f) => (done.has(f.id) ? { ...f, reviewed: true } : f)) });
      this.toasts.success(ids.length === 1 ? 'Feedback marked as reviewed' : `${ids.length} items marked as reviewed`);
      if (this.unreviewedOnly()) this.feedback.reload();
    } catch (err) {
      if (!toastedGlobally(err)) this.toasts.error('Could not update the feedback', errorMessage(err));
    } finally {
      this.busy.set([]);
    }
  }
}
