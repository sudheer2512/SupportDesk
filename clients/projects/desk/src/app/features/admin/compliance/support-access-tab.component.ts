import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { ApiError, SupportAccessApi, SupportAccessDto } from '@sd/core';
import { SdBadge, SdButton, SdDateTimePipe, SdDialogService, SdEmptyState, SdRelativeTimePipe, SdSkeleton, ToastService } from '@sd/ui';
import { SAAS_CSS, requestTone } from '../../subscription/saas-ui';

/**
 * Platform support access, tenant side: SupportDesk operators never sign in as your users; they request time-boxed,
 * read-only diagnostics access that an administrator approves, rejects or revokes here. Every step is audited.
 */
@Component({
  selector: 'desk-support-access-tab',
  imports: [SdBadge, SdButton, SdDateTimePipe, SdEmptyState, SdRelativeTimePipe, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p class="muted small">SupportDesk staff can only see usage, configuration counts and recent audit entries while access is active — never tickets or customer data.</p>
    @if (error(); as e) {
      <sd-empty-state icon="circle-alert" title="Requests could not be loaded" [message]="e"><button sdButton (click)="load()">Try again</button></sd-empty-state>
    } @else if (rows() === null) {
      <sd-skeleton [lines]="3" height="40px" />
    } @else if (rows()!.length === 0) {
      <sd-empty-state icon="life-buoy" title="No support access requests" />
    } @else {
      <div class="table-wrap">
        <table class="data">
          <caption class="sd-visually-hidden">Support access requests</caption>
          <thead><tr><th scope="col">Requested by</th><th scope="col">Reason</th><th scope="col">Status</th><th scope="col"><span class="sd-visually-hidden">Actions</span></th></tr></thead>
          <tbody>
            @for (g of rows(); track g.id) {
              <tr>
                <td>{{ g.operatorName }}<div class="muted small">{{ g.requestedAt | sdDateTime }} · {{ g.durationMinutes }} min</div></td>
                <td>{{ g.reason }}</td>
                <td>
                  <sd-badge size="sm" [tone]="tone(g.status)" [dot]="true">{{ g.status }}</sd-badge>
                  @if (g.active && g.expiresAt) {
                    <div class="muted small">ends {{ g.expiresAt | sdRelativeTime }}</div>
                  } @else if (g.decidedByName) {
                    <div class="muted small">{{ g.decidedByName }}{{ g.decisionNote ? ': ' + g.decisionNote : '' }}</div>
                  }
                </td>
                <td class="actions">
                  @if (g.status === 'Pending') {
                    <button sdButton size="sm" variant="ghost" [loading]="busyId() === g.id" (click)="decide(g, false)">Reject</button>
                    <button sdButton size="sm" variant="primary" [loading]="busyId() === g.id" (click)="decide(g, true)">Approve</button>
                  } @else if (g.active) {
                    <button sdButton size="sm" variant="danger" [loading]="busyId() === g.id" (click)="revoke(g)">Revoke</button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styles: SAAS_CSS,
})
export class SupportAccessTabComponent implements OnInit {
  private readonly api = inject(SupportAccessApi);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(SdDialogService);
  protected readonly rows = signal<SupportAccessDto[] | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busyId = signal<string | null>(null);
  protected readonly tone = requestTone;

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.error.set(null);
    this.api.list().subscribe({ next: (r) => this.rows.set(r), error: (e: ApiError) => this.error.set(e.userMessage) });
  }

  protected async decide(g: SupportAccessDto, approve: boolean): Promise<void> {
    if (approve) {
      const ok = await this.dialog.confirm({
        title: `Approve support access for ${g.operatorName}?`,
        message: `Read-only diagnostics for ${g.durationMinutes} minutes. Reason: ${g.reason}`,
        confirmLabel: 'Approve',
      });
      if (!ok) return;
    }
    this.busyId.set(g.id);
    this.api.decide(g.id, { approve, note: null }).subscribe({
      next: () => {
        this.busyId.set(null);
        this.toast.success(approve ? 'Access approved' : 'Request rejected');
        this.load();
      },
      error: (e: ApiError) => {
        this.busyId.set(null);
        this.toast.error('Could not save', e.userMessage);
      },
    });
  }

  protected revoke(g: SupportAccessDto): void {
    this.busyId.set(g.id);
    this.api.revoke(g.id).subscribe({
      next: () => {
        this.busyId.set(null);
        this.toast.success('Access revoked');
        this.load();
      },
      error: () => this.busyId.set(null),
    });
  }
}
