import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ModulePlaceholderComponent } from '../../shared/module-placeholder.component';

/** Customers landing page — an honest placeholder until the module's screens are built. */
@Component({
  selector: 'desk-customers-home',
  imports: [ModulePlaceholderComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<desk-module-placeholder title="Customers" icon="building" [subtitle]="subtitle" [planned]="planned" />`,
})
export class CustomersHomePage {
  protected readonly subtitle = 'Accounts, contacts, locations and their support history.';
  protected readonly planned = [
    'Accounts with tiers, locations and account managers',
    'Contacts, VIP flags and duplicate merge',
    'Support history, CSAT and SLA breaches per account',
    'Contracts and entitlements at a glance',
  ];
}
