import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { PortalConsentDto, PortalPreferencesDto, PortalTicketsApi } from '@sd/core';
import { SdButton, SdFormField, SdIcon, SdInput, SdRadio, SdRadioGroup, SdSkeleton, SdToggle, ToastService } from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { BrandService } from '../../brand.service';
import { ErrorStateComponent } from '../../shared/error-state.component';
import { browserLanguage, errorMessage, localeName } from '../../shared/portal-utils';

const CHANNELS = ['Email', 'WhatsApp', 'Sms'];
const PURPOSES = ['Transactional', 'Marketing', 'Surveys'];
const CHANNEL_LABELS: Record<string, string> = { Email: 'E-mail', WhatsApp: 'WhatsApp', Sms: 'SMS' };
const PURPOSE_TEXT: Record<string, { label: string; hint: string }> = {
  Transactional: { label: 'Ticket updates', hint: 'Replies, status changes and confirmations about your requests.' },
  Marketing: { label: 'News and offers', hint: 'Product news, events and promotions.' },
  Surveys: { label: 'Surveys', hint: 'Short feedback requests after we help you.' },
};

/** Preferred channel and language, and consent per channel × purpose (GET/PUT …/account/preferences). */
@Component({
  selector: 'portal-notifications-tab',
  imports: [SdButton, SdIcon, SdFormField, SdInput, SdRadioGroup, SdRadio, SdToggle, SdSkeleton, ErrorStateComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (res.error()) {
      <portal-error-state [error]="res.error()" compact (retry)="res.reload()" />
    } @else if (loaded()) {
      <form class="form" (submit)="$event.preventDefault(); save()" novalidate>
        @if (error()) {
          <p class="portal-callout portal-callout--danger" role="alert"><sd-icon name="circle-alert" [size]="15" />{{ error() }}</p>
        }
        <div class="row">
          <sd-form-field label="Preferred channel" hint="How we contact you first about your tickets.">
            <sd-radio-group [value]="channel()" (valueChange)="channel.set($event)" direction="row">
              <sd-radio value="">No preference</sd-radio>
              @for (c of channels(); track c) {
                <sd-radio [value]="c">{{ channelLabel(c) }}</sd-radio>
              }
            </sd-radio-group>
          </sd-form-field>
          <sd-form-field label="Language">
            <select sdInput (change)="language.set($any($event.target).value)">
              @for (l of languages(); track l) {
                <option [value]="l" [selected]="l === language()">{{ languageLabel(l) }}</option>
              }
            </select>
          </sd-form-field>
        </div>

        <div class="table-wrap">
          <table>
            <caption>
              What we may send you
            </caption>
            <thead>
              <tr>
                <th scope="col">Message type</th>
                @for (c of channels(); track c) {
                  <th scope="col" class="center">{{ channelLabel(c) }}</th>
                }
              </tr>
            </thead>
            <tbody>
              @for (p of purposes(); track p) {
                <tr>
                  <th scope="row">
                    <span class="purpose">{{ purposeLabel(p) }}</span>
                    <span class="hint">{{ purposeHint(p) }}</span>
                  </th>
                  @for (c of channels(); track c) {
                    <td class="center">
                      <sd-toggle [value]="isOn(c, p)" (valueChange)="setConsent(c, p, $event)" [ariaLabel]="purposeLabel(p) + ' by ' + channelLabel(c)" />
                    </td>
                  }
                </tr>
              }
            </tbody>
          </table>
        </div>

        <div class="actions">
          <button sdButton variant="primary" type="submit" [loading]="busy()" [disabled]="!dirty()">Save preferences</button>
        </div>
      </form>
    } @else {
      <div class="loading" aria-busy="true" aria-label="Loading preferences">
        <sd-skeleton height="60px" />
        <sd-skeleton height="160px" />
      </div>
    }
  `,
  styles: `
    .form {
      display: grid;
      gap: 18px;
    }
    .row {
      display: grid;
      grid-template-columns: minmax(0, 2fr) minmax(0, 1fr);
      gap: 16px;
      align-items: start;
    }
    .table-wrap {
      overflow-x: auto;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    table {
      width: 100%;
      border-collapse: collapse;
    }
    caption {
      padding: 12px 14px 4px;
      font-weight: var(--sd-weight-semibold);
      text-align: left;
    }
    th,
    td {
      padding: 12px 14px;
      border-bottom: 1px solid var(--sd-divider);
      text-align: left;
      vertical-align: middle;
    }
    thead th {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
    }
    tbody tr:last-child th,
    tbody tr:last-child td {
      border-bottom: 0;
    }
    tbody th {
      font-weight: normal;
    }
    .center {
      text-align: center;
    }
    td sd-toggle {
      display: inline-flex;
    }
    .purpose {
      display: block;
      font-weight: var(--sd-weight-medium);
    }
    .hint {
      display: block;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .actions {
      display: flex;
      justify-content: flex-end;
    }
    .loading {
      display: grid;
      gap: 14px;
    }
    @media (max-width: 720px) {
      .row {
        grid-template-columns: minmax(0, 1fr);
      }
    }
  `,
})
export class NotificationsTabComponent {
  private readonly api = inject(PortalTicketsApi);
  private readonly brand = inject(BrandService);
  private readonly toast = inject(ToastService);

  protected readonly res = rxResource({ params: () => this.brand.slug() ?? undefined, stream: ({ params }) => this.api.preferences(params) });
  private readonly saved = signal<PortalPreferencesDto | null>(null);
  protected readonly loaded = computed(() => this.saved() !== null);
  protected readonly channel = signal<string | null>('');
  protected readonly language = signal('');
  protected readonly consents = signal<PortalConsentDto[]>([]);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly channels = computed(() => unique([...CHANNELS, ...this.consents().map((c) => c.channel)]));
  protected readonly purposes = computed(() => unique([...PURPOSES, ...this.consents().map((c) => c.purpose)]));
  protected readonly languages = computed(() => {
    const set = new Set([...this.brand.locales(), this.language()].filter(Boolean));
    if (!set.size) set.add(browserLanguage());
    return [...set];
  });
  protected readonly dirty = computed(() => {
    const s = this.saved();
    if (!s) return false;
    return (
      (s.preferredChannel ?? '') !== (this.channel() ?? '') ||
      s.preferredLanguage !== this.language() ||
      JSON.stringify(normalise(s.consents)) !== JSON.stringify(normalise(this.consents()))
    );
  });

  constructor() {
    effect(() => {
      const p = this.res.hasValue() ? this.res.value() : null;
      if (p) untracked(() => this.apply(p));
    });
  }

  protected channelLabel(c: string): string {
    return CHANNEL_LABELS[c] ?? c;
  }

  protected purposeLabel(p: string): string {
    return PURPOSE_TEXT[p]?.label ?? p;
  }

  protected purposeHint(p: string): string {
    return PURPOSE_TEXT[p]?.hint ?? '';
  }

  protected languageLabel(code: string): string {
    return `${localeName(code)} (${code})`;
  }

  protected isOn(channel: string, purpose: string): boolean {
    return this.consents().some((c) => c.channel === channel && c.purpose === purpose && c.optedIn);
  }

  protected setConsent(channel: string, purpose: string, optedIn: boolean): void {
    this.consents.update((list) => {
      const exists = list.some((c) => c.channel === channel && c.purpose === purpose);
      return exists
        ? list.map((c) => (c.channel === channel && c.purpose === purpose ? { ...c, optedIn } : c))
        : [...list, { channel, purpose, optedIn }];
    });
  }

  protected async save(): Promise<void> {
    const slug = this.brand.slug();
    if (!slug || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const updated = await firstValueFrom(
        this.api.updatePreferences(slug, {
          preferredChannel: this.channel() || null,
          preferredLanguage: this.language() || null,
          consents: this.consents(),
        }),
      );
      this.apply(updated);
      this.toast.success('Preferences saved');
    } catch (err) {
      this.error.set(errorMessage(err, 'Your preferences could not be saved.'));
    } finally {
      this.busy.set(false);
    }
  }

  private apply(p: PortalPreferencesDto): void {
    this.saved.set(p);
    this.channel.set(p.preferredChannel ?? '');
    this.language.set(p.preferredLanguage);
    this.consents.set(p.consents.map((c) => ({ ...c })));
  }
}

function unique(values: string[]): string[] {
  return [...new Set(values)];
}

function normalise(list: PortalConsentDto[]): string[] {
  return list.filter((c) => c.optedIn).map((c) => `${c.channel}:${c.purpose}`).sort();
}
