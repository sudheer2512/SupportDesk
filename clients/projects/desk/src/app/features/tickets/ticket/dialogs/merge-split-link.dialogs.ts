import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError, Guid, TicketDetailDto, TicketsApi } from '@sd/core';
import { SdButton, SdCheckbox, SdCombobox, SdDialog, SdFormField, SdIcon, SdInput, SdOption, SdOptionSource, ToastService } from '@sd/ui';
import { Observable, firstValueFrom, map, tap } from 'rxjs';
import { htmlToText, toastApiError } from '../../shared/ticket-ui';
import { TicketDialogData } from './assign-transfer.dialogs';

/** Async ticket search for pickers (by number, subject, customer or contact), excluding the given ids. */
export function ticketSearchSource(tickets: TicketsApi, exclude: () => Guid[]): (q: string) => Observable<SdOption[]> {
  return (q: string) =>
    tickets.list({ q, limit: 10, sort: 'lastActivity', dir: 'desc' }, { silent: true }).pipe(
      map((page) =>
        page.items
          .filter((t) => !exclude().includes(t.id))
          .map<SdOption>((t) => ({
            value: t.id,
            label: `${t.displayNumber} · ${t.subject}`,
            description: [t.status, t.customerName, t.contactName].filter(Boolean).join(' · '),
          })),
      ),
    );
}

/** Merge other tickets INTO this one (their conversations move here; they are closed as merged). */
@Component({
  selector: 'desk-merge-dialog',
  imports: [SdDialog, SdButton, SdCombobox, SdFormField, SdIcon, FormsModule, SdInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Merge tickets" [description]="'Pick duplicates to merge into ' + target()?.displayNumber + '. Their messages move here and they are closed.'">
      <div class="form">
        <sd-form-field label="Find tickets" hint="Search by number, subject, customer or contact.">
          <sd-combobox [search]="search" [value]="pickerValue()" (valueChange)="pick($any($event))" placeholder="#1042 or subject…" emptyText="No matching tickets" />
        </sd-form-field>
        @if (picked().length) {
          <ul class="picked" aria-label="Tickets to merge">
            @for (p of picked(); track p.value) {
              <li>
                <sd-icon name="merge" [size]="14" />
                <span class="sd-truncate">{{ p.label }}</span>
                <button type="button" class="remove" [attr.aria-label]="'Remove ' + p.label" (click)="unpick(p.value)"><sd-icon name="x" [size]="14" /></button>
              </li>
            }
          </ul>
        } @else {
          <p class="sd-muted sd-small">No tickets selected yet.</p>
        }
        <sd-form-field label="Note" optional>
          <input sdInput name="note" [ngModel]="note()" (ngModelChange)="note.set($event)" maxlength="500" />
        </sd-form-field>
      </div>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close(false)">Cancel</button>
        <button sdButton variant="primary" icon="merge" type="button" [disabled]="picked().length === 0" [loading]="data.ctx.busy()" (click)="save()">
          Merge {{ picked().length || '' }}
        </button>
      </div>
    </sd-dialog>
  `,
  styles: `
    .form {
      display: grid;
      gap: var(--sd-space-3);
    }
    .picked {
      display: grid;
      gap: 4px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .picked li {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 6px 8px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-sm);
      background: var(--sd-surface-2);
      min-width: 0;
    }
    .picked span {
      flex: 1;
    }
    .remove {
      display: inline-flex;
      padding: 4px;
      border: 0;
      border-radius: var(--sd-radius-xs);
      background: transparent;
      color: var(--sd-text-3);
      cursor: pointer;
    }
    .remove:hover {
      color: var(--sd-danger);
    }
  `,
})
export class MergeDialog {
  protected readonly data = inject<TicketDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  private readonly tickets = inject(TicketsApi);
  protected readonly target = this.data.ctx.ticket;
  protected readonly picked = signal<SdOption[]>([]);
  protected readonly pickerValue = signal<string | null>(null);
  protected readonly note = signal('');
  private readonly lastResults = new Map<string, SdOption>();
  private readonly base = ticketSearchSource(this.tickets, () => [this.target()?.id ?? '', ...this.picked().map((p) => p.value)]);
  protected readonly search: SdOptionSource = (q) =>
    this.base(q).pipe(tap((options) => options.forEach((o) => this.lastResults.set(o.value, o))));

  protected pick(id: string | null): void {
    if (!id) return;
    const option = this.lastResults.get(id);
    if (option && !this.picked().some((p) => p.value === id)) this.picked.update((list) => [...list, option]);
    // Reset the picker so another ticket can be searched.
    this.pickerValue.set(id);
    queueMicrotask(() => this.pickerValue.set(null));
  }

  protected unpick(id: string): void {
    this.picked.update((list) => list.filter((p) => p.value !== id));
  }

  protected async save(): Promise<void> {
    const id = this.target()?.id;
    if (!id) return;
    const ok = await this.data.ctx.run(
      this.tickets.merge(id, { sourceTicketIds: this.picked().map((p) => p.value), note: this.note().trim() || null }),
      `Merged ${this.picked().length} ticket(s)`,
      'Could not merge',
    );
    if (ok) this.ref.close(true);
  }
}

/** Link to another ticket (related / duplicate / blocks / caused by). */
@Component({
  selector: 'desk-link-dialog',
  imports: [FormsModule, SdDialog, SdButton, SdCombobox, SdFormField, SdInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Link ticket">
      <div class="form">
        <sd-form-field label="Relationship">
          <select sdInput name="type" [ngModel]="type()" (ngModelChange)="type.set($event)">
            <option value="Related">Related to</option>
            <option value="Duplicate">Duplicate of</option>
            <option value="Blocks">Blocks</option>
            <option value="CausedBy">Caused by</option>
          </select>
        </sd-form-field>
        <sd-form-field label="Ticket" required>
          <sd-combobox [search]="search" [value]="targetId()" (valueChange)="targetId.set($any($event))" placeholder="#1042 or subject…" />
        </sd-form-field>
      </div>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close(false)">Cancel</button>
        <button sdButton variant="primary" icon="link" type="button" [disabled]="!targetId()" [loading]="data.ctx.busy()" (click)="save()">Link</button>
      </div>
    </sd-dialog>
  `,
  styles: `
    .form {
      display: grid;
      gap: var(--sd-space-3);
    }
  `,
})
export class LinkDialog {
  protected readonly data = inject<TicketDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  private readonly tickets = inject(TicketsApi);
  protected readonly type = signal('Related');
  protected readonly targetId = signal<string | null>(null);
  protected readonly search = ticketSearchSource(this.tickets, () => [this.data.ctx.id() ?? '']);

  protected async save(): Promise<void> {
    const id = this.data.ctx.id();
    const target = this.targetId();
    if (!id || !target) return;
    const ok = await this.data.ctx.run(this.tickets.addLink(id, { targetTicketId: target, linkType: this.type() }), 'Tickets linked', 'Could not link');
    if (ok) this.ref.close(true);
  }
}

/** Split selected messages into a new ticket; closes with the new ticket. */
@Component({
  selector: 'desk-split-dialog',
  imports: [FormsModule, SdDialog, SdButton, SdFormField, SdInput, SdCheckbox],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Split into a new ticket" description="The selected messages move to a new ticket for the same customer.">
      <div class="form">
        <sd-form-field label="New subject" required [error]="error()">
          <input sdInput name="subject" [ngModel]="subject()" (ngModelChange)="subject.set($event)" maxlength="250" />
        </sd-form-field>
        <fieldset>
          <legend>Messages ({{ selected().length }} selected)</legend>
          @if (threads().length === 0) {
            <p class="sd-muted sd-small">This ticket has no messages that can be split.</p>
          }
          <ul class="threads">
            @for (t of threads(); track t.id) {
              <li>
                <sd-checkbox [value]="selected().includes(t.id)" (valueChange)="toggle(t.id, $event)">
                  <span class="who">{{ t.author }}</span>
                  <span class="sd-muted sd-small"> · {{ t.kind }}</span>
                  <span class="excerpt">{{ t.excerpt }}</span>
                </sd-checkbox>
              </li>
            }
          </ul>
        </fieldset>
        <sd-checkbox [value]="asChild()" (valueChange)="asChild.set($event)">Link the new ticket as a child of this one</sd-checkbox>
      </div>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close(null)">Cancel</button>
        <button sdButton variant="primary" icon="split" type="button" [disabled]="selected().length === 0" [loading]="busy()" (click)="save()">Split</button>
      </div>
    </sd-dialog>
  `,
  styles: `
    .form {
      display: grid;
      gap: var(--sd-space-3);
    }
    fieldset {
      margin: 0;
      padding: var(--sd-space-2) var(--sd-space-3);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      min-width: 0;
    }
    legend {
      padding: 0 4px;
      font-weight: var(--sd-weight-semibold);
    }
    .threads {
      display: grid;
      gap: 6px;
      max-height: 300px;
      overflow: auto;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .who {
      font-weight: var(--sd-weight-medium);
    }
    .excerpt {
      display: block;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
  `,
})
export class SplitDialog {
  protected readonly data = inject<TicketDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<TicketDetailDto | null>>(DialogRef);
  private readonly tickets = inject(TicketsApi);
  private readonly toast = inject(ToastService);
  protected readonly subject = signal(`Split: ${this.data.ctx.ticket()?.subject ?? ''}`.slice(0, 250));
  protected readonly selected = signal<string[]>([]);
  protected readonly asChild = signal(true);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly threads = computed(() =>
    this.data.ctx
      .timeline()
      .filter((i) => i.itemType === 'thread' && i.thread && i.thread.kind !== 'SystemMessage')
      .map((i) => ({
        id: i.thread!.id,
        author: i.thread!.authorName ?? i.thread!.fromAddress ?? 'Unknown',
        kind: i.thread!.visibility === 'Internal' ? 'internal note' : i.thread!.direction === 'Inbound' ? 'customer' : 'reply',
        excerpt: htmlToText(i.thread!.bodyHtml ?? i.thread!.bodyText, 140),
      })),
  );

  protected toggle(id: string, on: boolean): void {
    this.selected.update((ids) => (on ? [...ids, id] : ids.filter((x) => x !== id)));
  }

  protected async save(): Promise<void> {
    const id = this.data.ctx.id();
    if (!id) return;
    if (!this.subject().trim()) {
      this.error.set('The new ticket needs a subject.');
      return;
    }
    this.busy.set(true);
    try {
      const created = await firstValueFrom(
        this.tickets.split(id, { threadIds: this.selected(), subject: this.subject().trim(), linkAsChild: this.asChild() }),
      );
      this.toast.success(`Created ${created.displayNumber}`, 'The selected messages were moved.');
      this.data.ctx.refresh();
      this.data.ctx.loadTimeline();
      this.ref.close(created);
    } catch (err) {
      const e = toastApiError(this.toast, err, 'Could not split');
      this.error.set(e instanceof ApiError ? e.fieldError('subject') : null);
    } finally {
      this.busy.set(false);
    }
  }
}
