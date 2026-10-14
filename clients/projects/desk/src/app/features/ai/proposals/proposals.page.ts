import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { AiApi, AiProposalDto, AiProposalStatus } from '@sd/core';
import { SdButton, SdCard, SdCombobox, SdEmptyState, SdFormField, SdInput, SdKpiTile, SdOption, SdSkeleton } from '@sd/ui';
import { AiProposalCardComponent } from '../shared/proposal-card.component';
import { aiErrorMessage } from '../shared/ai-ui';

const STATUS_OPTIONS: SdOption[] = [
  { value: 'Proposed', label: 'Waiting for review' },
  { value: 'Applied', label: 'Applied' },
  { value: 'Rejected', label: 'Rejected' },
  { value: 'Reverted', label: 'Reverted' },
  { value: 'Failed', label: 'Failed' },
  { value: 'Expired', label: 'Expired' },
];

const RISK_OPTIONS: SdOption[] = [
  { value: 'High', label: 'High risk' },
  { value: 'Medium', label: 'Medium risk' },
  { value: 'Low', label: 'Low risk' },
];

const ACTION_OPTIONS: SdOption[] = [
  { value: 'set_classification', label: 'Category / product / department' },
  { value: 'set_priority', label: 'Priority' },
  { value: 'add_tags', label: 'Tags' },
  { value: 'send_reply', label: 'Reply to customer' },
  { value: 'request_info', label: 'Request information' },
  { value: 'add_note', label: 'Internal note' },
  { value: 'consequential_review', label: 'Human decision required' },
];

/** Review queue for actions proposed by AI agents and features (approve & apply, reject, revert). */
@Component({
  selector: 'desk-ai-proposals',
  imports: [FormsModule, SdCard, SdButton, SdFormField, SdInput, SdCombobox, SdEmptyState, SdSkeleton, SdKpiTile, AiProposalCardComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="layout">
      <div class="kpis">
        <sd-kpi-tile label="Waiting for review" [value]="counts().proposed" icon="list-checks" interactive (activate)="setStatus('Proposed')" />
        <sd-kpi-tile label="High risk waiting" [value]="counts().highRisk" icon="shield-alert" interactive (activate)="showHighRisk()" />
        <sd-kpi-tile label="Applied (loaded)" [value]="counts().applied" icon="check-check" interactive (activate)="setStatus('Applied')" />
      </div>
      <sd-card>
        <div class="filters">
          <sd-form-field label="Status">
            <sd-combobox [options]="statusOptions" [value]="status()" (valueChange)="setStatus($any($event))" placeholder="Any status" [searchable]="false" />
          </sd-form-field>
          <sd-form-field label="Risk">
            <sd-combobox [options]="riskOptions" [(value)]="risk" placeholder="Any risk" [searchable]="false" />
          </sd-form-field>
          <sd-form-field label="Action">
            <sd-combobox [options]="actionOptions" [(value)]="action" placeholder="Any action" />
          </sd-form-field>
          <sd-form-field label="Search">
            <input sdInput [ngModel]="text()" (ngModelChange)="text.set($event)" placeholder="Ticket, subject, agent or rationale" />
          </sd-form-field>
          <button sdButton icon="refresh" class="refresh" [loading]="loading()" (click)="load()">Refresh</button>
        </div>
      </sd-card>

      @if (error()) {
        <div class="error" role="alert">{{ error() }}</div>
      }
      @if (loading() && items().length === 0) {
        <sd-card><sd-skeleton [lines]="8" /></sd-card>
      } @else if (filtered().length === 0) {
        <sd-card>
          <sd-empty-state icon="list-checks" title="Nothing to review" [message]="status() === 'Proposed' ? 'AI agents have no pending proposals. New ones appear as tickets arrive.' : 'No proposals match these filters.'" />
        </sd-card>
      } @else {
        <p class="sd-small sd-muted">{{ filtered().length }} of {{ items().length }} proposal(s)</p>
        <div class="list">
          @for (p of filtered(); track p.id) {
            <desk-ai-proposal-card [proposal]="p" (changed)="onChanged($event)" />
          }
        </div>
      }
    </div>
  `,
  styles: `
    .layout {
      display: grid;
      gap: var(--sd-space-4);
    }
    .kpis {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
      gap: 12px;
    }
    .filters {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
      gap: 12px;
      align-items: end;
    }
    .refresh {
      justify-self: start;
    }
    .list {
      display: grid;
      gap: 12px;
    }
    .error {
      padding: 10px 14px;
      border: 1px solid var(--sd-danger-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-danger-bg);
      color: var(--sd-danger);
    }
  `,
})
export class ProposalsPage implements OnInit {
  private readonly ai = inject(AiApi);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly statusOptions = STATUS_OPTIONS;
  protected readonly riskOptions = RISK_OPTIONS;
  protected readonly actionOptions = ACTION_OPTIONS;

  protected readonly status = signal<string | null>('Proposed');
  protected readonly risk = signal<string | string[] | null>(null);
  protected readonly action = signal<string | string[] | null>(null);
  protected readonly text = signal('');
  protected readonly items = signal<AiProposalDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly filtered = computed(() => {
    const risk = this.risk();
    const action = this.action();
    const q = this.text().trim().toLowerCase();
    return this.items().filter(
      (p) =>
        (!risk || p.risk === risk) &&
        (!action || p.actionType === action) &&
        (!q ||
          [p.ticketNumber?.toString(), p.ticketSubject, p.agentName, p.rationale, p.actionLabel]
            .filter(Boolean)
            .some((v) => v!.toLowerCase().includes(q))),
    );
  });

  protected readonly counts = computed(() => {
    const items = this.items();
    return {
      proposed: items.filter((p) => p.status === 'Proposed').length,
      highRisk: items.filter((p) => p.status === 'Proposed' && p.risk === 'High').length,
      applied: items.filter((p) => p.status === 'Applied').length,
    };
  });

  ngOnInit(): void {
    this.load();
  }

  protected setStatus(status: string | null): void {
    this.status.set(status);
    this.load();
  }

  protected showHighRisk(): void {
    this.risk.set('High');
    this.setStatus('Proposed');
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.ai
      .proposals({ status: (this.status() as AiProposalStatus | null) ?? null, limit: 300 }, { silent: true })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (items) => {
          this.items.set(items);
          this.loading.set(false);
        },
        error: (err: unknown) => {
          this.loading.set(false);
          this.error.set(aiErrorMessage(err));
        },
      });
  }

  protected onChanged(updated: AiProposalDto): void {
    this.items.update((list) => list.map((p) => (p.id === updated.id ? updated : p)));
    // A failed call may have changed the stored state (e.g. Failed): refresh from the server.
    if (updated.error) this.load();
  }
}
