import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ContactDetailDto, ContactMergeResultDto, ContactsApi, Guid, saveErrorMessage } from '@sd/core';
import { SdBadge, SdButton, SdCheckbox, SdDialog, SdIcon, SdRadio, SdRadioGroup, SdSkeleton } from '@sd/ui';
import { forkJoin } from 'rxjs';
import { TABLE_STYLES } from './shared/styles';

export interface MergeWizardData {
  firstId: Guid;
  secondId: Guid;
  score?: number | null;
  reasons?: string[] | null;
}

interface FieldRow {
  key: string;
  label: string;
  /** Can the merged value be preferred over the survivor's (mergeable on the server)? */
  choosable: boolean;
  get: (c: ContactDetailDto) => string | null;
}

const FIELDS: FieldRow[] = [
  { key: 'fullName', label: 'Name', choosable: true, get: (c) => c.fullName },
  { key: 'email', label: 'E-mail', choosable: true, get: (c) => c.email },
  { key: 'phone', label: 'Phone', choosable: true, get: (c) => c.phone },
  { key: 'mobile', label: 'Mobile', choosable: true, get: (c) => c.mobile },
  { key: 'whatsAppNumber', label: 'WhatsApp', choosable: true, get: (c) => c.whatsAppNumber },
  { key: 'jobTitle', label: 'Job title', choosable: true, get: (c) => c.jobTitle },
  { key: 'customerId', label: 'Customer', choosable: true, get: (c) => c.customerName },
  { key: 'locationId', label: 'Location', choosable: true, get: (c) => c.locationName },
  { key: 'customerDepartmentId', label: 'Department', choosable: true, get: (c) => c.customerDepartmentName },
  { key: 'preferredChannel', label: 'Preferred channel', choosable: true, get: (c) => c.preferredChannel },
  { key: 'preferredLanguage', label: 'Language', choosable: true, get: (c) => c.preferredLanguage },
  { key: 'timeZone', label: 'Time zone', choosable: true, get: (c) => c.timeZone },
  { key: 'vip', label: 'VIP', choosable: false, get: (c) => (c.isVip ? 'Yes' : 'No') },
  { key: 'portal', label: 'Portal access', choosable: false, get: (c) => `${c.portalAccess}${c.hasPortalPassword ? ' (password set)' : ''}` },
  { key: 'tickets', label: 'Tickets', choosable: false, get: (c) => `${c.stats.totalTickets} (${c.stats.openTickets} open)` },
  { key: 'identities', label: 'Identities', choosable: false, get: (c) => String(c.identities.length) },
];

/**
 * Contact merge wizard: (1) pick the contact that survives, (2) choose which differing values to take from the
 * contact being merged, (3) confirm. Tickets, conversations, identities, consents and CSAT move to the survivor;
 * the merge is transactional and audited. Closes with the merge result.
 */
@Component({
  selector: 'desk-merge-wizard-dialog',
  imports: [SdDialog, SdButton, SdBadge, SdIcon, SdRadioGroup, SdRadio, SdCheckbox, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Merge duplicate contacts" [description]="stepText()">
      @if (!first() || !second()) {
        <sd-skeleton [lines]="6" />
      } @else if (result(); as r) {
        <div class="done" role="status">
          <sd-icon name="circle-check" [size]="28" />
          <div>
            <h3>{{ r.alreadyMerged ? 'These contacts were already merged' : 'Contacts merged' }}</h3>
            <p class="sd-small sd-muted">{{ survivor()!.fullName }} now holds everything that belonged to {{ merged()!.fullName }}.</p>
            <ul class="counts">
              <li><strong>{{ r.tickets }}</strong> tickets</li>
              <li><strong>{{ r.threads }}</strong> conversation entries</li>
              <li><strong>{{ r.conversations }}</strong> channel conversations</li>
              <li><strong>{{ r.identities }}</strong> identities</li>
              <li><strong>{{ r.consents }}</strong> consents</li>
              <li><strong>{{ r.satisfactionResponses }}</strong> CSAT responses</li>
            </ul>
            @if (r.fieldsCopied.length) {
              <p class="sd-small">Fields taken from the merged contact: {{ r.fieldsCopied.join(', ') }}</p>
            }
          </div>
        </div>
      } @else {
        @if (data.reasons?.length) {
          <p class="why">
            <sd-badge tone="warning" icon="merge">{{ scoreLabel() }}</sd-badge>
            <span class="sd-small sd-muted">{{ data.reasons!.join(' · ') }}</span>
          </p>
        }
        @if (step() === 1) {
          <sd-radio-group [value]="survivorId()" (valueChange)="survivorId.set($event)" ariaLabel="Contact that survives" direction="row">
            <sd-radio [value]="first()!.id">Keep <strong>{{ first()!.fullName }}</strong></sd-radio>
            <sd-radio [value]="second()!.id">Keep <strong>{{ second()!.fullName }}</strong></sd-radio>
          </sd-radio-group>
          <p class="sd-small sd-muted hint">The kept contact retains its own values and fills any blanks from the other one.</p>
        }
        <div class="dt-wrap">
          <table class="dt">
            <thead>
              <tr>
                <th scope="col">Field</th>
                <th scope="col">Survives: {{ survivor()!.fullName }}</th>
                <th scope="col">Merged: {{ merged()!.fullName }}</th>
                @if (step() === 2) {
                  <th scope="col">Use merged value</th>
                }
              </tr>
            </thead>
            <tbody>
              @for (f of fields; track f.key) {
                <tr [class.diff]="differs(f)">
                  <th scope="row">{{ f.label }}</th>
                  <td>{{ f.get(survivor()!) ?? '—' }}</td>
                  <td>{{ f.get(merged()!) ?? '—' }}</td>
                  @if (step() === 2) {
                    <td>
                      @if (f.choosable && differs(f) && f.get(merged()!)) {
                        <sd-checkbox [value]="prefer().has(f.key)" (valueChange)="toggle(f.key, $event)" [ariaLabel]="'Use merged ' + f.label" />
                      }
                    </td>
                  }
                </tr>
              }
            </tbody>
          </table>
        </div>
        @if (step() === 2) {
          <p class="warn sd-small">
            <sd-icon name="triangle-alert" [size]="14" />
            {{ merged()!.fullName }} will be marked as merged and can no longer be edited. This is recorded in the audit log.
          </p>
        }
        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
      }
      <div sdDialogFooter>
        @if (result()) {
          <button sdButton variant="primary" (click)="ref.close(result()!)">Done</button>
        } @else if (step() === 1) {
          <button sdButton type="button" (click)="ref.close()">Cancel</button>
          <button sdButton variant="primary" iconRight="arrow-right" [disabled]="!survivorId()" (click)="step.set(2)">Next</button>
        } @else {
          <button sdButton type="button" icon="arrow-left" (click)="step.set(1)">Back</button>
          <button sdButton variant="danger" icon="merge" [loading]="merging()" (click)="merge()">Merge contacts</button>
        }
      </div>
    </sd-dialog>
  `,
  styles: [
    TABLE_STYLES,
    `
      .why {
        display: flex;
        align-items: center;
        gap: var(--sd-space-2);
        flex-wrap: wrap;
        margin-bottom: var(--sd-space-3);
      }
      .hint {
        margin: var(--sd-space-2) 0 var(--sd-space-3);
      }
      .dt th[scope='row'] {
        background: transparent;
        color: var(--sd-text-2);
        border-bottom: 1px solid var(--sd-divider);
      }
      tr.diff td {
        background: var(--sd-warning-bg);
      }
      .warn {
        display: flex;
        gap: 6px;
        align-items: center;
        color: var(--sd-warning);
        margin-top: var(--sd-space-3);
      }
      .error {
        color: var(--sd-danger);
        margin-top: var(--sd-space-2);
      }
      .done {
        display: flex;
        gap: var(--sd-space-3);
        color: var(--sd-success);
      }
      .done h3 {
        color: var(--sd-text);
        font-weight: var(--sd-weight-semibold);
      }
      .counts {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
        gap: 4px var(--sd-space-3);
        margin: var(--sd-space-3) 0;
        padding: 0;
        list-style: none;
        color: var(--sd-text-2);
      }
    `,
  ],
})
export class MergeWizardDialog {
  protected readonly data = inject<MergeWizardData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<ContactMergeResultDto>>(DialogRef);
  private readonly api = inject(ContactsApi);

  protected readonly fields = FIELDS;
  protected readonly first = signal<ContactDetailDto | null>(null);
  protected readonly second = signal<ContactDetailDto | null>(null);
  protected readonly survivorId = signal<string | null>(null);
  protected readonly step = signal<1 | 2>(1);
  protected readonly prefer = signal<Set<string>>(new Set());
  protected readonly merging = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly result = signal<ContactMergeResultDto | null>(null);

  protected readonly survivor = computed(() => (this.survivorId() === this.second()?.id ? this.second() : this.first()));
  protected readonly merged = computed(() => (this.survivorId() === this.second()?.id ? this.first() : this.second()));
  protected readonly stepText = computed(() =>
    this.result() ? 'Merge complete.' : this.step() === 1 ? 'Step 1 of 2 — choose the contact to keep.' : 'Step 2 of 2 — pick values to carry over, then merge.',
  );
  protected readonly scoreLabel = computed(() => {
    const s = this.data.score;
    return s ? `${Math.round(s * 100)}% match` : 'Possible duplicate';
  });

  constructor() {
    forkJoin([this.api.get(this.data.firstId), this.api.get(this.data.secondId)]).subscribe(([a, b]) => {
      this.first.set(a);
      this.second.set(b);
      // Default survivor: the contact with more history (tickets), then the older one.
      const keepA = a.stats.totalTickets > b.stats.totalTickets || (a.stats.totalTickets === b.stats.totalTickets && a.createdAt <= b.createdAt);
      this.survivorId.set(keepA ? a.id : b.id);
    });
  }

  protected differs(f: FieldRow): boolean {
    const s = this.survivor();
    const m = this.merged();
    return !!s && !!m && (f.get(s) ?? '') !== (f.get(m) ?? '');
  }

  protected toggle(key: string, on: boolean): void {
    this.prefer.update((set) => {
      const next = new Set(set);
      if (on) next.add(key);
      else next.delete(key);
      return next;
    });
  }

  protected merge(): void {
    const s = this.survivor();
    const m = this.merged();
    if (!s || !m) return;
    this.merging.set(true);
    this.error.set(null);
    this.api.merge({ survivorId: s.id, mergedId: m.id, preferMergedFields: [...this.prefer()] }).subscribe({
      next: (r) => {
        this.merging.set(false);
        this.result.set(r);
      },
      error: (err: unknown) => {
        this.merging.set(false);
        this.error.set(saveErrorMessage(err));
      },
    });
  }
}
