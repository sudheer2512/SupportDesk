import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { forkJoin } from 'rxjs';
import { ApiError, MetaApi, SaveTicketLifecycleRequest, TicketConfigApi, TicketDisplayDto, TicketLifecycleDto } from '@sd/core';
import { SdButton, SdEmptyState, SdFormField, SdIcon, SdInput, SdSkeleton, SdStatusPill, SdToggle, ToastService } from '@sd/ui';
import { ADMIN_EDITOR_STYLES, AdminLeaveGuarded, sameValue, saveErrors, warnOnUnload } from '../admin-shared';

/** Numbering (prefix; next number read-only), lifecycle rules (reopen, auto-close, merge/split, CSAT) and priority/severity display. */
@Component({
  selector: 'desk-admin-ticket-settings',
  imports: [FormsModule, SdButton, SdIcon, SdFormField, SdInput, SdToggle, SdEmptyState, SdSkeleton, SdStatusPill],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading()) {
      <sd-skeleton [lines]="10" height="36px" />
    } @else if (loadError()) {
      <sd-empty-state icon="circle-alert" title="Settings could not be loaded" [message]="loadError()">
        <button sdButton icon="refresh" (click)="load()">Try again</button>
      </sd-empty-state>
    } @else if (form(); as f) {
      @if (formError()) {
        <div class="callout danger" role="alert"><sd-icon name="circle-alert" [size]="16" /><div>{{ formError() }}</div></div>
      }
      <section class="card" aria-labelledby="num-h">
        <h2 id="num-h">Ticket numbering</h2>
        <p class="sd-muted">Numbers are gap-free per organisation. Changing the prefix changes how every ticket number is displayed.</p>
        <div class="grid3">
          <sd-form-field label="Prefix" [error]="errors()['numberPrefix']" hint="Up to 10 characters, e.g. #, SD- or WS/">
            <input sdInput name="prefix" class="mono" [ngModel]="f.numberPrefix" (ngModelChange)="patch({ numberPrefix: $event })" maxlength="10" />
          </sd-form-field>
          <sd-form-field label="Next ticket number" hint="Read-only — assigned automatically.">
            <input sdInput name="next" class="mono" [value]="lifecycle()?.nextNumber" readonly />
          </sd-form-field>
          <div class="sample" aria-live="polite">
            <span class="sd-muted small">Next ticket will show as</span>
            <strong class="mono">{{ f.numberPrefix }}{{ lifecycle()?.nextNumber }}</strong>
          </div>
        </div>
      </section>

      <section class="card" aria-labelledby="life-h">
        <h2 id="life-h">Lifecycle</h2>
        <p class="sd-muted">Reopening, automatic closure and resolution rules.</p>
        <div class="grid3">
          <sd-form-field label="Reopen window (days)" [error]="errors()['reopenWindowDays']" hint="Customer replies within this window reopen the ticket; later replies create a follow-up.">
            <input sdInput type="number" name="reopen" min="0" max="365" [ngModel]="f.reopenWindowDays" (ngModelChange)="patch({ reopenWindowDays: +$event })" />
          </sd-form-field>
          <sd-form-field label="Auto-close resolved tickets after (days)" [error]="errors()['autoCloseResolvedAfterDays']" hint="0 = never close automatically.">
            <input sdInput type="number" name="autoClose" min="0" max="365" [ngModel]="f.autoCloseResolvedAfterDays" (ngModelChange)="patch({ autoCloseResolvedAfterDays: +$event })" />
          </sd-form-field>
          <sd-form-field label="Soft lock (minutes)" [error]="errors()['softLockMinutes']" hint="How long an agent's edit lock lasts without activity.">
            <input sdInput type="number" name="lock" min="1" max="240" [ngModel]="f.softLockMinutes" (ngModelChange)="patch({ softLockMinutes: +$event })" />
          </sd-form-field>
        </div>
        <div class="toggles">
          <sd-toggle [value]="f.allowCustomerReopen" (valueChange)="patch({ allowCustomerReopen: $event })">Customers may reopen resolved tickets (within the window)</sd-toggle>
          <sd-toggle [value]="f.requireCustomerConfirmation" (valueChange)="patch({ requireCustomerConfirmation: $event })">Ask customers to confirm a resolution</sd-toggle>
          <sd-toggle [value]="f.requireResolutionCode" (valueChange)="patch({ requireResolutionCode: $event })">Require a resolution code when resolving</sd-toggle>
          <sd-toggle [value]="f.autoAcknowledge" (valueChange)="patch({ autoAcknowledge: $event })">Acknowledge new e-mail tickets automatically (where the mailbox allows it)</sd-toggle>
        </div>
      </section>

      <section class="card" aria-labelledby="merge-h">
        <h2 id="merge-h">Merge &amp; split</h2>
        <p class="sd-muted">Rules applied when agents merge duplicates or split messages into a new ticket.</p>
        <div class="grid3">
          <sd-form-field label="Most tickets per merge" [error]="errors()['maxMergeSources']">
            <input sdInput type="number" name="maxMerge" min="1" max="50" [ngModel]="f.maxMergeSources" (ngModelChange)="patch({ maxMergeSources: +$event })" />
          </sd-form-field>
        </div>
        <div class="toggles">
          <sd-toggle [value]="f.allowMergeAcrossCustomers" (valueChange)="patch({ allowMergeAcrossCustomers: $event })">Allow merging tickets of different customers</sd-toggle>
          <sd-toggle [value]="f.allowMergeAcrossDepartments" (valueChange)="patch({ allowMergeAcrossDepartments: $event })">Allow merging tickets of different departments</sd-toggle>
          <sd-toggle [value]="f.allowSplit" (valueChange)="patch({ allowSplit: $event })">Allow splitting messages into a new ticket</sd-toggle>
        </div>
      </section>

      <section class="card" aria-labelledby="csat-h">
        <h2 id="csat-h">Satisfaction surveys</h2>
        <div class="grid3">
          <div class="toggles"><sd-toggle [value]="f.csatEnabled" (valueChange)="patch({ csatEnabled: $event })">Send a survey when a ticket is resolved</sd-toggle></div>
          <sd-form-field label="Survey link valid for (days)" [error]="errors()['csatExpiryDays']">
            <input sdInput type="number" name="csatDays" min="1" max="90" [ngModel]="f.csatExpiryDays" (ngModelChange)="patch({ csatExpiryDays: +$event })" [disabled]="!f.csatEnabled" />
          </sd-form-field>
        </div>
      </section>

      @if (display(); as d) {
        <section class="card" aria-labelledby="disp-h">
          <h2 id="disp-h">Priorities &amp; severities</h2>
          <p class="sd-muted">The values are fixed (reports and SLAs rely on them); their labels and colours are yours.</p>
          @if (errors()['priorities'] || errors()['severities']) {
            <p class="err" role="alert">{{ errors()['priorities'] || errors()['severities'] }}</p>
          }
          <div class="grid2">
            @for (kind of displayKinds; track kind.key) {
              <fieldset>
                <legend>{{ kind.title }}</legend>
                @for (o of d[kind.key]; track o.value; let i = $index) {
                  <div class="disp">
                    <span class="mono sd-muted val">{{ o.value }}</span>
                    <input sdInput size="sm" [attr.aria-label]="kind.title + ' ' + o.value + ' label'" [ngModel]="o.label" (ngModelChange)="patchDisplay(kind.key, i, { label: $event })" maxlength="40" />
                    <input type="color" class="swatchpick" [attr.aria-label]="kind.title + ' ' + o.value + ' colour'" [ngModel]="o.color" (ngModelChange)="patchDisplay(kind.key, i, { color: $event })" />
                    <sd-status-pill class="pill" [label]="o.label" [color]="o.color" />
                  </div>
                }
              </fieldset>
            }
          </div>
        </section>
      }

      <div class="savebar">
        @if (dirty()) {
          <span class="sd-muted small"><sd-icon name="circle-dot" [size]="12" /> Unsaved changes</span>
        } @else {
          <span class="sd-muted small">All changes saved{{ lifecycle() ? ' · version ' + lifecycle()!.version : '' }}</span>
        }
        <span class="sd-spacer"></span>
        <button sdButton variant="ghost" [disabled]="!dirty() || saving()" (click)="discard()">Discard</button>
        <button sdButton variant="primary" icon="check" [loading]="saving()" [disabled]="!dirty()" (click)="save()">Save settings</button>
      </div>
    }
  `,
  styles: `
    ${ADMIN_EDITOR_STYLES}
    .sample { display: flex; flex-direction: column; justify-content: center; gap: 2px; padding: 8px 12px; border-radius: var(--sd-radius-md); background: var(--sd-surface-sunken); }
    .sample strong { font-size: var(--sd-text-lg); }
    .toggles { display: flex; flex-direction: column; gap: 8px; margin-top: var(--sd-space-3); }
    .disp { display: grid; grid-template-columns: 70px minmax(0, 1fr) 44px auto; align-items: center; gap: 8px; margin-bottom: 6px; }
    .val { font-size: var(--sd-text-xs); }
    .swatchpick { width: 40px; height: 30px; padding: 2px; border: 1px solid var(--sd-border); border-radius: var(--sd-radius-sm); background: var(--sd-surface); }
    .pill { justify-self: start; }
    @media (max-width: 760px) { .disp { grid-template-columns: 60px minmax(0, 1fr) 44px; } .pill { display: none; } }
  `,
})
export class TicketSettingsPage implements OnInit, AdminLeaveGuarded {
  private readonly api = inject(TicketConfigApi);
  private readonly meta = inject(MetaApi);
  private readonly toast = inject(ToastService);

  protected readonly displayKinds = [
    { key: 'priorities' as const, title: 'Priorities' },
    { key: 'severities' as const, title: 'Severities' },
  ];
  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);
  protected readonly lifecycle = signal<TicketLifecycleDto | null>(null);
  protected readonly form = signal<SaveTicketLifecycleRequest | null>(null);
  protected readonly display = signal<TicketDisplayDto | null>(null);
  private readonly originalDisplay = signal<TicketDisplayDto | null>(null);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);

  private readonly lifecycleDirty = computed(() => !!this.lifecycle() && !sameValue(this.form(), this.toRequest(this.lifecycle()!)));
  private readonly displayDirty = computed(() => !sameValue(this.display(), this.originalDisplay()));
  protected readonly dirty = computed(() => this.lifecycleDirty() || this.displayDirty());

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
    forkJoin({ lifecycle: this.api.lifecycle(), display: this.api.display() }).subscribe({
      next: ({ lifecycle, display }) => {
        this.applyLifecycle(lifecycle);
        this.applyDisplay(display);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loadError.set(ApiError.from(err).userMessage);
        this.loading.set(false);
      },
    });
  }

  protected patch(change: Partial<SaveTicketLifecycleRequest>): void {
    this.form.update((f) => (f ? { ...f, ...change } : f));
  }

  protected patchDisplay(kind: 'priorities' | 'severities', index: number, change: { label?: string; color?: string }): void {
    this.display.update((d) => (d ? { ...d, [kind]: d[kind].map((o, i) => (i === index ? { ...o, ...change } : o)) } : d));
  }

  protected discard(): void {
    const l = this.lifecycle();
    if (l) this.form.set(this.toRequest(l));
    this.display.set(structuredClone(this.originalDisplay()));
    this.errors.set({});
    this.formError.set(null);
  }

  protected save(): void {
    const f = this.form();
    const d = this.display();
    if (!f || !d) return;
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    const calls = {
      lifecycle: this.lifecycleDirty() ? this.api.saveLifecycle(f) : this.api.lifecycle(),
      display: this.displayDirty() ? this.api.saveDisplay(d) : this.api.display(),
    };
    forkJoin(calls).subscribe({
      next: ({ lifecycle, display }) => {
        this.saving.set(false);
        this.applyLifecycle(lifecycle);
        this.applyDisplay(display);
        this.meta.refresh();
        this.toast.success('Ticket settings saved');
      },
      error: (err: unknown) => {
        this.saving.set(false);
        const { fields, message } = saveErrors(err, 'The settings could not be saved.');
        this.errors.set(fields);
        this.formError.set(message);
      },
    });
  }

  private applyLifecycle(l: TicketLifecycleDto): void {
    this.lifecycle.set(l);
    this.form.set(this.toRequest(l));
  }

  private applyDisplay(d: TicketDisplayDto): void {
    this.display.set(structuredClone(d));
    this.originalDisplay.set(structuredClone(d));
  }

  private toRequest(l: TicketLifecycleDto): SaveTicketLifecycleRequest {
    return {
      numberPrefix: l.numberPrefix,
      reopenWindowDays: l.reopenWindowDays,
      allowCustomerReopen: l.allowCustomerReopen,
      autoCloseResolvedAfterDays: l.autoCloseResolvedAfterDays,
      requireCustomerConfirmation: l.requireCustomerConfirmation,
      requireResolutionCode: l.requireResolutionCode,
      softLockMinutes: l.softLockMinutes,
      allowMergeAcrossCustomers: l.allowMergeAcrossCustomers,
      allowMergeAcrossDepartments: l.allowMergeAcrossDepartments,
      maxMergeSources: l.maxMergeSources,
      allowSplit: l.allowSplit,
      csatEnabled: l.csatEnabled,
      csatExpiryDays: l.csatExpiryDays,
      autoAcknowledge: l.autoAcknowledge,
    };
  }
}
