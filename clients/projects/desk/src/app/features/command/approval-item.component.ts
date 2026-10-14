import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiError, ApprovalDto, ApprovalsApi, JsonValue, MeStore, MetaApi } from '@sd/core';
import { SdBadge, SdButton, SdIcon, SdRelativeTimePipe, ToastService } from '@sd/ui';

const TYPE_LABELS: Record<string, string> = {
  transfer: 'Department transfer',
  blueprint: 'Workflow transition',
  ai_action: 'AI action',
  custom: 'Request',
};

/** One approval request with approve / reject (and an optional note) for users allowed to decide it. */
@Component({
  selector: 'desk-approval-item',
  imports: [FormsModule, RouterLink, SdBadge, SdButton, SdIcon, SdRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let a = approval();
    <article class="item" [attr.aria-label]="typeLabel() + ' requested by ' + a.requestedByName">
      <div class="head">
        <sd-badge size="sm" [tone]="a.type === 'transfer' ? 'info' : a.type === 'ai_action' ? 'accent' : 'neutral'">{{ typeLabel() }}</sd-badge>
        @if (a.ticketId) {
          <a class="ticket sd-truncate" [routerLink]="['/tickets', a.ticketId]">#{{ a.ticketNumber }} · {{ a.ticketSubject }}</a>
        }
        <span class="sd-spacer"></span>
        @if (a.status !== 'Pending') {
          <sd-badge size="sm" [tone]="a.status === 'Approved' ? 'success' : a.status === 'Rejected' ? 'danger' : 'neutral'">{{ a.status }}</sd-badge>
        }
      </div>
      <p class="meta sd-small sd-muted">
        {{ a.requestedByName }} · {{ a.createdAt | sdRelativeTime }}
        @if (a.status === 'Pending' && a.expiresAt) {
          · expires {{ a.expiresAt | sdRelativeTime }}
        }
      </p>
      @if (detail()) {
        <p class="detail sd-small"><sd-icon name="share" [size]="13" /> {{ detail() }}</p>
      }
      @if (a.reason) {
        <p class="reason">“{{ a.reason }}”</p>
      }
      @if (a.status !== 'Pending') {
        <p class="sd-small sd-muted">
          {{ a.status }} by {{ a.decidedByName ?? 'someone' }}{{ a.decidedAt ? ' · ' : '' }}{{ a.decidedAt ? (a.decidedAt | sdRelativeTime) : '' }}
          @if (a.decisionNote) {
            — {{ a.decisionNote }}
          }
        </p>
      } @else if (canDecide()) {
        <div class="decide">
          <input class="sd-input sd-input-sm" [attr.aria-label]="'Note for request by ' + a.requestedByName" placeholder="Note (optional)" [ngModel]="note()" (ngModelChange)="note.set($event)" maxlength="500" />
          <button sdButton size="sm" type="button" icon="x" [loading]="busy() === 'reject'" [disabled]="!!busy()" (click)="decide(false)">Reject</button>
          <button sdButton size="sm" variant="primary" type="button" icon="check" [loading]="busy() === 'approve'" [disabled]="!!busy()" (click)="decide(true)">Approve</button>
        </div>
        @if (error()) {
          <p class="error sd-small" role="alert">{{ error() }}</p>
        }
      } @else if (a.requestedById === me.user()?.id) {
        <p class="sd-small sd-muted">Waiting for a supervisor’s decision.</p>
      }
    </article>
  `,
  styles: `
    .item {
      display: grid;
      gap: 4px;
      padding: 10px 12px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
    }
    .head {
      display: flex;
      align-items: center;
      gap: 8px;
      min-width: 0;
    }
    .ticket {
      min-width: 0;
      font-weight: var(--sd-weight-medium);
    }
    .detail {
      display: flex;
      align-items: center;
      gap: 6px;
    }
    .reason {
      color: var(--sd-text-2);
      font-style: italic;
    }
    .decide {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      margin-top: 4px;
    }
    .decide input {
      flex: 1 1 160px;
      min-width: 0;
    }
    .error {
      color: var(--sd-danger);
    }
  `,
})
export class ApprovalItemComponent {
  readonly approval = input.required<ApprovalDto>();
  readonly decided = output<ApprovalDto>();

  protected readonly me = inject(MeStore);
  private readonly approvals = inject(ApprovalsApi);
  private readonly meta = inject(MetaApi);
  private readonly toast = inject(ToastService);

  protected readonly note = signal('');
  protected readonly busy = signal<'approve' | 'reject' | null>(null);
  protected readonly error = signal<string | null>(null);

  protected readonly typeLabel = computed(() => TYPE_LABELS[this.approval().type] ?? this.approval().type);
  protected readonly canDecide = computed(() => this.me.can('approvals.decide') && this.approval().requestedById !== this.me.user()?.id);
  protected readonly detail = computed(() => {
    const a = this.approval();
    const p = (a.payload && typeof a.payload === 'object' && !Array.isArray(a.payload) ? a.payload : {}) as Record<string, JsonValue>;
    if (a.type === 'transfer') {
      const dept = this.meta.ticketForm()?.departments.find((d) => d.id === (p['departmentId'] ?? p['DepartmentId']));
      return dept ? `Transfer to ${dept.name}` : 'Transfer to another department';
    }
    const action = p['action'] ?? p['title'];
    return typeof action === 'string' ? action : null;
  });

  constructor() {
    this.meta.load();
  }

  protected decide(approve: boolean): void {
    this.busy.set(approve ? 'approve' : 'reject');
    this.error.set(null);
    this.approvals.decide(this.approval().id, { approve, note: this.note().trim() || null }).subscribe({
      next: (result) => {
        this.busy.set(null);
        this.toast.success(approve ? 'Request approved' : 'Request rejected', this.approval().ticketNumber ? `#${this.approval().ticketNumber}` : undefined);
        this.decided.emit(result);
      },
      error: (err: unknown) => {
        this.busy.set(null);
        const e = ApiError.from(err);
        this.error.set(e.userMessage);
        if (e.status === 409) this.decided.emit(this.approval());
      },
    });
  }
}
