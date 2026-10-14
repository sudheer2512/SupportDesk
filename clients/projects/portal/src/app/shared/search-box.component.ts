import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  booleanAttribute,
  computed,
  inject,
  input,
  linkedSignal,
  signal,
  viewChild,
} from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { PortalArticleSummaryDto, PortalContentApi, uniqueId } from '@sd/core';
import { SdIcon } from '@sd/ui';
import { catchError, debounceTime, distinctUntilChanged, map, of, switchMap } from 'rxjs';
import { BrandService } from '../brand.service';

/**
 * Help-center search: submits to /:brand/search?q=… and, while typing (≥3 characters), offers matching
 * articles as an ARIA combobox (↓/↑ to move, Enter to open, Esc to close).
 * `<portal-search-box size="lg" [initial]="q()" />`
 */
@Component({
  selector: 'portal-search-box',
  imports: [SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form class="search" [class.is-lg]="size() === 'lg'" role="search" [attr.aria-label]="label()" (submit)="submit($event)">
      <label class="sd-visually-hidden" [for]="inputId">{{ label() }}</label>
      <sd-icon name="search" [size]="size() === 'lg' ? 20 : 17" class="lead" />
      <input
        #field
        [id]="inputId"
        class="field"
        type="search"
        name="q"
        autocomplete="off"
        spellcheck="false"
        role="combobox"
        aria-autocomplete="list"
        [attr.aria-expanded]="showList()"
        [attr.aria-controls]="listId"
        [attr.aria-activedescendant]="activeId()"
        [placeholder]="placeholder()"
        [value]="query()"
        (input)="onInput($event)"
        (keydown)="onKeydown($event)"
        (focus)="open.set(true)"
        (blur)="open.set(false)"
      />
      <button type="submit" class="go">Search</button>
      <ul class="list" role="listbox" [id]="listId" [attr.aria-label]="'Suggested articles'" [hidden]="!showList()" (mousedown)="$event.preventDefault()">
        @for (a of suggestions(); track a.id; let i = $index) {
          <li role="option" class="option" [id]="listId + '-' + i" [attr.aria-selected]="active() === i" (click)="openArticle(a)">
            <sd-icon name="file-text" [size]="15" />
            <span class="opt-text">
              <span class="opt-title">{{ a.title }}</span>
              @if (a.categoryName) {
                <span class="opt-meta">{{ a.categoryName }}</span>
              }
            </span>
          </li>
        }
        <li role="option" class="option all" [id]="listId + '-all'" [attr.aria-selected]="active() === suggestions().length" (click)="goToResults()">
          <sd-icon name="search" [size]="15" />
          <span class="opt-text"><span class="opt-title">See all results for “{{ query().trim() }}”</span></span>
        </li>
      </ul>
    </form>
  `,
  styles: `
    :host {
      display: block;
    }
    .search {
      position: relative;
      display: flex;
      align-items: center;
      gap: 8px;
      height: 46px;
      padding: 0 5px 0 14px;
      border: 1px solid var(--sd-border-strong);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
      box-shadow: var(--sd-shadow-sm);
      transition: border-color var(--sd-dur-fast) var(--sd-ease), box-shadow var(--sd-dur-fast) var(--sd-ease);
    }
    .search.is-lg {
      height: 58px;
      padding-left: 18px;
      border-radius: var(--sd-radius-xl);
      box-shadow: var(--sd-shadow-md);
    }
    .search:focus-within {
      border-color: var(--sd-primary);
      box-shadow: 0 0 0 3px var(--sd-focus), var(--sd-shadow-md);
    }
    .lead {
      color: var(--sd-text-3);
    }
    .field {
      flex: 1;
      min-width: 0;
      height: 100%;
      border: 0;
      background: transparent;
      color: var(--sd-text);
      font: inherit;
      font-size: var(--sd-text-md);
      outline: none;
    }
    .is-lg .field {
      font-size: var(--sd-text-lg);
    }
    .field::placeholder {
      color: var(--sd-text-3);
    }
    .go {
      flex: none;
      height: 36px;
      padding: 0 16px;
      border: 0;
      border-radius: var(--sd-radius-md);
      background: var(--sd-primary);
      color: var(--sd-on-primary);
      font: inherit;
      font-weight: var(--sd-weight-semibold);
      cursor: pointer;
    }
    .is-lg .go {
      height: 46px;
      padding: 0 22px;
      border-radius: var(--sd-radius-lg);
    }
    .go:hover {
      background: var(--sd-primary-hover);
    }
    .go:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .list {
      position: absolute;
      top: calc(100% + 6px);
      left: 0;
      right: 0;
      z-index: var(--sd-z-overlay);
      margin: 0;
      padding: 6px;
      list-style: none;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
      box-shadow: var(--sd-shadow-lg);
      text-align: left;
    }
    .list[hidden] {
      display: none;
    }
    .option {
      display: flex;
      align-items: flex-start;
      gap: 10px;
      padding: 9px 10px;
      border-radius: var(--sd-radius-md);
      color: var(--sd-text);
      cursor: pointer;
    }
    .option sd-icon {
      margin-top: 3px;
      color: var(--sd-text-3);
    }
    .option[aria-selected='true'],
    .option:hover {
      background: var(--sd-primary-soft);
    }
    .option.all {
      color: var(--sd-primary);
    }
    .opt-text {
      display: grid;
      min-width: 0;
    }
    .opt-title {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .opt-meta {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    @media (max-width: 480px) {
      .go {
        padding: 0 12px;
      }
    }
  `,
})
export class SearchBoxComponent {
  private readonly router = inject(Router);
  private readonly api = inject(PortalContentApi);
  private readonly brand = inject(BrandService);

  readonly initial = input<string | null | undefined>('');
  readonly size = input<'md' | 'lg'>('md');
  readonly placeholder = input('Search articles and community topics…');
  readonly label = input('Search the help center');
  readonly suggest = input(true, { transform: booleanAttribute });

  protected readonly inputId = uniqueId('portal-search');
  protected readonly listId = uniqueId('portal-search-list');
  protected readonly query = linkedSignal(() => this.initial() ?? '');
  protected readonly open = signal(false);
  protected readonly active = signal(-1);
  private readonly field = viewChild.required<ElementRef<HTMLInputElement>>('field');

  protected readonly suggestions = toSignal(
    toObservable(this.query).pipe(
      map((q) => q.trim()),
      debounceTime(250),
      distinctUntilChanged(),
      switchMap((q) => {
        const slug = this.brand.slug();
        if (!this.suggest() || !this.brand.features().kb || !slug || q.length < 3) return of<PortalArticleSummaryDto[]>([]);
        return this.api.suggest(slug, q, 6).pipe(catchError(() => of<PortalArticleSummaryDto[]>([])));
      }),
    ),
    { initialValue: [] as PortalArticleSummaryDto[] },
  );
  protected readonly showList = computed(() => this.open() && this.suggestions().length > 0 && this.query().trim().length >= 3);
  protected readonly activeId = computed(() => {
    const i = this.active();
    if (!this.showList() || i < 0) return null;
    return i === this.suggestions().length ? `${this.listId}-all` : `${this.listId}-${i}`;
  });

  protected onInput(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
    this.active.set(-1);
    this.open.set(true);
  }

  protected onKeydown(event: KeyboardEvent): void {
    const count = this.suggestions().length + 1;
    switch (event.key) {
      case 'ArrowDown':
        if (!this.showList()) {
          this.open.set(true);
          return;
        }
        event.preventDefault();
        this.active.set((this.active() + 1) % count);
        break;
      case 'ArrowUp':
        if (!this.showList()) return;
        event.preventDefault();
        this.active.set(this.active() <= 0 ? count - 1 : this.active() - 1);
        break;
      case 'Escape':
        if (this.showList()) {
          event.preventDefault();
          this.open.set(false);
          this.active.set(-1);
        }
        break;
      case 'Enter': {
        const i = this.active();
        if (this.showList() && i >= 0 && i < this.suggestions().length) {
          event.preventDefault();
          this.openArticle(this.suggestions()[i]);
        }
        break;
      }
    }
  }

  protected submit(event: Event): void {
    event.preventDefault();
    this.goToResults();
  }

  protected goToResults(): void {
    const q = this.query().trim();
    this.open.set(false);
    if (!q) {
      this.field().nativeElement.focus();
      return;
    }
    void this.router.navigate([this.brand.base(), 'search'], { queryParams: { q } });
  }

  protected openArticle(article: PortalArticleSummaryDto): void {
    this.open.set(false);
    void this.router.navigate([this.brand.base(), 'kb', article.slug]);
  }
}
