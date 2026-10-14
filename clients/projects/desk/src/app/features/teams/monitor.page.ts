import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import {
  AVAILABILITIES,
  AgentsAdminApi,
  Guid,
  MetaApi,
  MonitorAgentDto,
  MonitorSlaItemDto,
  MonitorSnapshotDto,
  RealtimeService,
  SupervisorMonitorApi,
  TicketsApi,
  saveErrorMessage,
} from '@sd/core';
import {
  SdAvatar,
  SdBadge,
  SdButton,
  SdCard,
  SdDialog,
  SdDialogService,
  SdEmptyState,
  SdFormField,
  SdInput,
  SdKpiTile,
  SdPageHeader,
  SdRelativeTimePipe,
  SdSkeleton,
  ToastService,
} from '@sd/ui';
import { Subject, debounceTime, merge } from 'rxjs';
import { ModuleTabsComponent } from '../customers/shared/module-tabs.component';
import { SECTION_STYLES, TABLE_STYLES } from '../customers/shared/styles';
import { PAGE_STYLES, TEAMS_TABS, availabilityTone, formatMinutes, loadPercent } from './shared/org-ui';

/**
 * /teams/monitor — supervisor live monitor: agents (availability, shift/leave, load vs capacity, chats, idle time), queue
 * depth per department/team, SLA at-risk/breached tickets and chats waiting. Refreshes on SignalR events
 * (ticket.changed, sla.alert, agent.availability, conversation.updated) and every minute as a safety net.
 */
@Component({
  selector: 'desk-monitor-page',
  imports: [
    FormsModule,
    RouterLink,
    SdPageHeader,
    SdButton,
    SdBadge,
    SdCard,
    SdAvatar,
    SdKpiTile,
    SdSkeleton,
    SdEmptyState,
    SdRelativeTimePipe,
    ModuleTabsComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Teams & Agents" subtitle="Live view of agents, queues, SLA risk and waiting chats in your departments." icon="radar">
        <div sdPageActions class="live">
          <span class="pulse" [class.on]="realtime.connected()" aria-hidden="true"></span>
          <span class="small">{{ realtime.connected() ? 'Live' : 'Reconnecting…' }}</span>
          @if (snapshot(); as s) {
            <span class="xs muted">updated {{ s.generatedAt | sdRelativeTime }}</span>
          }
          <button sdButton size="sm" variant="ghost" icon="refresh" iconOnly aria-label="Refresh now" (click)="refresh()" [loading]="loading()"></button>
        </div>
      </sd-page-header>
      <desk-module-tabs [tabs]="tabs" ariaLabel="Teams & Agents sections" />
      <div class="desk-page__body">
        @if (snapshot(); as s) {
          <div class="toolbar">
            <label class="sd-visually-hidden" for="mon-dept">Department</label>
            <select id="mon-dept" class="sd-input" [ngModel]="department()" (ngModelChange)="department.set($event); refresh()">
              <option [ngValue]="null">{{ s.departments.length > 1 ? 'All my departments' : s.scope }}</option>
              @for (d of allDepartments(); track d.id) {
                <option [ngValue]="d.id">{{ d.name }}</option>
              }
            </select>
            <span class="small muted">Scope: {{ s.scope }}</span>
          </div>
          <div class="kpis">
            <sd-kpi-tile label="Agents online" icon="user-check" [value]="online() + ' / ' + s.agents.length" />
            <sd-kpi-tile label="Open tickets" icon="ticket" [value]="s.openTickets" />
            <sd-kpi-tile label="Unassigned" icon="inbox" [value]="s.unassignedTickets" />
            <sd-kpi-tile label="SLA at risk" icon="hourglass" [value]="s.atRiskCount" />
            <sd-kpi-tile label="SLA breached" icon="octagon-alert" [value]="s.breachedCount" />
            <sd-kpi-tile label="Chats waiting" icon="messages" [value]="s.chatsWaiting" />
          </div>
        }
        @if (error(); as e) {
          <div class="error-box" role="alert"><span>{{ e }}</span><button sdButton size="sm" (click)="refresh()">Try again</button></div>
        }
        @if (!snapshot() && loading()) {
          <div aria-busy="true"><sd-skeleton [lines]="10" /></div>
        } @else if (snapshot(); as s) {
          <div class="layout">
            <sd-card title="Agents" [subtitle]="s.agents.length + ' agents'" padding="none">
              @if (s.agents.length === 0) {
                <sd-empty-state icon="users" title="No agents in scope" compact />
              } @else {
                <div class="dt-wrap flat">
                  <table class="dt">
                    <caption class="sd-visually-hidden">Agents</caption>
                    <thead>
                      <tr>
                        <th scope="col">Agent</th>
                        <th scope="col">Availability</th>
                        <th scope="col">Load</th>
                        <th scope="col" class="num">In progress</th>
                        <th scope="col" class="num">Overdue</th>
                        <th scope="col" class="num">Chats</th>
                        <th scope="col">Idle</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (a of s.agents; track a.userId) {
                        <tr>
                          <td>
                            <span class="who">
                              <sd-avatar [name]="a.name" [src]="a.avatarUrl" size="sm" [presence]="$any(a.availability)" />
                              <span>
                                <a class="sd-truncate" [routerLink]="['/teams/agents', a.userId]">{{ a.name }}</a>
                                <span class="pills">
                                  @if (a.onLeave) {
                                    <sd-badge size="sm" tone="warning">On leave</sd-badge>
                                  }
                                  @if (!a.onShift) {
                                    <sd-badge size="sm">Off shift</sd-badge>
                                  }
                                  @if (a.delegateUserId) {
                                    <sd-badge size="sm" tone="info">Delegated</sd-badge>
                                  }
                                </span>
                              </span>
                            </span>
                          </td>
                          <td>
                            @if (s.canChangeAvailability) {
                              <select class="sd-input sd-input-sm" [attr.aria-label]="'Availability of ' + a.name" [ngModel]="a.availability"
                                (ngModelChange)="setAvailability(a, $event)">
                                @for (v of availabilities; track v) {
                                  <option [value]="v">{{ v }}</option>
                                }
                              </select>
                            } @else {
                              <sd-badge size="sm" [tone]="tone(a.availability)" dot>{{ a.availability }}</sd-badge>
                            }
                          </td>
                          <td>
                            <span class="load" [attr.aria-label]="a.openTickets + ' of ' + a.capacity + ' open tickets'">
                              <span class="bar" [class.is-warn]="pct(a) >= 75" [class.is-danger]="pct(a) >= 100"><span [style.width.%]="pct(a)"></span></span>
                              <span class="xs muted">{{ a.openTickets }}/{{ a.capacity }}</span>
                            </span>
                          </td>
                          <td class="num">{{ a.inProgressTickets }}</td>
                          <td class="num" [class.warn]="a.overdueTickets > 0">{{ a.overdueTickets }}</td>
                          <td class="num">{{ a.activeChats }}</td>
                          <td class="small nowrap">{{ a.idleMinutes === null ? '—' : minutes(a.idleMinutes) }}</td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              }
            </sd-card>

            <sd-card title="Queues" subtitle="Open tickets by department and team" padding="none">
              @if (s.queues.length === 0) {
                <sd-empty-state icon="inbox" title="Queues are empty" compact />
              } @else {
                <div class="dt-wrap flat">
                  <table class="dt">
                    <caption class="sd-visually-hidden">Queues</caption>
                    <thead>
                      <tr>
                        <th scope="col">Queue</th>
                        <th scope="col" class="num">Open</th>
                        <th scope="col" class="num">Unassigned</th>
                        <th scope="col" class="num">At risk</th>
                        <th scope="col" class="num">Breached</th>
                        <th scope="col">Oldest waiting</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (q of s.queues; track q.departmentId + '-' + (q.teamId ?? 'none')) {
                        <tr>
                          <td>
                            <strong>{{ q.teamName ?? 'No team' }}</strong>
                            <span class="xs muted"> {{ q.departmentName }}</span>
                          </td>
                          <td class="num">{{ q.open }}</td>
                          <td class="num" [class.warn]="q.unassigned > 0">{{ q.unassigned }}</td>
                          <td class="num">{{ q.atRisk }}</td>
                          <td class="num" [class.bad]="q.breached > 0">{{ q.breached }}</td>
                          <td class="small nowrap">{{ q.oldestUnassignedAt ? (q.oldestUnassignedAt | sdRelativeTime) : '—' }}</td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              }
            </sd-card>

            <sd-card title="SLA risk" [subtitle]="s.atRiskCount + ' at risk · ' + s.breachedCount + ' breached'" padding="none">
              @if (s.slaItems.length === 0) {
                <sd-empty-state icon="circle-check" title="Nothing at risk" message="No open tickets are at risk or breached." compact />
              } @else {
                <ul class="risk">
                  @for (t of s.slaItems; track t.ticketId) {
                    <li>
                      <span class="risk-main">
                        <sd-badge size="sm" [tone]="t.slaState === 'Breached' ? 'danger' : 'warning'" dot>{{ t.slaState === 'Breached' ? 'Breached' : 'At risk' }}</sd-badge>
                        <a [routerLink]="['/tickets', t.number]">{{ prefix() }}{{ t.number }}</a>
                        <span class="sd-truncate">{{ t.subject }}</span>
                      </span>
                      <span class="xs muted">
                        {{ milestone(t.milestone) }}@if (t.dueAt) { due {{ t.dueAt | sdRelativeTime }}} · {{ t.priority }} · {{ t.assigneeName ?? 'Unassigned' }} · {{ t.departmentName }}
                      </span>
                      @if (s.canReassign) {
                        <button sdButton size="sm" variant="ghost" icon="user-plus" (click)="reassign(t)" [attr.aria-label]="'Reassign ticket ' + t.number">Reassign</button>
                      }
                    </li>
                  }
                </ul>
              }
            </sd-card>

            <sd-card title="Chats waiting" [subtitle]="s.chatsWaiting + ' waiting for an agent'" padding="none">
              @if (s.chats.length === 0) {
                <sd-empty-state icon="messages" title="No one is waiting" compact />
              } @else {
                <ul class="risk">
                  @for (c of s.chats; track c.conversationId) {
                    <li>
                      <span class="risk-main">
                        <sd-badge size="sm" [tone]="c.waitingMinutes >= 5 ? 'danger' : 'warning'">{{ minutes(c.waitingMinutes) }}</sd-badge>
                        <strong class="sd-truncate">{{ c.visitorName ?? 'Visitor' }}</strong>
                        <span class="xs muted">{{ c.channel }}</span>
                      </span>
                      <span class="xs muted sd-truncate">{{ c.subject ?? 'No subject' }} · waiting since {{ c.waitingSince | sdRelativeTime }}</span>
                      <a class="small" routerLink="/inbox">Open inbox</a>
                    </li>
                  }
                </ul>
              }
            </sd-card>
          </div>
        }
      </div>
    </div>
  `,
  styles: [
    TABLE_STYLES,
    SECTION_STYLES,
    PAGE_STYLES,
    `
      .live { display: flex; align-items: center; gap: var(--sd-space-2); }
      .pulse { width: 10px; height: 10px; border-radius: 50%; background: var(--sd-text-3); }
      .pulse.on { background: var(--sd-success); box-shadow: 0 0 0 3px var(--sd-success-bg); }
      .layout { display: grid; gap: var(--sd-space-4); grid-template-columns: repeat(2, minmax(0, 1fr)); }
      .layout > :first-child { grid-column: 1 / -1; }
      @media (max-width: 1000px) { .layout { grid-template-columns: 1fr; } }
      .dt-wrap.flat { border: 0; border-radius: 0; }
      .warn { color: var(--sd-warning); font-weight: var(--sd-weight-semibold); }
      .bad { color: var(--sd-danger); font-weight: var(--sd-weight-semibold); }
      .risk { list-style: none; margin: 0; padding: 0; }
      .risk li { display: grid; gap: 2px; padding: 10px var(--sd-space-4); border-bottom: 1px solid var(--sd-divider); position: relative; }
      .risk li:last-child { border-bottom: 0; }
      .risk-main { display: flex; align-items: center; gap: var(--sd-space-2); min-width: 0; }
      .risk-main > sd-badge, .risk-main > a { flex: none; }
      .risk li button, .risk li > a { justify-self: start; }
      a { color: var(--sd-primary); }
    `,
  ],
})
export class MonitorPage {
  private readonly api = inject(SupervisorMonitorApi);
  private readonly agents = inject(AgentsAdminApi);
  private readonly dialogs = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  private readonly meta = inject(MetaApi);
  protected readonly realtime = inject(RealtimeService);
  protected readonly tabs = TEAMS_TABS;
  protected readonly availabilities = AVAILABILITIES;
  protected readonly snapshot = signal<MonitorSnapshotDto | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly department = signal<Guid | null>(null);
  /** Departments in scope from the unfiltered snapshot (the filter list must not shrink after filtering). */
  protected readonly allDepartments = signal<{ id: Guid; name: string }[]>([]);
  protected readonly online = computed(() => this.snapshot()?.agents.filter((a) => a.availability === 'Online').length ?? 0);
  protected readonly prefix = computed(() => this.meta.ticketForm()?.ticketNumberPrefix ?? '#');
  private readonly trigger = new Subject<void>();

  constructor() {
    this.meta.load();
    const destroy = inject(DestroyRef);
    const sub = merge(
      this.trigger,
      this.realtime.ticketChanged$,
      this.realtime.slaAlert$,
      this.realtime.agentAvailability$,
      this.realtime.conversationUpdated$,
      this.realtime.reconnected$,
    )
      .pipe(debounceTime(1200))
      .subscribe(() => this.load());
    const timer = setInterval(() => this.load(), 60_000);
    destroy.onDestroy(() => {
      sub.unsubscribe();
      clearInterval(timer);
    });
    this.load();
  }

  protected refresh(): void {
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.api.snapshot(this.department()).subscribe({
      next: (s) => {
        this.snapshot.set(s);
        if (this.department() === null) this.allDepartments.set(s.departments);
        this.error.set(null);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.error.set(saveErrorMessage(err));
        this.loading.set(false);
      },
    });
  }

  protected tone(a: string) {
    return availabilityTone(a);
  }

  protected pct(a: MonitorAgentDto): number {
    return loadPercent(a.openTickets, a.capacity);
  }

  protected minutes(m: number): string {
    return formatMinutes(m);
  }

  protected milestone(m: string | null): string {
    return m === 'FirstResponse' ? 'First response' : m === 'NextResponse' ? 'Next response' : m === 'Resolution' ? 'Resolution' : 'SLA';
  }

  protected setAvailability(a: MonitorAgentDto, value: string): void {
    this.agents.setAvailability(a.userId, value).subscribe({
      next: () => {
        this.toast.success(`${a.name} is now ${value}`);
        this.trigger.next();
      },
      error: (err: unknown) => {
        this.toast.error('Could not change availability', saveErrorMessage(err));
        this.trigger.next();
      },
    });
  }

  protected reassign(t: MonitorSlaItemDto): void {
    const s = this.snapshot();
    const candidates = (s?.agents ?? []).filter((a) => a.departmentIds.includes(t.departmentId) && a.userId !== t.assigneeId && !a.onLeave);
    this.dialogs.open<boolean, ReassignData>(ReassignDialog, { data: { ticket: t, candidates } }).closed.subscribe((done) => {
      if (done) this.trigger.next();
    });
  }
}

interface ReassignData {
  ticket: MonitorSlaItemDto;
  candidates: MonitorAgentDto[];
}

/** Reassign an at-risk ticket to an agent of its department (least loaded first). */
@Component({
  selector: 'desk-monitor-reassign-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="'Reassign #' + data.ticket.number" [description]="data.ticket.subject">
      <form id="reassign-form" class="form" (ngSubmit)="save()">
        <sd-form-field label="New owner" required>
          <select sdInput name="to" [(ngModel)]="to" required>
            <option [ngValue]="null" disabled>Choose an agent…</option>
            @for (a of sorted; track a.userId) {
              <option [ngValue]="a.userId">{{ a.name }} — {{ a.availability }}, {{ a.openTickets }}/{{ a.capacity }}</option>
            }
          </select>
        </sd-form-field>
        <sd-form-field label="Note" optional>
          <input sdInput name="note" [(ngModel)]="note" maxlength="300" />
        </sd-form-field>
        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="reassign-form" [loading]="saving()" [disabled]="!to">Reassign</button>
      </div>
    </sd-dialog>
  `,
  styles: `.form { display: grid; gap: var(--sd-space-3); } .error { color: var(--sd-danger); }`,
})
export class ReassignDialog {
  protected readonly data = inject<ReassignData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  private readonly tickets = inject(TicketsApi);
  private readonly toast = inject(ToastService);
  protected readonly sorted = [...this.data.candidates].sort((a, b) => loadPercent(a.openTickets, a.capacity) - loadPercent(b.openTickets, b.capacity));
  protected to: Guid | null = null;
  protected note = '';
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);

  protected save(): void {
    if (!this.to) return;
    this.saving.set(true);
    this.tickets.assign(this.data.ticket.ticketId, { assigneeId: this.to, teamId: null, note: this.note.trim() || null }).subscribe({
      next: () => {
        this.toast.success(`Ticket #${this.data.ticket.number} reassigned`);
        this.ref.close(true);
      },
      error: (err: unknown) => {
        this.saving.set(false);
        this.error.set(saveErrorMessage(err));
      },
    });
  }
}
