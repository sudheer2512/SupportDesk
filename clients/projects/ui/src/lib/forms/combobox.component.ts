import { CdkConnectedOverlay, CdkOverlayOrigin, ConnectedPosition } from '@angular/cdk/overlay';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  booleanAttribute,
  computed,
  effect,
  forwardRef,
  inject,
  input,
  model,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { NG_VALUE_ACCESSOR } from '@angular/forms';
import { fuzzyFilter, uniqueId } from '@sd/core';
import { Observable, Subscription, from, isObservable } from 'rxjs';
import { SdAvatar } from '../avatar/avatar.component';
import { SdIcon } from '../icon/icon.component';
import { SdSpinner } from '../spinner/spinner.component';
import { SdControlBase } from './control-base';

export interface SdOption {
  value: string;
  label: string;
  description?: string | null;
  icon?: string | null;
  /** Show an avatar with this name instead of an icon (agents, contacts). */
  avatar?: string | null;
  /** Colour swatch (statuses). */
  color?: string | null;
  group?: string | null;
  disabled?: boolean;
}

export type SdOptionSource = (query: string) => Observable<SdOption[]> | Promise<SdOption[]>;

type ComboValue = string | string[] | null;

const POSITIONS: ConnectedPosition[] = [
  { originX: 'start', originY: 'bottom', overlayX: 'start', overlayY: 'top', offsetY: 4 },
  { originX: 'start', originY: 'top', overlayX: 'start', overlayY: 'bottom', offsetY: -4 },
];

/**
 * Searchable select / multi-select (WAI-ARIA combobox + listbox).
 * `<sd-combobox [options]="agents" [(value)]="assigneeId" placeholder="Assignee" />`
 * `<sd-combobox multiple [options]="tags" [(value)]="tagIds" />`
 * `<sd-combobox [search]="searchContacts" />` — async source (debounced; stale results discarded).
 * Keyboard: ↓/↑ move, Enter selects, Esc closes, Backspace on empty input removes the last chip.
 */
@Component({
  selector: 'sd-combobox',
  imports: [CdkOverlayOrigin, CdkConnectedOverlay, SdIcon, SdAvatar, SdSpinner],
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => SdCombobox), multi: true }],
  template: `
    <div
      class="field"
      cdkOverlayOrigin
      #origin="cdkOverlayOrigin"
      [class.is-open]="open()"
      [class.is-disabled]="isDisabled()"
      [class.is-invalid]="invalid()"
      [class.is-sm]="size() === 'sm'"
      (click)="focusInput()"
    >
      @if (multiple()) {
        @for (opt of visibleChips(); track opt.value) {
          <span class="chip">
            <span class="sd-truncate">{{ opt.label }}</span>
            @if (!isDisabled()) {
              <button type="button" class="chip-x" [attr.aria-label]="'Remove ' + opt.label" (click)="remove(opt.value, $event)">
                <sd-icon name="x" [size]="11" />
              </button>
            }
          </span>
        }
        @if (hiddenChipCount() > 0) {
          <span class="chip chip--more">+{{ hiddenChipCount() }}</span>
        }
      } @else if (!open() && selectedOptions()[0]; as sel) {
        @if (sel.avatar) {
          <sd-avatar [name]="sel.avatar" size="xs" />
        } @else if (sel.color) {
          <span class="swatch" [style.background]="sel.color"></span>
        }
      }
      <input
        #input
        class="input"
        role="combobox"
        autocomplete="off"
        [id]="controlId()"
        [attr.aria-expanded]="open()"
        [attr.aria-controls]="listboxId"
        [attr.aria-activedescendant]="activeId()"
        [attr.aria-autocomplete]="'list'"
        [attr.aria-describedby]="describedBy()"
        [attr.aria-invalid]="invalid() ? 'true' : null"
        [attr.aria-label]="ariaLabel()"
        [disabled]="isDisabled()"
        [readOnly]="!searchable()"
        [placeholder]="showPlaceholder() ? placeholder() : ''"
        [value]="inputText()"
        (input)="onInput($event)"
        (keydown)="onKeydown($event)"
        (focus)="onFocus()"
        (blur)="onBlur()"
      />
      @if (loading()) {
        <sd-spinner [size]="14" class="trail" />
      } @else if (clearable() && hasValue() && !isDisabled()) {
        <button type="button" class="trail clear" aria-label="Clear selection" (click)="clear($event)">
          <sd-icon name="x" [size]="13" />
        </button>
      }
      <sd-icon class="trail caret" name="chevrons-up-down" [size]="14" />
    </div>

    <ng-template
      cdkConnectedOverlay
      [cdkConnectedOverlayOrigin]="origin"
      [cdkConnectedOverlayOpen]="open()"
      [cdkConnectedOverlayPositions]="positions"
      [cdkConnectedOverlayMinWidth]="overlayWidth()"
      (overlayOutsideClick)="onOutsideClick($event)"
      (detach)="close()"
    >
      <div class="sd-popover panel" [id]="listboxId" role="listbox" [attr.aria-multiselectable]="multiple() || null">
        @for (opt of filtered(); track opt.value; let i = $index) {
          @if (opt.group && (i === 0 || filtered()[i - 1].group !== opt.group)) {
            <div class="group" role="presentation">{{ opt.group }}</div>
          }
          <div
            class="option"
            role="option"
            [id]="listboxId + '-' + i"
            [class.is-active]="i === activeIndex()"
            [attr.aria-selected]="isSelected(opt.value)"
            [attr.aria-disabled]="opt.disabled || null"
            (mousedown)="$event.preventDefault()"
            (click)="choose(opt)"
            (mousemove)="activeIndex.set(i)"
          >
            @if (multiple()) {
              <span class="check" [class.on]="isSelected(opt.value)" aria-hidden="true">
                @if (isSelected(opt.value)) {
                  <sd-icon name="check" [size]="11" [strokeWidth]="3" />
                }
              </span>
            }
            @if (opt.avatar) {
              <sd-avatar [name]="opt.avatar" size="xs" />
            } @else if (opt.color) {
              <span class="swatch" [style.background]="opt.color"></span>
            } @else if (opt.icon) {
              <sd-icon [name]="opt.icon" [size]="15" />
            }
            <span class="labels">
              <span class="sd-truncate">{{ opt.label }}</span>
              @if (opt.description) {
                <span class="desc sd-truncate">{{ opt.description }}</span>
              }
            </span>
            @if (!multiple() && isSelected(opt.value)) {
              <sd-icon class="tick" name="check" [size]="14" />
            }
          </div>
        } @empty {
          <div class="empty">{{ loading() ? 'Searching…' : emptyText() }}</div>
        }
      </div>
    </ng-template>
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .field {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 4px;
      min-height: var(--sd-control-h);
      padding: 3px 8px 3px 6px;
      border: 1px solid var(--sd-border-strong);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      cursor: text;
      transition: border-color var(--sd-dur-fast) var(--sd-ease), box-shadow var(--sd-dur-fast) var(--sd-ease);
    }
    .field.is-sm {
      min-height: var(--sd-control-h-sm);
      padding: 1px 6px 1px 4px;
      font-size: var(--sd-text-sm);
    }
    .field:focus-within,
    .field.is-open {
      border-color: var(--sd-primary);
      box-shadow: 0 0 0 3px var(--sd-focus);
    }
    .field.is-invalid {
      border-color: var(--sd-danger);
    }
    .field.is-disabled {
      background: var(--sd-surface-2);
      cursor: not-allowed;
    }
    .input {
      flex: 1 1 60px;
      min-width: 60px;
      height: 26px;
      padding: 0 2px 0 4px;
      border: 0;
      outline: none;
      background: transparent;
      color: var(--sd-text);
    }
    .input::placeholder {
      color: var(--sd-text-3);
    }
    .input:disabled {
      cursor: not-allowed;
      color: var(--sd-text-disabled);
    }
    .input[readonly] {
      cursor: pointer;
    }
    .chip {
      display: inline-flex;
      align-items: center;
      gap: 2px;
      max-width: 180px;
      height: 22px;
      padding: 0 4px 0 8px;
      border-radius: var(--sd-radius-sm);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-medium);
    }
    .chip--more {
      padding: 0 8px;
      background: var(--sd-surface-3);
      color: var(--sd-text-2);
    }
    .chip-x {
      display: inline-flex;
      padding: 2px;
      border: 0;
      border-radius: 3px;
      background: transparent;
      color: inherit;
      cursor: pointer;
    }
    .chip-x:hover {
      background: color-mix(in oklab, var(--sd-primary) 18%, transparent);
    }
    .trail {
      color: var(--sd-text-3);
      flex: none;
    }
    .clear {
      display: inline-flex;
      padding: 3px;
      border: 0;
      border-radius: var(--sd-radius-xs);
      background: transparent;
      cursor: pointer;
    }
    .clear:hover {
      background: var(--sd-surface-3);
      color: var(--sd-text);
    }
    .panel {
      max-height: 300px;
      overflow: auto;
      padding: 4px;
      font-size: var(--sd-text-md);
    }
    .group {
      padding: 8px 8px 4px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .option {
      display: flex;
      align-items: center;
      gap: 8px;
      min-height: 32px;
      padding: 5px 8px;
      border-radius: var(--sd-radius-sm);
      cursor: pointer;
    }
    .option.is-active {
      background: var(--sd-surface-3);
    }
    .option[aria-selected='true'] {
      font-weight: var(--sd-weight-medium);
    }
    .option[aria-disabled='true'] {
      color: var(--sd-text-disabled);
      cursor: not-allowed;
    }
    .labels {
      display: flex;
      flex-direction: column;
      flex: 1;
      min-width: 0;
    }
    .desc {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .tick {
      color: var(--sd-primary);
    }
    .check {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 15px;
      height: 15px;
      flex: none;
      border: 1.5px solid var(--sd-border-strong);
      border-radius: 4px;
      color: var(--sd-on-primary);
    }
    .check.on {
      background: var(--sd-primary);
      border-color: var(--sd-primary);
    }
    .swatch {
      width: 10px;
      height: 10px;
      border-radius: 50%;
      flex: none;
    }
    .empty {
      padding: 10px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
  `,
})
export class SdCombobox extends SdControlBase<ComboValue> {
  readonly value = model<ComboValue>(null);
  readonly options = input<SdOption[]>([]);
  readonly multiple = input(false, { transform: booleanAttribute });
  readonly searchable = input(true, { transform: booleanAttribute });
  readonly clearable = input(true, { transform: booleanAttribute });
  readonly placeholder = input('Select…');
  readonly emptyText = input('No matches');
  readonly ariaLabel = input<string | null>(null);
  readonly size = input<'sm' | 'md'>('md');
  readonly maxChips = input(3);
  /** Async option source; when set, `options` are only the initial/known options. */
  readonly search = input<SdOptionSource | null>(null);

  protected readonly positions = POSITIONS;
  protected readonly listboxId = uniqueId('sd-listbox');
  protected readonly open = signal(false);
  protected readonly query = signal('');
  protected readonly activeIndex = signal(0);
  protected readonly loading = signal(false);
  private readonly remote = signal<SdOption[] | null>(null);
  private readonly known = signal<ReadonlyMap<string, SdOption>>(new Map());
  private readonly inputEl = viewChild.required<ElementRef<HTMLInputElement>>('input');
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly overlayWidth = signal(200);
  private searchSub: Subscription | null = null;
  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  private readonly selectedValues = computed<string[]>(() => {
    const v = this.value();
    if (v === null || v === undefined || v === '') return [];
    return Array.isArray(v) ? v : [v];
  });
  protected readonly selectedOptions = computed(() => {
    const known = this.known();
    return this.selectedValues().map((value) => known.get(value) ?? { value, label: value });
  });
  protected readonly visibleChips = computed(() => this.selectedOptions().slice(0, this.maxChips()));
  protected readonly hiddenChipCount = computed(() => Math.max(0, this.selectedOptions().length - this.maxChips()));
  protected readonly hasValue = computed(() => this.selectedValues().length > 0);
  protected readonly showPlaceholder = computed(() => !this.hasValue() || (!this.multiple() && this.open()));
  protected readonly inputText = computed(() => {
    if (this.open() || this.multiple()) return this.query();
    return this.selectedOptions()[0]?.label ?? '';
  });
  protected readonly filtered = computed(() => {
    const remote = this.remote();
    if (this.search() && remote) return remote;
    return fuzzyFilter(this.options(), this.query(), (o) => [o.label, o.description ?? ''], 200);
  });
  protected readonly activeId = computed(() =>
    this.open() && this.filtered().length ? `${this.listboxId}-${this.activeIndex()}` : null,
  );

  constructor() {
    super();
    effect(() => {
      const opts = this.options();
      untracked(() => this.remember(opts));
    });
    inject(DestroyRef).onDestroy(() => {
      this.searchSub?.unsubscribe();
      if (this.searchTimer) clearTimeout(this.searchTimer);
    });
  }

  protected emptyValue(): ComboValue {
    return this.multiple() ? [] : null;
  }

  protected isSelected(value: string): boolean {
    return this.selectedValues().includes(value);
  }

  protected focusInput(): void {
    if (this.isDisabled()) return;
    this.inputEl().nativeElement.focus();
    this.openPanel();
  }

  protected onFocus(): void {
    if (!this.searchable()) return;
  }

  protected onBlur(): void {
    this.touched();
  }

  protected onInput(event: Event): void {
    const text = (event.target as HTMLInputElement).value;
    this.query.set(text);
    this.activeIndex.set(0);
    this.openPanel();
    this.runSearch(text);
  }

  protected onKeydown(event: KeyboardEvent): void {
    const count = this.filtered().length;
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        if (!this.open()) this.openPanel();
        else this.move(1, count);
        break;
      case 'ArrowUp':
        event.preventDefault();
        if (!this.open()) this.openPanel();
        else this.move(-1, count);
        break;
      case 'Enter':
        if (this.open()) {
          event.preventDefault();
          const opt = this.filtered()[this.activeIndex()];
          if (opt) this.choose(opt);
        } else {
          this.openPanel();
        }
        break;
      case ' ':
        if (!this.searchable()) {
          event.preventDefault();
          this.openPanel();
        }
        break;
      case 'Escape':
        if (this.open()) {
          event.preventDefault();
          event.stopPropagation();
          this.close();
        }
        break;
      case 'Tab':
        this.close();
        break;
      case 'Backspace':
        if (this.multiple() && !this.query()) {
          const values = this.selectedValues();
          if (values.length) this.remove(values[values.length - 1]);
        }
        break;
    }
  }

  protected choose(option: SdOption): void {
    if (option.disabled) return;
    this.remember([option]);
    if (this.multiple()) {
      const values = this.selectedValues();
      this.commit(values.includes(option.value) ? values.filter((v) => v !== option.value) : [...values, option.value]);
      this.query.set('');
    } else {
      this.commit(option.value);
      this.close();
    }
  }

  protected remove(value: string, event?: Event): void {
    event?.stopPropagation();
    this.commit(this.selectedValues().filter((v) => v !== value));
  }

  protected clear(event: Event): void {
    event.stopPropagation();
    this.commit(this.multiple() ? [] : null);
    this.query.set('');
  }

  protected onOutsideClick(event: MouseEvent): void {
    if (!this.host.nativeElement.contains(event.target as Node)) this.close();
  }

  protected close(): void {
    if (!this.open()) return;
    this.open.set(false);
    this.query.set('');
    this.remote.set(null);
  }

  private openPanel(): void {
    if (this.isDisabled() || this.open()) return;
    this.overlayWidth.set(this.host.nativeElement.getBoundingClientRect().width || 200);
    const index = this.filtered().findIndex((o) => this.isSelected(o.value));
    this.activeIndex.set(Math.max(0, index));
    this.open.set(true);
    if (this.search()) this.runSearch(this.query());
  }

  private move(delta: number, count: number): void {
    if (count === 0) return;
    const filtered = this.filtered();
    let next = this.activeIndex();
    for (let i = 0; i < count; i++) {
      next = (next + delta + count) % count;
      if (!filtered[next]?.disabled) break;
    }
    this.activeIndex.set(next);
    queueMicrotask(() => document.getElementById(`${this.listboxId}-${next}`)?.scrollIntoView({ block: 'nearest' }));
  }

  private runSearch(text: string): void {
    const source = this.search();
    if (!source) return;
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      this.searchSub?.unsubscribe();
      this.loading.set(true);
      const result = source(text);
      const obs = isObservable(result) ? result : from(result);
      this.searchSub = obs.subscribe({
        next: (opts) => {
          this.remember(opts);
          this.remote.set(opts);
          this.activeIndex.set(0);
          this.loading.set(false);
        },
        error: () => {
          this.remote.set([]);
          this.loading.set(false);
        },
      });
    }, 200);
  }

  private remember(options: readonly SdOption[]): void {
    if (!options.length) return;
    this.known.update((map) => {
      const next = new Map(map);
      for (const o of options) next.set(o.value, o);
      return next;
    });
  }
}
