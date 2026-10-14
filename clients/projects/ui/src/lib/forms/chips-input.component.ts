import { CdkConnectedOverlay, CdkOverlayOrigin, ConnectedPosition } from '@angular/cdk/overlay';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  booleanAttribute,
  computed,
  forwardRef,
  inject,
  input,
  model,
  signal,
  viewChild,
} from '@angular/core';
import { NG_VALUE_ACCESSOR } from '@angular/forms';
import { fuzzyFilter, uniqueId } from '@sd/core';
import { SdIcon } from '../icon/icon.component';
import { SdControlBase } from './control-base';

const POSITIONS: ConnectedPosition[] = [
  { originX: 'start', originY: 'bottom', overlayX: 'start', overlayY: 'top', offsetY: 4 },
  { originX: 'start', originY: 'top', overlayX: 'start', overlayY: 'bottom', offsetY: -4 },
];

/**
 * Tag / chips input: `<sd-chips-input [(value)]="tags" [suggestions]="meta.tags" placeholder="Add tag" />`.
 * Enter, comma or Tab (with text) adds; Backspace on empty input removes the last chip; pasting
 * "a, b; c" adds several. `validate` rejects entries (e.g. e-mail addresses for CC). Duplicates ignored.
 */
@Component({
  selector: 'sd-chips-input',
  imports: [CdkOverlayOrigin, CdkConnectedOverlay, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => SdChipsInput), multi: true }],
  template: `
    <div
      class="field"
      cdkOverlayOrigin
      #origin="cdkOverlayOrigin"
      [class.is-disabled]="isDisabled()"
      [class.is-invalid]="invalid() || rejected()"
      (click)="input.focus()"
    >
      @for (chip of value(); track chip) {
        <span class="chip">
          <span class="sd-truncate">{{ chip }}</span>
          @if (!isDisabled()) {
            <button type="button" class="chip-x" [attr.aria-label]="'Remove ' + chip" (click)="remove(chip, $event)">
              <sd-icon name="x" [size]="11" />
            </button>
          }
        </span>
      }
      <input
        #input
        class="input"
        autocomplete="off"
        [id]="controlId()"
        role="combobox"
        [attr.aria-expanded]="showSuggestions()"
        [attr.aria-controls]="listId"
        [attr.aria-activedescendant]="showSuggestions() ? listId + '-' + active() : null"
        [attr.aria-describedby]="describedBy()"
        [attr.aria-label]="ariaLabel()"
        [disabled]="isDisabled()"
        [placeholder]="value().length ? '' : placeholder()"
        [value]="text()"
        (input)="onInput($event)"
        (keydown)="onKeydown($event)"
        (paste)="onPaste($event)"
        (blur)="onBlur()"
      />
    </div>
    @if (rejected()) {
      <p class="rejected" role="alert">“{{ rejected() }}” is not valid.</p>
    }
    <ng-template
      cdkConnectedOverlay
      [cdkConnectedOverlayOrigin]="origin"
      [cdkConnectedOverlayOpen]="showSuggestions()"
      [cdkConnectedOverlayPositions]="positions"
      (overlayOutsideClick)="text.set('')"
    >
      <div class="sd-popover panel" role="listbox" [id]="listId">
        @for (s of matches(); track s; let i = $index) {
          <div
            class="option"
            role="option"
            [id]="listId + '-' + i"
            [class.is-active]="i === active()"
            [attr.aria-selected]="i === active()"
            (mousedown)="$event.preventDefault()"
            (click)="add(s)"
          >
            {{ s }}
          </div>
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
      padding: 3px 6px;
      border: 1px solid var(--sd-border-strong);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      cursor: text;
    }
    .field:focus-within {
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
      flex: 1 1 80px;
      min-width: 80px;
      height: 26px;
      border: 0;
      outline: none;
      background: transparent;
      color: var(--sd-text);
      padding: 0 4px;
    }
    .input::placeholder {
      color: var(--sd-text-3);
    }
    .chip {
      display: inline-flex;
      align-items: center;
      gap: 2px;
      max-width: 220px;
      height: 22px;
      padding: 0 4px 0 8px;
      border-radius: var(--sd-radius-sm);
      background: var(--sd-surface-3);
      color: var(--sd-text);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-medium);
    }
    .chip-x {
      display: inline-flex;
      padding: 2px;
      border: 0;
      border-radius: 3px;
      background: transparent;
      color: var(--sd-text-3);
      cursor: pointer;
    }
    .chip-x:hover {
      color: var(--sd-text);
      background: var(--sd-border);
    }
    .rejected {
      margin-top: 4px;
      color: var(--sd-danger);
      font-size: var(--sd-text-xs);
    }
    .panel {
      min-width: 200px;
      max-height: 240px;
      overflow: auto;
      padding: 4px;
    }
    .option {
      padding: 6px 8px;
      border-radius: var(--sd-radius-sm);
      cursor: pointer;
    }
    .option.is-active {
      background: var(--sd-surface-3);
    }
  `,
})
export class SdChipsInput extends SdControlBase<string[]> {
  readonly value = model<string[]>([]);
  readonly suggestions = input<readonly string[]>([]);
  readonly placeholder = input('Add…');
  readonly ariaLabel = input<string | null>(null);
  readonly allowNew = input(true, { transform: booleanAttribute });
  readonly lowercase = input(false, { transform: booleanAttribute });
  readonly validate = input<((value: string) => boolean) | null>(null);

  protected readonly positions = POSITIONS;
  protected readonly listId = uniqueId('sd-chips');
  protected readonly text = signal('');
  protected readonly active = signal(0);
  protected readonly rejected = signal<string | null>(null);
  private readonly inputEl = viewChild.required<ElementRef<HTMLInputElement>>('input');
  protected readonly host = inject(ElementRef);

  protected readonly matches = computed(() => {
    const q = this.text().trim();
    if (!q) return [];
    const taken = new Set(this.value().map((v) => v.toLowerCase()));
    return fuzzyFilter(
      this.suggestions().filter((s) => !taken.has(s.toLowerCase())),
      q,
      (s) => s,
      8,
    );
  });
  protected readonly showSuggestions = computed(() => this.matches().length > 0);

  protected emptyValue(): string[] {
    return [];
  }

  protected onInput(event: Event): void {
    this.text.set((event.target as HTMLInputElement).value);
    this.active.set(0);
    this.rejected.set(null);
  }

  protected onKeydown(event: KeyboardEvent): void {
    const matches = this.matches();
    if (event.key === 'ArrowDown' && matches.length) {
      event.preventDefault();
      this.active.update((i) => (i + 1) % matches.length);
    } else if (event.key === 'ArrowUp' && matches.length) {
      event.preventDefault();
      this.active.update((i) => (i - 1 + matches.length) % matches.length);
    } else if (event.key === 'Enter' || event.key === ',' || (event.key === 'Tab' && this.text().trim())) {
      if (!this.text().trim()) return;
      event.preventDefault();
      const pick = matches.length && event.key !== ',' ? matches[this.active()] : this.text();
      this.add(pick);
    } else if (event.key === 'Backspace' && !this.text()) {
      const values = this.value();
      if (values.length) this.commit(values.slice(0, -1));
    } else if (event.key === 'Escape' && this.text()) {
      event.stopPropagation();
      this.text.set('');
    }
  }

  protected onPaste(event: ClipboardEvent): void {
    const data = event.clipboardData?.getData('text') ?? '';
    if (!/[,;\n]/.test(data)) return;
    event.preventDefault();
    for (const part of data.split(/[,;\n]+/)) this.add(part, false);
  }

  protected onBlur(): void {
    if (this.text().trim() && this.allowNew()) this.add(this.text());
    this.touched();
  }

  add(raw: string, clearText = true): void {
    let item = raw.trim();
    if (!item) return;
    if (this.lowercase()) item = item.toLowerCase();
    const known = this.suggestions().some((s) => s.toLowerCase() === item.toLowerCase());
    if (!known && !this.allowNew()) {
      this.rejected.set(item);
      return;
    }
    const validate = this.validate();
    if (validate && !validate(item)) {
      this.rejected.set(item);
      return;
    }
    this.rejected.set(null);
    if (!this.value().some((v) => v.toLowerCase() === item.toLowerCase())) this.commit([...this.value(), item]);
    if (clearText) {
      this.text.set('');
      this.inputEl().nativeElement.value = '';
    }
  }

  protected remove(chip: string, event: Event): void {
    event.stopPropagation();
    this.commit(this.value().filter((v) => v !== chip));
  }
}
