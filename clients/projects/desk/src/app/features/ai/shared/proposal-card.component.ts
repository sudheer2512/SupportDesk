import { ChangeDetectionStrategy, Component, booleanAttribute, computed, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AiApi, AiProposalDto, MeStore, isApiError } from '@sd/core';
import { SdBadge, SdButton, SdDialogService, SdIcon, SdRelativeTimePipe, ToastService } from '@sd/ui';
import { AiChangesComponent, AiMeterComponent, proposalTone, riskTone } from './ai-ui';

/**
 * One AI action proposal: what the AI wants to do (before → after), why, how sure it is and how risky it is, with
 * approve & apply / reject / revert for people holding ai.approve_actions. Emits the updated proposal.
 */
@Component({
  selector: 'desk-ai-proposal-card',
  imports: [RouterLink, SdBadge, SdButton, SdIcon, SdRelativeTimePipe, AiChangesComponent, AiMeterComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let p = proposal();
    <article class="card" [class.pending]="p.status === 'Proposed'" [attr.aria-label]="p.actionLabel">
      <header class="head">
        <div class="title">
          <sd-icon [name]="icon()" [size]="16" aria-hidden="true" />
          <strong>{{ p.actionLabel }}</strong>
          @if (showTicket() && p.ticketNumber) {
            <a class="ticket" [routerLink]="['/ai/tickets', p.ticketId]">#{{ p.ticketNumber }} {{ p.ticketSubject }}</a>
          }
        </div>
        <div class="badges">
          <sd-badge size="sm" [tone]="risk()" variant="outline">{{ p.risk }} risk</sd-badge>
          <sd-badge size="sm" [tone]="status()" dot>{{ p.status }}</sd-badge>
        </div>
      </header>
      <desk-ai-changes [changes]="p.changes" />
      @if (p.rationale) {
        <p class="why">{{ p.rationale }}</p>
      }
      @if (p.error) {
        <p class="err" role="alert"><sd-icon name="circle-alert" [size]="14" />{{ p.error }}</p>
      }
      <footer class="foot">
        <desk-ai-meter [value]="p.confidence" label="Confidence" />
        <span class="meta sd-small sd-muted">
          {{ p.agentName ?? 'AI' }} · {{ p.createdAt | sdRelativeTime }}
          @if (p.decidedByName) {
            · decided by {{ p.decidedByName }}
          }
          @if (!p.requiresApproval && p.status === 'Applied') {
            · applied automatically
          }
          @if (p.revertedAt) {
            · reverted {{ p.revertedAt | sdRelativeTime }}
          }
        </span>
        @if (canDecide()) {
          <span class="actions">
            @if (p.status === 'Proposed') {
              <button sdButton size="sm" variant="ghost" icon="x" [loading]="busy() === 'reject'" [disabled]="!!busy()" (click)="reject()">Reject</button>
              <button sdButton size="sm" variant="primary" icon="check" [loading]="busy() === 'approve'" [disabled]="!!busy()" (click)="approve()">Approve &amp; apply</button>
            } @else if (p.status === 'Applied' && p.reversible) {
              <button sdButton size="sm" icon="undo" [loading]="busy() === 'revert'" [disabled]="!!busy()" (click)="revert()">Revert</button>
            }
          </span>
        }
      </footer>
    </article>
  `,
  styles: `
    .card {
      display: grid;
      gap: 10px;
      padding: 14px 16px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    .card.pending {
      border-color: var(--sd-primary-border);
    }
    .head {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      justify-content: space-between;
      align-items: center;
    }
    .title {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      align-items: center;
      min-width: 0;
    }
    .ticket {
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .badges {
      display: flex;
      gap: 6px;
    }
    .why {
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .err {
      display: flex;
      gap: 6px;
      align-items: center;
      color: var(--sd-danger);
      font-size: var(--sd-text-sm);
    }
    .foot {
      display: flex;
      flex-wrap: wrap;
      gap: 12px;
      align-items: center;
    }
    .meta {
      flex: 1;
      min-width: 180px;
    }
    .actions {
      display: flex;
      gap: 6px;
      margin-left: auto;
    }
  `,
})
export class AiProposalCardComponent {
  private readonly ai = inject(AiApi);
  private readonly me = inject(MeStore);
  private readonly dialogs = inject(SdDialogService);
  private readonly toast = inject(ToastService);

  readonly proposal = input.required<AiProposalDto>();
  readonly showTicket = input(true, { transform: booleanAttribute });
  readonly changed = output<AiProposalDto>();

  protected readonly busy = signal<'approve' | 'reject' | 'revert' | null>(null);
  protected readonly canDecide = computed(() => this.me.can('ai.approve_actions'));
  protected readonly risk = computed(() => riskTone(this.proposal().risk));
  protected readonly status = computed(() => proposalTone(this.proposal().status));
  protected readonly icon = computed(() => {
    switch (this.proposal().actionType) {
      case 'set_classification':
        return 'tag';
      case 'set_priority':
        return 'flag';
      case 'add_tags':
        return 'tag';
      case 'send_reply':
        return 'send';
      case 'request_info':
        return 'help';
      case 'add_note':
        return 'pencil';
      case 'consequential_review':
        return 'shield-alert';
      default:
        return 'sparkles';
    }
  });

  protected async approve(): Promise<void> {
    const p = this.proposal();
    if (p.risk === 'High' || p.actionType === 'send_reply' || p.actionType === 'request_info') {
      const ok = await this.dialogs.confirm({
        title: p.actionType === 'consequential_review' ? 'Record your decision?' : `${p.actionLabel}?`,
        message:
          p.actionType === 'consequential_review'
            ? 'This records that you approved handling the request. The AI does not take any financial, account or contract action.'
            : p.actionType === 'send_reply' || p.actionType === 'request_info'
              ? 'The message is sent to the customer under your name and cannot be undone.'
              : 'This change will be applied to the ticket.',
        confirmLabel: 'Approve & apply',
      });
      if (!ok) return;
    }
    this.run('approve', () => this.ai.approve(p.id), 'Applied');
  }

  protected reject(): void {
    this.run('reject', () => this.ai.reject(this.proposal().id), 'Rejected');
  }

  protected async revert(): Promise<void> {
    const ok = await this.dialogs.confirm({
      title: 'Revert this AI action?',
      message: 'Fields the AI changed go back to their previous values. Changes people made since then are kept.',
      confirmLabel: 'Revert',
    });
    if (ok) this.run('revert', () => this.ai.revert(this.proposal().id), 'Reverted');
  }

  private run(kind: 'approve' | 'reject' | 'revert', call: () => ReturnType<AiApi['approve']>, done: string): void {
    this.busy.set(kind);
    call().subscribe({
      next: (updated) => {
        this.busy.set(null);
        this.toast.success(`${done}: ${updated.actionLabel}`);
        this.changed.emit(updated);
      },
      error: (err: unknown) => {
        this.busy.set(null);
        this.toast.error('The AI action could not be completed', isApiError(err) ? err.userMessage : undefined);
        // The proposal may have changed state (e.g. failed); let the container reload it.
        this.changed.emit({ ...this.proposal(), error: isApiError(err) ? err.userMessage : this.proposal().error });
      },
    });
  }
}
