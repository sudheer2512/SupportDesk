import { ChangeDetectionStrategy, Component, booleanAttribute, computed, inject, input } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { PortalAuthService, PortalContentApi, PortalSupportInfoDto } from '@sd/core';
import { SdAvatar, SdIcon, SdSkeleton } from '@sd/ui';
import { BrandService } from '../brand.service';
import { ErrorStateComponent } from './error-state.component';

/**
 * Ways to reach support (GET /portal/{brand}/support): e-mail, phone, the signed-in customer's account
 * manager and department contacts. `<portal-support-contacts />`
 */
@Component({
  selector: 'portal-support-contacts',
  imports: [SdIcon, SdAvatar, SdSkeleton, ErrorStateComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (res.error()) {
      <portal-error-state [error]="res.error()" errorTitle="Contact details couldn't be loaded" compact (retry)="res.reload()" />
    } @else if (info(); as s) {
      <div class="grid" [class.is-stacked]="stacked()">
        @if (s.supportEmail || s.supportPhone) {
          <section class="card" aria-labelledby="sc-general">
            <h3 id="sc-general"><sd-icon name="headphones" [size]="17" />Support team</h3>
            <ul class="lines">
              @if (s.supportEmail) {
                <li><sd-icon name="mail" [size]="15" /><a [href]="'mailto:' + s.supportEmail">{{ s.supportEmail }}</a></li>
              }
              @if (s.supportPhone) {
                <li><sd-icon name="phone" [size]="15" /><a [href]="'tel:' + s.supportPhone">{{ s.supportPhone }}</a></li>
              }
            </ul>
          </section>
        }
        @if (s.accountManager; as m) {
          <section class="card" aria-labelledby="sc-am">
            <h3 id="sc-am"><sd-icon name="user-check" [size]="17" />Your account manager</h3>
            <div class="person">
              <sd-avatar [name]="m.name" size="md" />
              <div>
                <p class="name">{{ m.name }}</p>
                @if (m.title) {
                  <p class="muted">{{ m.title }}</p>
                }
              </div>
            </div>
            <ul class="lines">
              @if (m.email) {
                <li><sd-icon name="mail" [size]="15" /><a [href]="'mailto:' + m.email">{{ m.email }}</a></li>
              }
              @if (m.phone) {
                <li><sd-icon name="phone" [size]="15" /><a [href]="'tel:' + m.phone">{{ m.phone }}</a></li>
              }
            </ul>
          </section>
        }
        @if (s.departments.length) {
          <section class="card wide" aria-labelledby="sc-deps">
            <h3 id="sc-deps"><sd-icon name="building" [size]="17" />Departments</h3>
            <ul class="deps">
              @for (d of s.departments; track d.name) {
                <li>
                  <p class="name">{{ d.name }}</p>
                  @if (d.description) {
                    <p class="muted">{{ d.description }}</p>
                  }
                  @if (d.email) {
                    <a [href]="'mailto:' + d.email"><sd-icon name="mail" [size]="13" />{{ d.email }}</a>
                  }
                </li>
              }
            </ul>
          </section>
        }
        @if (!s.supportEmail && !s.supportPhone && !s.accountManager && !s.departments.length) {
          <p class="muted">Submit a ticket and our team will get back to you.</p>
        }
      </div>
    } @else {
      <div class="grid" aria-busy="true" aria-label="Loading contact details">
        <sd-skeleton height="110px" radius="var(--sd-radius-lg)" />
        <sd-skeleton height="110px" radius="var(--sd-radius-lg)" />
      </div>
    }
  `,
  styles: `
    .grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 260px), 1fr));
      gap: 14px;
    }
    .grid.is-stacked {
      grid-template-columns: minmax(0, 1fr);
    }
    .card {
      display: grid;
      align-content: start;
      gap: 10px;
      padding: 16px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    .card.wide {
      grid-column: 1 / -1;
    }
    h3 {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      font-size: var(--sd-text-md);
    }
    h3 sd-icon {
      color: var(--sd-primary);
    }
    .lines {
      display: grid;
      gap: 6px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .lines li {
      display: flex;
      align-items: center;
      gap: 8px;
      min-width: 0;
      overflow-wrap: anywhere;
    }
    .lines sd-icon {
      color: var(--sd-text-3);
    }
    .person {
      display: flex;
      align-items: center;
      gap: 10px;
    }
    .name {
      margin: 0;
      font-weight: var(--sd-weight-semibold);
    }
    .muted {
      margin: 0;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .deps {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 220px), 1fr));
      gap: 12px 18px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .deps li {
      display: grid;
      gap: 3px;
    }
    .deps a {
      display: inline-flex;
      align-items: center;
      gap: 5px;
      font-size: var(--sd-text-sm);
      overflow-wrap: anywhere;
    }
  `,
})
export class SupportContactsComponent {
  private readonly api = inject(PortalContentApi);
  private readonly brand = inject(BrandService);
  private readonly auth = inject(PortalAuthService);

  readonly stacked = input(false, { transform: booleanAttribute });

  protected readonly res = rxResource({
    params: () => {
      const slug = this.brand.slug();
      return slug ? { slug, signedIn: this.auth.isAuthenticated() } : undefined;
    },
    stream: ({ params }) => this.api.support(params.slug),
  });
  protected readonly info = computed<PortalSupportInfoDto | null>(() => (this.res.hasValue() ? this.res.value() : null));
}
