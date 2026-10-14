import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ModulePlaceholderComponent } from '../../shared/module-placeholder.component';

/** Subscription landing page — an honest placeholder until the module's screens are built. */
@Component({
  selector: 'desk-subscription-home',
  imports: [ModulePlaceholderComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<desk-module-placeholder title="Subscription" icon="credit-card" [subtitle]="subtitle" [planned]="planned" />`,
})
export class SubscriptionHomePage {
  protected readonly subtitle = 'Plan, usage, limits and invoices.';
  protected readonly planned = [
    'Current plan and included features',
    'Usage against plan limits',
    'Invoices and billing contacts',
  ];
}
