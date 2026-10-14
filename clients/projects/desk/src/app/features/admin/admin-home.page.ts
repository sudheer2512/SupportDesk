import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ModulePlaceholderComponent } from '../../shared/module-placeholder.component';

/** Administration landing page — an honest placeholder until the module's screens are built. */
@Component({
  selector: 'desk-admin-home',
  imports: [ModulePlaceholderComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<desk-module-placeholder title="Administration" icon="settings" [subtitle]="subtitle" [planned]="planned" />`,
})
export class AdminHomePage {
  protected readonly subtitle = 'Users, roles, channels, ticket configuration, security and audit.';
  protected readonly planned = [
    'Users, roles and permissions',
    'Organisation, brands and customer portals',
    'Ticket fields, statuses, layouts and numbering',
    'Channels: e-mail, chat, social and phone',
    'Security: MFA policy, IP rules and sessions',
    'Audit log, compliance and configuration transfer',
  ];
}
