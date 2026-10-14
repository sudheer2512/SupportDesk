import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { SdButton, SdIcon } from '@sd/ui';
import { BrandService } from '../brand.service';
import { SearchBoxComponent } from '../shared/search-box.component';

/** Friendly 404 inside a brand: search, home and contact options. */
@Component({
  selector: 'portal-not-found-page',
  imports: [RouterLink, SdButton, SdIcon, SearchBoxComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="portal-container wrap">
      <span class="art" aria-hidden="true"><sd-icon name="compass" [size]="34" /></span>
      <p class="code">404</p>
      <h1>We couldn't find that page</h1>
      <p class="lead">The link may be broken or the page may have moved. Try searching, or head back to the help center.</p>
      @if (brand.features().kb || brand.features().community) {
        <portal-search-box class="search" />
      }
      <div class="actions">
        <a sdButton variant="primary" icon="home" [routerLink]="brand.base()">Help center home</a>
        <a sdButton icon="file-plus" [routerLink]="brand.base() + '/submit'">Submit a ticket</a>
        <a sdButton variant="ghost" icon="life-buoy" [routerLink]="brand.base() + '/help'">Contact support</a>
      </div>
    </div>
  `,
  styles: `
    .wrap {
      display: grid;
      justify-items: center;
      padding: clamp(40px, 8vw, 80px) 0;
      text-align: center;
    }
    .art {
      display: inline-flex;
      padding: 16px;
      border-radius: 50%;
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    .code {
      margin: 16px 0 0;
      color: var(--sd-text-3);
      font-weight: var(--sd-weight-semibold);
      letter-spacing: 0.2em;
    }
    h1 {
      margin-top: 4px;
      font-size: clamp(1.5rem, 4vw, 2rem);
    }
    .lead {
      max-width: 520px;
      margin: 10px 0 0;
      color: var(--sd-text-2);
    }
    .search {
      width: min(520px, 100%);
      margin-top: 24px;
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      justify-content: center;
      gap: 10px;
      margin-top: 24px;
    }
  `,
})
export class PortalNotFoundPage {
  protected readonly brand = inject(BrandService);
}
