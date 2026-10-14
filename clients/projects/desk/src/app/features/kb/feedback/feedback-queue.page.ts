import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ArticleFeedbackDto, ArticleFeedbackQuery, KnowledgeApi, MeStore, PageResult, ToastBridge } from '@sd/core';
import { SdButton, SdCheckbox, SdEmptyState, SdIcon, SdInput, SdSkeleton, SdToggle } from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { KbFeedbackList } from '../components/feedback-list.component';
import { KbPager } from '../components/kb-pager.component';
import { KB_STYLES, errorMessage, remote, toastedGlobally } from '../kb-common';

const PAGE_SIZE = 25;

type ReviewedFilter = 'unreviewed' | 'reviewed' | 'all';
type VerdictFilter = 'all' | 'helpful' | 'not-helpful';

/** /kb/feedback — reader feedback across all articles: filter, multi-select and mark reviewed. */
@Component({
  selector: 'desk-kb-feedback-queue',
  imports: [FormsModule, SdButton, SdIcon, SdInput, SdToggle, SdCheckbox, SdSkeleton, SdEmptyState, KbFeedbackList, KbPager],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section aria-labelledby="kb-fb-title">
      <header class="kb-head">
        <div class="kb-head__titles">
          <h2 id="kb-fb-title">Feedback</h2>
          <p class="kb-head__sub">What readers say about articles — triage comments, then mark them reviewed.</p>
        </div>
        <div class="kb-head__actions">
          <button sdButton variant="ghost" icon="refresh" type="button" [loading]="result.loading()" (click)="result.reload()">Refresh</button>
        </div>
      </header>

      <div class="kb-toolbar" role="search" aria-label="Filter feedback">
        <div class="kb-filter">
          <label for="kb-fb-reviewed">Review state</label>
          <select sdInput id="kb-fb-reviewed" [ngModel]="reviewed()" (ngModelChange)="setReviewed($event)">
            <option value="unreviewed">Needs review</option>
            <option value="reviewed">Reviewed</option>
            <option value="all">All</option>
          </select>
        </div>
        <div class="kb-filter">
          <label for="kb-fb-verdict">Verdict</label>
          <select sdInput id="kb-fb-verdict" [ngModel]="verdict()" (ngModelChange)="setVerdict($event)">
            <option value="all">Helpful and not helpful</option>
            <option value="helpful">Helpful</option>
            <option value="not-helpful">Not helpful</option>
          </select>
        </div>
        <div class="toggle">
          <sd-toggle [value]="withComments()" (valueChange)="setWithComments($event)">With comments only</sd-toggle>
        </div>
      </div>

      @if (canReview() && items().length) {
        <div class="selection-bar" role="region" aria-label="Selection">
          <sd-checkbox [value]="allSelected()" [indeterminate]="someSelected()" ariaLabel="Select all unreviewed feedback on this page" (valueChange)="selectAll($event)">
            Select all on page
          </sd-checkbox>
          <span class="sd-small sd-muted" aria-live="polite">{{ selected().length ? selected().length + ' selected' : '' }}</span>
          <span class="sd-spacer"></span>
          <button sdButton size="sm" variant="primary" icon="check-check" type="button" [disabled]="!selected().length" [loading]="marking()" (click)="markReviewed(selected())">
            Mark reviewed
          </button>
        </div>
      }

      @if (result.error(); as err) {
        <div class="kb-error" role="alert">
          <sd-icon name="circle-alert" [size]="16" />
          <p>Could not load feedback. {{ err }}</p>
          <button sdButton size="sm" type="button" icon="refresh" (click)="result.reload()">Retry</button>
        </div>
      } @else if (result.loading() && !result.value()) {
        <div class="skeleton" aria-busy="true" aria-label="Loading feedback">
          @for (n of [1, 2, 3, 4]; track n) {
            <sd-skeleton height="84px" radius="var(--sd-radius-md)" />
          }
        </div>
      } @else if (items().length === 0) {
        <sd-empty-state
          icon="check-check"
          [title]="reviewed() === 'unreviewed' ? 'All caught up' : 'No feedback matches'"
          [message]="reviewed() === 'unreviewed' ? 'There is no feedback waiting for review.' : 'Try a different filter.'"
        />
      } @else {
        <desk-kb-feedback-list
          [items]="items()"
          showArticle
          [selectable]="canReview()"
          [canReview]="canReview()"
          [selected]="selected()"
          [busyIds]="busyIds()"
          ariaLabel="Feedback"
          (selectedChange)="selected.set($event)"
          (review)="markReviewed($event)"
        />
      }

      @if (result.value(); as r) {
        @if (r.total > 0) {
          <desk-kb-pager [page]="r.page" [pageSize]="r.pageSize" [total]="r.total" [disabled]="result.loading()" label="Feedback pages" (pageChange)="goToPage($event)" />
        }
      }
    </section>
  `,
  styles: [
    KB_STYLES,
    `
      .toggle {
        align-self: center;
        padding-top: 16px;
      }
      .selection-bar {
        position: sticky;
        top: 0;
        z-index: var(--sd-z-sticky);
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sd-space-3);
        margin-bottom: var(--sd-space-2);
        padding: var(--sd-space-2) var(--sd-space-3);
        border: 1px solid var(--sd-border);
        border-radius: var(--sd-radius-md);
        background: var(--sd-surface-2);
      }
      .skeleton {
        display: grid;
        gap: 6px;
      }
    `,
  ],
})
export class FeedbackQueuePage {
  private readonly api = inject(KnowledgeApi);
  private readonly me = inject(MeStore);
  private readonly toasts = inject(ToastBridge);

  protected readonly result = remote<PageResult<ArticleFeedbackDto>>();
  protected readonly items = computed(() => this.result.value()?.items ?? []);
  protected readonly reviewed = signal<ReviewedFilter>('unreviewed');
  protected readonly verdict = signal<VerdictFilter>('all');
  protected readonly withComments = signal(false);
  protected readonly page = signal(1);
  protected readonly selected = signal<string[]>([]);
  protected readonly busyIds = signal<string[]>([]);
  protected readonly marking = signal(false);
  protected readonly canReview = computed(() => this.me.canAny(['kb.author', 'kb.review', 'kb.publish']));

  private readonly selectable = computed(() => this.items().filter((f) => !f.reviewed).map((f) => f.id));
  protected readonly allSelected = computed(() => {
    const ids = this.selectable();
    const set = new Set(this.selected());
    return ids.length > 0 && ids.every((id) => set.has(id));
  });
  protected readonly someSelected = computed(() => !this.allSelected() && this.selected().length > 0);

  private readonly query = computed<ArticleFeedbackQuery>(() => ({
    reviewed: this.reviewed() === 'all' ? null : this.reviewed() === 'reviewed',
    helpful: this.verdict() === 'all' ? null : this.verdict() === 'helpful',
    withComments: this.withComments() || null,
    page: this.page(),
    pageSize: PAGE_SIZE,
  }));

  constructor() {
    effect(() => {
      const query = this.query();
      untracked(() => {
        this.selected.set([]);
        this.result.load(() => this.api.feedback(query), { keepValue: true });
      });
    });
    // Reviewing the last items of the last page leaves it empty: step back to the new last page.
    effect(() => {
      const r = this.result.value();
      if (!r || r.items.length > 0 || r.total === 0) return;
      const last = Math.max(1, Math.ceil(r.total / Math.max(1, r.pageSize)));
      untracked(() => {
        if (this.page() > last) this.page.set(last);
      });
    });
  }

  protected setReviewed(value: ReviewedFilter): void {
    this.reviewed.set(value);
    this.page.set(1);
  }

  protected setVerdict(value: VerdictFilter): void {
    this.verdict.set(value);
    this.page.set(1);
  }

  protected setWithComments(value: boolean): void {
    this.withComments.set(value);
    this.page.set(1);
  }

  protected goToPage(page: number): void {
    this.page.set(page);
  }

  protected selectAll(on: boolean): void {
    this.selected.set(on ? this.selectable() : []);
  }

  protected async markReviewed(ids: string[]): Promise<void> {
    if (!ids.length) return;
    this.busyIds.set(ids);
    this.marking.set(ids.length > 1 || this.selected().length > 0);
    try {
      await firstValueFrom(this.api.markFeedbackReviewed(ids));
      const done = new Set(ids);
      this.selected.update((list) => list.filter((id) => !done.has(id)));
      this.toasts.success(ids.length === 1 ? 'Marked as reviewed' : `${ids.length} items marked as reviewed`);
      if (this.reviewed() === 'unreviewed') {
        this.result.reload();
      } else {
        const current = this.result.value();
        if (current) this.result.set({ ...current, items: current.items.map((f) => (done.has(f.id) ? { ...f, reviewed: true } : f)) });
      }
    } catch (err) {
      if (!toastedGlobally(err)) this.toasts.error('Could not mark the feedback as reviewed', errorMessage(err));
    } finally {
      this.busyIds.set([]);
      this.marking.set(false);
    }
  }
}
