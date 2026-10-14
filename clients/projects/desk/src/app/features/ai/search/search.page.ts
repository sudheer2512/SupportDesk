import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AiApi, SemanticSearchHitDto, SemanticSearchResultDto } from '@sd/core';
import { SdBadge, SdButton, SdCard, SdEmptyState, SdIcon, SdRadio, SdRadioGroup, SdSkeleton, SdTone } from '@sd/ui';
import { AiMeterComponent, aiErrorMessage } from '../shared/ai-ui';

type Scope = 'all' | 'tickets' | 'articles';

/**
 * Semantic search across knowledge articles, resolved tickets (the AI knowledge index) and open tickets, ranked by
 * meaning (embeddings) blended with keyword relevance. Results are numbered so they can be cited.
 */
@Component({
  selector: 'desk-ai-search',
  imports: [FormsModule, RouterLink, SdCard, SdButton, SdBadge, SdIcon, SdEmptyState, SdSkeleton, SdRadioGroup, SdRadio, AiMeterComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="layout">
      <sd-card>
        <form class="bar" role="search" (ngSubmit)="search()">
          <label for="ai-search" class="sd-visually-hidden">Search by meaning</label>
          <sd-icon name="search" [size]="18" aria-hidden="true" class="lead" />
          <input id="ai-search" class="sd-input q" name="q" [(ngModel)]="query" placeholder="Describe the problem in your own words…" autocomplete="off" />
          <button sdButton variant="primary" type="submit" [loading]="loading()" [disabled]="query.trim().length < 2">Search</button>
        </form>
        <sd-radio-group class="scope" [(value)]="scope" direction="row" ariaLabel="Search in" (valueChange)="search()">
          <sd-radio value="all">Everything</sd-radio>
          <sd-radio value="tickets">Tickets</sd-radio>
          <sd-radio value="articles">Knowledge articles</sd-radio>
        </sd-radio-group>
      </sd-card>

      @if (error()) {
        <div class="error" role="alert"><sd-icon name="circle-alert" [size]="16" />{{ error() }}</div>
      }

      @if (loading() && !result()) {
        <sd-card><sd-skeleton [lines]="8" /></sd-card>
      } @else if (result(); as r) {
        <sd-card [title]="r.items.length + ' result' + (r.items.length === 1 ? '' : 's') + ' for “' + r.query + '”'" [subtitle]="'Embedding model: ' + r.embeddingModel" padding="none">
          @if (r.items.length === 0) {
            <sd-empty-state icon="search" title="Nothing similar found" message="Try different words, or search everything." />
          } @else {
            <ol class="hits">
              @for (hit of r.items; track hit.sourceType + hit.sourceId; let i = $index) {
                <li class="hit">
                  <span class="n" aria-hidden="true">[{{ i + 1 }}]</span>
                  <div class="body">
                    <div class="head">
                      <sd-badge [tone]="tone(hit)" size="sm" [icon]="icon(hit)">{{ kind(hit) }}</sd-badge>
                      @if (link(hit); as l) {
                        <a class="title" [routerLink]="l">{{ hit.title }}</a>
                      } @else {
                        <span class="title">{{ hit.title }}</span>
                      }
                      @if (hit.status) {
                        <sd-badge size="sm" variant="outline">{{ hit.status }}</sd-badge>
                      }
                      @if (hit.visibility === 'Internal' && hit.sourceType === 'Article') {
                        <sd-badge size="sm" tone="warning" icon="lock">Internal</sd-badge>
                      }
                    </div>
                    @if (hit.snippet) {
                      <p class="snippet">{{ hit.snippet }}</p>
                    }
                    @if (hit.sourceType !== 'Article') {
                      <a class="sd-small" [routerLink]="['/ai/tickets', hit.sourceId]">AI insights for this ticket</a>
                    }
                  </div>
                  <desk-ai-meter class="score" [value]="hit.score" label="Relevance" />
                </li>
              }
            </ol>
          }
        </sd-card>
      } @else {
        <sd-card>
          <sd-empty-state icon="search" title="Search by meaning, not just keywords" message="Find similar resolved tickets and knowledge articles even when they use different words." />
        </sd-card>
      }
    </div>
  `,
  styles: `
    .layout {
      display: grid;
      gap: var(--sd-space-4);
    }
    .bar {
      display: flex;
      gap: 10px;
      align-items: center;
    }
    .lead {
      color: var(--sd-text-3);
    }
    .q {
      flex: 1;
      min-width: 0;
    }
    .scope {
      margin-top: 12px;
    }
    .error {
      display: flex;
      gap: 8px;
      align-items: center;
      padding: 10px 14px;
      border: 1px solid var(--sd-danger-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-danger-bg);
      color: var(--sd-danger);
    }
    .hits {
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .hit {
      display: grid;
      grid-template-columns: auto minmax(0, 1fr) 150px;
      gap: 12px;
      padding: 14px 16px;
      border-top: 1px solid var(--sd-divider);
    }
    .n {
      color: var(--sd-text-3);
      font-variant-numeric: tabular-nums;
      font-size: var(--sd-text-sm);
    }
    .head {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
    }
    .title {
      font-weight: var(--sd-weight-semibold);
    }
    .snippet {
      margin-top: 6px;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
      display: -webkit-box;
      -webkit-line-clamp: 3;
      -webkit-box-orient: vertical;
      overflow: hidden;
    }
    .score {
      align-self: start;
    }
    @media (max-width: 720px) {
      .hit {
        grid-template-columns: auto minmax(0, 1fr);
      }
      .score {
        grid-column: 2;
      }
    }
  `,
})
export class SearchPage implements OnInit {
  private readonly ai = inject(AiApi);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  /** `?q=` pre-fills and runs the search (links from the command palette or other screens). */
  readonly q = input<string | null>(null);

  protected query = '';
  protected readonly scope = signal<string | null>('all');
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly result = signal<SemanticSearchResultDto | null>(null);

  ngOnInit(): void {
    const initial = this.q();
    if (initial) {
      this.query = initial;
      this.search();
    }
  }

  protected search(): void {
    const q = this.query.trim();
    if (q.length < 2) return;
    this.loading.set(true);
    this.error.set(null);
    void this.router.navigate([], { queryParams: { q }, replaceUrl: true });
    this.ai
      .search({ query: q, scope: (this.scope() ?? 'all') as Scope, take: 30 }, { silent: true })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (r) => {
          this.result.set(r);
          this.loading.set(false);
        },
        error: (err: unknown) => {
          this.loading.set(false);
          this.error.set(aiErrorMessage(err));
        },
      });
  }

  protected kind(hit: SemanticSearchHitDto): string {
    return hit.sourceType === 'Article' ? 'Article' : hit.sourceType === 'Ticket' ? 'Resolved ticket' : 'Open ticket';
  }

  protected icon(hit: SemanticSearchHitDto): string {
    return hit.sourceType === 'Article' ? 'book-open' : 'ticket';
  }

  protected tone(hit: SemanticSearchHitDto): SdTone {
    return hit.sourceType === 'Article' ? 'accent' : hit.sourceType === 'Ticket' ? 'success' : 'primary';
  }

  protected link(hit: SemanticSearchHitDto): unknown[] | null {
    if (hit.sourceType === 'Article') return ['/kb/articles', hit.sourceId];
    return ['/tickets', hit.sourceId];
  }
}
