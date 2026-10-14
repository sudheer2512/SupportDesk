import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { SdToastOutlet } from '@sd/ui';

@Component({
  selector: 'portal-root',
  imports: [RouterOutlet, SdToastOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<router-outlet /><sd-toast-outlet />`,
})
export class App {}
