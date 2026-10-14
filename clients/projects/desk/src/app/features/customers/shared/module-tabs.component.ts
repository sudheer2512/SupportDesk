import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { MeStore } from '@sd/core';
import { SdIcon } from '@sd/ui';

export interface ModuleTab {
  /** Absolute router link, e.g. "/customers/contacts". */
  link: string;
  label: string;
  icon: string;
  /** Shown when the user has ANY of these permissions (empty = always). */
  permissions?: readonly string[];
  /** Match the link exactly (for the module root). */
  exact?: boolean;
}

/**
 * Secondary navigation for a module: `<desk-module-tabs [tabs]="tabs" ariaLabel="Customers sections" />`.
 * Router links styled as tabs (aria-current on the active one); horizontally scrollable on phones.
 */
@Component({
  selector: 'desk-module-tabs',
  imports: [RouterLink, RouterLinkActive, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav class="tabs" [attr.aria-label]="ariaLabel()">
      @for (tab of visible(); track tab.link) {
        <a
          class="tab"
          [routerLink]="tab.link"
          routerLinkActive="is-active"
          [routerLinkActiveOptions]="{ exact: !!tab.exact }"
          ariaCurrentWhenActive="page"
        >
          <sd-icon [name]="tab.icon" [size]="15" />
          <span>{{ tab.label }}</span>
        </a>
      }
    </nav>
  `,
  styles: `
    :host {
      display: block;
      padding: 0 var(--sd-space-6);
      border-bottom: 1px solid var(--sd-border);
      margin-bottom: var(--sd-space-5);
    }
    @media (max-width: 640px) {
      :host {
        padding: 0 var(--sd-space-4);
      }
    }
    .tabs {
      display: flex;
      gap: var(--sd-space-1);
      overflow-x: auto;
      scrollbar-width: none;
    }
    .tab {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 10px 12px;
      border-bottom: 2px solid transparent;
      color: var(--sd-text-2);
      text-decoration: none;
      white-space: nowrap;
      font-weight: var(--sd-weight-medium);
      margin-bottom: -1px;
    }
    .tab:hover {
      color: var(--sd-text);
    }
    .tab.is-active {
      color: var(--sd-primary);
      border-bottom-color: var(--sd-primary);
    }
    .tab:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
      border-radius: var(--sd-radius-sm);
    }
  `,
})
export class ModuleTabsComponent {
  private readonly me = inject(MeStore);
  readonly tabs = input.required<readonly ModuleTab[]>();
  readonly ariaLabel = input('Sections');
  protected readonly visible = computed(() => this.tabs().filter((t) => !t.permissions?.length || this.me.canAny(t.permissions)));
}

export const CUSTOMER_TABS: readonly ModuleTab[] = [
  { link: '/customers', label: 'Customers', icon: 'building', exact: true },
  { link: '/customers/contacts', label: 'Contacts', icon: 'users' },
  { link: '/customers/duplicates', label: 'Duplicates', icon: 'merge' },
  { link: '/customers/activities', label: 'Service activities', icon: 'calendar' },
  { link: '/customers/groups', label: 'Groups', icon: 'layers' },
];

export const PRODUCT_TABS: readonly ModuleTab[] = [
  { link: '/products', label: 'Catalog', icon: 'package', exact: true, permissions: ['products.view'] },
  { link: '/products/families', label: 'Families', icon: 'folder-tree', permissions: ['products.view'] },
  { link: '/products/installations', label: 'Installations', icon: 'server', permissions: ['products.view'] },
  { link: '/products/licences', label: 'Licences', icon: 'key', permissions: ['products.view'] },
  { link: '/products/assets', label: 'Assets', icon: 'devices', permissions: ['products.view'] },
  { link: '/products/install-base', label: 'Install base', icon: 'chart-column', permissions: ['products.view'] },
  { link: '/products/contracts', label: 'Contracts', icon: 'file-text', permissions: ['contracts.view'] },
  { link: '/products/renewals', label: 'Renewals', icon: 'history', permissions: ['contracts.view'] },
  { link: '/products/erp', label: 'ERP integration', icon: 'plug', permissions: ['admin.integrations'] },
];
