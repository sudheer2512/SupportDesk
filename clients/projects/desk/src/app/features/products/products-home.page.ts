import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ModulePlaceholderComponent } from '../../shared/module-placeholder.component';

/** Products & Assets landing page — an honest placeholder until the module's screens are built. */
@Component({
  selector: 'desk-products-home',
  imports: [ModulePlaceholderComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<desk-module-placeholder title="Products &amp; Assets" icon="package" [subtitle]="subtitle" [planned]="planned" />`,
})
export class ProductsHomePage {
  protected readonly subtitle = 'Products, modules, versions, installations, assets and contracts.';
  protected readonly planned = [
    'Products, modules and versions',
    'Installations and assets per customer',
    'Contracts, prepaid hours and consumption',
    'Entitlement checks used by ticket routing',
  ];
}
