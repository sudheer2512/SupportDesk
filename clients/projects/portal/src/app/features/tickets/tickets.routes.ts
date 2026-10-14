import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Routes } from '@angular/router';
import { PortalAuthService } from '@sd/core';
import { PortalPlaceholderComponent } from '../../shared/portal-placeholder.component';

/** My tickets — placeholder until the contact ticket list/detail screens are built. */
@Component({
  selector: 'portal-tickets-home',
  imports: [PortalPlaceholderComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <portal-placeholder
      title="My Tickets"
      icon="ticket"
      [subtitle]="auth.contact()?.customerName ? 'Requests from ' + auth.contact()!.customerName : 'Your requests and their status.'"
      [planned]="planned"
    />
  `,
})
export class TicketsHomePage {
  protected readonly auth = inject(PortalAuthService);
  protected readonly planned = [
    'Your open and closed tickets with status',
    "Your organisation's tickets (when your access allows it)",
    'Reply, attach files and confirm resolutions',
    'Rate the support you received',
  ];
}

export const ROUTES: Routes = [{ path: '', title: 'My Tickets', component: TicketsHomePage }];
