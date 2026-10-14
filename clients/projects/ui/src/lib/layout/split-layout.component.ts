import {
  ChangeDetectionStrategy,
  Component,
  booleanAttribute,
  computed,
  effect,
  input,
  model,
  signal,
  untracked,
} from '@angular/core';
import { STORAGE_KEYS, safeStorage } from '@sd/core';
import { SdIcon } from '../icon/icon.component';

interface SplitPrefs {
  left: number;
  right: number;
  rightCollapsed: boolean;
  leftCollapsed: boolean;
}

/**
 * Three-panel workbench layout (list | detail | context):
 * ```html
 * <sd-split-layout storageKey="tickets" [(rightCollapsed)]="ctxHidden">
 *   <section sdSplitLeft>…</section> <section sdSplitMain>…</section> <aside sdSplitRight>…</aside>
 * </sd-split-layout>
 * ```
 * Side panels resize by dragging the separators (or ←/→ when focused; Home resets), collapse with the
 * toggle buttons, and remember widths per `storageKey`. Under 1100 px the right panel floats over the main
 * area; under 768 px the left one does too. Set `showLeft=false` for a two-panel layout.
 */
@Component({
  selector: 'sd-split-layout',
  imports: [SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (showLeft() && !leftCollapsed()) {
      <div class="panel panel--left" [style.width.px]="left()">
        <ng-content select="[sdSplitLeft]" />
      </div>
      <div
        class="handle"
        role="separator"
        tabindex="0"
        aria-orientation="vertical"
        aria-label="Resize list panel"
        [attr.aria-valuenow]="left()"
        [attr.aria-valuemin]="minLeft()"
        [attr.aria-valuemax]="maxLeft()"
        (pointerdown)="startDrag($event, 'left')"
        (keydown)="onHandleKey($event, 'left')"
        (dblclick)="reset('left')"
      ></div>
    }
    <div class="panel panel--main">
      <div class="toggles">
        @if (showLeft()) {
          <button
            type="button"
            class="toggle"
            [attr.aria-label]="leftCollapsed() ? 'Show list panel' : 'Hide list panel'"
            [attr.aria-expanded]="!leftCollapsed()"
            (click)="setLeftCollapsed(!leftCollapsed())"
          >
            <sd-icon [name]="leftCollapsed() ? 'panel-left-open' : 'panel-left-close'" [size]="15" />
          </button>
        }
        <span class="sd-spacer"></span>
        @if (showRight()) {
          <button
            type="button"
            class="toggle"
            [attr.aria-label]="rightCollapsed() ? 'Show context panel' : 'Hide context panel'"
            [attr.aria-expanded]="!rightCollapsed()"
            (click)="setRightCollapsed(!rightCollapsed())"
          >
            <sd-icon [name]="rightCollapsed() ? 'panel-right-open' : 'panel-right-close'" [size]="15" />
          </button>
        }
      </div>
      <ng-content select="[sdSplitMain]" />
    </div>
    @if (showRight() && !rightCollapsed()) {
      <div
        class="handle"
        role="separator"
        tabindex="0"
        aria-orientation="vertical"
        aria-label="Resize context panel"
        [attr.aria-valuenow]="right()"
        [attr.aria-valuemin]="minRight()"
        [attr.aria-valuemax]="maxRight()"
        (pointerdown)="startDrag($event, 'right')"
        (keydown)="onHandleKey($event, 'right')"
        (dblclick)="reset('right')"
      ></div>
      <div class="panel panel--right" [style.width.px]="right()">
        <ng-content select="[sdSplitRight]" />
      </div>
    }
  `,
  host: { '[class.is-dragging]': 'dragging() !== null' },
  styles: `
    :host {
      position: relative;
      display: flex;
      min-height: 0;
      height: 100%;
      overflow: hidden;
    }
    :host(.is-dragging) {
      cursor: col-resize;
      user-select: none;
    }
    .panel {
      display: flex;
      flex-direction: column;
      min-width: 0;
      min-height: 0;
      overflow: hidden;
      background: var(--sd-surface);
    }
    .panel--main {
      position: relative;
      flex: 1;
      background: var(--sd-bg);
    }
    .panel--left,
    .panel--right {
      flex: none;
    }
    .toggles {
      position: absolute;
      top: 6px;
      left: 6px;
      right: 6px;
      display: flex;
      pointer-events: none;
      z-index: 2;
    }
    .toggle {
      pointer-events: auto;
      display: inline-flex;
      padding: 5px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-sm);
      background: var(--sd-surface);
      color: var(--sd-text-3);
      cursor: pointer;
      box-shadow: var(--sd-shadow-xs);
    }
    .toggle:hover {
      color: var(--sd-text);
    }
    .handle {
      position: relative;
      flex: none;
      width: 1px;
      background: var(--sd-border);
      cursor: col-resize;
      touch-action: none;
      z-index: 3;
    }
    .handle::after {
      content: '';
      position: absolute;
      inset: 0 -4px;
    }
    .handle:hover,
    .handle:focus-visible,
    :host(.is-dragging) .handle {
      background: var(--sd-primary);
      outline: none;
    }
    @media (max-width: 1100px) {
      .panel--right {
        position: absolute;
        top: 0;
        right: 0;
        bottom: 0;
        z-index: 4;
        max-width: calc(100% - 48px);
        box-shadow: var(--sd-shadow-overlay);
      }
      .panel--right + .handle,
      .handle:has(+ .panel--right) {
        display: none;
      }
    }
    @media (max-width: 768px) {
      .panel--left {
        position: absolute;
        top: 0;
        left: 0;
        bottom: 0;
        z-index: 4;
        max-width: calc(100% - 48px);
        box-shadow: var(--sd-shadow-overlay);
      }
      .panel--left + .handle {
        display: none;
      }
    }
  `,
})
export class SdSplitLayout {
  readonly storageKey = input<string | null>(null);
  readonly showLeft = input(true, { transform: booleanAttribute });
  readonly showRight = input(true, { transform: booleanAttribute });
  readonly defaultLeft = input(320);
  readonly defaultRight = input(360);
  readonly minLeft = input(220);
  readonly maxLeft = input(560);
  readonly minRight = input(260);
  readonly maxRight = input(640);
  readonly rightCollapsed = model(false);
  readonly leftCollapsed = model(false);

  protected readonly left = signal(320);
  protected readonly right = signal(360);
  protected readonly dragging = signal<'left' | 'right' | null>(null);
  private readonly prefsKey = computed(() => (this.storageKey() ? STORAGE_KEYS.splitPrefix + this.storageKey() : null));

  constructor() {
    effect(() => {
      const key = this.prefsKey();
      untracked(() => {
        const prefs = key ? safeStorage.getJson<SplitPrefs>(key) : null;
        this.left.set(this.clamp(prefs?.left ?? this.defaultLeft(), 'left'));
        this.right.set(this.clamp(prefs?.right ?? this.defaultRight(), 'right'));
        const narrow = typeof matchMedia !== 'undefined' && matchMedia('(max-width: 1100px)').matches;
        if (prefs) {
          this.rightCollapsed.set(prefs.rightCollapsed || narrow);
          this.leftCollapsed.set(prefs.leftCollapsed);
        } else if (narrow) {
          this.rightCollapsed.set(true);
        }
      });
    });
  }

  setRightCollapsed(collapsed: boolean): void {
    this.rightCollapsed.set(collapsed);
    this.save();
  }

  setLeftCollapsed(collapsed: boolean): void {
    this.leftCollapsed.set(collapsed);
    this.save();
  }

  protected startDrag(event: PointerEvent, side: 'left' | 'right'): void {
    event.preventDefault();
    const handle = event.currentTarget as HTMLElement;
    handle.setPointerCapture(event.pointerId);
    this.dragging.set(side);
    const startX = event.clientX;
    const startWidth = side === 'left' ? this.left() : this.right();
    const move = (e: PointerEvent) => {
      const dx = e.clientX - startX;
      const width = this.clamp(side === 'left' ? startWidth + dx : startWidth - dx, side);
      (side === 'left' ? this.left : this.right).set(width);
    };
    const up = () => {
      handle.removeEventListener('pointermove', move);
      handle.removeEventListener('pointerup', up);
      handle.removeEventListener('pointercancel', up);
      this.dragging.set(null);
      this.save();
    };
    handle.addEventListener('pointermove', move);
    handle.addEventListener('pointerup', up);
    handle.addEventListener('pointercancel', up);
  }

  protected onHandleKey(event: KeyboardEvent, side: 'left' | 'right'): void {
    const step = event.shiftKey ? 64 : 16;
    const target = side === 'left' ? this.left : this.right;
    const grow = side === 'left' ? 'ArrowRight' : 'ArrowLeft';
    const shrink = side === 'left' ? 'ArrowLeft' : 'ArrowRight';
    if (event.key === grow) target.set(this.clamp(target() + step, side));
    else if (event.key === shrink) target.set(this.clamp(target() - step, side));
    else if (event.key === 'Home') {
      this.reset(side);
      return;
    } else return;
    event.preventDefault();
    this.save();
  }

  protected reset(side: 'left' | 'right'): void {
    if (side === 'left') this.left.set(this.defaultLeft());
    else this.right.set(this.defaultRight());
    this.save();
  }

  private clamp(width: number, side: 'left' | 'right'): number {
    const [min, max] = side === 'left' ? [this.minLeft(), this.maxLeft()] : [this.minRight(), this.maxRight()];
    return Math.round(Math.min(max, Math.max(min, width)));
  }

  private save(): void {
    const key = this.prefsKey();
    if (!key) return;
    safeStorage.setJson(key, {
      left: this.left(),
      right: this.right(),
      rightCollapsed: this.rightCollapsed(),
      leftCollapsed: this.leftCollapsed(),
    } satisfies SplitPrefs);
  }
}
