import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';

/**
 * A keyboard shortcut. `keys` syntax:
 *  - combos joined by "+": "mod+k" (mod = ⌘ on macOS, Ctrl elsewhere), "shift+n", "alt+arrowup", "?"
 *  - two-step sequences separated by a space: "g t" (press g, then t within 1.2 s)
 */
export interface ShortcutDef {
  id: string;
  keys: string;
  description: string;
  /** Heading in the shortcut help overlay. */
  group?: string;
  /** Fire even while typing in an input/textarea/contenteditable (combos with mod/ctrl/meta always do). */
  allowInInputs?: boolean;
  /** Extra guard evaluated at key time. */
  when?: () => boolean;
  handler: (event: KeyboardEvent) => void;
}

const SEQUENCE_TIMEOUT_MS = 1200;

/** Global keyboard shortcut registry with a single document listener; feeds the help overlay. */
@Injectable({ providedIn: 'root' })
export class ShortcutRegistry {
  private readonly document = inject(DOCUMENT);
  private readonly defs = signal<ShortcutDef[]>([]);
  private pendingStep: string | null = null;
  private pendingAt = 0;

  readonly isMac = /Mac|iPhone|iPad/i.test(globalThis.navigator?.platform ?? globalThis.navigator?.userAgent ?? '');
  /** All registered shortcuts grouped for display. */
  readonly groups = computed(() => {
    const map = new Map<string, ShortcutDef[]>();
    for (const def of this.defs()) {
      const g = def.group ?? 'General';
      map.set(g, [...(map.get(g) ?? []), def]);
    }
    return [...map.entries()].map(([group, items]) => ({ group, items }));
  });
  /** Whether the shortcut help overlay is open (toggled by "?" in the apps). */
  readonly helpOpen = signal(false);

  constructor() {
    const listener = (e: KeyboardEvent) => this.onKeydown(e);
    this.document.addEventListener('keydown', listener);
    inject(DestroyRef).onDestroy(() => this.document.removeEventListener('keydown', listener));
  }

  /** Registers one or more shortcuts; returns a function that unregisters them. */
  register(defs: ShortcutDef | ShortcutDef[]): () => void {
    const list = Array.isArray(defs) ? defs : [defs];
    const ids = new Set(list.map((d) => d.id));
    this.defs.update((current) => [...current.filter((d) => !ids.has(d.id)), ...list]);
    return () => this.defs.update((current) => current.filter((d) => !list.includes(d)));
  }

  /** Display tokens for a key spec: "mod+k" → [["Ctrl","K"]]; "g t" → [["G"],["T"]]. */
  display(keys: string): string[][] {
    return keys.split(' ').map((step) => step.split('+').map((k) => this.label(k)));
  }

  private label(key: string): string {
    switch (key.toLowerCase()) {
      case 'mod':
        return this.isMac ? '⌘' : 'Ctrl';
      case 'ctrl':
        return 'Ctrl';
      case 'meta':
        return this.isMac ? '⌘' : 'Win';
      case 'alt':
        return this.isMac ? '⌥' : 'Alt';
      case 'shift':
        return '⇧';
      case 'escape':
        return 'Esc';
      case 'enter':
        return '↵';
      case 'arrowup':
        return '↑';
      case 'arrowdown':
        return '↓';
      case 'arrowleft':
        return '←';
      case 'arrowright':
        return '→';
      case 'space':
        return 'Space';
      default:
        return key.length === 1 ? key.toUpperCase() : key[0].toUpperCase() + key.slice(1);
    }
  }

  private normaliseSpec(step: string): string {
    const parts = step
      .toLowerCase()
      .split('+')
      .map((p) => (p === 'mod' ? (this.isMac ? 'meta' : 'ctrl') : p));
    const key = parts.pop() ?? '';
    const mods = ['ctrl', 'alt', 'shift', 'meta'].filter((m) => parts.includes(m));
    return [...mods, key].join('+');
  }

  private eventCombo(e: KeyboardEvent): string {
    let key = e.key.toLowerCase();
    if (key === ' ') key = 'space';
    const symbol = key.length === 1 && !/[a-z0-9]/.test(key);
    const mods: string[] = [];
    if (e.ctrlKey) mods.push('ctrl');
    if (e.altKey) mods.push('alt');
    if (e.shiftKey && !symbol) mods.push('shift');
    if (e.metaKey) mods.push('meta');
    return [...mods, key].join('+');
  }

  private onKeydown(e: KeyboardEvent): void {
    if (e.defaultPrevented || e.isComposing || e.key === undefined) return;
    if (['Control', 'Shift', 'Alt', 'Meta'].includes(e.key)) return;
    const combo = this.eventCombo(e);
    const editable = isEditable(e.target);
    const now = Date.now();
    const pending = this.pendingStep && now - this.pendingAt < SEQUENCE_TIMEOUT_MS ? this.pendingStep : null;
    this.pendingStep = null;

    let startsSequence = false;
    for (const def of this.defs()) {
      const steps = def.keys.split(' ').map((s) => this.normaliseSpec(s));
      const hasStrongModifier = /(^|\+)(ctrl|meta)\+/.test(steps[steps.length - 1]);
      if (editable && !def.allowInInputs && !hasStrongModifier) continue;
      if (def.when && !def.when()) continue;
      if (steps.length === 1 && steps[0] === combo && !pending) {
        e.preventDefault();
        def.handler(e);
        return;
      }
      if (steps.length === 2) {
        if (pending && steps[0] === pending && steps[1] === combo) {
          e.preventDefault();
          def.handler(e);
          return;
        }
        if (steps[0] === combo) startsSequence = true;
      }
    }
    if (startsSequence) {
      this.pendingStep = combo;
      this.pendingAt = now;
    }
  }
}

function isEditable(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  if (target.isContentEditable) return true;
  const tag = target.tagName;
  if (tag === 'TEXTAREA' || tag === 'SELECT') return true;
  if (tag === 'INPUT') {
    const type = (target as HTMLInputElement).type;
    return !['checkbox', 'radio', 'button', 'submit', 'reset', 'range', 'color'].includes(type);
  }
  return false;
}

/** Registers shortcuts for the lifetime of the calling component/service (injection context required). */
export function injectShortcuts(defs: ShortcutDef[]): void {
  const registry = inject(ShortcutRegistry);
  const unregister = registry.register(defs);
  inject(DestroyRef).onDestroy(unregister);
}
