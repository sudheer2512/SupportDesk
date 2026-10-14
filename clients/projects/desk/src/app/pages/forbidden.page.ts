import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { SdButton, SdEmptyState } from '@sd/ui';

/** Friendly 403: where permissionGuard sends users who open a module their role does not include. */
@Component({
  selector: 'desk-forbidden-page',
  imports: [RouterLink, SdButton, SdEmptyState],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-empty-state
      icon="lock"
      title="You don't have access to this area"
      [message]="
        'Your role does not include the permission needed' +
        (from() ? ' for ' + from() : '') +
        '. If you need it for your work, ask a SupportDesk administrator to update your role.'
      "
    >
      <a sdButton variant="primary" routerLink="/">Go to my workspace</a>
      <a sdButton routerLink="/me">View my profile</a>
    </sd-empty-state>
  `,
  styles: `
    :host {
      display: block;
      padding-top: 6vh;
    }
  `,
})
export class ForbiddenPage {
  readonly from = input<string | undefined>();
}
