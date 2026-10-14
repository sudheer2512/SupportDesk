import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError, ComplianceApi, ContactsApi, CustomersApi, LegalHoldDto } from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdChipsInput,
  SdCombobox,
  SdDateInput,
  SdDateTimePipe,
  SdDialogService,
  SdDrawer,
  SdEmptyState,
  SdFormField,
  SdInput,
  SdOption,
  SdOptionSource,
  SdSkeleton,
  SdToggle,
  ToastService,
} from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { SAAS_CSS } from '../../subscription/saas-ui';

/** Legal holds: preserve records of a customer, contact, specific tickets and/or a date range; block purge and erasure. */
@Component({
  selector: 'desk-holds-tab',
  imports: [FormsModule, SdBadge, SdButton, SdChipsInput, SdCombobox, SdDateInput, SdDateTimePipe, SdDrawer, SdEmptyState, SdFormField, SdInput, SdSkeleton, SdToggle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="toolbar">
      <sd-toggle [value]="includeReleased()" (valueChange)="includeReleased.set($event); load()">Show released holds</sd-toggle>
      <span class="sd-spacer"></span>
      <button sdButton variant="primary" icon="plus" (click)="openEditor()">Place legal hold</button>
    </div>
    @if (error(); as e) {
      <sd-empty-state icon="circle-alert" title="Legal holds could not be loaded" [message]="e"><button sdButton (click)="load()">Try again</button></sd-empty-state>
    } @else if (holds() === null) {
      <sd-skeleton [lines]="4" height="40px" />
    } @else if (holds()!.length === 0) {
      <sd-empty-state icon="scale" title="No legal holds" message="Place a hold to preserve a customer's or contact's records during litigation, audits or investigations." />
    } @else {
      <div class="table-wrap">
        <table class="data">
          <caption class="sd-visually-hidden">Legal holds</caption>
          <thead><tr><th scope="col">Hold</th><th scope="col">Covers</th><th scope="col" class="num">Tickets</th><th scope="col">Status</th><th scope="col"><span class="sd-visually-hidden">Actions</span></th></tr></thead>
          <tbody>
            @for (h of holds(); track h.id) {
              <tr>
                <td><strong>{{ h.name }}</strong><div class="muted small">{{ h.reason }}</div><div class="muted small">placed {{ h.createdAt | sdDateTime: 'date' }}{{ h.createdByName ? ' by ' + h.createdByName : '' }}</div></td>
                <td class="small">
                  @if (h.customerName) {
                    <div>Customer: {{ h.customerName }}</div>
                  }
                  @if (h.contactName) {
                    <div>Contact: {{ h.contactName }}</div>
                  }
                  @if (h.ticketNumbers.length) {
                    <div>Tickets: {{ h.ticketNumbers.join(', ') }}</div>
                  }
                  @if (h.fromDate || h.toDate) {
                    <div>Created {{ h.fromDate ? 'from ' + (h.fromDate | sdDateTime: 'date') : '' }} {{ h.toDate ? 'until ' + (h.toDate | sdDateTime: 'date') : '' }}</div>
                  }
                </td>
                <td class="num">{{ h.coveredTickets }}</td>
                <td><sd-badge size="sm" [tone]="h.active ? 'warning' : 'neutral'" [dot]="true">{{ h.active ? 'Active' : 'Released' }}</sd-badge></td>
                <td class="actions">
                  @if (h.active) {
                    <button sdButton size="sm" (click)="release(h)">Release</button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }

    <sd-drawer [(open)]="editorOpen" title="Place legal hold" width="520px">
      <form class="form" id="hold-form" (ngSubmit)="save()">
        <sd-form-field label="Name" required [error]="errors()['name']"><input sdInput name="name" [(ngModel)]="name" maxlength="200" /></sd-form-field>
        <sd-form-field label="Reason / matter" required [error]="errors()['reason']"><textarea sdInput name="reason" rows="3" [(ngModel)]="reason"></textarea></sd-form-field>
        <p class="muted small">The hold covers tickets matching <em>all</em> criteria you set.</p>
        <sd-form-field label="Customer" optional [error]="errors()['customerId']">
          <sd-combobox [(value)]="customerId" [search]="customerSearch" placeholder="Search customers…" ariaLabel="Customer" />
        </sd-form-field>
        <sd-form-field label="Contact" optional [error]="errors()['contactId']">
          <sd-combobox [(value)]="contactId" [search]="contactSearch" placeholder="Search contacts…" ariaLabel="Contact" />
        </sd-form-field>
        <sd-form-field label="Ticket numbers" optional hint="Press Enter after each number" [error]="errors()['ticketNumbers']">
          <sd-chips-input [(value)]="tickets" placeholder="e.g. 1042" ariaLabel="Ticket numbers" [validate]="isNumber" />
        </sd-form-field>
        <div class="form-grid">
          <sd-form-field label="Created from" optional><sd-date-input [(value)]="fromDate" ariaLabel="Created from" /></sd-form-field>
          <sd-form-field label="Created until" optional [error]="errors()['toDate']"><sd-date-input [(value)]="toDate" ariaLabel="Created until" /></sd-form-field>
        </div>
        @if (errors()['scope'] || formError()) {
          <p class="err" role="alert">{{ errors()['scope'] || formError() }}</p>
        }
      </form>
      <div sdDrawerFooter class="footer">
        <button sdButton type="button" variant="ghost" (click)="editorOpen.set(false)">Cancel</button>
        <button sdButton type="submit" form="hold-form" variant="primary" [loading]="saving()">Place hold</button>
      </div>
    </sd-drawer>
  `,
  styles: SAAS_CSS,
})
export class HoldsTabComponent implements OnInit {
  private readonly api = inject(ComplianceApi);
  private readonly customers = inject(CustomersApi);
  private readonly contacts = inject(ContactsApi);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(SdDialogService);

  protected readonly holds = signal<LegalHoldDto[] | null>(null);
  protected readonly includeReleased = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly editorOpen = signal(false);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string | null>>({});
  protected readonly formError = signal<string | null>(null);
  protected name = '';
  protected reason = '';
  protected customerId: string | null = null;
  protected contactId: string | null = null;
  protected tickets: string[] = [];
  protected fromDate: string | null = null;
  protected toDate: string | null = null;

  protected readonly isNumber = (v: string) => /^\d{1,12}$/.test(v.trim());

  protected readonly customerSearch: SdOptionSource = async (q) => {
    const page = await firstValueFrom(this.customers.list({ q, limit: 20 }));
    return page.items.map((c): SdOption => ({ value: c.id, label: c.name, description: c.code }));
  };

  protected readonly contactSearch: SdOptionSource = async (q) => {
    const page = await firstValueFrom(this.contacts.list({ q, limit: 20 }));
    return page.items.map((c): SdOption => ({ value: c.id, label: c.fullName, description: [c.email, c.customerName].filter(Boolean).join(' · '), avatar: c.fullName }));
  };

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.error.set(null);
    this.api.holds(this.includeReleased()).subscribe({ next: (h) => this.holds.set(h), error: (e: ApiError) => this.error.set(e.userMessage) });
  }

  protected openEditor(): void {
    this.name = '';
    this.reason = '';
    this.customerId = null;
    this.contactId = null;
    this.tickets = [];
    this.fromDate = null;
    this.toDate = null;
    this.errors.set({});
    this.formError.set(null);
    this.editorOpen.set(true);
  }

  protected save(): void {
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    this.api
      .createHold({
        name: this.name,
        reason: this.reason,
        customerId: this.customerId,
        contactId: this.contactId,
        ticketNumbers: this.tickets.map((t) => Number(t.trim())),
        fromDate: this.fromDate ? new Date(this.fromDate + 'T00:00:00Z').toISOString() : null,
        toDate: this.toDate ? new Date(this.toDate + 'T23:59:59Z').toISOString() : null,
      })
      .subscribe({
        next: (h) => {
          this.saving.set(false);
          this.editorOpen.set(false);
          this.toast.success('Legal hold placed', `${h.coveredTickets} ticket(s) are now preserved.`);
          this.load();
        },
        error: (e: ApiError) => {
          this.saving.set(false);
          const keys = ['name', 'reason', 'customerId', 'contactId', 'ticketNumbers', 'toDate', 'scope'];
          const map: Record<string, string | null> = {};
          for (const k of keys) map[k] = e.fieldError(k);
          this.errors.set(map);
          if (!keys.some((k) => map[k])) this.formError.set(e.userMessage);
        },
      });
  }

  protected async release(h: LegalHoldDto): Promise<void> {
    const ok = await this.dialog.confirm({
      title: `Release “${h.name}”?`,
      message: 'Covered records become subject to retention policies and erasure requests again.',
      confirmLabel: 'Release hold',
      danger: true,
    });
    if (!ok) return;
    this.api.releaseHold(h.id).subscribe({
      next: () => {
        this.toast.success('Legal hold released');
        this.load();
      },
      error: (e: ApiError) => this.toast.error('Could not release', e.userMessage),
    });
  }
}
