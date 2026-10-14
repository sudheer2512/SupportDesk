import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ShortcutRegistry } from '@sd/core';
import { SdCommandPalette, SdShortcutHelp, SdToastOutlet } from '@sd/ui';

/** Root: router outlet plus app-wide overlays (toasts, command palette, shortcut help). */
@Component({
  selector: 'desk-root',
  imports: [RouterOutlet, SdToastOutlet, SdCommandPalette, SdShortcutHelp],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <router-outlet />
    <sd-command-palette />
    <sd-shortcut-help />
    <sd-toast-outlet />
  `,
  styles: `
    :host {
      display: block;
      height: 100%;
    }
  `,
})
export class App {
  constructor() {
    const shortcuts = inject(ShortcutRegistry);
    shortcuts.register({
      id: 'help.shortcuts',
      keys: '?',
      description: 'Show keyboard shortcuts',
      group: 'General',
      handler: () => shortcuts.helpOpen.update((open) => !open),
    });
  }
}
