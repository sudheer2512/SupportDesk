import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiError, InvoiceDto, InvoiceSummaryDto, PlatformApi, saveBlob } from '@sd/core';
import { SdBadge, SdButton, SdDateTimePipe, SdDrawer, SdEmptyState, SdFormField, SdInput, SdPageHeader, SdSkeleton, ToastService } from '@sd/ui';
import { InvoiceViewComponent } from '../subscription/invoice-view.component';
import { SAAS_CSS, invoiceLabel, invoiceTone, money } from '../subscription/saas-ui';

/** Invoices across tenants: filter by status, open detail, download PDF, record offline payments (mark paid) or void. */
@Component({
  selector: 'desk-platform-invoices-page',
  imports: [FormsModule, RouterLink, SdBadge, SdButton, SdDateTimePipe, SdDrawer, SdEmptyState, SdFormField, SdInput, SdPageHeader, SdSkeleton, InvoiceViewComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Invoices" icon="receipt" [subtitle]="total() !== null ? total() + ' invoices' : 'All tenants'" />
      <div class="desk-page__body">
        <div class="toolbar" role="group" aria-label="Filter invoices">
          <sd-form-field label="Status">
            <select sdInput name="status" [ngModel]="status()" (ngModelChange)="status.set($event); load(false)">
              <option value="">All</option>
              <option value="Open">Open</option>
              <option value="Overdue">Overdue</option>
              <option value="Paid">Paid</option>
              <option value="Void">Void</option>
            </select>
          </sd-form-field>
        </div>
        @if (error(); as e) {
          <sd-empty-state icon="circle-alert" title="Invoices could not be loaded" [message]="e"><button sdButton (click)="load(false)">Try again</button></sd-empty-state>
        } @else if (rows() === null) {
          <sd-skeleton [lines]="8" height="36px" />
        } @else if (rows()!.length === 0) {
          <sd-empty-state icon="receipt" title="No invoices" message="Invoices appear when subscription periods start or plans are upgraded." />
        } @else {
          <div class="table-wrap">
            <table class="grid">
              <caption class="sd-visually-hidden">Invoices</caption>
              <thead>
                <tr>
                  <th scope="col">Number</th><th scope="col">Tenant</th><th scope="col" class="hide-sm">Issued</th><th scope="col" class="hide-sm">Due</th>
                  <th scope="col" class="num">Total</th><th scope="col">Status</th><th scope="col"><span class="sd-visually-hidden">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                @for (i of rows(); track i.id) {
                  <tr>
                    <td><button class="linkish" type="button" (click)="open(i)">{{ i.number }}</button><div class="muted small">{{ i.kind }}</div></td>
                    <td><a [routerLink]="['/platform/tenants', i.tenantId]">{{ i.tenantName }}</a></td>
                    <td class="hide-sm">{{ i.issuedAt | sdDateTime: 'date' }}</td>
                    <td class="hide-sm">{{ i.dueAt | sdDateTime: 'date' }}</td>
                    <td class="num">{{ money(i.total, i.currency) }}</td>
                    <td><sd-badge size="sm" [tone]="tone(i)">{{ label(i) }}</sd-badge></td>
                    <td class="actions">
                      @if (i.status === 'Open') {
                        <button sdButton size="sm" (click)="startPay(i)">Mark paid</button>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          @if (cursor()) {
            <div class="more"><button sdButton size="sm" [loading]="loading()" (click)="load(true)">Load more</button></div>
          }
        }
      </div>
    </div>

    <sd-drawer [(open)]="detailOpen" [title]="detail()?.summary?.number ?? 'Invoice'" width="560px">
      @if (detail(); as d) {
        <desk-invoice-view [invoice]="d" [downloading]="downloading()" (download)="pdf(d)" />
        @if (d.summary.status === 'Open') {
          <div class="footer actions-row">
            <button sdButton variant="ghost" (click)="startVoid(d.summary)">Void…</button>
            <button sdButton variant="primary" (click)="startPay(d.summary)">Mark paid…</button>
          </div>
        }
      } @else {
        <sd-skeleton [lines]="8" />
      }
    </sd-drawer>

    <sd-drawer [(open)]="actionOpen" [title]="mode() === 'pay' ? 'Mark ' + (target()?.number ?? '') + ' paid' : 'Void ' + (target()?.number ?? '')" width="440px">
      <form class="form" id="inv-action" (ngSubmit)="submit()">
        @if (mode() === 'pay') {
          <p class="small">Records an offline payment of {{ money(target()?.total, target()?.currency) }}. Settling overdue invoices restores billing suspensions.</p>
          <sd-form-field label="Payment reference" optional><input sdInput name="ref" [(ngModel)]="reference" maxlength="200" /></sd-form-field>
        } @else {
          <sd-form-field label="Reason" required [error]="actionError()"><textarea sdInput name="reason" rows="3" [(ngModel)]="reference"></textarea></sd-form-field>
        }
        @if (actionError() && mode() === 'pay') {
          <p class="err" role="alert">{{ actionError() }}</p>
        }
      </form>
      <div sdDrawerFooter class="footer">
        <button sdButton type="button" variant="ghost" (click)="actionOpen.set(false)">Cancel</button>
        <button sdButton type="submit" form="inv-action" [variant]="mode() === 'pay' ? 'primary' : 'danger'" [loading]="saving()">{{ mode() === 'pay' ? 'Mark paid' : 'Void invoice' }}</button>
      </div>
    </sd-drawer>
  `,
  styles: `
    ${SAAS_CSS}
    .linkish { border: 0; background: none; padding: 0; font: inherit; font-weight: var(--sd-weight-semibold); color: var(--sd-primary); cursor: pointer; }
    .linkish:hover { text-decoration: underline; }
    a { color: var(--sd-text); }
    .actions-row { margin-top: 16px; }
  `,
})
export class PlatformInvoicesPage implements OnInit {
  private readonly api = inject(PlatformApi);
  private readonly toast = inject(ToastService);

  protected readonly status = signal('');
  protected readonly rows = signal<InvoiceSummaryDto[] | null>(null);
  protected readonly cursor = signal<string | null>(null);
  protected readonly total = signal<number | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly detailOpen = signal(false);
  protected readonly detail = signal<InvoiceDto | null>(null);
  protected readonly downloading = signal(false);
  protected readonly actionOpen = signal(false);
  protected readonly mode = signal<'pay' | 'void'>('pay');
  protected readonly target = signal<InvoiceSummaryDto | null>(null);
  protected readonly saving = signal(false);
  protected readonly actionError = signal<string | null>(null);
  protected reference = '';
  protected readonly money = money;
  protected readonly tone = invoiceTone;
  protected readonly label = invoiceLabel;

  ngOnInit(): void {
    this.load(false);
  }

  protected load(more: boolean): void {
    this.loading.set(true);
    this.error.set(null);
    if (!more) this.rows.set(null);
    this.api.invoices({ status: this.status() || null, cursor: more ? this.cursor() : null, limit: 50 }).subscribe({
      next: (p) => {
        this.rows.set(more ? [...(this.rows() ?? []), ...p.items] : p.items);
        this.cursor.set(p.nextCursor);
        this.total.set(p.total ?? null);
        this.loading.set(false);
      },
      error: (e: ApiError) => {
        this.error.set(e.userMessage);
        this.loading.set(false);
      },
    });
  }

  protected open(i: InvoiceSummaryDto): void {
    this.detail.set(null);
    this.detailOpen.set(true);
    this.api.invoice(i.id).subscribe({ next: (d) => this.detail.set(d), error: () => this.detailOpen.set(false) });
  }

  protected pdf(d: InvoiceDto): void {
    this.downloading.set(true);
    this.api.invoicePdf(d.summary.id).subscribe({
      next: (blob) => {
        saveBlob(blob, `${d.summary.number}.pdf`);
        this.downloading.set(false);
      },
      error: () => this.downloading.set(false),
    });
  }

  protected startPay(i: InvoiceSummaryDto): void {
    this.mode.set('pay');
    this.target.set(i);
    this.reference = '';
    this.actionError.set(null);
    this.actionOpen.set(true);
  }

  protected startVoid(i: InvoiceSummaryDto): void {
    this.mode.set('void');
    this.target.set(i);
    this.reference = '';
    this.actionError.set(null);
    this.actionOpen.set(true);
  }

  protected submit(): void {
    const t = this.target();
    if (!t) return;
    if (this.mode() === 'void' && !this.reference.trim()) {
      this.actionError.set('Give a reason.');
      return;
    }
    this.saving.set(true);
    const call = this.mode() === 'pay' ? this.api.markPaid(t.id, { reference: this.reference || null }) : this.api.voidInvoice(t.id, this.reference);
    call.subscribe({
      next: (d) => {
        this.saving.set(false);
        this.actionOpen.set(false);
        if (this.detailOpen()) this.detail.set(d);
        this.toast.success(this.mode() === 'pay' ? `${t.number} marked paid` : `${t.number} voided`);
        this.load(false);
      },
      error: (e: ApiError) => {
        this.saving.set(false);
        this.actionError.set(e.fieldError('reason') ?? e.userMessage);
      },
    });
  }
}
