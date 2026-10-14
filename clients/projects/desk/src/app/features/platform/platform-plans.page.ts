import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError, PlanDto, PlanFeatureDto, PlatformApi, SavePlanRequest } from '@sd/core';
import { SdBadge, SdButton, SdCard, SdCheckbox, SdDialogService, SdDrawer, SdEmptyState, SdFormField, SdInput, SdPageHeader, SdSkeleton, SdToggle, ToastService } from '@sd/ui';
import { PlanLimitsComponent, SAAS_CSS, money } from '../subscription/saas-ui';

type LimitKey = 'maxAgents' | 'maxLightAgents' | 'maxPortalUsers' | 'storageMb' | 'maxChannels' | 'aiTokensMonthly' | 'maxAttachmentMb' | 'apiCallsMonthly' | 'maxBrands';

/** Plans CRUD: prices (decimal + currency), limits (empty = unlimited), features, visibility for sign-up. */
@Component({
  selector: 'desk-platform-plans-page',
  imports: [FormsModule, SdBadge, SdButton, SdCard, SdCheckbox, SdDrawer, SdEmptyState, SdFormField, SdInput, SdPageHeader, SdSkeleton, SdToggle, PlanLimitsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Plans" icon="layers" subtitle="Prices, limits and features offered to tenants.">
        <button sdButton sdPageActions variant="primary" icon="plus" (click)="edit(null)">New plan</button>
      </sd-page-header>
      <div class="desk-page__body">
        @if (error(); as e) {
          <sd-empty-state icon="circle-alert" title="Plans could not be loaded" [message]="e"><button sdButton (click)="load()">Try again</button></sd-empty-state>
        } @else if (plans() === null) {
          <div class="cards"><sd-skeleton height="260px" /><sd-skeleton height="260px" /><sd-skeleton height="260px" /></div>
        } @else if (plans()!.length === 0) {
          <sd-empty-state icon="layers" title="No plans yet"><button sdButton variant="primary" (click)="edit(null)">Create a plan</button></sd-empty-state>
        } @else {
          <div class="cards">
            @for (p of plans(); track p.id) {
              <sd-card [title]="p.name" [subtitle]="p.code">
                <div sdCardActions class="badges">
                  @if (!p.isPublic) {
                    <sd-badge size="sm" variant="outline">Hidden</sd-badge>
                  }
                  <sd-badge size="sm" tone="info">{{ p.tenantCount }} tenant{{ p.tenantCount === 1 ? '' : 's' }}</sd-badge>
                </div>
                <div class="price">{{ money(p.monthlyPricePerAgent, p.currency) }} <span class="muted small">/ agent / month · {{ money(p.annualPricePerAgent, p.currency) }} / year</span></div>
                @if (p.trialDays) {
                  <div class="muted small">{{ p.trialDays }}-day trial</div>
                }
                <desk-plan-limits [plan]="p" />
                <div class="feats">
                  @for (f of p.features; track f) {
                    <sd-badge size="sm" variant="outline">{{ featureLabel(f) }}</sd-badge>
                  }
                </div>
                <div class="footer">
                  <button sdButton size="sm" variant="ghost" icon="trash" [disabled]="p.tenantCount > 0" [attr.aria-label]="'Delete ' + p.name" (click)="remove(p)">Delete</button>
                  <button sdButton size="sm" icon="pencil" (click)="edit(p)">Edit</button>
                </div>
              </sd-card>
            }
          </div>
        }
      </div>
    </div>

    <sd-drawer [(open)]="editorOpen" [title]="editingId() ? 'Edit plan' : 'New plan'" width="620px">
      <form class="form" id="plan-form" (ngSubmit)="save()">
        <div class="form-grid">
          <sd-form-field label="Code" required [hint]="editingId() ? 'Cannot be changed' : 'lower-case, e.g. growth'" [error]="err('code')">
            <input sdInput name="code" [(ngModel)]="form.code" [disabled]="!!editingId()" autocapitalize="none" />
          </sd-form-field>
          <sd-form-field label="Name" required [error]="err('name')"><input sdInput name="name" [(ngModel)]="form.name" maxlength="100" /></sd-form-field>
        </div>
        <sd-form-field label="Description" optional><textarea sdInput name="desc" rows="2" [(ngModel)]="form.description"></textarea></sd-form-field>
        <div class="form-grid">
          <sd-form-field label="Monthly price / agent" required [error]="err('monthlyPricePerAgent')">
            <input sdInput type="number" min="0" step="0.01" name="pm" [(ngModel)]="form.monthlyPricePerAgent" />
          </sd-form-field>
          <sd-form-field label="Annual price / agent" required [error]="err('annualPricePerAgent')">
            <input sdInput type="number" min="0" step="0.01" name="pa" [(ngModel)]="form.annualPricePerAgent" />
          </sd-form-field>
          <sd-form-field label="Currency" required [error]="err('currency')"><input sdInput name="cur" maxlength="3" [(ngModel)]="form.currency" /></sd-form-field>
          <sd-form-field label="Trial days" [error]="err('trialDays')"><input sdInput type="number" min="0" max="365" name="td" [(ngModel)]="form.trialDays" /></sd-form-field>
          <sd-form-field label="Sort order"><input sdInput type="number" name="so" [(ngModel)]="form.sortOrder" /></sd-form-field>
        </div>
        <fieldset>
          <legend>Limits <span class="muted small">(empty = unlimited)</span></legend>
          <div class="form-grid">
            @for (l of limits; track l.key) {
              <sd-form-field [label]="l.label" [error]="err(l.key)">
                <input sdInput type="number" min="0" [name]="l.key" [ngModel]="form[l.key]" (ngModelChange)="setLimit(l.key, $event)" />
              </sd-form-field>
            }
          </div>
        </fieldset>
        <fieldset>
          <legend>Features</legend>
          <div class="checks">
            @for (f of features(); track f.key) {
              <sd-checkbox [value]="form.features.includes(f.key)" (valueChange)="toggleFeature(f.key, $event)">{{ f.label }}</sd-checkbox>
            }
          </div>
          @if (err('features'); as fe) {
            <p class="err">{{ fe }}</p>
          }
        </fieldset>
        <sd-toggle name="public" [value]="form.isPublic" (valueChange)="form.isPublic = $event">Offered at sign-up and in plan comparison</sd-toggle>
        @if (formError()) {
          <p class="err" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDrawerFooter class="footer">
        <button sdButton type="button" variant="ghost" (click)="editorOpen.set(false)">Cancel</button>
        <button sdButton type="submit" form="plan-form" variant="primary" [loading]="saving()">{{ editingId() ? 'Save' : 'Create plan' }}</button>
      </div>
    </sd-drawer>
  `,
  styles: `
    ${SAAS_CSS}
    .cards { display: grid; gap: var(--sd-space-4); grid-template-columns: repeat(auto-fill, minmax(min(100%, 300px), 1fr)); }
    .badges { display: flex; gap: 6px; }
    .price { font-weight: var(--sd-weight-semibold); margin-bottom: 4px; }
    .feats { display: flex; flex-wrap: wrap; gap: 4px; margin: 12px 0; }
    fieldset { border: 1px solid var(--sd-border); border-radius: var(--sd-radius-md); padding: 12px; margin: 0; }
    legend { padding: 0 6px; font-weight: var(--sd-weight-medium); }
    .checks { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(100%, 200px), 1fr)); gap: 6px; }
  `,
})
export class PlatformPlansPage implements OnInit {
  private readonly api = inject(PlatformApi);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(SdDialogService);

  protected readonly plans = signal<PlanDto[] | null>(null);
  protected readonly features = signal<PlanFeatureDto[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly editorOpen = signal(false);
  protected readonly editingId = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string | null>>({});
  protected readonly formError = signal<string | null>(null);
  protected form: SavePlanRequest = this.blank();
  protected readonly money = money;

  protected readonly limits: { key: LimitKey; label: string }[] = [
    { key: 'maxAgents', label: 'Agents' },
    { key: 'maxLightAgents', label: 'Light agents' },
    { key: 'maxPortalUsers', label: 'Portal users' },
    { key: 'storageMb', label: 'Storage (MB)' },
    { key: 'maxChannels', label: 'Channels' },
    { key: 'aiTokensMonthly', label: 'AI tokens / month' },
    { key: 'apiCallsMonthly', label: 'API calls / month' },
    { key: 'maxAttachmentMb', label: 'Attachment size (MB)' },
    { key: 'maxBrands', label: 'Brands' },
  ];

  ngOnInit(): void {
    this.load();
    this.api.planFeatures().subscribe({ next: (f) => this.features.set(f), error: () => undefined });
  }

  protected load(): void {
    this.error.set(null);
    this.api.plans().subscribe({ next: (p) => this.plans.set(p), error: (e: ApiError) => this.error.set(e.userMessage) });
  }

  private blank(): SavePlanRequest {
    return {
      code: '', name: '', description: null, monthlyPricePerAgent: 0, annualPricePerAgent: 0, currency: 'USD', isPublic: true, trialDays: 0,
      maxAgents: null, maxLightAgents: null, maxPortalUsers: null, storageMb: null, maxChannels: null, aiTokensMonthly: null, maxAttachmentMb: null,
      apiCallsMonthly: null, maxBrands: null, features: [], sortOrder: 10,
    };
  }

  protected edit(p: PlanDto | null): void {
    this.errors.set({});
    this.formError.set(null);
    this.editingId.set(p?.id ?? null);
    this.form = p
      ? {
          code: p.code, name: p.name, description: p.description, monthlyPricePerAgent: p.monthlyPricePerAgent, annualPricePerAgent: p.annualPricePerAgent,
          currency: p.currency, isPublic: p.isPublic, trialDays: p.trialDays, maxAgents: p.maxAgents, maxLightAgents: p.maxLightAgents,
          maxPortalUsers: p.maxPortalUsers, storageMb: p.storageMb, maxChannels: p.maxChannels, aiTokensMonthly: p.aiTokensMonthly,
          maxAttachmentMb: p.maxAttachmentMb, apiCallsMonthly: p.apiCallsMonthly, maxBrands: p.maxBrands, features: [...p.features], sortOrder: p.sortOrder,
        }
      : this.blank();
    this.editorOpen.set(true);
  }

  protected setLimit(key: LimitKey, value: unknown): void {
    this.form = { ...this.form, [key]: value === '' || value === null || value === undefined ? null : Number(value) };
  }

  protected toggleFeature(key: string, on: boolean): void {
    this.form = { ...this.form, features: on ? [...new Set([...this.form.features, key])] : this.form.features.filter((f) => f !== key) };
  }

  protected err(key: string): string | null {
    return this.errors()[key] ?? null;
  }

  protected save(): void {
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    const id = this.editingId();
    const request = { ...this.form, monthlyPricePerAgent: Number(this.form.monthlyPricePerAgent), annualPricePerAgent: Number(this.form.annualPricePerAgent),
      trialDays: Number(this.form.trialDays), sortOrder: Number(this.form.sortOrder), currency: this.form.currency.toUpperCase() };
    (id ? this.api.updatePlan(id, request) : this.api.createPlan(request)).subscribe({
      next: () => {
        this.saving.set(false);
        this.editorOpen.set(false);
        this.toast.success(id ? 'Plan saved' : 'Plan created');
        this.load();
      },
      error: (e: ApiError) => {
        this.saving.set(false);
        const map: Record<string, string | null> = {};
        for (const k of Object.keys(e.errors ?? {})) map[k] = e.fieldError(k);
        this.errors.set(map);
        if (Object.keys(map).length === 0) this.formError.set(e.userMessage);
      },
    });
  }

  protected async remove(p: PlanDto): Promise<void> {
    if (!(await this.dialog.confirm({ title: `Delete plan ${p.name}?`, message: 'The plan is removed permanently.', danger: true, confirmLabel: 'Delete' }))) return;
    this.api.deletePlan(p.id).subscribe({
      next: () => {
        this.toast.success('Plan deleted');
        this.load();
      },
      error: (e: ApiError) => this.toast.error('Could not delete', e.userMessage),
    });
  }

  protected featureLabel(key: string): string {
    return this.features().find((f) => f.key === key)?.label ?? key;
  }
}
