import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { MeStore, TicketDetailDto, TicketsApi, isApiError } from '@sd/core';
import { SdBadge, SdBreadcrumb, SdButton, SdCard, SdEmptyState, SdPageHeader, SdPriorityBadge, SdRelativeTimePipe, SdRichTextEditor, SdSkeleton, SdStatusPill, ToastService } from '@sd/ui';
import { AiDraftReplyActionComponent } from '../composer/ai-draft-reply-action.component';
import { TicketAiPanelComponent } from '../ticket-ai-panel/ticket-ai-panel.component';

/**
 * Full-page AI assistance for one ticket (/ai/tickets/:id): the ticket at a glance, a reply composer that accepts
 * AI drafts, and the same AI panel the ticket workbench embeds. Reached from search results, proposals and anomaly
 * drill-downs, or by ticket number.
 */
@Component({
  selector: 'desk-ticket-ai-page',
  imports: [
    FormsModule,
    RouterLink,
    SdPageHeader,
    SdCard,
    SdButton,
    SdBadge,
    SdPriorityBadge,
    SdStatusPill,
    SdSkeleton,
    SdEmptyState,
    SdRichTextEditor,
    SdRelativeTimePipe,
    TicketAiPanelComponent,
    AiDraftReplyActionComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header [title]="title()" icon="sparkles" [subtitle]="ticket()?.subject ?? null" [breadcrumbs]="crumbs()">
        <form sdPageActions class="jump" (ngSubmit)="jump()">
          <label for="ticket-jump" class="sd-visually-hidden">Open ticket number</label>
          <input id="ticket-jump" class="sd-input sd-input-sm" name="n" [(ngModel)]="number" placeholder="Ticket #" inputmode="numeric" />
          <button sdButton size="sm" type="submit" icon="arrow-right" [disabled]="!number.trim()">Open</button>
        </form>
        @if (ticket(); as t) {
          <a sdPageActions sdButton size="sm" variant="ghost" icon="external-link" [routerLink]="['/tickets', t.id]">Ticket</a>
        }
      </sd-page-header>
      <div class="desk-page__body">
        @if (notFound()) {
          <sd-card><sd-empty-state icon="ticket" title="Ticket not found" message="It does not exist or is outside your ticket scope." /></sd-card>
        } @else {
          <div class="cols">
            <div class="left">
              <sd-card title="Ticket">
                @if (ticket(); as t) {
                  <dl class="facts">
                    <div><dt>Status</dt><dd><sd-status-pill [label]="t.status" [color]="t.statusColor" /></dd></div>
                    <div><dt>Priority</dt><dd><sd-priority-badge [priority]="t.priority" /></dd></div>
                    <div><dt>Customer</dt><dd>{{ t.customerName ?? '—' }}</dd></div>
                    <div><dt>Contact</dt><dd>{{ t.contactName ?? '—' }}</dd></div>
                    <div><dt>Channel</dt><dd>{{ t.channel }}</dd></div>
                    <div><dt>Category</dt><dd>{{ t.categoryName ?? '—' }}{{ t.subcategoryName ? ' › ' + t.subcategoryName : '' }}</dd></div>
                    <div><dt>Product</dt><dd>{{ t.productName ?? '—' }}</dd></div>
                    <div><dt>Assignee</dt><dd>{{ t.assigneeName ?? 'Unassigned' }}</dd></div>
                    <div><dt>Created</dt><dd>{{ t.createdAt | sdRelativeTime }}</dd></div>
                    <div><dt>Sentiment</dt><dd><sd-badge size="sm">{{ t.sentiment }}</sd-badge></dd></div>
                  </dl>
                } @else {
                  <sd-skeleton [lines]="6" />
                }
              </sd-card>
              @if (canReply() && ticket()) {
                <sd-card title="Reply">
                  <div sdCardActions>
                    <desk-ai-draft-reply-action [ticketId]="id()" (draft)="reply.set($event)" />
                  </div>
                  <sd-rich-text-editor [(value)]="reply" placeholder="Write a reply, or insert an AI draft…" toolbar="compact" minHeight="160px" ariaLabel="Reply to the customer" (submitted)="send()" />
                  <div class="send">
                    <span class="sd-xs sd-muted">Remove the [n] source markers before sending. Ctrl+Enter sends.</span>
                    <button sdButton variant="primary" icon="send" [loading]="sending()" [disabled]="!hasText()" (click)="send()">Send reply</button>
                  </div>
                </sd-card>
              }
            </div>
            <desk-ticket-ai-panel class="right" [ticketId]="id()" (insertReply)="reply.set($event)" (ticketChanged)="load()" />
          </div>
        }
      </div>
    </div>
  `,
  styles: `
    .jump {
      display: flex;
      gap: 6px;
      align-items: center;
    }
    .jump input {
      width: 110px;
    }
    .cols {
      display: grid;
      grid-template-columns: minmax(0, 5fr) minmax(0, 6fr);
      gap: var(--sd-space-4);
      align-items: start;
    }
    @media (max-width: 1100px) {
      .cols {
        grid-template-columns: 1fr;
      }
    }
    .left {
      display: grid;
      gap: var(--sd-space-4);
    }
    .facts {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(160px, 1fr));
      gap: 12px 16px;
      margin: 0;
    }
    dt {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    dd {
      margin: 2px 0 0;
    }
    .send {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 12px;
      margin-top: 10px;
    }
  `,
})
export class TicketAiPage {
  private readonly tickets = inject(TicketsApi);
  private readonly me = inject(MeStore);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = input.required<string>();

  protected readonly ticket = signal<TicketDetailDto | null>(null);
  protected readonly notFound = signal(false);
  protected readonly reply = signal('');
  protected readonly sending = signal(false);
  protected number = '';
  protected readonly canReply = computed(() => this.me.can('tickets.reply'));
  protected readonly hasText = computed(() => this.reply().replace(/<[^>]+>/g, '').trim().length > 0);
  protected readonly title = computed(() => (this.ticket() ? `AI assistant · ${this.ticket()!.displayNumber}` : 'AI assistant'));
  protected readonly crumbs = computed<SdBreadcrumb[]>(() => [{ label: 'AI Assistant', link: '/ai' }, { label: this.ticket()?.displayNumber ?? 'Ticket' }]);

  constructor() {
    effect(() => {
      this.id();
      untracked(() => {
        this.reply.set('');
        this.ticket.set(null);
        this.notFound.set(false);
        this.load();
      });
    });
  }

  protected load(): void {
    this.tickets
      .get(this.id())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (t) => {
          this.ticket.set(t);
          this.notFound.set(false);
        },
        error: (err: unknown) => {
          if (isApiError(err) && err.status === 404) this.notFound.set(true);
        },
      });
  }

  protected jump(): void {
    const n = this.number.replace(/[^0-9]/g, '');
    if (!n) return;
    this.tickets.getByNumber(n).subscribe({
      next: (t) => {
        this.number = '';
        void this.router.navigate(['/ai/tickets', t.id]);
      },
      error: () => this.toast.warning(`Ticket ${n} was not found`),
    });
  }

  protected send(): void {
    const t = this.ticket();
    if (!t || !this.hasText() || this.sending()) return;
    this.sending.set(true);
    this.tickets.reply(t.id, { bodyHtml: this.reply(), force: true }).subscribe({
      next: () => {
        this.sending.set(false);
        this.reply.set('');
        this.toast.success('Reply sent');
        this.load();
      },
      error: (err: unknown) => {
        this.sending.set(false);
        this.toast.error('The reply was not sent', isApiError(err) ? err.userMessage : undefined);
      },
    });
  }
}
