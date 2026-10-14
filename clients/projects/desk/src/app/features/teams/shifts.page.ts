import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  AgentListItemDto,
  AgentsAdminApi,
  Guid,
  LEAVE_TYPES,
  LeaveDto,
  MeStore,
  ScheduleEntryDto,
  ShiftAssignmentDto,
  ShiftDto,
  WorkforceApi,
  WeeklyHoursDto,
  saveErrorMessage,
  validationMessages,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCalendar,
  SdCalendarEvent,
  SdCard,
  SdCheckbox,
  SdCombobox,
  SdDateInput,
  SdDateTimePipe,
  SdDialog,
  SdDialogService,
  SdEmptyState,
  SdFormField,
  SdInput,
  SdOption,
  SdPageHeader,
  SdSkeleton,
  SdTab,
  SdTabs,
  ToastService,
} from '@sd/ui';
import { ModuleTabsComponent } from '../customers/shared/module-tabs.component';
import { SECTION_STYLES, TABLE_STYLES } from '../customers/shared/styles';
import { FORM_STYLES, PAGE_STYLES, TEAMS_TABS, errorText, leaveStatusTone, timeZoneOptions, weeklySummary } from './shared/org-ui';
import { WeeklyHoursEditorComponent } from './shared/weekly-hours-editor.component';

/** /teams/shifts — shift patterns, who works which shift, leave requests/approvals and the team schedule calendar. */
@Component({
  selector: 'desk-shifts-page',
  imports: [
    FormsModule,
    SdPageHeader,
    SdButton,
    SdBadge,
    SdCard,
    SdSkeleton,
    SdEmptyState,
    SdTabs,
    SdTab,
    SdCalendar,
    SdDateTimePipe,
    ModuleTabsComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Teams & Agents" subtitle="Shifts, leave and the team schedule. Routing skips agents on leave and (when a team asks) off shift." icon="users">
        <div sdPageActions class="actions">
          <button sdButton icon="calendar" (click)="requestLeave()">Request leave</button>
          @if (canManage) {
            <button sdButton variant="primary" icon="plus" (click)="editShift(null)">New shift</button>
          }
        </div>
      </sd-page-header>
      <desk-module-tabs [tabs]="tabs" ariaLabel="Teams & Agents sections" />
      <div class="desk-page__body">
        <sd-tabs [(selected)]="tab" ariaLabel="Shifts and leave" variant="pill">
          <sd-tab id="shifts" label="Shifts" icon="clock" [badge]="shifts().length">
            @if (shiftError()) {
              <div class="error-box" role="alert"><span>{{ shiftError() }}</span><button sdButton size="sm" (click)="loadShifts()">Try again</button></div>
            } @else if (shiftsLoading()) {
              <div class="grid" aria-busy="true"><sd-card><sd-skeleton [lines]="4" /></sd-card><sd-card><sd-skeleton [lines]="4" /></sd-card></div>
            } @else if (shifts().length === 0) {
              <sd-empty-state icon="clock" title="No shifts yet" message="Define weekly working patterns and assign agents to them." />
            } @else {
              <div class="grid">
                @for (s of shifts(); track s.id) {
                  <sd-card [title]="s.name" [subtitle]="s.description">
                    <div sdCardActions>
                      @if (canManage) {
                        <button sdButton size="sm" variant="ghost" icon="pencil" iconOnly [attr.aria-label]="'Edit ' + s.name" (click)="editShift(s)"></button>
                        <button sdButton size="sm" variant="ghost" icon="trash" iconOnly [attr.aria-label]="'Delete ' + s.name" (click)="deleteShift(s)"></button>
                      }
                    </div>
                    <p class="swatch-row">
                      <span class="swatch" [style.background]="s.color ?? 'var(--sd-primary)'" aria-hidden="true"></span>
                      <span class="small">{{ summary(s.weeklyHours) }}</span>
                    </p>
                    <p class="small muted">{{ s.timeZone }} · {{ s.weeklyHoursTotal }} h/week · {{ s.assignedAgents }} agents today</p>
                    @if (!s.isActive) {
                      <sd-badge size="sm">Inactive</sd-badge>
                    }
                  </sd-card>
                }
              </div>
            }
          </sd-tab>

          <sd-tab id="assignments" label="Assignments" icon="users">
            <div class="toolbar">
              <span class="small muted">Agents without an assignment are always considered on shift.</span>
              <span class="sd-spacer"></span>
              @if (canManage) {
                <button sdButton size="sm" variant="primary" icon="plus" (click)="assign()" [disabled]="shifts().length === 0">Assign shift</button>
              }
            </div>
            @if (assignments().length === 0) {
              <sd-empty-state icon="users" title="No assignments" message="Assign agents to a shift from a start date." compact />
            } @else {
              <div class="dt-wrap">
                <table class="dt">
                  <caption class="sd-visually-hidden">Shift assignments</caption>
                  <thead>
                    <tr>
                      <th scope="col">Agent</th>
                      <th scope="col">Shift</th>
                      <th scope="col">From</th>
                      <th scope="col">To</th>
                      <th scope="col">Now</th>
                      <th scope="col"><span class="sd-visually-hidden">Actions</span></th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (a of assignments(); track a.id) {
                      <tr>
                        <td>{{ a.userName }}</td>
                        <td>{{ a.shiftName }}</td>
                        <td class="nowrap">{{ a.effectiveFrom }}</td>
                        <td class="nowrap">{{ a.effectiveTo ?? 'Open-ended' }}</td>
                        <td><sd-badge size="sm" [tone]="a.onShiftNow ? 'success' : 'neutral'" dot>{{ a.onShiftNow ? 'On shift' : 'Off shift' }}</sd-badge></td>
                        <td class="row-actions">
                          @if (canManage) {
                            <button sdButton size="sm" variant="ghost" icon="x" iconOnly [attr.aria-label]="'Remove ' + a.userName + ' from ' + a.shiftName" (click)="unassign(a)"></button>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          </sd-tab>

          <sd-tab id="leave" label="Leave" icon="calendar" [badge]="pendingLeave()">
            <div class="toolbar">
              <label class="sd-visually-hidden" for="leave-status">Status</label>
              <select id="leave-status" class="sd-input" [ngModel]="leaveStatus()" (ngModelChange)="leaveStatus.set($event); loadLeave()">
                <option value="">All</option>
                <option value="Requested">Awaiting approval</option>
                <option value="Approved">Approved</option>
                <option value="Rejected">Rejected</option>
                <option value="Cancelled">Cancelled</option>
              </select>
              <span class="sd-spacer"></span>
              <button sdButton size="sm" variant="primary" icon="plus" (click)="requestLeave()">Request leave</button>
            </div>
            @if (leaveError()) {
              <div class="error-box" role="alert"><span>{{ leaveError() }}</span><button sdButton size="sm" (click)="loadLeave()">Try again</button></div>
            } @else if (leaveLoading()) {
              <div aria-busy="true"><sd-skeleton [lines]="5" /></div>
            } @else if (leaves().length === 0) {
              <sd-empty-state icon="calendar" title="No leave" message="Leave requests and approved absences appear here." compact />
            } @else {
              <div class="dt-wrap">
                <table class="dt">
                  <caption class="sd-visually-hidden">Leave</caption>
                  <thead>
                    <tr>
                      <th scope="col">Agent</th>
                      <th scope="col">Type</th>
                      <th scope="col">From</th>
                      <th scope="col">To</th>
                      <th scope="col">Status</th>
                      <th scope="col">Reason</th>
                      <th scope="col"><span class="sd-visually-hidden">Actions</span></th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (l of leaves(); track l.id) {
                      <tr>
                        <td>{{ l.userName }}</td>
                        <td>{{ l.type }}</td>
                        <td class="nowrap">{{ l.startsAt | sdDateTime: 'datetime' : 'short' }}</td>
                        <td class="nowrap">{{ l.endsAt | sdDateTime: 'datetime' : 'short' }}</td>
                        <td>
                          <sd-badge size="sm" [tone]="statusTone(l.status)" dot>{{ l.status }}</sd-badge>
                          @if (l.approvedByName && l.status !== 'Requested') {
                            <span class="xs muted"> by {{ l.approvedByName }}</span>
                          }
                        </td>
                        <td class="small">{{ l.reason ?? '' }}{{ l.decisionNote ? ' — ' + l.decisionNote : '' }}</td>
                        <td class="row-actions">
                          @if (l.canDecide) {
                            <button sdButton size="sm" variant="ghost" icon="check" (click)="decide(l, true)">Approve</button>
                            <button sdButton size="sm" variant="ghost" icon="x" (click)="decide(l, false)">Reject</button>
                          }
                          @if (l.canCancel) {
                            <button sdButton size="sm" variant="ghost" (click)="cancel(l)">Cancel</button>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          </sd-tab>

          <sd-tab id="calendar" label="Calendar" icon="calendar">
            <p class="small muted legend">
              <span class="dot leave"></span> Leave <span class="dot requested"></span> Requested <span class="dot delegation"></span> Delegation
              <span class="dot holiday"></span> Holiday (default calendar)
            </p>
            @if (scheduleError()) {
              <div class="error-box" role="alert"><span>{{ scheduleError() }}</span></div>
            }
            <sd-calendar [events]="events()" (rangeChange)="loadSchedule($event.start, $event.end)" (eventClick)="eventClick($event)" />
          </sd-tab>
        </sd-tabs>
      </div>
    </div>
  `,
  styles: [
    TABLE_STYLES,
    SECTION_STYLES,
    PAGE_STYLES,
    `
      .actions { display: flex; gap: var(--sd-space-2); flex-wrap: wrap; }
      sd-tabs { display: block; }
      .swatch-row { display: flex; align-items: center; gap: var(--sd-space-2); margin: 0 0 var(--sd-space-2); }
      .swatch { width: 12px; height: 12px; border-radius: 3px; flex: none; }
      .legend { display: flex; flex-wrap: wrap; gap: var(--sd-space-2); align-items: center; }
      .dot { width: 10px; height: 10px; border-radius: 50%; display: inline-block; }
      .dot.leave { background: var(--sd-warning); }
      .dot.requested { background: var(--sd-sla-paused); }
      .dot.delegation { background: var(--sd-accent); }
      .dot.holiday { background: var(--sd-danger); }
    `,
  ],
})
export class ShiftsPage {
  private readonly api = inject(WorkforceApi);
  private readonly dialogs = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  private readonly me = inject(MeStore);
  protected readonly tabs = TEAMS_TABS;
  protected readonly canManage = this.me.canAny(['agents.manage', 'admin.organisation']);
  protected readonly tab = signal<string | null>('shifts');
  protected readonly shifts = signal<ShiftDto[]>([]);
  protected readonly shiftsLoading = signal(true);
  protected readonly shiftError = signal<string | null>(null);
  protected readonly assignments = signal<ShiftAssignmentDto[]>([]);
  protected readonly leaves = signal<LeaveDto[]>([]);
  protected readonly leaveStatus = signal('');
  protected readonly leaveLoading = signal(true);
  protected readonly leaveError = signal<string | null>(null);
  protected readonly pendingLeave = computed(() => this.leaves().filter((l) => l.status === 'Requested' && l.canDecide).length || null);
  private readonly schedule = signal<ScheduleEntryDto[]>([]);
  protected readonly scheduleError = signal<string | null>(null);
  private range: { start: Date; end: Date } | null = null;
  protected readonly events = computed<SdCalendarEvent[]>(() =>
    this.schedule().map((e) => ({
      id: `${e.kind}-${e.id}-${e.startsAt}`,
      title: e.title,
      start: e.startsAt,
      end: e.endsAt,
      allDay: e.allDay || e.kind === 'leave',
      color:
        e.kind === 'holiday' ? 'var(--sd-danger)' : e.kind === 'delegation' ? 'var(--sd-accent)' : e.status === 'Requested' ? 'var(--sd-sla-paused)' : 'var(--sd-warning)',
      data: e,
    })),
  );

  constructor() {
    this.loadShifts();
    this.loadLeave();
  }

  protected summary(w: WeeklyHoursDto): string {
    return weeklySummary(w);
  }

  protected statusTone(s: string) {
    return leaveStatusTone(s);
  }

  protected loadShifts(): void {
    this.shiftsLoading.set(true);
    this.shiftError.set(null);
    this.api.shifts().subscribe({
      next: (s) => {
        this.shifts.set(s);
        this.shiftsLoading.set(false);
      },
      error: (err: unknown) => {
        this.shiftError.set(errorText(err));
        this.shiftsLoading.set(false);
      },
    });
    this.api.assignments().subscribe({ next: (a) => this.assignments.set(a) });
  }

  protected loadLeave(): void {
    this.leaveLoading.set(true);
    this.leaveError.set(null);
    this.api.leaves({ status: this.leaveStatus() || null }).subscribe({
      next: (l) => {
        this.leaves.set(l);
        this.leaveLoading.set(false);
      },
      error: (err: unknown) => {
        this.leaveError.set(errorText(err));
        this.leaveLoading.set(false);
      },
    });
  }

  protected loadSchedule(start: Date, end: Date): void {
    this.range = { start, end };
    this.scheduleError.set(null);
    this.api.schedule(start, end).subscribe({
      next: (s) => this.schedule.set(s),
      error: (err: unknown) => this.scheduleError.set(errorText(err)),
    });
  }

  protected eventClick(e: SdCalendarEvent): void {
    const entry = e.data as ScheduleEntryDto;
    if (entry.kind === 'leave') this.tab.set('leave');
  }

  private refreshSchedule(): void {
    if (this.range) this.loadSchedule(this.range.start, this.range.end);
  }

  protected editShift(shift: ShiftDto | null): void {
    this.dialogs.open<ShiftDto, ShiftDto | null>(ShiftFormDialog, { data: shift, size: 'lg' }).closed.subscribe((saved) => {
      if (!saved) return;
      this.toast.success(shift ? 'Shift updated' : 'Shift created', saved.name);
      this.loadShifts();
    });
  }

  protected async deleteShift(shift: ShiftDto): Promise<void> {
    const ok = await this.dialogs.confirm({ title: `Delete ${shift.name}?`, message: 'Its assignments are removed too.', danger: true, confirmLabel: 'Delete' });
    if (!ok) return;
    this.api.deleteShift(shift.id).subscribe(() => {
      this.toast.success('Shift deleted', shift.name);
      this.loadShifts();
    });
  }

  protected assign(): void {
    this.dialogs.open<ShiftAssignmentDto[], ShiftDto[]>(AssignShiftDialog, { data: this.shifts() }).closed.subscribe((created) => {
      if (!created) return;
      this.toast.success('Shift assigned', `${created.length} agent(s)`);
      this.loadShifts();
    });
  }

  protected async unassign(a: ShiftAssignmentDto): Promise<void> {
    const ok = await this.dialogs.confirm({ title: 'Remove assignment?', message: `${a.userName} — ${a.shiftName}`, confirmLabel: 'Remove' });
    if (!ok) return;
    this.api.removeAssignment(a.id).subscribe(() => this.loadShifts());
  }

  protected requestLeave(): void {
    this.dialogs.open<LeaveDto, boolean>(LeaveRequestDialog, { data: this.canManage }).closed.subscribe((leave) => {
      if (!leave) return;
      this.toast.success(leave.status === 'Approved' ? 'Leave recorded' : 'Leave requested', leave.status === 'Requested' ? 'Sent for approval.' : undefined);
      this.tab.set('leave');
      this.loadLeave();
      this.refreshSchedule();
    });
  }

  protected decide(leave: LeaveDto, approve: boolean): void {
    this.dialogs
      .open<string, DecisionNoteData>(DecisionNoteDialog, { data: { title: `${approve ? 'Approve' : 'Reject'} leave — ${leave.userName}`, approve } })
      .closed.subscribe((note) => {
        if (note === undefined) return;
        const text = note.trim() || null;
        (approve ? this.api.approveLeave(leave.id, text) : this.api.rejectLeave(leave.id, text)).subscribe({
          next: (r) => {
            this.toast.success(`Leave ${r.status.toLowerCase()}`, r.userName);
            this.loadLeave();
            this.refreshSchedule();
          },
          error: (err: unknown) => this.toast.error('Could not decide', saveErrorMessage(err)),
        });
      });
  }

  protected async cancel(leave: LeaveDto): Promise<void> {
    const ok = await this.dialogs.confirm({ title: 'Cancel this leave?', message: `${leave.userName} — ${leave.type}`, confirmLabel: 'Cancel leave', danger: true });
    if (!ok) return;
    this.api.cancelLeave(leave.id).subscribe({
      next: () => {
        this.loadLeave();
        this.refreshSchedule();
      },
      error: (err: unknown) => this.toast.error('Could not cancel', saveErrorMessage(err)),
    });
  }
}

/** Create / edit a shift pattern (weekly hours in a time zone). */
@Component({
  selector: 'desk-shift-form-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdCheckbox, WeeklyHoursEditorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="s ? 'Edit ' + s.name : 'New shift'">
      <form id="shift-form" class="form" (ngSubmit)="save()" novalidate>
        <div class="three">
          <sd-form-field label="Name" required [error]="errors()['name']">
            <input sdInput name="name" [(ngModel)]="name" required maxlength="80" />
          </sd-form-field>
          <sd-form-field label="Time zone" required [error]="errors()['timeZone']">
            <select sdInput name="tz" [(ngModel)]="timeZone">
              @for (z of zones; track z) {
                <option [ngValue]="z">{{ z }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Colour" optional [error]="errors()['color']">
            <input sdInput name="color" type="color" [(ngModel)]="color" />
          </sd-form-field>
        </div>
        <sd-form-field label="Description" optional>
          <input sdInput name="description" [(ngModel)]="description" maxlength="300" />
        </sd-form-field>
        <fieldset class="hours">
          <legend>Weekly hours</legend>
          <desk-weekly-hours-editor [(value)]="weekly" />
          @if (weeklyError()) {
            <p class="error" role="alert">{{ weeklyError() }}</p>
          }
        </fieldset>
        <sd-checkbox [(value)]="active">Active</sd-checkbox>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="shift-form" [loading]="saving()">Save</button>
      </div>
    </sd-dialog>
  `,
  styles: [FORM_STYLES, `.hours { border: 0; padding: 0; margin: 0; } legend { font-weight: var(--sd-weight-medium); margin-bottom: 6px; }`],
})
export class ShiftFormDialog {
  protected readonly s = inject<ShiftDto | null>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<ShiftDto>>(DialogRef);
  private readonly api = inject(WorkforceApi);
  protected name = this.s?.name ?? '';
  protected description = this.s?.description ?? '';
  protected timeZone = this.s?.timeZone ?? Intl.DateTimeFormat().resolvedOptions().timeZone ?? 'UTC';
  protected color = this.s?.color ?? '#2563eb';
  protected readonly zones = timeZoneOptions(this.timeZone);
  protected readonly weekly = signal<WeeklyHoursDto>(this.s?.weeklyHours ?? {});
  protected readonly active = signal(this.s?.isActive ?? true);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly weeklyError = computed(() => Object.entries(this.errors()).find(([k]) => k.startsWith('weeklyHours'))?.[1] ?? null);
  protected readonly formError = signal<string | null>(null);

  protected save(): void {
    this.saving.set(true);
    this.formError.set(null);
    this.api
      .saveShift(this.s?.id ?? null, {
        name: this.name.trim(),
        description: this.description.trim() || null,
        timeZone: this.timeZone,
        color: this.color || null,
        isActive: this.active(),
        weeklyHours: this.weekly(),
        rowVersion: this.s?.rowVersion ?? null,
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

/** Assign a shift to agents from a date (overlapping assignments are closed automatically). */
@Component({
  selector: 'desk-assign-shift-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdCombobox, SdDateInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Assign shift">
      <form id="assign-form" class="form" (ngSubmit)="save()" novalidate>
        <sd-form-field label="Shift" required>
          <select sdInput name="shift" [(ngModel)]="shiftId" required>
            @for (s of shifts; track s.id) {
              <option [ngValue]="s.id">{{ s.name }}</option>
            }
          </select>
        </sd-form-field>
        <sd-form-field label="Agents" required [error]="errors()['userIds']">
          <sd-combobox multiple [options]="agentOptions()" [value]="userIds()" (valueChange)="userIds.set($any($event) ?? [])" placeholder="Choose agents…" ariaLabel="Agents" [maxChips]="8" />
        </sd-form-field>
        <div class="two">
          <sd-form-field label="From" required>
            <sd-date-input [(value)]="from" ariaLabel="Effective from" />
          </sd-form-field>
          <sd-form-field label="To" optional hint="Empty = open-ended" [error]="errors()['effectiveTo']">
            <sd-date-input [(value)]="to" ariaLabel="Effective to" />
          </sd-form-field>
        </div>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="assign-form" [loading]="saving()" [disabled]="!shiftId || userIds().length === 0 || !from()">Assign</button>
      </div>
    </sd-dialog>
  `,
  styles: FORM_STYLES,
})
export class AssignShiftDialog {
  protected readonly shifts = inject<ShiftDto[]>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<ShiftAssignmentDto[]>>(DialogRef);
  private readonly api = inject(WorkforceApi);
  private readonly agentsApi = inject(AgentsAdminApi);
  protected shiftId: Guid | null = this.shifts[0]?.id ?? null;
  protected readonly userIds = signal<string[]>([]);
  protected readonly from = signal<string | null>(new Date().toISOString().slice(0, 10));
  protected readonly to = signal<string | null>(null);
  private readonly agents = signal<AgentListItemDto[]>([]);
  protected readonly agentOptions = computed<SdOption[]>(() =>
    this.agents().map((a) => ({ value: a.userId, label: a.name, avatar: a.name, description: a.departments.map((d) => d.name).join(', ') })),
  );
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  constructor() {
    this.agentsApi.list({ status: 'Active,Invited', limit: 200 }).subscribe({ next: (r) => this.agents.set(r.items) });
  }

  protected save(): void {
    if (!this.shiftId || !this.from()) return;
    this.saving.set(true);
    this.api.assignShift({ shiftId: this.shiftId, userIds: this.userIds(), effectiveFrom: this.from()!, effectiveTo: this.to() || null }).subscribe({
      next: (r) => this.ref.close(r),
      error: (err: unknown) => {
        this.saving.set(false);
        this.errors.set(validationMessages(err));
        this.formError.set(saveErrorMessage(err));
      },
    });
  }
}

/** Request leave (own leave → approval request) or record approved leave for an agent (managers). */
@Component({
  selector: 'desk-leave-request-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdDateInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Leave" [description]="forOther ? 'Recording leave for someone else approves it immediately.' : 'Your request goes to your manager or a department supervisor.'">
      <form id="leave-form" class="form" (ngSubmit)="save()" novalidate>
        @if (canManage) {
          <sd-form-field label="Agent">
            <select sdInput name="who" [(ngModel)]="userId">
              <option [ngValue]="null">Me</option>
              @for (a of agents(); track a.userId) {
                <option [ngValue]="a.userId">{{ a.name }}</option>
              }
            </select>
          </sd-form-field>
        }
        <div class="two">
          <sd-form-field label="Starts" required [error]="errors()['startsAt']">
            <sd-date-input type="datetime" [(value)]="startsAt" ariaLabel="Leave starts" />
          </sd-form-field>
          <sd-form-field label="Ends" required [error]="errors()['endsAt']">
            <sd-date-input type="datetime" [(value)]="endsAt" ariaLabel="Leave ends" />
          </sd-form-field>
        </div>
        <sd-form-field label="Type" [error]="errors()['type']">
          <select sdInput name="type" [(ngModel)]="type">
            @for (t of types; track t) {
              <option [ngValue]="t">{{ t === 'PublicHoliday' ? 'Public holiday' : t }}</option>
            }
          </select>
        </sd-form-field>
        <sd-form-field label="Reason" optional>
          <textarea sdInput name="reason" rows="2" [(ngModel)]="reason" maxlength="1000"></textarea>
        </sd-form-field>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="leave-form" [loading]="saving()" [disabled]="!startsAt() || !endsAt()">
          {{ forOther ? 'Record leave' : 'Request leave' }}
        </button>
      </div>
    </sd-dialog>
  `,
  styles: FORM_STYLES,
})
export class LeaveRequestDialog {
  protected readonly canManage = inject<boolean>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<LeaveDto>>(DialogRef);
  private readonly api = inject(WorkforceApi);
  private readonly agentsApi = inject(AgentsAdminApi);
  private readonly me = inject(MeStore);
  protected userId: Guid | null = null;
  protected readonly types = LEAVE_TYPES;
  protected type = 'Leave';
  protected reason = '';
  protected readonly startsAt = signal<string | null>(defaultStart(1));
  protected readonly endsAt = signal<string | null>(defaultStart(2));
  protected readonly agents = signal<AgentListItemDto[]>([]);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  get forOther(): boolean {
    return this.userId !== null && this.userId !== this.me.user()?.id;
  }

  constructor() {
    if (this.canManage) this.agentsApi.list({ status: 'Active', limit: 200 }).subscribe({ next: (r) => this.agents.set(r.items) });
  }

  protected save(): void {
    if (!this.startsAt() || !this.endsAt()) return;
    this.saving.set(true);
    this.formError.set(null);
    this.api.requestLeave({ userId: this.userId, startsAt: this.startsAt()!, endsAt: this.endsAt()!, type: this.type, reason: this.reason.trim() || null }).subscribe({
      next: (r) => this.ref.close(r),
      error: (err: unknown) => {
        this.saving.set(false);
        this.errors.set(validationMessages(err));
        this.formError.set(saveErrorMessage(err));
      },
    });
  }
}

interface DecisionNoteData {
  title: string;
  approve: boolean;
}

/** Approve / reject with an optional note (closes with the note text, possibly empty). */
@Component({
  selector: 'desk-decision-note-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="data.title">
      <form id="note-form" (ngSubmit)="ref.close(note)">
        <sd-form-field label="Note" optional hint="Shared with the agent.">
          <textarea sdInput name="note" rows="3" [(ngModel)]="note" maxlength="1000"></textarea>
        </sd-form-field>
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton [variant]="data.approve ? 'primary' : 'danger'" type="submit" form="note-form">{{ data.approve ? 'Approve' : 'Reject' }}</button>
      </div>
    </sd-dialog>
  `,
})
export class DecisionNoteDialog {
  protected readonly data = inject<DecisionNoteData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<string>>(DialogRef);
  protected note = '';
}

/** Local 09:00 of today + n days as a UTC ISO string. */
function defaultStart(days: number): string {
  const d = new Date();
  d.setDate(d.getDate() + days);
  d.setHours(9, 0, 0, 0);
  return d.toISOString();
}
