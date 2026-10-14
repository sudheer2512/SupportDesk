import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AgentListItemDto, AgentsAdminApi, DelegationDto, Guid, MeStore, WorkforceApi, saveErrorMessage, validationMessages } from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCheckbox,
  SdDateInput,
  SdDateTimePipe,
  SdDialog,
  SdDialogService,
  SdEmptyState,
  SdFormField,
  SdInput,
  SdPageHeader,
  SdSkeleton,
  SdTone,
  ToastService,
} from '@sd/ui';
import { ModuleTabsComponent } from '../customers/shared/module-tabs.component';
import { SECTION_STYLES, TABLE_STYLES } from '../customers/shared/styles';
import { FORM_STYLES, PAGE_STYLES, TEAMS_TABS, errorText } from './shared/org-ui';

/** /teams/delegations — out-of-office coverage: while away, new tickets and approvals go to a delegate for a time box. */
@Component({
  selector: 'desk-delegations-page',
  imports: [FormsModule, SdPageHeader, SdButton, SdBadge, SdSkeleton, SdEmptyState, SdCheckbox, SdDateTimePipe, ModuleTabsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Teams & Agents" subtitle="While someone is away, their new tickets and approval requests go to a delegate." icon="users">
        <button sdButton sdPageActions variant="primary" icon="plus" (click)="edit(null)">New delegation</button>
      </sd-page-header>
      <desk-module-tabs [tabs]="tabs" ariaLabel="Teams & Agents sections" />
      <div class="desk-page__body">
        <div class="toolbar">
          <sd-checkbox [value]="includeEnded()" (valueChange)="includeEnded.set($event); load()">Show ended</sd-checkbox>
          <span class="sd-spacer"></span>
          <button sdButton size="sm" variant="ghost" icon="refresh" (click)="load()" [loading]="loading()">Refresh</button>
        </div>
        @if (error()) {
          <div class="error-box" role="alert"><span>{{ error() }}</span><button sdButton size="sm" (click)="load()">Try again</button></div>
        } @else if (loading() && rows().length === 0) {
          <div aria-busy="true"><sd-skeleton [lines]="5" /></div>
        } @else if (rows().length === 0) {
          <sd-empty-state icon="share" title="No delegations" message="Set up a delegate before going on leave so nothing waits for you." />
        } @else {
          <div class="dt-wrap">
            <table class="dt">
              <caption class="sd-visually-hidden">Delegations</caption>
              <thead>
                <tr>
                  <th scope="col">Away</th>
                  <th scope="col">Delegate</th>
                  <th scope="col">From</th>
                  <th scope="col">To</th>
                  <th scope="col">Covers</th>
                  <th scope="col">State</th>
                  <th scope="col"><span class="sd-visually-hidden">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                @for (d of rows(); track d.id) {
                  <tr>
                    <td>{{ d.userName }}</td>
                    <td>{{ d.delegateName }}</td>
                    <td class="nowrap">{{ d.startsAt | sdDateTime: 'datetime' : 'short' }}</td>
                    <td class="nowrap">{{ d.endsAt | sdDateTime: 'datetime' : 'short' }}</td>
                    <td class="small">
                      New tickets{{ d.includeApprovals ? ', approvals' : '' }}{{ d.reassignOpenTickets ? ', open tickets' : '' }}
                      @if (d.reason) {
                        <span class="muted"> — {{ d.reason }}</span>
                      }
                    </td>
                    <td><sd-badge size="sm" [tone]="tone(d.state)" dot>{{ d.state }}</sd-badge></td>
                    <td class="row-actions">
                      @if (d.state !== 'Ended') {
                        <button sdButton size="sm" variant="ghost" icon="pencil" iconOnly [attr.aria-label]="'Edit delegation of ' + d.userName" (click)="edit(d)"></button>
                        <button sdButton size="sm" variant="ghost" (click)="end(d)">End now</button>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </div>
    </div>
  `,
  styles: [TABLE_STYLES, SECTION_STYLES, PAGE_STYLES],
})
export class DelegationsPage {
  private readonly api = inject(WorkforceApi);
  private readonly dialogs = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  protected readonly tabs = TEAMS_TABS;
  protected readonly rows = signal<DelegationDto[]>([]);
  protected readonly includeEnded = signal(false);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  constructor() {
    this.load();
  }

  protected tone(state: string): SdTone {
    return state === 'Active' ? 'success' : state === 'Scheduled' ? 'info' : 'neutral';
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.delegations(this.includeEnded()).subscribe({
      next: (r) => {
        this.rows.set(r);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.error.set(errorText(err));
        this.loading.set(false);
      },
    });
  }

  protected edit(d: DelegationDto | null): void {
    this.dialogs.open<DelegationDto, DelegationDto | null>(DelegationFormDialog, { data: d }).closed.subscribe((saved) => {
      if (!saved) return;
      this.toast.success(d ? 'Delegation updated' : 'Delegation created', `${saved.userName} → ${saved.delegateName}`);
      this.load();
    });
  }

  protected async end(d: DelegationDto): Promise<void> {
    const ok = await this.dialogs.confirm({ title: 'End this delegation now?', message: `${d.userName} → ${d.delegateName}`, confirmLabel: 'End now' });
    if (!ok) return;
    this.api.endDelegation(d.id).subscribe({
      next: () => this.load(),
      error: (err: unknown) => this.toast.error('Could not end the delegation', saveErrorMessage(err)),
    });
  }
}

/** Create / edit a delegation (absent agent = me unless I manage agents). */
@Component({
  selector: 'desk-delegation-form-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdCheckbox, SdDateInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="d ? 'Edit delegation' : 'New delegation'" description="New assignments and approval requests for the absent agent go to the delegate during the window.">
      <form id="delegation-form" class="form" (ngSubmit)="save()" novalidate>
        <div class="two">
          <sd-form-field label="Away" [error]="errors()['userId']">
            <select sdInput name="away" [(ngModel)]="userId" [disabled]="!canManage || !!d">
              <option [ngValue]="null">Me</option>
              @for (a of agents(); track a.userId) {
                <option [ngValue]="a.userId">{{ a.name }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Delegate" required [error]="errors()['delegateUserId']">
            <select sdInput name="delegate" [(ngModel)]="delegateUserId" required>
              <option [ngValue]="null" disabled>Choose an agent…</option>
              @for (a of agents(); track a.userId) {
                <option [ngValue]="a.userId">{{ a.name }}{{ a.isLightAgent ? ' (light)' : '' }}</option>
              }
            </select>
          </sd-form-field>
        </div>
        <div class="two">
          <sd-form-field label="From" required [error]="errors()['startsAt']">
            <sd-date-input type="datetime" [(value)]="startsAt" ariaLabel="Delegation starts" />
          </sd-form-field>
          <sd-form-field label="To" required [error]="errors()['endsAt']">
            <sd-date-input type="datetime" [(value)]="endsAt" ariaLabel="Delegation ends" />
          </sd-form-field>
        </div>
        <sd-form-field label="Reason" optional>
          <input sdInput name="reason" [(ngModel)]="reason" maxlength="300" />
        </sd-form-field>
        <div class="checks">
          <sd-checkbox [(value)]="includeApprovals">Route approval requests to the delegate</sd-checkbox>
          <sd-checkbox [(value)]="reassignOpen">Also move open tickets when the window starts</sd-checkbox>
        </div>
        @if (errors()['reassignOpenTickets']) {
          <p class="error">{{ errors()['reassignOpenTickets'] }}</p>
        }
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="delegation-form" [loading]="saving()" [disabled]="!delegateUserId || !startsAt() || !endsAt()">Save</button>
      </div>
    </sd-dialog>
  `,
  styles: FORM_STYLES,
})
export class DelegationFormDialog {
  protected readonly d = inject<DelegationDto | null>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<DelegationDto>>(DialogRef);
  private readonly api = inject(WorkforceApi);
  private readonly agentsApi = inject(AgentsAdminApi);
  private readonly me = inject(MeStore);
  protected readonly canManage = this.me.canAny(['agents.manage', 'admin.users']);
  protected userId: Guid | null = this.d && this.d.userId !== this.me.user()?.id ? this.d.userId : null;
  protected delegateUserId: Guid | null = this.d?.delegateUserId ?? null;
  protected reason = this.d?.reason ?? '';
  protected readonly startsAt = signal<string | null>(this.d?.startsAt ?? new Date().toISOString());
  protected readonly endsAt = signal<string | null>(this.d?.endsAt ?? new Date(Date.now() + 3 * 86_400_000).toISOString());
  protected readonly includeApprovals = signal(this.d?.includeApprovals ?? true);
  protected readonly reassignOpen = signal(this.d?.reassignOpenTickets ?? false);
  protected readonly agents = signal<AgentListItemDto[]>([]);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  constructor() {
    this.agentsApi.list({ status: 'Active', limit: 200 }).subscribe({
      next: (r) => this.agents.set(r.items),
      error: (err: unknown) => this.formError.set(errorText(err)),
    });
  }

  protected save(): void {
    if (!this.delegateUserId || !this.startsAt() || !this.endsAt()) return;
    this.saving.set(true);
    this.formError.set(null);
    this.api
      .saveDelegation(this.d?.id ?? null, {
        userId: this.d?.userId ?? this.userId,
        delegateUserId: this.delegateUserId,
        startsAt: this.startsAt()!,
        endsAt: this.endsAt()!,
        reason: this.reason.trim() || null,
        reassignOpenTickets: this.reassignOpen(),
        includeApprovals: this.includeApprovals(),
      })
      .subscribe({
        next: (r) => this.ref.close(r),
        error: (err: unknown) => {
          this.saving.set(false);
          this.errors.set(validationMessages(err));
          this.formError.set(saveErrorMessage(err));
        },
      });
  }
}
