import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { PortalAuthService, PortalContentApi, PortalFlowSummaryDto } from '@sd/core';
import { SdButton, SdEmptyState, SdIcon, SdSkeleton } from '@sd/ui';
import { BrandService } from '../../brand.service';
import { BreadcrumbComponent } from '../../shared/breadcrumb.component';
import { ErrorStateComponent } from '../../shared/error-state.component';
import { SupportContactsComponent } from '../../shared/support-contacts.component';

/** Help & contact: guided troubleshooting flows, self-service shortcuts and support contacts. */
@Component({
  selector: 'portal-help-page',
  imports: [RouterLink, SdIcon, SdButton, SdSkeleton, SdEmptyState, BreadcrumbComponent, ErrorStateComponent, SupportContactsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="portal-container portal-page">
      <portal-breadcrumb [items]="[{ label: 'Home', link: brand.base() }, { label: 'Help & contact' }]" />
      <header class="portal-page-head">
        <div>
          <h1>Help &amp; contact</h1>
          <p class="portal-sub">Solve common problems step by step, or get in touch with our team.</p>
        </div>
      </header>

      <section aria-labelledby="flows-title" class="block">
        <h2 id="flows-title" class="block-title"><sd-icon name="signpost" [size]="19" />Guided troubleshooting</h2>
        <p class="block-sub">Answer a few questions and we'll walk you to a fix — or straight to the right team.</p>
        @if (res.error()) {
          <portal-error-state [error]="res.error()" compact (retry)="res.reload()" />
        } @else if (flows(); as list) {
          @if (list.length) {
            <ul class="flows">
              @for (f of list; track f.id) {
                <li>
                  <a class="flow" [routerLink]="[brand.base(), 'troubleshoot', f.id]">
                    <span class="flow-ic" aria-hidden="true"><sd-icon name="route" [size]="18" /></span>
                    <span class="flow-text">
                      <span class="flow-name">{{ f.name }}</span>
                      @if (f.description) {
                        <span class="flow-desc">{{ f.description }}</span>
                      }
                      @if (f.productName) {
                        <span class="flow-product"><sd-icon name="package" [size]="12" />{{ f.productName }}</span>
                      }
                    </span>
                    <span class="flow-go">Start <sd-icon name="arrow-right" [size]="14" /></span>
                  </a>
                </li>
              }
            </ul>
          } @else {
            <sd-empty-state icon="signpost" title="No guided troubleshooters yet" message="Search our articles or contact us below." compact />
          }
        } @else {
          <ul class="flows" aria-hidden="true">
            @for (i of [1, 2, 3]; track i) {
              <li><sd-skeleton height="88px" radius="var(--sd-radius-lg)" /></li>
            }
          </ul>
        }
      </section>

      <section aria-labelledby="self-title" class="block">
        <h2 id="self-title" class="block-title"><sd-icon name="compass" [size]="19" />Other ways to get help</h2>
        <ul class="shortcuts">
          @if (brand.features().kb) {
            <li>
              <a class="shortcut" [routerLink]="brand.base() + '/kb'"><sd-icon name="book-open" [size]="18" /><span>Browse the knowledge base</span></a>
            </li>
          }
          @if (brand.features().community) {
            <li>
              <a class="shortcut" [routerLink]="brand.base() + '/community'"><sd-icon name="messages" [size]="18" /><span>Ask the community</span></a>
            </li>
          }
          <li>
            <a class="shortcut" [routerLink]="brand.base() + '/submit'"><sd-icon name="file-plus" [size]="18" /><span>Submit a ticket</span></a>
          </li>
          @if (auth.isAuthenticated()) {
            <li>
              <a class="shortcut" [routerLink]="brand.base() + '/tickets'"><sd-icon name="ticket" [size]="18" /><span>Check my tickets</span></a>
            </li>
          }
        </ul>
      </section>

      <section aria-labelledby="contact-title" class="block">
        <h2 id="contact-title" class="block-title"><sd-icon name="headphones" [size]="19" />Contact us</h2>
        <portal-support-contacts />
        @if (!auth.isAuthenticated()) {
          <p class="signin">
            <a sdButton variant="link" [routerLink]="brand.base() + '/login'">Sign in</a> to see your account manager and follow your tickets.
          </p>
        }
      </section>
    </div>
  `,
  styles: `
    .block {
      margin-bottom: 32px;
    }
    .block-title {
      display: flex;
      align-items: center;
      gap: 8px;
      font-size: var(--sd-text-xl);
    }
    .block-title sd-icon {
      color: var(--sd-primary);
    }
    .block-sub {
      margin: 4px 0 14px;
      color: var(--sd-text-2);
    }
    .flows {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 320px), 1fr));
      gap: 12px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .flow {
      display: flex;
      align-items: flex-start;
      gap: 12px;
      height: 100%;
      padding: 16px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
      color: var(--sd-text);
      text-decoration: none;
      transition: border-color var(--sd-dur) var(--sd-ease), box-shadow var(--sd-dur) var(--sd-ease);
    }
    .flow:hover {
      border-color: var(--sd-primary-border);
      box-shadow: var(--sd-shadow-md);
      text-decoration: none;
    }
    .flow-ic {
      display: inline-flex;
      padding: 8px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-accent-soft);
      color: var(--sd-accent);
    }
    .flow-text {
      display: grid;
      gap: 3px;
      flex: 1;
      min-width: 0;
    }
    .flow-name {
      font-weight: var(--sd-weight-semibold);
    }
    .flow-desc {
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .flow-product {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .flow-go {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      color: var(--sd-primary);
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-medium);
      white-space: nowrap;
    }
    .shortcuts {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 230px), 1fr));
      gap: 10px;
      margin: 14px 0 0;
      padding: 0;
      list-style: none;
    }
    .shortcut {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 14px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      color: var(--sd-text);
      font-weight: var(--sd-weight-medium);
      text-decoration: none;
    }
    .shortcut:hover {
      border-color: var(--sd-primary-border);
      background: var(--sd-primary-soft);
      text-decoration: none;
    }
    .shortcut sd-icon {
      color: var(--sd-primary);
    }
    #contact-title {
      margin-bottom: 14px;
    }
    .signin {
      margin: 12px 0 0;
      color: var(--sd-text-2);
    }
  `,
})
export class HelpPage {
  protected readonly brand = inject(BrandService);
  protected readonly auth = inject(PortalAuthService);
  private readonly api = inject(PortalContentApi);

  protected readonly res = rxResource({
    params: () => {
      const slug = this.brand.slug();
      return slug ? { slug, signedIn: this.auth.isAuthenticated() } : undefined;
    },
    stream: ({ params }) => this.api.flows(params.slug),
  });
  protected readonly flows = computed<PortalFlowSummaryDto[] | null>(() => (this.res.hasValue() ? this.res.value() : null));
}
