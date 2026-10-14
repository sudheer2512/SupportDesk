import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { SdButton } from '@sd/ui';

/**
 * Offset pager for `PageResult` lists:
 * `<desk-kb-pager [page]="r.page" [pageSize]="r.pageSize" [total]="r.total" (pageChange)="go($event)" />`
 * Pages are 1-based. Announces the visible range politely to screen readers.
 */
@Component({
  selector: 'desk-kb-pager',
  imports: [SdButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav class="pager" [attr.aria-label]="label()">
      <span class="range" aria-live="polite">
        @if (total() > 0) {
          Showing <strong>{{ from() }}–{{ to() }}</strong> of <strong>{{ total().toLocaleString() }}</strong>
        } @else {
          No results
        }
      </span>
      @if (pages() > 1) {
        <span class="sd-spacer"></span>
        <button sdButton size="sm" variant="ghost" icon="chevron-left" type="button" [disabled]="page() <= 1 || disabled()" (click)="go(page() - 1)">
          Previous
        </button>
        <span class="count sd-small">Page {{ page() }} of {{ pages() }}</span>
        <button
          sdButton
          size="sm"
          variant="ghost"
          iconRight="chevron-right"
          type="button"
          [disabled]="page() >= pages() || disabled()"
          (click)="go(page() + 1)"
        >
          Next
        </button>
      }
    </nav>
  `,
  styles: `
    .pager {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--sd-space-2);
      padding: var(--sd-space-2) 0;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .count {
      color: var(--sd-text-3);
      font-variant-numeric: tabular-nums;
    }
  `,
})
export class KbPager {
  readonly page = input(1);
  readonly pageSize = input(25);
  readonly total = input(0);
  readonly disabled = input(false);
  readonly label = input('Pagination');
  readonly pageChange = output<number>();

  protected readonly pages = computed(() => Math.max(1, Math.ceil(this.total() / Math.max(1, this.pageSize()))));
  protected readonly from = computed(() => (this.total() === 0 ? 0 : (this.page() - 1) * this.pageSize() + 1));
  protected readonly to = computed(() => Math.min(this.total(), this.page() * this.pageSize()));

  protected go(page: number): void {
    const next = Math.min(Math.max(1, page), this.pages());
    if (next !== this.page()) this.pageChange.emit(next);
  }
}
