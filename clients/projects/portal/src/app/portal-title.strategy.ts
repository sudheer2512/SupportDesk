import { Injectable, inject } from '@angular/core';
import { RouterStateSnapshot, TitleStrategy } from '@angular/router';
import { BrandService } from './brand.service';

/** Document titles: "<page> · <brand portal title>". */
@Injectable()
export class PortalTitleStrategy extends TitleStrategy {
  private readonly brand = inject(BrandService);

  override updateTitle(snapshot: RouterStateSnapshot): void {
    this.brand.setPageTitle(this.buildTitle(snapshot) ?? null);
  }
}
