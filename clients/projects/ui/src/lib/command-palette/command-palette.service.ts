import { Injectable, computed, inject, signal } from '@angular/core';
import { ShortcutRegistry, safeStorage } from '@sd/core';
import { Observable } from 'rxjs';

export interface SdCommand {
  id: string;
  title: string;
  subtitle?: string | null;
  /** Section heading in the palette ("Navigation", "Tickets", …). */
  group?: string;
  icon?: string | null;
  /** Extra search terms. */
  keywords?: string[];
  /** Display-only shortcut hint, e.g. "g t". */
  shortcut?: string | null;
  /** Hide the command when this returns false (permissions, context). */
  when?: () => boolean;
  run: () => void | Promise<unknown>;
}

/** Async results (tickets by number, customers, articles…) merged into the palette while typing. */
export interface SdCommandProvider {
  id: string;
  group: string;
  /** Only query when the input has at least this many characters (default 2). */
  minQueryLength?: number;
  search: (query: string) => Observable<SdCommand[]> | Promise<SdCommand[]>;
}

const RECENT_KEY = 'sd.palette.recent';

/**
 * Registry + state for the command palette (`<sd-command-palette />`, opened with Ctrl/⌘+K).
 * Features register static commands (`register`) and async search providers (`registerProvider`);
 * both return an unregister function.
 */
@Injectable({ providedIn: 'root' })
export class CommandPaletteService {
  private readonly _commands = signal<SdCommand[]>([]);
  private readonly _providers = signal<SdCommandProvider[]>([]);
  private readonly _recent = signal<string[]>(safeStorage.getJson<string[]>(RECENT_KEY) ?? []);

  readonly isOpen = signal(false);
  readonly initialQuery = signal('');
  readonly providers = this._providers.asReadonly();
  readonly commands = computed(() => this._commands().filter((c) => !c.when || c.when()));
  readonly recent = computed(() => {
    const byId = new Map(this.commands().map((c) => [c.id, c]));
    return this._recent()
      .map((id) => byId.get(id))
      .filter((c): c is SdCommand => !!c)
      .slice(0, 5);
  });

  constructor() {
    inject(ShortcutRegistry).register({
      id: 'palette.toggle',
      keys: 'mod+k',
      description: 'Open the command palette',
      group: 'General',
      allowInInputs: true,
      handler: () => this.toggle(),
    });
  }

  register(commands: SdCommand | SdCommand[]): () => void {
    const list = Array.isArray(commands) ? commands : [commands];
    const ids = new Set(list.map((c) => c.id));
    this._commands.update((current) => [...current.filter((c) => !ids.has(c.id)), ...list]);
    return () => this._commands.update((current) => current.filter((c) => !list.includes(c)));
  }

  registerProvider(provider: SdCommandProvider): () => void {
    this._providers.update((current) => [...current.filter((p) => p.id !== provider.id), provider]);
    return () => this._providers.update((current) => current.filter((p) => p !== provider));
  }

  open(query = ''): void {
    this.initialQuery.set(query);
    this.isOpen.set(true);
  }

  close(): void {
    this.isOpen.set(false);
  }

  toggle(): void {
    if (this.isOpen()) this.close();
    else this.open();
  }

  async execute(command: SdCommand): Promise<void> {
    this.close();
    this._recent.update((ids) => [command.id, ...ids.filter((id) => id !== command.id)].slice(0, 12));
    safeStorage.setJson(RECENT_KEY, this._recent());
    await command.run();
  }
}
