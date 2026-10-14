import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CommandPaletteService, SdButton, SdEmptyState } from '@sd/ui';

/** 404 inside the authenticated shell. */
@Component({
  selector: 'desk-not-found-page',
  imports: [RouterLink, SdButton, SdEmptyState],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-empty-state icon="search" title="Page not found" message="The page you opened does not exist or has moved.">
      <a sdButton variant="primary" routerLink="/">Go to my workspace</a>
      <button sdButton type="button" icon="command" (click)="palette.open()">Search</button>
    </sd-empty-state>
  `,
  styles: `
    :host {
      display: block;
      padding-top: 6vh;
    }
  `,
})
export class NotFoundPage {
  protected readonly palette = inject(CommandPaletteService);
}
