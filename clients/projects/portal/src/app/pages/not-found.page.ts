import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { SdButton, SdEmptyState } from '@sd/ui';
import { BrandService } from '../brand.service';

@Component({
  selector: 'portal-not-found-page',
  imports: [RouterLink, SdButton, SdEmptyState],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-empty-state icon="search" title="Page not found" message="The page you are looking for does not exist.">
      <a sdButton variant="primary" [routerLink]="home()">Back to the portal</a>
    </sd-empty-state>
  `,
})
export class PortalNotFoundPage {
  private readonly brand = inject(BrandService);
  protected readonly home = computed(() => `/${this.brand.slug() ?? ''}`);
}
