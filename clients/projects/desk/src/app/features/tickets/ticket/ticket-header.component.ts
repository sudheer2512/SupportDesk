import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, linkedSignal, output, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MeStore, PRIORITIES, Priority, RealtimeService } from '@sd/core';
import {
  SdAvatar,
  SdBadge,
  SdButton,
  SdCombobox,
  SdDropdownMenu,
  SdIcon,
  SdOption,
  SdSlaCountdown,
  SdTooltip,
  ToastService,
} from '@sd/ui';
import {
  agentOptions,
  allowedStatuses,
  channelIcon,
  channelLabel,
  departmentOptions,
  entitlementLabel,
  entitlementTone,
  statusOptions,
  teamOptions,
} from '../shared/ticket-ui';
import { TicketActions } from './ticket-actions.service';
import { TicketContext } from './ticket-context.service';

/**
 * Ticket header: number, inline-editable subject, quick editors (status, priority, assignee, department, team —
 * each respecting `ticket.can`), SLA countdowns, lock indicator, presence of other agents (viewing / replying)
 * and the actions menu.
 */
@Component({
  selector: 'desk-ticket-header',
  imports: [FormsModule, RouterLink, SdAvatar, SdBadge, SdButton, SdCombobox, SdDropdownMenu, SdIcon, SdSlaCountdown, SdTooltip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (ctx.ticket(); as t) {
      <header class="hdr">
        <div class="row1">
          <a class="back" routerLink="/tickets" aria-label="Back to the ticket list"><sd-icon name="arrow-left" [size]="18" /></a>
          <button type="button" class="num" (click)="copyLink()" [sdTooltip]="'Copy link to ' + t.displayNumber" [attr.aria-label]="'Copy link to ' + t.displayNumber">
            {{ t.displayNumber }}
          </button>
          @if (editingSubject()) {
            <form class="subject-form" (ngSubmit)="saveSubject()">
              <input
                #subjectInput
                class="sd-input subject-input"
                name="subject"
                aria-label="Subject"
                maxlength="250"
                [ngModel]="subjectDraft()"
                (ngModelChange)="subjectDraft.set($event)"
                (keydown.escape)="editingSubject.set(false)"
              />
              <button sdButton size="sm" variant="primary" type="submit" [loading]="ctx.busy()">Save</button>
              <button sdButton size="sm" type="button" (click)="editingSubject.set(false)">Cancel</button>
            </form>
          } @else {
            <h1 class="subject" [title]="t.subject">{{ t.subject }}</h1>
            @if (t.can.edit) {
              <button sdButton size="sm" variant="ghost" iconOnly icon="pencil" type="button" aria-label="Edit subject" sdTooltip="Edit subject" (click)="editSubject()"></button>
            }
          }
          <span class="sd-spacer"></span>
          @if (viewers().length) {
            <div class="presence" role="group" [attr.aria-label]="presenceLabel()">
              @for (v of viewers(); track v.userId) {
                <span class="viewer" [class.is-typing]="v.activity !== 'viewing'" [sdTooltip]="v.name + ' — ' + v.activity">
                  <sd-avatar [name]="v.name" size="sm" />
                </span>
              }
            </div>
          }
          <button sdButton size="sm" variant="ghost" iconOnly icon="sliders" type="button" class="details-btn" aria-label="Ticket details" sdTooltip="Details, tasks, time…" (click)="openDetails.emit()"></button>
          <sd-dropdown-menu variant="secondary" size="sm" icon="ellipsis" iconOnly [caret]="false" align="end" ariaLabel="More actions" [items]="actions.menu()" />
        </div>

        @if (typing().length) {
          <p class="typing" role="status">
            <sd-icon name="pencil" [size]="13" />
            {{ typingLabel() }}
          </p>
        }

        <div class="facts">
          <span class="fact"><sd-icon [name]="channelIcon(t.channel)" [size]="14" />{{ channelLabel(t.channel) }}</span>
          @if (t.customerName) {
            <span class="fact"><sd-icon name="building" [size]="14" />{{ t.customerName }}@if (t.customerTier) {<span class="sd-muted">&nbsp;· {{ t.customerTier }}</span>}</span>
          }
          @if (t.contactName) {
            <span class="fact"><sd-icon name="user" [size]="14" />{{ t.contactName }}</span>
          }
          @if (t.contactIsVip) {
            <sd-badge tone="accent" icon="star" size="sm">VIP</sd-badge>
          }
          @if (t.isEscalated) {
            <sd-badge tone="danger" icon="arrow-up-right" size="sm">Escalated · L{{ t.escalationLevel }}</sd-badge>
          }
          <sd-badge [tone]="entitlementTone(t.entitlementStatus)" icon="scale" size="sm" [sdTooltip]="t.entitlementNote ?? t.contractName ?? ''">
            {{ entitlementLabel(t.entitlementStatus) }}
          </sd-badge>
          @if (t.awaitingCustomerConfirmation) {
            <sd-badge tone="info" icon="hourglass" size="sm">Awaiting customer confirmation</sd-badge>
          }
          @if (ctx.lockedByOther()) {
            <sd-badge tone="warning" icon="lock" size="sm">Locked by {{ t.lock!.userName }}</sd-badge>
          } @else if (t.lock) {
            <sd-badge tone="neutral" icon="lock" size="sm">Locked by you</sd-badge>
          }
          @if (t.mergedIntoTicketId) {
            <a class="merged" [routerLink]="['/tickets', t.mergedIntoTicketId]"><sd-badge tone="neutral" icon="merge" size="sm">Merged — open target</sd-badge></a>
          }
          <span class="sd-spacer"></span>
          @if (!isDone()) {
            @if (!t.firstResponseAt && t.firstResponseDueAt) {
              <sd-sla-countdown [dueAt]="t.firstResponseDueAt" [state]="t.slaState" label="First response" />
            } @else if (t.nextResponseDueAt) {
              <sd-sla-countdown [dueAt]="t.nextResponseDueAt" [state]="t.slaState" label="Next response" />
            }
            @if (t.dueAt) {
              <sd-sla-countdown [dueAt]="t.dueAt" [state]="t.slaState" label="Resolve" />
            }
          } @else {
            <sd-badge [tone]="t.slaState === 'Breached' ? 'danger' : 'success'" icon="timer" size="sm">SLA {{ t.slaState === 'Breached' ? 'breached' : 'met' }}</sd-badge>
          }
        </div>

        <div class="quick" role="group" aria-label="Quick edit">
          <label class="q">
            <span class="ql">Status</span>
            <sd-combobox
              size="sm"
              ariaLabel="Status"
              [clearable]="false"
              [options]="statusOptions()"
              [value]="status()"
              [disabled]="!t.can.edit || ctx.busy()"
              (valueChange)="onStatus($any($event))"
            />
          </label>
          <label class="q q--narrow">
            <span class="ql">Priority</span>
            <sd-combobox
              size="sm"
              ariaLabel="Priority"
              [clearable]="false"
              [searchable]="false"
              [options]="priorityOptions"
              [value]="priority()"
              [disabled]="!t.can.edit || ctx.busy()"
              (valueChange)="onPriority($any($event))"
            />
          </label>
          <label class="q">
            <span class="ql">Assignee</span>
            <span class="with-btn">
              <sd-combobox
                size="sm"
                ariaLabel="Assignee"
                placeholder="Unassigned"
                [options]="agentOptions()"
                [value]="assignee()"
                [disabled]="!t.can.assign || ctx.busy()"
                (valueChange)="onAssignee($any($event))"
              />
              @if (t.can.assign && t.assigneeId !== me.user()?.id) {
                <button sdButton size="sm" variant="ghost" iconOnly icon="user-round-check" type="button" aria-label="Assign to me" sdTooltip="Assign to me (a)" (click)="ctx.assignToMe()"></button>
              }
            </span>
          </label>
          <label class="q">
            <span class="ql">Department</span>
            <sd-combobox
              size="sm"
              ariaLabel="Department"
              [clearable]="false"
              [options]="departmentOptions()"
              [value]="department()"
              [disabled]="!t.can.transfer || ctx.busy()"
              (valueChange)="onDepartment($any($event))"
            />
          </label>
          <label class="q">
            <span class="ql">Team</span>
            <sd-combobox
              size="sm"
              ariaLabel="Team"
              placeholder="No team"
              [options]="teamOptions()"
              [value]="team()"
              [disabled]="!t.can.assign || ctx.busy()"
              (valueChange)="onTeam($any($event))"
            />
          </label>
        </div>
      </header>
    }
  `,
  styles: `
    :host {
      display: block;
      flex: none;
      background: var(--sd-surface);
      border-bottom: 1px solid var(--sd-border);
    }
    .hdr {
      display: grid;
      gap: 8px;
      padding: 10px 16px 12px;
    }
    .row1 {
      display: flex;
      align-items: center;
      gap: 8px;
      min-width: 0;
    }
    .back {
      display: none;
      color: var(--sd-text-2);
    }
    .num {
      flex: none;
      padding: 2px 8px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface-2);
      color: var(--sd-text-2);
      font: inherit;
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-semibold);
      font-variant-numeric: tabular-nums;
      cursor: copy;
    }
    .subject {
      min-width: 0;
      font-size: var(--sd-text-lg);
      font-weight: var(--sd-weight-semibold);
      line-height: var(--sd-leading-tight);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .subject-form {
      display: flex;
      flex: 1;
      gap: 6px;
      min-width: 0;
    }
    .subject-input {
      flex: 1;
      min-width: 0;
    }
    .presence {
      display: flex;
      align-items: center;
    }
    .viewer {
      margin-left: -6px;
      border: 2px solid var(--sd-surface);
      border-radius: 50%;
    }
    .viewer.is-typing {
      border-color: var(--sd-warning);
      animation: pulse 1.6s ease-in-out infinite;
    }
    @keyframes pulse {
      50% {
        box-shadow: 0 0 0 3px var(--sd-warning-bg);
      }
    }
    .typing {
      display: flex;
      align-items: center;
      gap: 6px;
      padding: 4px 10px;
      border-radius: var(--sd-radius-sm);
      background: var(--sd-warning-bg);
      color: var(--sd-warning);
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-medium);
    }
    .facts {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: 6px 12px;
      font-size: var(--sd-text-sm);
      color: var(--sd-text-2);
    }
    .fact {
      display: inline-flex;
      align-items: center;
      gap: 5px;
      min-width: 0;
    }
    .merged {
      text-decoration: none;
    }
    .quick {
      display: grid;
      grid-template-columns: minmax(150px, 1.2fr) minmax(110px, 0.7fr) minmax(170px, 1.3fr) minmax(140px, 1fr) minmax(130px, 1fr);
      gap: 8px;
    }
    .q {
      display: grid;
      gap: 2px;
      min-width: 0;
    }
    .ql {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-medium);
    }
    .with-btn {
      display: flex;
      gap: 2px;
      min-width: 0;
    }
    .with-btn sd-combobox {
      flex: 1;
      min-width: 0;
    }
    .details-btn {
      display: none;
    }
    :host-context(.is-narrow) .details-btn {
      display: inline-flex;
    }
    :host-context(.is-narrow) .quick {
      grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
    }
    @media (max-width: 767px) {
      .hdr {
        padding: 8px 10px 10px;
      }
      .row1 {
        padding-right: 0;
      }
      .back {
        display: inline-flex;
      }
      .subject {
        font-size: var(--sd-text-md);
        white-space: normal;
        display: -webkit-box;
        -webkit-line-clamp: 2;
        -webkit-box-orient: vertical;
      }
      .quick {
        grid-template-columns: 1fr 1fr;
      }
    }
  `,
})
export class TicketHeaderComponent {
  /** Narrow layouts open the side tabs (details, tasks…) in a drawer. */
  readonly openDetails = output<void>();

  protected readonly ctx = inject(TicketContext);
  protected readonly actions = inject(TicketActions);
  protected readonly me = inject(MeStore);
  private readonly realtime = inject(RealtimeService);
  private readonly toast = inject(ToastService);
  private readonly subjectInput = viewChild<ElementRef<HTMLInputElement>>('subjectInput');

  protected readonly channelIcon = channelIcon;
  protected readonly channelLabel = channelLabel;
  protected readonly entitlementTone = entitlementTone;
  protected readonly entitlementLabel = entitlementLabel;
  protected readonly priorityOptions: SdOption[] = PRIORITIES.map((p) => ({ value: p, label: p }));

  protected readonly editingSubject = signal(false);
  protected readonly subjectDraft = signal('');

  // Local values that snap back to the server's value when an edit fails.
  protected readonly status = linkedSignal(() => this.ctx.ticket()?.statusId ?? null);
  protected readonly priority = linkedSignal<string | null>(() => this.ctx.ticket()?.priority ?? null);
  protected readonly assignee = linkedSignal(() => this.ctx.ticket()?.assigneeId ?? null);
  protected readonly department = linkedSignal(() => this.ctx.ticket()?.departmentId ?? null);
  protected readonly team = linkedSignal(() => this.ctx.ticket()?.teamId ?? null);

  protected readonly isDone = computed(() => ['Resolved', 'Closed'].includes(this.ctx.ticket()?.statusCategory ?? ''));
  protected readonly statusOptions = computed(() => statusOptions(allowedStatuses(this.ctx.meta.ticketForm(), this.ctx.ticket()?.statusId ?? null)));
  protected readonly agentOptions = computed(() => agentOptions(this.ctx.meta.ticketForm(), this.ctx.ticket()?.departmentId ?? null));
  protected readonly departmentOptions = computed(() => departmentOptions(this.ctx.meta.ticketForm()));
  protected readonly teamOptions = computed(() => teamOptions(this.ctx.meta.ticketForm(), this.ctx.ticket()?.departmentId ?? null));

  protected readonly viewers = computed(() => {
    const id = this.ctx.id();
    const me = this.me.user()?.id;
    return id ? (this.realtime.presence().get(id) ?? []).filter((v) => v.userId !== me) : [];
  });
  protected readonly typing = computed(() => this.viewers().filter((v) => v.activity !== 'viewing'));
  protected readonly typingLabel = computed(() =>
    this.typing()
      .map((v) => `${v.name} is ${v.activity === 'replying' ? 'writing a reply' : 'writing a note'}`)
      .join(' · '),
  );
  protected readonly presenceLabel = computed(() => `Also here: ${this.viewers().map((v) => `${v.name} (${v.activity})`).join(', ')}`);

  protected editSubject(): void {
    this.subjectDraft.set(this.ctx.ticket()?.subject ?? '');
    this.editingSubject.set(true);
    setTimeout(() => this.subjectInput()?.nativeElement.select());
  }

  protected async saveSubject(): Promise<void> {
    const subject = this.subjectDraft().trim();
    if (!subject) {
      this.toast.warning('The subject cannot be empty');
      return;
    }
    if (subject === this.ctx.ticket()?.subject) {
      this.editingSubject.set(false);
      return;
    }
    const ok = await this.ctx.update({ changes: { subject }, labels: { subject } }, 'Subject updated');
    if (ok) this.editingSubject.set(false);
  }

  protected async onStatus(id: string | null): Promise<void> {
    if (!id || id === this.ctx.ticket()?.statusId) return;
    this.status.set(id);
    const ok = await this.ctx.changeStatus(id);
    if (!ok) this.status.set(this.ctx.ticket()?.statusId ?? null);
  }

  protected async onPriority(p: string | null): Promise<void> {
    if (!p || p === this.ctx.ticket()?.priority) return;
    this.priority.set(p);
    const ok = await this.ctx.update({ changes: { priority: p as Priority }, labels: { priority: p } }, `Priority set to ${p}`);
    if (!ok) this.priority.set(this.ctx.ticket()?.priority ?? null);
  }

  protected async onAssignee(id: string | null): Promise<void> {
    const t = this.ctx.ticket();
    if (!t || id === t.assigneeId) return;
    this.assignee.set(id);
    const ok = await this.ctx.assign(id, t.teamId);
    if (!ok) this.assignee.set(this.ctx.ticket()?.assigneeId ?? null);
  }

  protected async onDepartment(id: string | null): Promise<void> {
    const t = this.ctx.ticket();
    if (!t || !id || id === t.departmentId) return;
    this.department.set(id);
    await this.ctx.transfer(id, null, null, null);
    // Approval-required transfers leave the ticket where it is.
    this.department.set(this.ctx.ticket()?.departmentId ?? null);
  }

  protected async onTeam(id: string | null): Promise<void> {
    const t = this.ctx.ticket();
    if (!t || id === t.teamId) return;
    this.team.set(id);
    const ok = await this.ctx.assign(t.assigneeId, id);
    if (!ok) this.team.set(this.ctx.ticket()?.teamId ?? null);
  }

  protected copyLink(): void {
    const t = this.ctx.ticket();
    if (!t) return;
    const url = `${location.origin}/tickets/${t.id}`;
    navigator.clipboard?.writeText(url).then(
      () => this.toast.success('Link copied', t.displayNumber),
      () => this.toast.info(url),
    );
  }
}
