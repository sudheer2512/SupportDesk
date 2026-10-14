import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AdminBrandListItemDto, AdminBrandsApi, ApiError } from '@sd/core';
import { SdBadge, SdButton, SdDialogService, SdDropdownMenu, SdEmptyState, SdIcon, SdMenuItem, SdPageHeader, SdSkeleton, ToastService } from '@sd/ui';

/** Brands & portals (admin.organisation): every brand's portal, domain, colours and default flag. */
@Component({
  selector: 'desk-admin-brands-list',
  imports: [RouterLink, SdPageHeader, SdButton, SdBadge, SdIcon, SdDropdownMenu, SdEmptyState, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Brands & portals" icon="globe" [breadcrumbs]="crumbs" subtitle="Each brand has its own help center, knowledge base, chat widget and e-mail identity.">
        <a sdButton sdPageActions variant="primary" icon="plus" routerLink="new">New brand</a>
      </sd-page-header>
      <div class="desk-page__body">
        @if (loading()) {
          <sd-skeleton [lines]="3" height="120px" />
        } @else if (error()) {
          <sd-empty-state icon="circle-alert" title="Brands could not be loaded" [message]="error()">
            <button sdButton icon="refresh" (click)="load()">Try again</button>
          </sd-empty-state>
        } @else if (brands().length === 0) {
          <sd-empty-state icon="globe" title="No brands yet" message="Create a brand to publish a customer help center.">
            <a sdButton variant="primary" icon="plus" routerLink="new">Create a brand</a>
          </sd-empty-state>
        } @else {
          <div class="grid">
            @for (b of brands(); track b.id) {
              <article class="brand" [attr.aria-labelledby]="'b-' + b.id">
                <div class="banner" [style.background]="b.primaryColor">
                  @if (b.logoUrl) {
                    <img [src]="b.logoUrl" alt="" class="logo" />
                  } @else {
                    <span class="initial" [style.color]="b.primaryColor">{{ b.name.charAt(0) }}</span>
                  }
                  <span class="accent" [style.background]="b.accentColor" aria-hidden="true"></span>
                </div>
                <div class="body">
                  <div class="title">
                    <a [id]="'b-' + b.id" [routerLink]="b.id" class="name">{{ b.name }}</a>
                    @if (b.isDefault) {
                      <sd-badge size="sm" tone="primary">Default</sd-badge>
                    }
                    <sd-badge size="sm" [tone]="b.portalEnabled ? 'success' : 'neutral'" dot>{{ b.portalEnabled ? 'Portal on' : 'Portal off' }}</sd-badge>
                  </div>
                  <a class="url sd-truncate" [href]="b.portalUrl" target="_blank" rel="noopener">{{ b.portalUrl }} <sd-icon name="external-link" [size]="12" /></a>
                  @if (b.customDomain) {
                    <span class="sd-muted small"><sd-icon name="globe" [size]="12" /> {{ b.customDomain }}</span>
                  }
                </div>
                <div class="actions">
                  <a sdButton size="sm" icon="pencil" [routerLink]="b.id">Edit</a>
                  <sd-dropdown-menu iconOnly icon="ellipsis" variant="ghost" size="sm" align="end" [ariaLabel]="'More actions for ' + b.name" [items]="menuFor(b)" (select)="onMenu(b, $event)" />
                </div>
              </article>
            }
          </div>
        }
      </div>
    </div>
  `,
  styles: `
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(100%, 300px), 1fr)); gap: var(--sd-space-4); }
    .brand { display: flex; flex-direction: column; border: 1px solid var(--sd-border); border-radius: var(--sd-radius-md); background: var(--sd-surface); overflow: hidden; }
    .banner { position: relative; display: flex; align-items: center; height: 72px; padding: 0 var(--sd-space-4); }
    .logo { max-height: 40px; max-width: 160px; padding: 4px 6px; border-radius: var(--sd-radius-sm); background: var(--sd-surface); object-fit: contain; }
    .initial { display: grid; place-items: center; width: 40px; height: 40px; border-radius: var(--sd-radius-md); background: var(--sd-surface); font-weight: var(--sd-weight-bold); font-size: var(--sd-text-lg); }
    .accent { position: absolute; left: 0; right: 0; bottom: 0; height: 4px; }
    .body { display: flex; flex-direction: column; gap: 4px; padding: var(--sd-space-3) var(--sd-space-4); flex: 1; min-width: 0; }
    .title { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
    .name { font-weight: var(--sd-weight-semibold); color: var(--sd-text); text-decoration: none; }
    .name:hover, .name:focus-visible { color: var(--sd-primary); text-decoration: underline; }
    .url { font-size: var(--sd-text-sm); color: var(--sd-primary); }
    .small { font-size: var(--sd-text-xs); display: inline-flex; gap: 4px; align-items: center; }
    .actions { display: flex; justify-content: flex-end; gap: 6px; padding: 0 var(--sd-space-3) var(--sd-space-3); }
  `,
})
export class BrandsListPage implements OnInit {
  private readonly api = inject(AdminBrandsApi);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(SdDialogService);

  protected readonly crumbs = [{ label: 'Administration', link: '/admin' }, { label: 'Brands & portals' }];
  protected readonly brands = signal<AdminBrandListItemDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.list().subscribe({
      next: (list) => {
        this.brands.set(list);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.error.set(ApiError.from(err).userMessage);
        this.loading.set(false);
      },
    });
  }

  protected menuFor(b: AdminBrandListItemDto): SdMenuItem[] {
    return [
      { id: 'portal', label: 'Open the portal', icon: 'external-link', disabled: !b.portalEnabled },
      { id: 'default', label: 'Make default brand', icon: 'star', disabled: b.isDefault || !b.portalEnabled },
      { id: 'divider', divider: true },
      { id: 'delete', label: 'Delete', icon: 'trash', danger: true, disabled: b.isDefault },
    ];
  }

  protected async onMenu(b: AdminBrandListItemDto, item: SdMenuItem): Promise<void> {
    if (item.id === 'portal') window.open(b.portalUrl, '_blank', 'noopener');
    if (item.id === 'default')
      this.api.makeDefault(b.id).subscribe({
        next: () => {
          this.toast.success('Default brand changed', b.name);
          this.load();
        },
        error: (err: unknown) => this.toast.error('Could not change the default brand', ApiError.from(err).userMessage),
      });
    if (item.id === 'delete') {
      const ok = await this.dialogs.confirm({ title: `Delete “${b.name}”?`, message: 'Its help center stops working. Brands with tickets or channels cannot be deleted — switch their portal off instead.', danger: true, confirmLabel: 'Delete', typeToConfirm: b.slug });
      if (!ok) return;
      this.api.delete(b.id).subscribe({
        next: () => {
          this.toast.success('Brand deleted', b.name);
          this.load();
        },
        error: (err: unknown) => this.toast.error('Could not delete the brand', ApiError.from(err).userMessage),
      });
    }
  }
}
