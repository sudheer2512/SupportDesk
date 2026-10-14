import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  ContactDetailDto,
  ContactListItemDto,
  ContactMergeResultDto,
  ContactsApi,
  CustomersApi,
  DuplicatePairDto,
  Guid,
  MeStore,
} from '@sd/core';
import { SdAvatar, SdBadge, SdButton, SdDialogService, SdEmptyState, SdSkeleton, ToastService } from '@sd/ui';
import { ContactFormData, ContactFormDialog } from './contact-form.dialog';
import { MergeWizardData, MergeWizardDialog } from './merge-wizard.dialog';
import { SECTION_STYLES, TABLE_STYLES } from './shared/styles';

/** Contacts of a customer with possible duplicates (merge wizard) — Customer 360 "Contacts" tab. */
@Component({
  selector: 'desk-c360-contacts',
  imports: [RouterLink, SdButton, SdBadge, SdAvatar, SdSkeleton, SdEmptyState],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="section-head">
      <h2>Contacts</h2>
      <span class="sd-spacer"></span>
      @if (me.can('customers.manage')) {
        <button sdButton size="sm" icon="user-plus" (click)="add()">Add contact</button>
      }
    </div>
    @if (loading()) {
      <sd-skeleton [lines]="4" />
    } @else if (contacts().length === 0) {
      <sd-empty-state icon="users" title="No contacts yet" compact />
    } @else {
      <div class="dt-wrap">
        <table class="dt">
          <thead>
            <tr>
              <th scope="col">Name</th>
              <th scope="col">E-mail</th>
              <th scope="col">Phone</th>
              <th scope="col">Location</th>
              <th scope="col">Portal</th>
              <th scope="col" class="num">Open</th>
            </tr>
          </thead>
          <tbody>
            @for (c of contacts(); track c.id) {
              <tr>
                <td>
                  <span class="who">
                    <sd-avatar [name]="c.fullName" size="xs" />
                    <a [routerLink]="['/customers/contacts', c.id]">{{ c.fullName }}</a>
                    @if (c.isPrimary) {
                      <sd-badge tone="primary" size="sm">Primary</sd-badge>
                    }
                    @if (c.isVip) {
                      <sd-badge tone="warning" size="sm" icon="star">VIP</sd-badge>
                    }
                  </span>
                  @if (c.jobTitle) {
                    <div class="sd-xs sd-muted">{{ c.jobTitle }}</div>
                  }
                </td>
                <td>{{ c.email ?? '—' }}</td>
                <td class="nowrap">{{ c.phone ?? '—' }}</td>
                <td>{{ c.locationName ?? '—' }}</td>
                <td>{{ c.portalAccess === 'None' ? 'No access' : c.portalAccess }}</td>
                <td class="num">{{ c.openTickets }}</td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }

    @if (duplicates().length) {
      <div class="section-head">
        <h2>Possible duplicates</h2>
        <sd-badge tone="warning" size="sm">{{ duplicates().length }}</sd-badge>
      </div>
      <div class="dt-wrap">
        <table class="dt">
          <thead>
            <tr>
              <th scope="col">Contacts</th>
              <th scope="col">Why</th>
              <th scope="col">Match</th>
              <th scope="col"><span class="sd-visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (d of duplicates(); track d.first.id + d.second.id) {
              <tr>
                <td>{{ d.first.fullName }} ↔ {{ d.second.fullName }}</td>
                <td class="sd-small">{{ d.reasons.join(' · ') }}</td>
                <td><sd-badge [tone]="d.confidence === 'High' ? 'danger' : d.confidence === 'Medium' ? 'warning' : 'neutral'" size="sm">{{ (d.score * 100).toFixed(0) }}%</sd-badge></td>
                <td class="row-actions">
                  @if (me.can('contacts.merge')) {
                    <button sdButton size="sm" icon="merge" (click)="merge(d)">Review &amp; merge</button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styles: [
    TABLE_STYLES,
    SECTION_STYLES,
    `
      .section-head:first-child {
        margin-top: 0;
      }
      .who {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        flex-wrap: wrap;
      }
    `,
  ],
})
export class C360ContactsComponent {
  protected readonly me = inject(MeStore);
  private readonly contactsApi = inject(ContactsApi);
  private readonly customersApi = inject(CustomersApi);
  private readonly dialog = inject(SdDialogService);
  private readonly toast = inject(ToastService);

  readonly customerId = input.required<Guid>();
  readonly customerName = input<string>('');
  /** Emitted after contacts changed (created or merged) so the parent can refresh its KPIs. */
  readonly changed = output<void>();

  protected readonly contacts = signal<ContactListItemDto[]>([]);
  protected readonly duplicates = signal<DuplicatePairDto[]>([]);
  protected readonly loading = signal(true);

  constructor() {
    effect(() => {
      const id = this.customerId();
      untracked(() => this.load(id));
    });
  }

  private load(id: Guid): void {
    this.loading.set(true);
    this.contactsApi.list({ customerId: id, limit: 200 }).subscribe({
      next: (p) => {
        this.contacts.set(p.items);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
    this.customersApi.contactDuplicates(id).subscribe((d) => this.duplicates.set(d));
  }

  protected add(): void {
    this.dialog
      .open<ContactDetailDto, ContactFormData>(ContactFormDialog, { data: { customerId: this.customerId(), customerName: this.customerName() }, size: 'lg', disableClose: true })
      .closed.subscribe((saved) => {
        if (!saved) return;
        this.toast.success('Contact created', saved.fullName);
        this.load(this.customerId());
        this.changed.emit();
      });
  }

  protected merge(pair: DuplicatePairDto): void {
    this.dialog
      .open<ContactMergeResultDto, MergeWizardData>(MergeWizardDialog, {
        data: { firstId: pair.first.id, secondId: pair.second.id, score: pair.score, reasons: pair.reasons },
        size: 'lg',
      })
      .closed.subscribe((result) => {
        if (!result) return;
        this.load(this.customerId());
        this.changed.emit();
      });
  }
}
