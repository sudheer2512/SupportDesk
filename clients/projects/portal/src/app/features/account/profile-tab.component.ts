import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { ApiError, PortalAuthService, PortalProfileDto, PortalTicketsApi, timeZoneOptions } from '@sd/core';
import { SdButton, SdFormField, SdIcon, SdInput, SdRelativeTimePipe, SdSkeleton, ToastService } from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { BrandService } from '../../brand.service';
import { ErrorStateComponent } from '../../shared/error-state.component';
import { browserLanguage, errorMessage, localeName } from '../../shared/portal-utils';
import { scopeLabel } from './account-utils';

interface ProfileDraft {
  fullName: string;
  phone: string;
  mobile: string;
  jobTitle: string;
  preferredLanguage: string;
  timeZone: string;
}

/** Edit name, phone numbers, job title, language and time zone (the e-mail address is read-only). */
@Component({
  selector: 'portal-profile-tab',
  imports: [SdButton, SdIcon, SdFormField, SdInput, SdSkeleton, SdRelativeTimePipe, ErrorStateComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (res.error()) {
      <portal-error-state [error]="res.error()" compact (retry)="res.reload()" />
    } @else if (profile(); as p) {
      <div class="layout">
        <form class="form" (submit)="$event.preventDefault(); save(p)" novalidate>
          @if (error()) {
            <p class="portal-callout portal-callout--danger" role="alert"><sd-icon name="circle-alert" [size]="15" />{{ error() }}</p>
          }
          <div class="grid">
            <sd-form-field label="Full name" required [error]="errors()['fullName']">
              <input sdInput name="fullName" autocomplete="name" maxlength="200" [value]="draft().fullName" (input)="patch('fullName', $event)" />
            </sd-form-field>
            <sd-form-field label="E-mail" hint="Contact support to change your sign-in e-mail.">
              <input sdInput type="email" name="email" [value]="p.email ?? ''" readonly aria-readonly="true" />
            </sd-form-field>
            <sd-form-field label="Phone" optional [error]="errors()['phone']">
              <input sdInput type="tel" name="phone" autocomplete="tel" maxlength="40" [value]="draft().phone" (input)="patch('phone', $event)" />
            </sd-form-field>
            <sd-form-field label="Mobile" optional [error]="errors()['mobile']">
              <input sdInput type="tel" name="mobile" autocomplete="tel" maxlength="40" [value]="draft().mobile" (input)="patch('mobile', $event)" />
            </sd-form-field>
            <sd-form-field label="Job title" optional>
              <input sdInput name="jobTitle" autocomplete="organization-title" maxlength="120" [value]="draft().jobTitle" (input)="patch('jobTitle', $event)" />
            </sd-form-field>
            <sd-form-field label="Language" hint="Used for e-mails and articles when available.">
              <select sdInput name="language" (change)="patch('preferredLanguage', $event)">
                @for (l of languages(); track l) {
                  <option [value]="l" [selected]="l === draft().preferredLanguage">{{ label(l) }}</option>
                }
              </select>
            </sd-form-field>
            <sd-form-field label="Time zone" optional>
              <select sdInput name="timeZone" (change)="patch('timeZone', $event)">
                <option value="" [selected]="!draft().timeZone">Use my browser's time zone</option>
                @for (z of timeZones(); track z) {
                  <option [value]="z" [selected]="z === draft().timeZone">{{ z }}</option>
                }
              </select>
            </sd-form-field>
          </div>
          <div class="actions">
            <button sdButton variant="ghost" type="button" [disabled]="!dirty() || busy()" (click)="reset(p)">Discard changes</button>
            <button sdButton variant="primary" type="submit" [loading]="busy()" [disabled]="!dirty()">Save profile</button>
          </div>
        </form>

        <aside class="info" aria-label="Your account">
          <dl>
            <dt>Organisation</dt>
            <dd>{{ p.customerName || '—' }}</dd>
            <dt>Location</dt>
            <dd>{{ p.locationName || '—' }}</dd>
            <dt>Ticket access</dt>
            <dd>{{ access(p.accessScope) }}</dd>
            @if (p.lastLoginAt) {
              <dt>Last sign-in</dt>
              <dd>{{ p.lastLoginAt | sdRelativeTime }}</dd>
            }
          </dl>
        </aside>
      </div>
    } @else {
      <div class="grid" aria-busy="true" aria-label="Loading profile">
        @for (i of [1, 2, 3, 4, 5, 6]; track i) {
          <sd-skeleton height="62px" />
        }
      </div>
    }
  `,
  styles: `
    .layout {
      display: grid;
      grid-template-columns: minmax(0, 1fr) 260px;
      gap: 24px;
      align-items: start;
    }
    .form {
      display: grid;
      gap: 16px;
    }
    .grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 14px 16px;
    }
    .actions {
      display: flex;
      justify-content: flex-end;
      gap: 8px;
    }
    input[readonly] {
      background: var(--sd-surface-2);
      color: var(--sd-text-2);
    }
    .info {
      padding: 16px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface-2);
    }
    dl {
      display: grid;
      gap: 4px;
      margin: 0;
      font-size: var(--sd-text-sm);
    }
    dt {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    dt:not(:first-child) {
      margin-top: 8px;
    }
    dd {
      margin: 0;
      font-weight: var(--sd-weight-medium);
    }
    @media (max-width: 860px) {
      .layout {
        grid-template-columns: minmax(0, 1fr);
      }
    }
    @media (max-width: 560px) {
      .grid {
        grid-template-columns: minmax(0, 1fr);
      }
    }
  `,
})
export class ProfileTabComponent {
  private readonly api = inject(PortalTicketsApi);
  private readonly brand = inject(BrandService);
  private readonly auth = inject(PortalAuthService);
  private readonly toast = inject(ToastService);

  protected readonly res = rxResource({ params: () => this.brand.slug() ?? undefined, stream: ({ params }) => this.api.profile(params) });
  protected readonly profile = signal<PortalProfileDto | null>(null);
  protected readonly draft = signal<ProfileDraft>(toDraft(null));
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly errors = signal<Record<string, string | null>>({});
  protected readonly dirty = computed(() => JSON.stringify(this.draft()) !== JSON.stringify(toDraft(this.profile())));

  protected readonly languages = computed(() => {
    const set = new Set([...this.brand.locales(), this.draft().preferredLanguage].filter(Boolean));
    if (!set.size) set.add(browserLanguage());
    return [...set];
  });
  protected readonly timeZones = computed(() => timeZoneOptions(this.draft().timeZone));

  constructor() {
    effect(() => {
      const p = this.res.hasValue() ? this.res.value() : null;
      if (p) untracked(() => this.reset(p));
    });
  }

  protected label(code: string): string {
    return `${localeName(code)} (${code})`;
  }

  protected access(scope: string): string {
    return scopeLabel(scope);
  }

  protected patch(key: keyof ProfileDraft, event: Event): void {
    const value = (event.target as HTMLInputElement | HTMLSelectElement).value;
    this.draft.update((d) => ({ ...d, [key]: value }));
  }

  protected reset(p: PortalProfileDto): void {
    this.profile.set(p);
    this.draft.set(toDraft(p));
    this.errors.set({});
    this.error.set(null);
  }

  protected async save(p: PortalProfileDto): Promise<void> {
    const slug = this.brand.slug();
    const d = this.draft();
    if (!slug || this.busy()) return;
    if (!d.fullName.trim()) {
      this.errors.set({ fullName: 'Enter your name.' });
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    this.errors.set({});
    try {
      const updated = await firstValueFrom(
        this.api.updateProfile(slug, {
          fullName: d.fullName.trim(),
          phone: d.phone.trim() || null,
          mobile: d.mobile.trim() || null,
          jobTitle: d.jobTitle.trim() || null,
          preferredLanguage: d.preferredLanguage || p.preferredLanguage,
          preferredChannel: p.preferredChannel,
          timeZone: d.timeZone || null,
        }),
      );
      this.reset(updated);
      this.auth.updateContact({ fullName: updated.fullName, preferredLanguage: updated.preferredLanguage });
      this.toast.success('Profile saved');
    } catch (err) {
      const e = ApiError.from(err);
      const fields = { fullName: e.fieldError('fullName'), phone: e.fieldError('phone'), mobile: e.fieldError('mobile') };
      this.errors.set(fields);
      this.error.set(Object.values(fields).some(Boolean) ? 'Please check the highlighted fields.' : errorMessage(err, 'Your profile could not be saved.'));
    } finally {
      this.busy.set(false);
    }
  }
}

function toDraft(p: PortalProfileDto | null): ProfileDraft {
  return {
    fullName: p?.fullName ?? '',
    phone: p?.phone ?? '',
    mobile: p?.mobile ?? '',
    jobTitle: p?.jobTitle ?? '',
    preferredLanguage: p?.preferredLanguage ?? '',
    timeZone: p?.timeZone ?? '',
  };
}
