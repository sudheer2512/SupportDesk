import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  TemplateRef,
  booleanAttribute,
  computed,
  contentChildren,
  input,
  model,
  viewChild,
  viewChildren,
} from '@angular/core';
import { uniqueId } from '@sd/core';
import { SdIcon } from '../icon/icon.component';

/** One tab inside `<sd-tabs>`; its content is rendered only while selected. */
@Component({
  selector: 'sd-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<ng-template><ng-content /></ng-template>`,
})
export class SdTab {
  readonly id = input.required<string>();
  readonly label = input.required<string>();
  readonly icon = input<string | null>(null);
  readonly badge = input<string | number | null>(null);
  readonly disabled = input(false, { transform: booleanAttribute });
  readonly content = viewChild.required(TemplateRef);
}

/**
 * `<sd-tabs [(selected)]="tab"><sd-tab id="details" label="Details">…</sd-tab>…</sd-tabs>`
 * WAI-ARIA tabs: roving tabindex, ←/→/Home/End move and activate, panels labelled by their tab.
 */
@Component({
  selector: 'sd-tabs',
  imports: [NgTemplateOutlet, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="list" role="tablist" [attr.aria-label]="ariaLabel()" [class.list--pill]="variant() === 'pill'">
      @for (tab of tabs(); track tab.id(); let i = $index) {
        <button
          #tabButton
          type="button"
          role="tab"
          class="tab"
          [id]="baseId + '-tab-' + tab.id()"
          [attr.aria-selected]="active() === tab"
          [attr.aria-controls]="baseId + '-panel-' + tab.id()"
          [attr.tabindex]="active() === tab ? 0 : -1"
          [disabled]="tab.disabled()"
          (click)="select(tab)"
          (keydown)="onKeydown($event, i)"
        >
          @if (tab.icon()) {
            <sd-icon [name]="tab.icon()!" [size]="15" />
          }
          <span>{{ tab.label() }}</span>
          @if (tab.badge() !== null && tab.badge() !== undefined) {
            <span class="count">{{ tab.badge() }}</span>
          }
        </button>
      }
    </div>
    @if (active(); as tab) {
      <div
        class="panel"
        role="tabpanel"
        tabindex="0"
        [id]="baseId + '-panel-' + tab.id()"
        [attr.aria-labelledby]="baseId + '-tab-' + tab.id()"
      >
        <ng-container [ngTemplateOutlet]="tab.content()" />
      </div>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      min-height: 0;
    }
    .list {
      display: flex;
      gap: 2px;
      border-bottom: 1px solid var(--sd-border);
      overflow-x: auto;
      scrollbar-width: none;
    }
    .tab {
      position: relative;
      display: inline-flex;
      align-items: center;
      gap: 6px;
      height: 38px;
      padding: 0 12px;
      border: 0;
      background: transparent;
      color: var(--sd-text-2);
      font-weight: var(--sd-weight-medium);
      white-space: nowrap;
      cursor: pointer;
      border-radius: var(--sd-radius-sm) var(--sd-radius-sm) 0 0;
      transition: color var(--sd-dur-fast) var(--sd-ease), background-color var(--sd-dur-fast) var(--sd-ease);
    }
    .tab:hover:not(:disabled) {
      color: var(--sd-text);
      background: var(--sd-surface-3);
    }
    .tab[aria-selected='true'] {
      color: var(--sd-primary);
    }
    .tab[aria-selected='true']::after {
      content: '';
      position: absolute;
      left: 8px;
      right: 8px;
      bottom: -1px;
      height: 2px;
      border-radius: 2px;
      background: var(--sd-primary);
    }
    .tab:disabled {
      color: var(--sd-text-disabled);
      cursor: not-allowed;
    }
    .tab:focus-visible {
      outline: none;
      box-shadow: inset 0 0 0 2px var(--sd-focus);
    }
    .list--pill {
      border-bottom: 0;
      gap: 4px;
      padding: 3px;
      background: var(--sd-surface-3);
      border-radius: var(--sd-radius-md);
      width: fit-content;
      max-width: 100%;
    }
    .list--pill .tab {
      height: 30px;
      border-radius: var(--sd-radius-sm);
    }
    .list--pill .tab[aria-selected='true'] {
      background: var(--sd-surface);
      color: var(--sd-text);
      box-shadow: var(--sd-shadow-sm);
    }
    .list--pill .tab[aria-selected='true']::after {
      display: none;
    }
    .count {
      min-width: 18px;
      height: 18px;
      padding: 0 5px;
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface-3);
      color: var(--sd-text-2);
      font-size: 11px;
      line-height: 18px;
      text-align: center;
    }
    .panel {
      flex: 1;
      min-height: 0;
      padding-top: var(--sd-space-4);
    }
    .panel:focus-visible {
      outline: none;
    }
  `,
})
export class SdTabs {
  readonly selected = model<string | null>(null);
  readonly variant = input<'line' | 'pill'>('line');
  readonly ariaLabel = input<string | null>(null);

  protected readonly tabs = contentChildren(SdTab);
  private readonly buttons = viewChildren<ElementRef<HTMLButtonElement>>('tabButton');
  protected readonly baseId = uniqueId('sd-tabs');
  protected readonly active = computed(() => {
    const tabs = this.tabs();
    const id = this.selected();
    return tabs.find((t) => t.id() === id && !t.disabled()) ?? tabs.find((t) => !t.disabled()) ?? null;
  });

  protected select(tab: SdTab): void {
    if (!tab.disabled()) this.selected.set(tab.id());
  }

  protected onKeydown(event: KeyboardEvent, index: number): void {
    const tabs = this.tabs();
    const enabled = tabs.map((t, i) => ({ t, i })).filter((x) => !x.t.disabled());
    if (enabled.length === 0) return;
    const pos = enabled.findIndex((x) => x.i === index);
    let next: number | null = null;
    switch (event.key) {
      case 'ArrowRight':
        next = enabled[(pos + 1) % enabled.length].i;
        break;
      case 'ArrowLeft':
        next = enabled[(pos - 1 + enabled.length) % enabled.length].i;
        break;
      case 'Home':
        next = enabled[0].i;
        break;
      case 'End':
        next = enabled[enabled.length - 1].i;
        break;
    }
    if (next === null) return;
    event.preventDefault();
    this.select(tabs[next]);
    this.buttons()[next]?.nativeElement.focus();
  }
}
