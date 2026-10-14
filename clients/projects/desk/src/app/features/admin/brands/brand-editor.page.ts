import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AdminBrandDto, AdminBrandsApi, AdminPortalAuthMode, ApiError, DomainCheckResultDto, MetaApi, SaveBrandRequest } from '@sd/core';
import { SdBadge, SdButton, SdChipsInput, SdEmptyState, SdFormField, SdIcon, SdInput, SdPageHeader, SdSkeleton, SdToggle, ToastService } from '@sd/ui';
import { ADMIN_EDITOR_STYLES, AdminLeaveGuarded, sameValue, saveErrors, warnOnUnload } from '../admin-shared';

type BrandForm = Omit<SaveBrandRequest, 'rowVersion' | 'customDomain' | 'supportEmail' | 'supportPhone' | 'portalWelcome' | 'footerHtml' | 'emailFooterHtml' | 'defaultDepartmentId'> & {
  customDomain: string;
  supportEmail: string;
  supportPhone: string;
  portalWelcome: string;
  footerHtml: string;
  emailFooterHtml: string;
  defaultDepartmentId: string;
};

const PORTAL_TOGGLES: { key: keyof BrandForm; label: string; hint: string }[] = [
  { key: 'knowledgeBaseEnabled', label: 'Knowledge base', hint: 'Public and customer-only articles.' },
  { key: 'communityEnabled', label: 'Community forums', hint: 'Customers ask and answer questions.' },
  { key: 'chatEnabled', label: 'Live chat widget', hint: 'Chat with agents from the portal.' },
  { key: 'aiAssistantEnabled', label: 'AI assistant', hint: 'Answers from the knowledge base before a ticket is raised.' },
  { key: 'allowAnonymousTickets', label: 'Anonymous tickets', hint: 'Visitors can raise tickets without signing in.' },
  { key: 'allowSelfRegistration', label: 'Self-registration', hint: 'Customers can create their own portal account.' },
];

/** Brand editor: identity, logo/favicon, colours with a live preview, custom domain + DNS check, portal options, contact details and footers. */
@Component({
  selector: 'desk-admin-brand-editor',
  imports: [FormsModule, RouterLink, SdPageHeader, SdButton, SdBadge, SdIcon, SdFormField, SdInput, SdToggle, SdChipsInput, SdEmptyState, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header [title]="brand()?.name ?? (loading() ? 'Brand' : 'New brand')" icon="globe" [breadcrumbs]="crumbs()" subtitle="Branding and options of the customer help center.">
        @if (brand(); as b) {
          <a sdButton sdPageActions variant="ghost" icon="external-link" [href]="b.portalUrl" target="_blank" rel="noopener">Open portal</a>
        }
      </sd-page-header>
      <div class="desk-page__body">
        @if (loading()) {
          <sd-skeleton [lines]="8" height="44px" />
        } @else if (loadError()) {
          <sd-empty-state icon="circle-alert" title="This brand could not be loaded" [message]="loadError()">
            <a sdButton routerLink="/admin/brands" icon="arrow-left">Back to brands</a>
          </sd-empty-state>
        } @else {
          @if (formError()) {
            <div class="callout danger" role="alert"><sd-icon name="circle-alert" [size]="16" /><div>{{ formError() }}</div></div>
          }
          <div class="layout">
            <div class="main">
              <section class="card" aria-labelledby="b-id">
                <h2 id="b-id">Identity</h2>
                <p class="sd-muted">The portal address is unique across SupportDesk.</p>
                <div class="grid2">
                  <sd-form-field label="Brand name" required [error]="errors()['name']">
                    <input sdInput name="name" [ngModel]="form().name" (ngModelChange)="setName($event)" maxlength="100" />
                  </sd-form-field>
                  <sd-form-field label="Portal address" required [error]="errors()['slug']" [hint]="portalPreviewUrl()">
                    <input sdInput name="slug" class="mono" [ngModel]="form().slug" (ngModelChange)="patch({ slug: $event.toLowerCase() })" maxlength="64" />
                  </sd-form-field>
                  <sd-form-field label="Primary colour" required [error]="errors()['primaryColor']">
                    <span class="color"><input type="color" aria-label="Pick the primary colour" [ngModel]="form().primaryColor" (ngModelChange)="patch({ primaryColor: $event })" /><input sdInput name="primary" class="mono" [ngModel]="form().primaryColor" (ngModelChange)="patch({ primaryColor: $event })" maxlength="7" /></span>
                  </sd-form-field>
                  <sd-form-field label="Accent colour" required [error]="errors()['accentColor']">
                    <span class="color"><input type="color" aria-label="Pick the accent colour" [ngModel]="form().accentColor" (ngModelChange)="patch({ accentColor: $event })" /><input sdInput name="accent" class="mono" [ngModel]="form().accentColor" (ngModelChange)="patch({ accentColor: $event })" maxlength="7" /></span>
                  </sd-form-field>
                </div>
              </section>

              <section class="card" aria-labelledby="b-assets">
                <h2 id="b-assets">Logo &amp; favicon</h2>
                <p class="sd-muted">PNG, JPEG, GIF or WebP up to 2 MB. Files are malware-scanned and served from your SupportDesk.</p>
                @if (brand(); as b) {
                  <div class="assets">
                    @for (a of assetKinds; track a.kind) {
                      <div class="asset">
                        <div class="thumb" [class.fav]="a.kind === 'favicon'">
                          @if (a.kind === 'logo' ? b.logoUrl : b.faviconUrl; as url) {
                            <img [src]="url" [alt]="a.label + ' of ' + b.name" />
                          } @else {
                            <sd-icon name="image" [size]="22" />
                          }
                        </div>
                        <div class="aside">
                          <strong>{{ a.label }}</strong>
                          <span class="sd-muted small">{{ a.hint }}</span>
                          <span class="abtns">
                            <label class="upload" [class.busy]="uploading() === a.kind">
                              <input type="file" accept="image/png,image/jpeg,image/gif,image/webp" (change)="upload(a.kind, $event)" [attr.aria-label]="'Upload ' + a.label" />
                              <sd-icon name="upload" [size]="14" /> {{ uploading() === a.kind ? 'Uploading…' : 'Upload' }}
                            </label>
                            @if (a.kind === 'logo' ? b.logoUrl : b.faviconUrl) {
                              <button sdButton size="sm" variant="ghost" icon="trash" (click)="removeAsset(a.kind)">Remove</button>
                            }
                          </span>
                        </div>
                      </div>
                    }
                  </div>
                } @else {
                  <p class="sd-muted small">Create the brand first, then upload its logo and favicon.</p>
                }
              </section>

              <section class="card" aria-labelledby="b-domain">
                <h2 id="b-domain">Custom domain</h2>
                <p class="sd-muted">Serve the help center from your own address, e.g. support.example.com.</p>
                <sd-form-field label="Domain" optional [error]="errors()['customDomain']">
                  <input sdInput name="domain" class="mono" placeholder="support.example.com" [ngModel]="form().customDomain" (ngModelChange)="patch({ customDomain: $event.trim().toLowerCase() })" maxlength="253" />
                </sd-form-field>
                @if (brand()?.dns; as dns) {
                  <div class="dns" role="region" aria-label="DNS instructions">
                    <strong>DNS record to create</strong>
                    <table>
                      <thead><tr><th scope="col">Type</th><th scope="col">Name</th><th scope="col">Points to</th></tr></thead>
                      <tbody><tr><td class="mono">{{ dns.recordType }}</td><td class="mono">{{ dns.host }}</td><td class="mono">{{ dns.target }}</td></tr></tbody>
                    </table>
                    <p class="sd-muted small">{{ dns.instructions }}</p>
                    <div class="row">
                      <button sdButton size="sm" icon="refresh" [loading]="checking()" [disabled]="dirtyDomain()" (click)="checkDomain()">Check DNS</button>
                      @if (dirtyDomain()) {
                        <span class="sd-muted small">Save the new domain before checking it.</span>
                      }
                      @if (domainCheck(); as c) {
                        <sd-badge [tone]="c.pointsToPortal ? 'success' : c.resolves ? 'warning' : 'danger'" dot>{{ c.pointsToPortal ? 'Connected' : c.resolves ? 'Points elsewhere' : 'Not found' }}</sd-badge>
                      }
                    </div>
                    @if (domainCheck(); as c) {
                      <p class="small" role="status">{{ c.message }}@if (c.addresses.length) { Resolves to {{ c.addresses.join(', ') }}.}</p>
                    }
                  </div>
                }
              </section>

              <section class="card" aria-labelledby="b-portal">
                <h2 id="b-portal">Portal</h2>
                <div class="grid2">
                  <sd-form-field label="Portal title" required [error]="errors()['portalTitle']">
                    <input sdInput name="title" [ngModel]="form().portalTitle" (ngModelChange)="patch({ portalTitle: $event })" maxlength="100" />
                  </sd-form-field>
                  <sd-form-field label="Default department for portal tickets" optional [error]="errors()['defaultDepartmentId']">
                    <select sdInput name="dept" [ngModel]="form().defaultDepartmentId" (ngModelChange)="patch({ defaultDepartmentId: $event })">
                      <option value="">Routing decides</option>
                      @for (d of departments(); track d.id) {
                        <option [value]="d.id">{{ d.name }}</option>
                      }
                    </select>
                  </sd-form-field>
                  <sd-form-field label="Welcome message" optional class="full" [error]="errors()['portalWelcome']">
                    <textarea sdInput name="welcome" rows="2" [ngModel]="form().portalWelcome" (ngModelChange)="patch({ portalWelcome: $event })" maxlength="1000"></textarea>
                  </sd-form-field>
                  <sd-form-field label="Default language" required [error]="errors()['defaultLocale']" hint="e.g. en, en-GB, hi">
                    <input sdInput name="locale" class="mono" [ngModel]="form().defaultLocale" (ngModelChange)="patch({ defaultLocale: $event })" maxlength="12" />
                  </sd-form-field>
                  <sd-form-field label="Languages offered" [error]="errors()['locales']">
                    <sd-chips-input [value]="form().locales" (valueChange)="patch({ locales: $event })" placeholder="Add language code" ariaLabel="Languages offered" [lowercase]="false" />
                  </sd-form-field>
                  <sd-form-field label="Customer sign-in" [error]="errors()['portalAuthMode']">
                    <select sdInput name="auth" [ngModel]="form().portalAuthMode" (ngModelChange)="patch({ portalAuthMode: $event })">
                      @for (m of authModes; track m.value) {
                        <option [value]="m.value">{{ m.label }}</option>
                      }
                    </select>
                  </sd-form-field>
                </div>
                <div class="toggles">
                  <sd-toggle [value]="form().portalEnabled" (valueChange)="patch({ portalEnabled: $event })" [disabled]="!!brand()?.isDefault">Help center is online</sd-toggle>
                  @if (errors()['portalEnabled']) {
                    <p class="err">{{ errors()['portalEnabled'] }}</p>
                  }
                  @for (t of portalToggles; track t.key) {
                    <div class="tg">
                      <sd-toggle [value]="!!form()[t.key]" (valueChange)="setFlag(t.key, $event)" [disabled]="!form().portalEnabled">{{ t.label }}</sd-toggle>
                      <span class="sd-muted small">{{ t.hint }}</span>
                    </div>
                  }
                  @if (errors()['allowAnonymousTickets']) {
                    <p class="err">{{ errors()['allowAnonymousTickets'] }}</p>
                  }
                </div>
              </section>

              <section class="card" aria-labelledby="b-contact">
                <h2 id="b-contact">Contact &amp; footers</h2>
                <div class="grid2">
                  <sd-form-field label="Support e-mail" optional [error]="errors()['supportEmail']">
                    <input sdInput name="email" type="email" [ngModel]="form().supportEmail" (ngModelChange)="patch({ supportEmail: $event })" maxlength="320" />
                  </sd-form-field>
                  <sd-form-field label="Support phone" optional [error]="errors()['supportPhone']">
                    <input sdInput name="phone" type="tel" [ngModel]="form().supportPhone" (ngModelChange)="patch({ supportPhone: $event })" maxlength="40" />
                  </sd-form-field>
                  <sd-form-field label="Portal footer (HTML)" optional hint="Sanitised: links, formatting and images only." [error]="errors()['footerHtml']">
                    <textarea sdInput name="footer" class="mono" rows="4" [ngModel]="form().footerHtml" (ngModelChange)="patch({ footerHtml: $event })"></textarea>
                  </sd-form-field>
                  <sd-form-field label="E-mail footer / signature (HTML)" optional hint="Appended to replies, acknowledgements and surveys sent for this brand." [error]="errors()['emailFooterHtml']">
                    <textarea sdInput name="emailFooter" class="mono" rows="4" [ngModel]="form().emailFooterHtml" (ngModelChange)="patch({ emailFooterHtml: $event })"></textarea>
                  </sd-form-field>
                </div>
                @if (form().emailFooterHtml.trim()) {
                  <div class="footer-preview">
                    <span class="sd-muted small">E-mail footer preview</span>
                    <div class="sd-prose" [innerHTML]="form().emailFooterHtml"></div>
                  </div>
                }
              </section>
            </div>

            <aside class="side" aria-label="Portal preview">
              <div class="preview">
                <div class="phead" [style.background]="form().primaryColor">
                  @if (brand()?.logoUrl; as logo) {
                    <img [src]="logo" alt="" />
                  } @else {
                    <span class="pinitial" [style.color]="form().primaryColor">{{ (form().name || 'B').charAt(0) }}</span>
                  }
                  <span class="ptitle">{{ form().portalTitle || 'Help Center' }}</span>
                </div>
                <div class="pbody">
                  <p class="pwelcome">{{ form().portalWelcome || 'How can we help you today?' }}</p>
                  <span class="psearch"><sd-icon name="search" [size]="14" /> Search articles…</span>
                  <span class="pbtn" [style.background]="form().accentColor">Submit a ticket</span>
                  <ul class="pfeatures">
                    @for (t of portalToggles.slice(0, 4); track t.key) {
                      @if (form()[t.key]) {
                        <li><sd-icon name="check" [size]="12" /> {{ t.label }}</li>
                      }
                    }
                  </ul>
                </div>
              </div>
              <p class="sd-muted small">Preview — colours apply to the portal immediately after saving.</p>
            </aside>
          </div>

          <div class="savebar">
            @if (dirty()) {
              <span class="sd-muted small"><sd-icon name="circle-dot" [size]="12" /> Unsaved changes</span>
            } @else {
              <span class="sd-muted small">All changes saved</span>
            }
            <span class="sd-spacer"></span>
            <button sdButton variant="ghost" [disabled]="!dirty() || saving()" (click)="discard()">Discard</button>
            <button sdButton variant="primary" icon="check" [loading]="saving()" [disabled]="!!brand() && !dirty()" (click)="save()">{{ brand() ? 'Save brand' : 'Create brand' }}</button>
          </div>
        }
      </div>
    </div>
  `,
  styles: `
    ${ADMIN_EDITOR_STYLES}
    .layout { display: grid; grid-template-columns: minmax(0, 1fr) 300px; gap: var(--sd-space-4); align-items: start; }
    .side { position: sticky; top: var(--sd-space-3); display: flex; flex-direction: column; gap: 6px; }
    .color { display: flex; gap: 6px; align-items: center; }
    .color input[type='color'] { width: 40px; height: var(--sd-control-h); padding: 2px; border: 1px solid var(--sd-border); border-radius: var(--sd-radius-sm); background: var(--sd-surface); }
    .assets { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--sd-space-3); }
    .asset { display: flex; gap: var(--sd-space-3); align-items: center; }
    .thumb { display: grid; place-items: center; width: 96px; height: 56px; flex: none; border: 1px dashed var(--sd-border-strong); border-radius: var(--sd-radius-sm); background: var(--sd-surface-sunken); color: var(--sd-text-3); overflow: hidden; }
    .thumb.fav { width: 56px; }
    .thumb img { max-width: 100%; max-height: 100%; object-fit: contain; }
    .aside { display: flex; flex-direction: column; gap: 4px; min-width: 0; }
    .abtns { display: flex; gap: 6px; align-items: center; }
    .upload { position: relative; display: inline-flex; align-items: center; gap: 6px; height: var(--sd-control-h-sm); padding: 0 10px; border: 1px solid var(--sd-border); border-radius: var(--sd-radius-sm); background: var(--sd-surface); font-size: var(--sd-text-sm); cursor: pointer; }
    .upload:focus-within { box-shadow: var(--sd-focus-ring); }
    .upload input { position: absolute; inset: 0; opacity: 0; cursor: pointer; }
    .upload.busy { opacity: 0.7; pointer-events: none; }
    .dns { display: flex; flex-direction: column; gap: 6px; margin-top: var(--sd-space-3); padding: var(--sd-space-3); border: 1px solid var(--sd-info-border); border-radius: var(--sd-radius-md); background: var(--sd-info-bg); }
    .dns table { width: 100%; border-collapse: collapse; font-size: var(--sd-text-sm); }
    .dns th, .dns td { padding: 4px 6px; text-align: left; border-bottom: 1px solid var(--sd-border); word-break: break-all; }
    .dns p { margin: 0; }
    .row { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
    .toggles { display: flex; flex-direction: column; gap: 8px; margin-top: var(--sd-space-3); }
    .tg { display: flex; flex-direction: column; gap: 2px; }
    .footer-preview { margin-top: var(--sd-space-3); padding: var(--sd-space-3); border: 1px dashed var(--sd-border); border-radius: var(--sd-radius-sm); }
    .preview { border: 1px solid var(--sd-border); border-radius: var(--sd-radius-md); overflow: hidden; background: var(--sd-surface); box-shadow: var(--sd-shadow-sm); }
    .phead { display: flex; align-items: center; gap: 8px; padding: 12px; }
    .phead img { max-height: 28px; max-width: 110px; padding: 2px 4px; border-radius: var(--sd-radius-xs); background: var(--sd-surface); object-fit: contain; }
    .pinitial { display: grid; place-items: center; width: 28px; height: 28px; border-radius: var(--sd-radius-sm); background: var(--sd-surface); font-weight: var(--sd-weight-bold); }
    .ptitle { padding: 2px 6px; border-radius: var(--sd-radius-xs); background: var(--sd-surface); color: var(--sd-text); font-weight: var(--sd-weight-semibold); font-size: var(--sd-text-sm); }
    .pbody { display: flex; flex-direction: column; gap: 8px; padding: 12px; }
    .pwelcome { margin: 0; font-size: var(--sd-text-sm); }
    .psearch { display: flex; gap: 6px; align-items: center; padding: 6px 8px; border: 1px solid var(--sd-border); border-radius: var(--sd-radius-sm); color: var(--sd-text-3); font-size: var(--sd-text-sm); }
    .pbtn { align-self: flex-start; padding: 6px 10px; border-radius: var(--sd-radius-sm); color: var(--sd-on-accent); font-size: var(--sd-text-sm); font-weight: var(--sd-weight-semibold); }
    .pfeatures { margin: 0; padding: 0; list-style: none; font-size: var(--sd-text-xs); color: var(--sd-text-2); }
    @media (max-width: 1100px) { .layout { grid-template-columns: minmax(0, 1fr); } .side { position: static; max-width: 360px; } }
    @media (max-width: 760px) { .assets { grid-template-columns: minmax(0, 1fr); } }
  `,
})
export class BrandEditorPage implements OnInit, AdminLeaveGuarded {
  private readonly api = inject(AdminBrandsApi);
  private readonly meta = inject(MetaApi);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  /** Route param: a brand id, or absent for /admin/brands/new. */
  readonly id = input<string | undefined>();

  protected readonly portalToggles = PORTAL_TOGGLES;
  protected readonly assetKinds = [
    { kind: 'logo' as const, label: 'Logo', hint: 'Shown in the portal header and e-mails. Wide images work best.' },
    { kind: 'favicon' as const, label: 'Favicon', hint: 'Browser tab icon, square (e.g. 64 × 64).' },
  ];
  protected readonly authModes: { value: AdminPortalAuthMode; label: string }[] = [
    { value: 'Local', label: 'E-mail and password / one-time code' },
    { value: 'Sso', label: 'Single sign-on only' },
    { value: 'LocalAndSso', label: 'Both' },
  ];
  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);
  protected readonly brand = signal<AdminBrandDto | null>(null);
  protected readonly form = signal<BrandForm>(this.blank());
  private readonly original = signal<BrandForm>(this.blank());
  private slugTouched = false;
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly uploading = signal<'logo' | 'favicon' | null>(null);
  protected readonly checking = signal(false);
  protected readonly domainCheck = signal<DomainCheckResultDto | null>(null);

  protected readonly departments = computed(() => this.meta.ticketForm()?.departments ?? []);
  protected readonly dirty = computed(() => !sameValue(this.form(), this.original()));
  protected readonly dirtyDomain = computed(() => (this.form().customDomain || null) !== (this.brand()?.customDomain ?? null));
  protected readonly portalPreviewUrl = computed(() => {
    const b = this.brand();
    const base = b ? b.portalUrl.slice(0, b.portalUrl.lastIndexOf('/') + 1) : `${location.origin}/portal/`;
    return `${base}${this.form().slug || 'your-brand'}`;
  });
  protected readonly crumbs = computed(() => [
    { label: 'Administration', link: '/admin' },
    { label: 'Brands & portals', link: '/admin/brands' },
    { label: this.brand()?.name ?? 'New brand' },
  ]);

  constructor() {
    warnOnUnload(this.dirty);
  }

  ngOnInit(): void {
    this.meta.load();
    const brandId = this.id();
    if (!brandId || brandId === 'new') {
      this.loading.set(false);
      return;
    }
    this.api.get(brandId).subscribe({
      next: (b) => {
        this.apply(b);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loadError.set(ApiError.from(err).userMessage);
        this.loading.set(false);
      },
    });
  }

  hasUnsavedChanges(): boolean {
    return this.dirty() && !this.saving();
  }

  protected patch(change: Partial<BrandForm>): void {
    if ('slug' in change) this.slugTouched = true;
    this.form.update((f) => ({ ...f, ...change }));
  }

  protected setFlag(key: keyof BrandForm, value: boolean): void {
    this.form.update((f) => ({ ...f, [key]: value }));
  }

  /** New brands derive their portal address from the name until the address is edited. */
  protected setName(name: string): void {
    const derived = name
      .toLowerCase()
      .normalize('NFKD')
      .replace(/[^a-z0-9]+/g, '-')
      .replace(/^-+|-+$/g, '')
      .slice(0, 64);
    this.form.update((f) => ({ ...f, name, slug: !this.brand() && !this.slugTouched ? derived : f.slug }));
  }

  protected discard(): void {
    this.form.set(structuredClone(this.original()));
    this.errors.set({});
    this.formError.set(null);
  }

  protected save(): void {
    const f = this.form();
    const request: SaveBrandRequest = {
      ...f,
      customDomain: f.customDomain.trim() || null,
      supportEmail: f.supportEmail.trim() || null,
      supportPhone: f.supportPhone.trim() || null,
      portalWelcome: f.portalWelcome.trim() || null,
      footerHtml: f.footerHtml.trim() || null,
      emailFooterHtml: f.emailFooterHtml.trim() || null,
      defaultDepartmentId: f.defaultDepartmentId || null,
      rowVersion: this.brand()?.rowVersion ?? null,
    };
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    const existing = this.brand();
    (existing ? this.api.update(existing.id, request) : this.api.create(request)).subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.apply(saved);
        this.meta.refresh();
        this.toast.success(existing ? 'Brand saved' : 'Brand created', saved.name);
        if (!existing) void this.router.navigate(['/admin/brands', saved.id], { replaceUrl: true });
      },
      error: (err: unknown) => {
        this.saving.set(false);
        if (ApiError.from(err).is('concurrency_conflict')) {
          this.formError.set('Someone else changed this brand. Reload the page to see their version.');
          return;
        }
        const { fields, message } = saveErrors(err, 'The brand could not be saved.');
        this.errors.set(fields);
        this.formError.set(message);
      },
    });
  }

  protected upload(kind: 'logo' | 'favicon', event: Event): void {
    const inputEl = event.target as HTMLInputElement;
    const file = inputEl.files?.[0];
    inputEl.value = '';
    const b = this.brand();
    if (!file || !b) return;
    if (file.size > 2 * 1024 * 1024) {
      this.toast.error('The image is too large', 'Brand images may be at most 2 MB.');
      return;
    }
    this.uploading.set(kind);
    this.api.uploadAsset(b.id, kind, file).subscribe({
      next: (updated) => {
        this.uploading.set(null);
        this.brand.set(updated);
        this.toast.success(kind === 'logo' ? 'Logo uploaded' : 'Favicon uploaded');
      },
      error: (err: unknown) => {
        this.uploading.set(null);
        this.toast.error('Upload failed', ApiError.from(err).userMessage);
      },
    });
  }

  protected removeAsset(kind: 'logo' | 'favicon'): void {
    const b = this.brand();
    if (!b) return;
    this.api.removeAsset(b.id, kind).subscribe({
      next: (updated) => this.brand.set(updated),
      error: (err: unknown) => this.toast.error('Could not remove the image', ApiError.from(err).userMessage),
    });
  }

  protected checkDomain(): void {
    const b = this.brand();
    if (!b) return;
    this.checking.set(true);
    this.api.checkDomain(b.id).subscribe({
      next: (r) => {
        this.checking.set(false);
        this.domainCheck.set(r);
      },
      error: (err: unknown) => {
        this.checking.set(false);
        this.toast.error('DNS check failed', ApiError.from(err).userMessage);
      },
    });
  }

  private apply(b: AdminBrandDto): void {
    this.brand.set(b);
    const f: BrandForm = {
      name: b.name,
      slug: b.slug,
      customDomain: b.customDomain ?? '',
      primaryColor: b.primaryColor,
      accentColor: b.accentColor,
      supportEmail: b.supportEmail ?? '',
      supportPhone: b.supportPhone ?? '',
      portalTitle: b.portalTitle,
      portalWelcome: b.portalWelcome ?? '',
      footerHtml: b.footerHtml ?? '',
      emailFooterHtml: b.emailFooterHtml ?? '',
      defaultDepartmentId: b.defaultDepartmentId ?? '',
      defaultLocale: b.defaultLocale,
      locales: [...b.locales],
      portalEnabled: b.portalEnabled,
      knowledgeBaseEnabled: b.knowledgeBaseEnabled,
      communityEnabled: b.communityEnabled,
      chatEnabled: b.chatEnabled,
      aiAssistantEnabled: b.aiAssistantEnabled,
      allowAnonymousTickets: b.allowAnonymousTickets,
      allowSelfRegistration: b.allowSelfRegistration,
      portalAuthMode: b.portalAuthMode,
    };
    this.form.set(f);
    this.original.set(structuredClone(f));
  }

  private blank(): BrandForm {
    return {
      name: '',
      slug: '',
      customDomain: '',
      primaryColor: '#2563eb',
      accentColor: '#0ea5e9',
      supportEmail: '',
      supportPhone: '',
      portalTitle: 'Help Center',
      portalWelcome: '',
      footerHtml: '',
      emailFooterHtml: '',
      defaultDepartmentId: '',
      defaultLocale: 'en',
      locales: ['en'],
      portalEnabled: true,
      knowledgeBaseEnabled: true,
      communityEnabled: false,
      chatEnabled: true,
      aiAssistantEnabled: true,
      allowAnonymousTickets: false,
      allowSelfRegistration: false,
      portalAuthMode: 'Local',
    };
  }
}
