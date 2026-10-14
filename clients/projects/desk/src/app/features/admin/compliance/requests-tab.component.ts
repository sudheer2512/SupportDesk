import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiClient, ApiError, ComplianceApi, ContactsApi, DataRequestDto } from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCombobox,
  SdDateTimePipe,
  SdDrawer,
  SdEmptyState,
  SdFileSizePipe,
  SdFormField,
  SdIcon,
  SdInput,
  SdOption,
  SdOptionSource,
  SdRadio,
  SdRadioGroup,
  SdSkeleton,
  ToastService,
} from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { SAAS_CSS, requestTone } from '../../subscription/saas-ui';

/**
 * Data-subject requests: export (zip, one-time expiring link) and erasure (approval, then pseudonymisation that keeps
 * ticket statistics; refused while a legal hold covers the contact). Processing requests refresh automatically.
 */
@Component({
  selector: 'desk-requests-tab',
  imports: [FormsModule, SdBadge, SdButton, SdCombobox, SdDateTimePipe, SdDrawer, SdEmptyState, SdFileSizePipe, SdFormField, SdIcon, SdInput, SdRadio, SdRadioGroup, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="toolbar">
      <sd-form-field label="Status">
        <select sdInput [ngModel]="status()" (ngModelChange)="status.set($event); load()" name="status">
          <option value="">All</option>
          <option value="Pending">Awaiting approval</option>
          <option value="Processing">Processing</option>
          <option value="Completed">Completed</option>
          <option value="Rejected">Rejected</option>
          <option value="Failed">Failed</option>
        </select>
      </sd-form-field>
      <span class="sd-spacer"></span>
      <button sdButton variant="primary" icon="plus" (click)="openEditor()">New request</button>
    </div>
    @if (link(); as l) {
      <div class="callout callout--success" role="status">
        <sd-icon name="download" [size]="16" />
        <div class="grow">
          One-time download link ready (valid until {{ l.expiresAt | sdDateTime }}). It works once — the export is removed after download.
          <div><a sdButton size="sm" variant="primary" [href]="l.href" (click)="linkUsed()">Download export</a></div>
        </div>
      </div>
    }
    @if (error(); as e) {
      <sd-empty-state icon="circle-alert" title="Requests could not be loaded" [message]="e"><button sdButton (click)="load()">Try again</button></sd-empty-state>
    } @else if (rows() === null) {
      <sd-skeleton [lines]="4" height="40px" />
    } @else if (rows()!.length === 0) {
      <sd-empty-state icon="user-check" title="No data-subject requests" message="Record export and erasure requests from customers here; every step is audited." />
    } @else {
      <div class="table-wrap">
        <table class="data">
          <caption class="sd-visually-hidden">Data-subject requests</caption>
          <thead><tr><th scope="col">Contact</th><th scope="col">Type</th><th scope="col">Status</th><th scope="col" class="hide-sm">Requested</th><th scope="col"><span class="sd-visually-hidden">Actions</span></th></tr></thead>
          <tbody>
            @for (r of rows(); track r.id) {
              <tr>
                <td><strong>{{ r.contactName }}</strong>@if (r.notes) {<div class="muted small">{{ r.notes }}</div>}</td>
                <td>{{ r.type }}</td>
                <td>
                  <sd-badge size="sm" [tone]="tone(r.status)" [dot]="true">{{ r.status === 'Pending' ? 'Awaiting approval' : r.status }}</sd-badge>
                  @if (r.blockedByHold) {
                    <div class="small warn">Blocked by a legal hold</div>
                  }
                  @if (r.rejectionReason) {
                    <div class="muted small">{{ r.rejectionReason }}</div>
                  }
                  @if (r.error) {
                    <div class="small warn">{{ r.error }}</div>
                  }
                  @if (r.downloadedAt) {
                    <div class="muted small">downloaded {{ r.downloadedAt | sdDateTime }}</div>
                  } @else if (r.resultSizeBytes) {
                    <div class="muted small">{{ r.resultFileName }} · {{ r.resultSizeBytes | sdFileSize }}</div>
                  }
                </td>
                <td class="hide-sm">{{ r.createdAt | sdDateTime }}<div class="muted small">{{ r.requestedByName }}{{ r.approvedByName ? ' · approved by ' + r.approvedByName : '' }}</div></td>
                <td class="actions">
                  @if (r.type === 'Erasure' && r.status === 'Pending') {
                    <button sdButton size="sm" variant="ghost" (click)="openReject(r)">Reject</button>
                    <button sdButton size="sm" variant="danger" [disabled]="r.blockedByHold" [loading]="busyId() === r.id" (click)="approve(r)">Approve erasure</button>
                  }
                  @if (r.canDownload) {
                    <button sdButton size="sm" icon="download" [loading]="busyId() === r.id" (click)="download(r)">Get link</button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }

    <sd-drawer [(open)]="editorOpen" title="New data-subject request" width="520px">
      <form class="form" id="dsr-form" (ngSubmit)="create()">
        <sd-radio-group [(value)]="type" ariaLabel="Request type">
          <sd-radio value="Export">Export — a zip of the contact's profile, tickets, messages and attachments</sd-radio>
          <sd-radio value="Erasure">Erasure — pseudonymise the contact and remove their content (needs approval)</sd-radio>
        </sd-radio-group>
        <sd-form-field label="Contact" required [error]="formErrors()['contactId']">
          <sd-combobox [(value)]="contactId" [search]="contactSearch" placeholder="Search contacts…" ariaLabel="Contact" />
        </sd-form-field>
        <sd-form-field label="Notes" optional hint="How the request arrived and how the identity was verified.">
          <textarea sdInput name="notes" rows="3" [(ngModel)]="notes" maxlength="2000"></textarea>
        </sd-form-field>
        @if (formError()) {
          <p class="err" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDrawerFooter class="footer">
        <button sdButton type="button" variant="ghost" (click)="editorOpen.set(false)">Cancel</button>
        <button sdButton type="submit" form="dsr-form" variant="primary" [loading]="saving()">{{ type === 'Export' ? 'Start export' : 'Request erasure' }}</button>
      </div>
    </sd-drawer>

    <sd-drawer [(open)]="rejectOpen" title="Reject request" width="440px">
      <form class="form" id="reject-form" (ngSubmit)="reject()">
        <sd-form-field label="Reason" required [error]="rejectError()"><textarea sdInput name="reason" rows="3" [(ngModel)]="rejectReason"></textarea></sd-form-field>
      </form>
      <div sdDrawerFooter class="footer">
        <button sdButton type="button" variant="ghost" (click)="rejectOpen.set(false)">Cancel</button>
        <button sdButton type="submit" form="reject-form" variant="danger" [loading]="saving()">Reject</button>
      </div>
    </sd-drawer>
  `,
  styles: `
    ${SAAS_CSS}
    .warn { color: var(--sd-danger); }
  `,
})
export class RequestsTabComponent implements OnInit {
  private readonly api = inject(ComplianceApi);
  private readonly http = inject(ApiClient);
  private readonly contacts = inject(ContactsApi);
  private readonly toast = inject(ToastService);
  private timer: ReturnType<typeof setTimeout> | null = null;

  protected readonly status = signal('');
  protected readonly rows = signal<DataRequestDto[] | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busyId = signal<string | null>(null);
  protected readonly editorOpen = signal(false);
  protected readonly rejectOpen = signal(false);
  protected readonly saving = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly formErrors = signal<Record<string, string | null>>({});
  protected readonly rejectError = signal<string | null>(null);
  protected readonly link = signal<{ href: string; expiresAt: string } | null>(null);
  protected type: 'Export' | 'Erasure' = 'Export';
  protected contactId: string | null = null;
  protected notes = '';
  protected rejectReason = '';
  private rejecting: DataRequestDto | null = null;
  protected readonly tone = requestTone;

  protected readonly contactSearch: SdOptionSource = async (q) => {
    const page = await firstValueFrom(this.contacts.list({ q, limit: 20 }));
    return page.items.map((c): SdOption => ({ value: c.id, label: c.fullName, description: [c.email, c.customerName].filter(Boolean).join(' · '), avatar: c.fullName }));
  };

  constructor() {
    inject(DestroyRef).onDestroy(() => this.timer && clearTimeout(this.timer));
  }

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.error.set(null);
    this.api.requests(this.status() || null).subscribe({
      next: (r) => {
        this.rows.set(r);
        this.schedulePoll(r);
      },
      error: (e: ApiError) => this.error.set(e.userMessage),
    });
  }

  /** Exports and approved erasures run as background jobs: refresh while any is processing. */
  private schedulePoll(rows: DataRequestDto[]): void {
    if (this.timer) clearTimeout(this.timer);
    if (rows.some((r) => r.status === 'Processing')) this.timer = setTimeout(() => this.load(), 3000);
  }

  protected openEditor(): void {
    this.type = 'Export';
    this.contactId = null;
    this.notes = '';
    this.formError.set(null);
    this.formErrors.set({});
    this.editorOpen.set(true);
  }

  protected create(): void {
    if (!this.contactId) {
      this.formErrors.set({ contactId: 'Choose a contact.' });
      return;
    }
    this.saving.set(true);
    this.formError.set(null);
    this.api.createRequest({ type: this.type, contactId: this.contactId, notes: this.notes || null }).subscribe({
      next: (r) => {
        this.saving.set(false);
        this.editorOpen.set(false);
        this.toast.success(r.type === 'Export' ? 'Export started' : 'Erasure requested', r.type === 'Export' ? 'The zip is built in the background.' : 'Another compliance administrator should approve it.');
        this.load();
      },
      error: (e: ApiError) => {
        this.saving.set(false);
        this.formErrors.set({ contactId: e.fieldError('contactId') });
        if (!e.fieldError('contactId')) this.formError.set(e.userMessage);
      },
    });
  }

  protected approve(r: DataRequestDto): void {
    this.busyId.set(r.id);
    this.api.approve(r.id).subscribe({
      next: () => {
        this.busyId.set(null);
        this.toast.success('Erasure approved', 'The contact is pseudonymised in the background.');
        this.load();
      },
      error: (e: ApiError) => {
        this.busyId.set(null);
        this.toast.error('Could not approve', e.userMessage);
        this.load();
      },
    });
  }

  protected openReject(r: DataRequestDto): void {
    this.rejecting = r;
    this.rejectReason = '';
    this.rejectError.set(null);
    this.rejectOpen.set(true);
  }

  protected reject(): void {
    const r = this.rejecting;
    if (!r) return;
    if (this.rejectReason.trim().length < 3) {
      this.rejectError.set('Give a reason.');
      return;
    }
    this.saving.set(true);
    this.api.reject(r.id, this.rejectReason).subscribe({
      next: () => {
        this.saving.set(false);
        this.rejectOpen.set(false);
        this.load();
      },
      error: (e: ApiError) => {
        this.saving.set(false);
        this.rejectError.set(e.fieldError('reason') ?? e.userMessage);
      },
    });
  }

  protected download(r: DataRequestDto): void {
    this.busyId.set(r.id);
    this.api.downloadLink(r.id).subscribe({
      next: (l) => {
        this.busyId.set(null);
        // The API returns an absolute path ("/api/v1/…"); resolve it against the configured API origin.
        const href = l.url.startsWith('/api/v1/') ? this.http.url(l.url.slice('/api/v1/'.length)) : l.url;
        this.link.set({ href, expiresAt: l.expiresAt });
      },
      error: (e: ApiError) => {
        this.busyId.set(null);
        this.toast.error('No link', e.userMessage);
      },
    });
  }

  protected linkUsed(): void {
    setTimeout(() => {
      this.link.set(null);
      this.load();
    }, 1500);
  }
}
