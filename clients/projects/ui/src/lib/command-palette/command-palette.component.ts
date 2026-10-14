import { CdkTrapFocus } from '@angular/cdk/a11y';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { ShortcutRegistry, fuzzyFilter, uniqueId } from '@sd/core';
import { Subscription, from, isObservable } from 'rxjs';
import { SdIcon } from '../icon/icon.component';
import { SdSpinner } from '../spinner/spinner.component';
import { CommandPaletteService, SdCommand } from './command-palette.service';

interface Section {
  group: string;
  items: { cmd: SdCommand; index: number }[];
}

/**
 * `<sd-command-palette />` — place once in the app shell. Ctrl/⌘+K opens it; fuzzy search over registered
 * commands plus debounced async providers; ↑/↓ + Enter run, Esc closes (focus returns where it was).
 */
@Component({
  selector: 'sd-command-palette',
  imports: [CdkTrapFocus, SdIcon, SdSpinner],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (palette.isOpen()) {
      <div class="backdrop" (click)="palette.close()" aria-hidden="true"></div>
      <div
        class="panel"
        role="dialog"
        aria-modal="true"
        aria-label="Command palette"
        cdkTrapFocus
        [cdkTrapFocusAutoCapture]="true"
        (keydown)="onKeydown($event)"
      >
        <div class="search">
          <sd-icon name="search" [size]="18" />
          <input
            #input
            class="input"
            role="combobox"
            aria-autocomplete="list"
            aria-expanded="true"
            [attr.aria-controls]="listId"
            [attr.aria-activedescendant]="flat().length ? listId + '-' + active() : null"
            placeholder="Search commands, tickets, customers…"
            autocomplete="off"
            spellcheck="false"
            [value]="query()"
            (input)="setQuery($any($event.target).value)"
          />
          @if (loading()) {
            <sd-spinner [size]="16" />
          }
          <kbd>Esc</kbd>
        </div>
        <div class="results" role="listbox" [id]="listId" aria-label="Results">
          @for (section of sections(); track section.group) {
            <div class="group" role="presentation">{{ section.group }}</div>
            @for (item of section.items; track item.cmd.id) {
              <div
                class="item"
                role="option"
                [id]="listId + '-' + item.index"
                [attr.aria-selected]="item.index === active()"
                [class.is-active]="item.index === active()"
                (mousemove)="active.set(item.index)"
                (click)="run(item.cmd)"
              >
                <span class="icon" aria-hidden="true"><sd-icon [name]="item.cmd.icon || 'corner-down-left'" [size]="15" /></span>
                <span class="text">
                  <span class="title sd-truncate">{{ item.cmd.title }}</span>
                  @if (item.cmd.subtitle) {
                    <span class="subtitle sd-truncate">{{ item.cmd.subtitle }}</span>
                  }
                </span>
                @if (item.cmd.shortcut) {
                  <span class="keys">
                    @for (step of shortcuts.display(item.cmd.shortcut); track $index) {
                      @for (k of step; track $index) {
                        <kbd>{{ k }}</kbd>
                      }
                    }
                  </span>
                }
              </div>
            }
          } @empty {
            <p class="empty">{{ loading() ? 'Searching…' : 'No results for “' + query() + '”' }}</p>
          }
        </div>
        <div class="foot" aria-hidden="true">
          <span><kbd>↑</kbd><kbd>↓</kbd> navigate</span>
          <span><kbd>↵</kbd> open</span>
          <span><kbd>?</kbd> all shortcuts</span>
        </div>
      </div>
    }
  `,
  styles: `
    .backdrop {
      position: fixed;
      inset: 0;
      z-index: var(--sd-z-overlay);
      background: var(--sd-overlay-backdrop);
      animation: sd-fade var(--sd-dur) var(--sd-ease);
    }
    @keyframes sd-fade {
      from {
        opacity: 0;
      }
    }
    .panel {
      position: fixed;
      z-index: calc(var(--sd-z-overlay) + 1);
      top: min(14vh, 120px);
      left: 50%;
      width: min(640px, calc(100vw - 24px));
      max-height: min(70vh, 560px);
      transform: translateX(-50%);
      display: flex;
      flex-direction: column;
      background: var(--sd-surface);
      color: var(--sd-text);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-xl);
      box-shadow: var(--sd-shadow-overlay);
      overflow: hidden;
      animation: sd-palette-in var(--sd-dur-slow) var(--sd-ease);
    }
    @keyframes sd-palette-in {
      from {
        opacity: 0;
        transform: translate(-50%, -6px) scale(0.99);
      }
    }
    .search {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 12px 16px;
      border-bottom: 1px solid var(--sd-divider);
      color: var(--sd-text-3);
    }
    .input {
      flex: 1;
      height: 30px;
      border: 0;
      outline: none;
      background: transparent;
      color: var(--sd-text);
      font-size: var(--sd-text-lg);
    }
    .input::placeholder {
      color: var(--sd-text-3);
    }
    .results {
      flex: 1;
      overflow: auto;
      padding: 6px;
    }
    .group {
      padding: 10px 10px 4px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      text-transform: uppercase;
      letter-spacing: 0.05em;
    }
    .item {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 8px 10px;
      border-radius: var(--sd-radius-md);
      cursor: pointer;
    }
    .item.is-active {
      background: var(--sd-primary-soft);
    }
    .item.is-active .icon {
      color: var(--sd-primary);
    }
    .icon {
      display: inline-flex;
      color: var(--sd-text-3);
    }
    .text {
      display: flex;
      flex-direction: column;
      flex: 1;
      min-width: 0;
    }
    .title {
      font-weight: var(--sd-weight-medium);
    }
    .subtitle {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .keys {
      display: inline-flex;
      gap: 3px;
    }
    .empty {
      padding: 24px;
      text-align: center;
      color: var(--sd-text-3);
    }
    .foot {
      display: flex;
      gap: 16px;
      padding: 8px 16px;
      border-top: 1px solid var(--sd-divider);
      background: var(--sd-surface-2);
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .foot span {
      display: inline-flex;
      align-items: center;
      gap: 3px;
    }
    @media (max-width: 640px) {
      .panel {
        top: 8px;
        max-height: calc(100dvh - 16px);
      }
      .foot {
        display: none;
      }
    }
  `,
})
export class SdCommandPalette {
  protected readonly palette = inject(CommandPaletteService);
  protected readonly shortcuts = inject(ShortcutRegistry);
  protected readonly listId = uniqueId('sd-palette');
  protected readonly query = signal('');
  protected readonly active = signal(0);
  protected readonly loading = signal(false);
  private readonly asyncResults = signal<ReadonlyMap<string, SdCommand[]>>(new Map());
  private readonly input = viewChild<ElementRef<HTMLInputElement>>('input');
  private searchSubs: Subscription[] = [];
  private searchTimer: ReturnType<typeof setTimeout> | null = null;
  private generation = 0;

  protected readonly sections = computed<Section[]>(() => {
    const q = this.query().trim();
    const sections: Section[] = [];
    let index = 0;
    const push = (group: string, cmds: SdCommand[]) => {
      if (!cmds.length) return;
      sections.push({ group, items: cmds.map((cmd) => ({ cmd, index: index++ })) });
    };
    if (!q) {
      push('Recent', this.palette.recent());
      const recentIds = new Set(this.palette.recent().map((c) => c.id));
      const groups = new Map<string, SdCommand[]>();
      for (const c of this.palette.commands()) {
        if (recentIds.has(c.id)) continue;
        const g = c.group ?? 'Commands';
        groups.set(g, [...(groups.get(g) ?? []), c]);
      }
      for (const [g, cmds] of groups) push(g, cmds);
      return sections;
    }
    for (const [group, cmds] of this.asyncResults()) push(group, cmds);
    push(
      'Commands',
      fuzzyFilter(this.palette.commands(), q, (c) => [c.title, c.subtitle ?? '', ...(c.keywords ?? []), c.group ?? ''], 30),
    );
    return sections;
  });
  protected readonly flat = computed(() => this.sections().flatMap((s) => s.items.map((i) => i.cmd)));

  constructor() {
    let previousFocus: HTMLElement | null = null;
    effect(() => {
      const open = this.palette.isOpen();
      untracked(() => {
        if (open) {
          previousFocus = document.activeElement as HTMLElement | null;
          this.setQuery(this.palette.initialQuery());
          queueMicrotask(() => this.input()?.nativeElement.focus());
        } else {
          this.cancelSearches();
          this.asyncResults.set(new Map());
          previousFocus?.focus?.();
          previousFocus = null;
        }
      });
    });
    inject(DestroyRef).onDestroy(() => this.cancelSearches());
  }

  protected setQuery(text: string): void {
    this.query.set(text);
    this.active.set(0);
    this.runProviders(text.trim());
  }

  protected run(cmd: SdCommand): void {
    void this.palette.execute(cmd);
  }

  protected onKeydown(event: KeyboardEvent): void {
    const count = this.flat().length;
    switch (event.key) {
      case 'Escape':
        event.preventDefault();
        event.stopPropagation();
        this.palette.close();
        break;
      case 'ArrowDown':
        event.preventDefault();
        if (count) this.active.set((this.active() + 1) % count);
        this.scrollActive();
        break;
      case 'ArrowUp':
        event.preventDefault();
        if (count) this.active.set((this.active() - 1 + count) % count);
        this.scrollActive();
        break;
      case 'Enter': {
        event.preventDefault();
        const cmd = this.flat()[this.active()];
        if (cmd) this.run(cmd);
        break;
      }
    }
  }

  private scrollActive(): void {
    queueMicrotask(() => document.getElementById(`${this.listId}-${this.active()}`)?.scrollIntoView({ block: 'nearest' }));
  }

  private runProviders(q: string): void {
    this.cancelSearches();
    const generation = ++this.generation;
    const providers = this.palette.providers().filter((p) => q.length >= (p.minQueryLength ?? 2));
    if (!providers.length) {
      this.asyncResults.set(new Map());
      this.loading.set(false);
      return;
    }
    this.loading.set(true);
    this.searchTimer = setTimeout(() => {
      let pending = providers.length;
      const results = new Map<string, SdCommand[]>();
      for (const provider of providers) {
        const r = provider.search(q);
        const obs = isObservable(r) ? r : from(r);
        this.searchSubs.push(
          obs.subscribe({
            next: (cmds) => {
              if (generation !== this.generation) return;
              results.set(provider.group, cmds);
              this.asyncResults.set(new Map(results));
            },
            error: () => {
              if (generation === this.generation && --pending === 0) this.loading.set(false);
            },
            complete: () => {
              if (generation === this.generation && --pending === 0) this.loading.set(false);
            },
          }),
        );
      }
    }, 180);
  }

  private cancelSearches(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = null;
    for (const s of this.searchSubs) s.unsubscribe();
    this.searchSubs = [];
  }
}
