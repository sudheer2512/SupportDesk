import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { AiApi, MeStore, SuggestedReplyDto } from '@sd/core';
import { SdButton, SdTooltip, ToastService } from '@sd/ui';
import { aiErrorMessage } from '../shared/ai-ui';

/**
 * Composer action "AI draft reply": asks the AI for a reply grounded in published knowledge and similar resolved
 * tickets, then emits it as HTML for the composer to insert. Hidden without ai.use.
 *
 * `<desk-ai-draft-reply-action [ticketId]="id" (draft)="editor.insert($event)" />`
 */
@Component({
  selector: 'desk-ai-draft-reply-action',
  imports: [SdButton, SdTooltip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (allowed()) {
      <button
        sdButton
        type="button"
        size="sm"
        variant="ghost"
        icon="sparkles"
        [loading]="loading()"
        sdTooltip="Draft a reply from the knowledge base and similar resolved tickets"
        (click)="run()"
      >
        AI draft
      </button>
    }
  `,
})
export class AiDraftReplyActionComponent {
  private readonly ai = inject(AiApi);
  private readonly me = inject(MeStore);
  private readonly toast = inject(ToastService);

  readonly ticketId = input.required<string>();
  /** Optional steering for the draft ("mention the upgrade", "be brief"). */
  readonly instructions = input<string | null>(null);
  /** The drafted reply (HTML, with [n] citation markers). */
  readonly draft = output<string>();
  /** The full suggestion (citations, confidence) for hosts that show sources. */
  readonly suggestion = output<SuggestedReplyDto>();

  protected readonly loading = signal(false);
  protected readonly allowed = computed(() => this.me.can('ai.use') && this.me.can('tickets.reply'));

  protected run(): void {
    if (this.loading()) return;
    this.loading.set(true);
    this.ai.suggestReply(this.ticketId(), { instructions: this.instructions() }, { silent: true }).subscribe({
      next: (r) => {
        this.loading.set(false);
        this.draft.emit(r.bodyHtml);
        this.suggestion.emit(r);
        if (!r.grounded) this.toast.warning('Draft is not grounded in knowledge', 'No matching article or resolved ticket was found — review it carefully.');
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.toast.error('Could not draft a reply', aiErrorMessage(err));
      },
    });
  }
}
