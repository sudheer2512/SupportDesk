import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { InvoiceDto } from '@sd/core';
import { SdBadge, SdButton, SdDateTimePipe } from '@sd/ui';
import { SAAS_CSS, invoiceLabel, invoiceTone, money } from './saas-ui';

/** `<desk-invoice-view [invoice]="inv" (download)="pdf()" />` — invoice detail: bill-to, lines, totals, payment instructions. */
@Component({
  selector: 'desk-invoice-view',
  imports: [SdBadge, SdButton, SdDateTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let inv = invoice();
    @let s = inv.summary;
    <div class="head">
      <div>
        <div class="number">{{ s.number }}</div>
        <div class="muted small">{{ s.kind }} invoice · {{ s.tenantName }}</div>
      </div>
      <sd-badge [tone]="tone(s)" [dot]="true">{{ label(s) }}</sd-badge>
    </div>
    <dl class="facts">
      <dt>Period</dt>
      <dd>{{ s.periodStart | sdDateTime: 'date' }} – {{ s.periodEnd | sdDateTime: 'date' }}</dd>
      <dt>Issued</dt>
      <dd>{{ s.issuedAt | sdDateTime: 'date' }}</dd>
      <dt>Due</dt>
      <dd>{{ s.dueAt | sdDateTime: 'date' }}</dd>
      @if (s.paidAt) {
        <dt>Paid</dt>
        <dd>{{ s.paidAt | sdDateTime: 'date' }}{{ inv.paymentReference ? ' · ref ' + inv.paymentReference : '' }}</dd>
      }
      <dt>Bill to</dt>
      <dd>
        {{ inv.billTo.name || s.tenantName }}
        @if (inv.billTo.email) {
          <br />{{ inv.billTo.email }}
        }
        @if (inv.billTo.address) {
          <br /><span class="addr">{{ inv.billTo.address }}</span>
        }
        @if (inv.billTo.taxId) {
          <br />Tax id {{ inv.billTo.taxId }}
        }
      </dd>
    </dl>
    <div class="table-wrap lines">
      <table class="data">
        <caption class="sd-visually-hidden">Invoice lines</caption>
        <thead>
          <tr><th scope="col">Description</th><th scope="col" class="num">Qty</th><th scope="col" class="num hide-sm">Unit price</th><th scope="col" class="num">Amount</th></tr>
        </thead>
        <tbody>
          @for (line of inv.lines; track $index) {
            <tr>
              <td>{{ line.description }}</td>
              <td class="num">{{ line.quantity || '' }}</td>
              <td class="num hide-sm">{{ line.quantity ? money(line.unitPrice, s.currency) : '' }}</td>
              <td class="num">{{ line.quantity ? money(line.amount, s.currency) : '' }}</td>
            </tr>
          }
        </tbody>
        <tfoot>
          <tr><td colspan="2"></td><th scope="row" class="num hide-sm">Subtotal</th><td class="num">{{ money(inv.subtotal, s.currency) }}</td></tr>
          <tr><td colspan="2"></td><th scope="row" class="num hide-sm">Tax</th><td class="num">{{ money(inv.tax, s.currency) }}</td></tr>
          <tr class="total"><td colspan="2"></td><th scope="row" class="num hide-sm">Total</th><td class="num">{{ money(s.total, s.currency) }}</td></tr>
        </tfoot>
      </table>
    </div>
    @if (inv.paymentInstructions) {
      <div class="callout callout--info"><div class="grow"><strong>How to pay.</strong> {{ inv.paymentInstructions }}</div></div>
    }
    @if (inv.notes) {
      <p class="muted small">{{ inv.notes }}</p>
    }
    <button sdButton icon="download" [loading]="downloading()" (click)="download.emit()">Download PDF</button>
  `,
  styles: `
    ${SAAS_CSS}
    :host { display: grid; gap: 16px; }
    .head { display: flex; align-items: flex-start; justify-content: space-between; gap: 12px; }
    .number { font-size: var(--sd-text-lg); font-weight: var(--sd-weight-semibold); }
    .addr { white-space: pre-line; }
    tfoot th, tfoot td { border-bottom: 0; }
    tr.total td, tr.total th { font-weight: var(--sd-weight-bold); }
    .lines .callout { margin: 0; }
  `,
})
export class InvoiceViewComponent {
  readonly invoice = input.required<InvoiceDto>();
  readonly downloading = input(false);
  readonly download = output<void>();
  protected readonly money = money;
  protected readonly tone = invoiceTone;
  protected readonly label = invoiceLabel;
}
