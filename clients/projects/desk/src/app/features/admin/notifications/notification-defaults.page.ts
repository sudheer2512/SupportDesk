import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminNotificationsApi, ApiError, NotificationEventDefaultDto, NotificationPolicyDto, SaveNotificationPolicyRequest, timeZoneOptions } from '@sd/core';
import { SdButton, SdCheckbox, SdEmptyState, SdFormField, SdIcon, SdInput, SdSkeleton, SdToggle, ToastService } from '@sd/ui';
import { ADMIN_EDITOR_STYLES, AdminLeaveGuarded, sameValue, saveErrors, warnOnUnload } from '../admin-shared';

type Channel = 'inApp' | 'email' | 'push' | 'digest';

const CHANNELS: { key: Channel; label: string; hint: string }[] = [
  { key: 'inApp', label: 'In-app', hint: 'Bell icon in the desk' },
  { key: 'email', label: 'E-mail', hint: 'Sent right away (after quiet hours)' },
  { key: 'push', label: 'Push', hint: 'Mobile app' },
  { key: 'digest', label: 'Digest', hint: 'Bundled into the digest e-mail' },
];

/**
 * Tenant notification defaults: event × channel matrix (used for every agent who has not chosen their own
 * preferences under Profile → Notifications), default quiet hours and digest schedule.
 */
@Component({
  selector: 'desk-admin-notification-defaults',
  imports: [FormsModule, SdButton, SdCheckbox, SdToggle, SdIcon, SdFormField, SdInput, SdEmptyState, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading()) {
      <sd-skeleton [lines]="10" height="34px" />
    } @else if (loadError()) {
      <sd-empty-state icon="circle-alert" title="Notification defaults could not be loaded" [message]="loadError()">
        <button sdButton icon="refresh" (click)="load()">Try again</button>
      </sd-empty-state>
    } @else if (form(); as f) {
      @if (formError()) {
        <div class="callout danger" role="alert"><sd-icon name="circle-alert" [size]="16" /><div>{{ formError() }}</div></div>
      }
      <div class="callout" role="note"><sd-icon name="info" [size]="16" /><div>Agents can override these defaults for themselves. Critical alerts ignore quiet hours. When both e-mail and digest are on, the event goes into the digest.</div></div>

      <section class="card matrix-card" aria-labelledby="mx-h">
        <h2 id="mx-h">Default channels per event</h2>
        <div class="scroll">
          <table class="matrix">
            <thead>
              <tr>
                <th scope="col">Event</th>
                @for (c of channels; track c.key) {
                  <th scope="col">
                    <sd-checkbox [value]="columnState(c.key) === 'all'" [indeterminate]="columnState(c.key) === 'some'" (valueChange)="setColumn(c.key, $event)"
                      [ariaLabel]="'All events by ' + c.label">{{ c.label }}</sd-checkbox>
                    <span class="hint">{{ c.hint }}</span>
                  </th>
                }
              </tr>
            </thead>
            <tbody>
              @for (e of f.events; track e.eventType; let i = $index) {
                <tr>
                  <th scope="row"><strong>{{ e.label }}</strong><span class="hint">{{ e.description }}</span></th>
                  @for (c of channels; track c.key) {
                    <td><sd-checkbox [value]="e[c.key]" (valueChange)="setCell(i, c.key, $event)" [ariaLabel]="e.label + ' by ' + c.label" /></td>
                  }
                </tr>
              }
            </tbody>
          </table>
        </div>
        @if (errors()['events']) {
          <p class="err">{{ errors()['events'] }}</p>
        }
      </section>

      <section class="card" aria-labelledby="qh-h">
        <h2 id="qh-h">Quiet hours &amp; digests</h2>
        <p class="sd-muted">Defaults for agents who have not set their own. E-mail and push wait until quiet hours end.</p>
        <sd-toggle [value]="quietOn()" (valueChange)="setQuiet($event)">Quiet hours</sd-toggle>
        <div class="grid3 gap">
          @if (quietOn()) {
            <sd-form-field label="From" [error]="errors()['quietHoursStart']">
              <input sdInput type="time" name="qs" [ngModel]="f.quietHoursStart" (ngModelChange)="patch({ quietHoursStart: $event })" />
            </sd-form-field>
            <sd-form-field label="Until" [error]="errors()['quietHoursEnd']">
              <input sdInput type="time" name="qe" [ngModel]="f.quietHoursEnd" (ngModelChange)="patch({ quietHoursEnd: $event })" />
            </sd-form-field>
          }
          <sd-form-field label="Time zone" [error]="errors()['timeZone']">
            <select sdInput name="tz" [ngModel]="f.timeZone" (ngModelChange)="patch({ timeZone: $event })">
              @for (z of zones(); track z) {
                <option [value]="z">{{ z }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Digest frequency" [error]="errors()['digestFrequency']">
            <select sdInput name="freq" [ngModel]="f.digestFrequency" (ngModelChange)="patch({ digestFrequency: $event })">
              <option value="Hourly">Hourly</option>
              <option value="Daily">Daily</option>
            </select>
          </sd-form-field>
          @if (f.digestFrequency === 'Daily') {
            <sd-form-field label="Daily digest at" [error]="errors()['dailyDigestAt']">
              <input sdInput type="time" name="dd" [ngModel]="f.dailyDigestAt" (ngModelChange)="patch({ dailyDigestAt: $event })" />
            </sd-form-field>
          }
        </div>
      </section>

      <div class="savebar">
        @if (dirty()) {
          <span class="sd-muted small"><sd-icon name="circle-dot" [size]="12" /> Unsaved changes</span>
        } @else {
          <span class="sd-muted small">All changes saved{{ version() ? ' · version ' + version() : '' }}</span>
        }
        <span class="sd-spacer"></span>
        <button sdButton variant="ghost" [disabled]="!dirty() || saving()" (click)="discard()">Discard</button>
        <button sdButton variant="primary" icon="check" [loading]="saving()" [disabled]="!dirty()" (click)="save()">Save defaults</button>
      </div>
    }
  `,
  styles: `
    ${ADMIN_EDITOR_STYLES}
    .callout { margin-bottom: var(--sd-space-4); }
    .scroll { overflow-x: auto; }
    .matrix { width: 100%; min-width: 620px; border-collapse: collapse; font-size: var(--sd-text-sm); }
    .matrix th, .matrix td { padding: 8px 10px; border-bottom: 1px solid var(--sd-divider); text-align: left; vertical-align: middle; }
    .matrix thead th { font-weight: var(--sd-weight-semibold); vertical-align: bottom; }
    .matrix tbody th { font-weight: var(--sd-weight-regular); }
    .matrix td { width: 120px; }
    .hint { display: block; color: var(--sd-text-3); font-size: var(--sd-text-xs); font-weight: var(--sd-weight-regular); }
    .gap { margin-top: var(--sd-space-3); }
  `,
})
export class NotificationDefaultsPage implements OnInit, AdminLeaveGuarded {
  private readonly api = inject(AdminNotificationsApi);
  private readonly toast = inject(ToastService);

  protected readonly channels = CHANNELS;
  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);
  protected readonly form = signal<SaveNotificationPolicyRequest | null>(null);
  private readonly original = signal<SaveNotificationPolicyRequest | null>(null);
  protected readonly version = signal(0);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);

  protected readonly dirty = computed(() => !sameValue(this.form(), this.original()));
  protected readonly quietOn = computed(() => !!this.form()?.quietHoursStart || !!this.form()?.quietHoursEnd);
  protected readonly zones = computed(() => timeZoneOptions(this.form()?.timeZone));

  constructor() {
    warnOnUnload(this.dirty);
  }

  ngOnInit(): void {
    this.load();
  }

  hasUnsavedChanges(): boolean {
    return this.dirty() && !this.saving();
  }

  protected load(): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.api.defaults().subscribe({
      next: (p) => {
        this.apply(p);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loadError.set(ApiError.from(err).userMessage);
        this.loading.set(false);
      },
    });
  }

  protected patch(change: Partial<SaveNotificationPolicyRequest>): void {
    this.form.update((f) => (f ? { ...f, ...change } : f));
  }

  protected setCell(index: number, channel: Channel, on: boolean): void {
    this.form.update((f) => (f ? { ...f, events: f.events.map((e, i) => (i === index ? { ...e, [channel]: on } : e)) } : f));
  }

  protected columnState(channel: Channel): 'none' | 'some' | 'all' {
    const events = this.form()?.events ?? [];
    const n = events.filter((e) => e[channel]).length;
    return n === 0 ? 'none' : n === events.length ? 'all' : 'some';
  }

  protected setColumn(channel: Channel, on: boolean): void {
    this.form.update((f) => (f ? { ...f, events: f.events.map((e): NotificationEventDefaultDto => ({ ...e, [channel]: on })) } : f));
  }

  protected setQuiet(on: boolean): void {
    this.patch(on ? { quietHoursStart: '20:00', quietHoursEnd: '08:00' } : { quietHoursStart: null, quietHoursEnd: null });
  }

  protected discard(): void {
    this.form.set(structuredClone(this.original()));
    this.errors.set({});
    this.formError.set(null);
  }

  protected save(): void {
    const f = this.form();
    if (!f) return;
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    this.api.saveDefaults(f).subscribe({
      next: (p) => {
        this.saving.set(false);
        this.apply(p);
        this.toast.success('Notification defaults saved');
      },
      error: (err: unknown) => {
        this.saving.set(false);
        const { fields, message } = saveErrors(err, 'The defaults could not be saved.');
        this.errors.set(fields);
        this.formError.set(message);
      },
    });
  }

  private apply(p: NotificationPolicyDto): void {
    const f: SaveNotificationPolicyRequest = {
      events: p.events.map((e) => ({ ...e })),
      quietHoursStart: p.quietHoursStart,
      quietHoursEnd: p.quietHoursEnd,
      timeZone: p.timeZone,
      digestFrequency: p.digestFrequency,
      dailyDigestAt: p.dailyDigestAt,
    };
    this.form.set(f);
    this.original.set(structuredClone(f));
    this.version.set(p.version);
  }
}
