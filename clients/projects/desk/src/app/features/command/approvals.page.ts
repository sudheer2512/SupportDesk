import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { ApiError, ApprovalDto, ApprovalsApi, MeStore, RealtimeService } from '@sd/core';
import { SdButton, SdEmptyState, SdPageHeader, SdSkeleton, SdTab, SdTabs } from '@sd/ui';
import { ApprovalItemComponent } from './approval-item.component';
import '../tickets/shared/workbench-icons';

type ApprovalTab = 'pending' | 'decided' | 'mine';

/** /approvals — pending requests (approve / reject with `approvals.decide`), decided ones, and my own requests. */
@Component({
  selector: 'desk-approvals-page',
  imports: [NgTemplateOutlet, SdPageHeader, SdTabs, SdTab, SdButton, SdEmptyState, SdSkeleton, ApprovalItemComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header
        title="Approvals"
        icon="shield-check"
        [subtitle]="canDecide() ? 'Transfers, workflow transitions and AI actions waiting for a decision.' : 'Requests you raised or that are addressed to you.'"
        [breadcrumbs]="[{ label: 'Command Center', link: '/command' }, { label: 'Approvals' }]"
      >
        <button sdButton sdPageActions size="sm" icon="refresh" type="button" (click)="load()">Refresh</button>
      </sd-page-header>
      <div class="desk-page__body">
        <sd-tabs [selected]="tab()" (selectedChange)="tab.set($any($event) ?? 'pending')" ariaLabel="Approval lists">
          <sd-tab id="pending" label="Pending" [badge]="pendingCount()">
            <ng-container [ngTemplateOutlet]="list" />
          </sd-tab>
          <sd-tab id="decided" label="Decided">
            <ng-container [ngTemplateOutlet]="list" />
          </sd-tab>
          <sd-tab id="mine" label="My requests">
            <ng-container [ngTemplateOutlet]="list" />
          </sd-tab>
        </sd-tabs>
      </div>
    </div>

    <ng-template #list>
      @if (loading() && items().length === 0) {
        <sd-skeleton [lines]="6" />
      } @else if (error()) {
        <p class="error" role="alert">{{ error() }} <button sdButton size="sm" variant="link" type="button" (click)="load()">Retry</button></p>
      } @else if (visible().length === 0) {
        <sd-empty-state icon="shield-check" [title]="emptyTitle()" compact />
      } @else {
        <div class="items">
          @for (a of visible(); track a.id) {
            <desk-approval-item [approval]="a" (decided)="load()" />
          }
        </div>
      }
    </ng-template>
  `,
  styles: `
    .items {
      display: grid;
      gap: 10px;
      max-width: 860px;
    }
    .error {
      color: var(--sd-danger);
    }
  `,
})
export class ApprovalsPage {
  private readonly approvals = inject(ApprovalsApi);
  private readonly me = inject(MeStore);
  private readonly realtime = inject(RealtimeService);

  protected readonly canDecide = computed(() => this.me.can('approvals.decide'));
  protected readonly tab = signal<ApprovalTab>('pending');
  protected readonly items = signal<ApprovalDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  private readonly pending = signal<number | null>(null);

  protected readonly pendingCount = computed(() => this.pending());
  protected readonly visible = computed(() => {
    const items = this.items();
    return this.tab() === 'decided' ? items.filter((a) => a.status !== 'Pending') : items;
  });
  protected readonly emptyTitle = computed(() =>
    this.tab() === 'pending' ? 'Nothing is waiting for a decision' : this.tab() === 'mine' ? 'You have not requested any approvals' : 'No decided requests yet',
  );

  constructor() {
    effect(() => {
      this.tab();
      untracked(() => this.load());
    });
    const sub = this.realtime.notification$.subscribe((n) => {
      if (n.type.startsWith('approval.')) this.load();
    });
    inject(DestroyRef).onDestroy(() => sub.unsubscribe());
  }

  load(): void {
    const tab = this.tab();
    this.loading.set(true);
    this.error.set(null);
    const call = tab === 'pending' ? this.approvals.list('Pending') : tab === 'mine' ? this.approvals.list(null, true) : this.approvals.list(null);
    call.subscribe({
      next: (list) => {
        this.items.set(list);
        this.loading.set(false);
        if (tab === 'pending') this.pending.set(list.length);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.error.set(ApiError.from(err).userMessage);
      },
    });
  }
}
