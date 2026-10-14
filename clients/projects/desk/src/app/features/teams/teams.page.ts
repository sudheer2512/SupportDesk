import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import {
  AgentListItemDto,
  AgentsAdminApi,
  DepartmentDto,
  Guid,
  MeStore,
  OrganisationApi,
  ROUTING_MODES,
  RoutingMode,
  TeamDto,
  saveErrorMessage,
  validationMessages,
} from '@sd/core';
import {
  SdAvatar,
  SdBadge,
  SdButton,
  SdCard,
  SdCheckbox,
  SdCombobox,
  SdDialog,
  SdDialogService,
  SdEmptyState,
  SdFormField,
  SdInput,
  SdOption,
  SdPageHeader,
  SdRadio,
  SdRadioGroup,
  SdSkeleton,
  ToastService,
} from '@sd/ui';
import { ModuleTabsComponent } from '../customers/shared/module-tabs.component';
import { RoutingPreviewPanel } from './routing-preview.component';
import { SECTION_STYLES, TABLE_STYLES } from '../customers/shared/styles';
import { FORM_STYLES, PAGE_STYLES, ROUTING_MODE_INFO, TEAMS_TABS, availabilityTone, errorText, loadPercent, routingModeLabel } from './shared/org-ui';

/** /teams/teams — teams per department: members and load, lead, routing configuration and a test-route preview. */
@Component({
  selector: 'desk-teams-page',
  imports: [FormsModule, SdPageHeader, SdButton, SdBadge, SdCard, SdSkeleton, SdEmptyState, SdAvatar, ModuleTabsComponent, RoutingPreviewPanel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Teams & Agents" subtitle="Teams, their members and how new tickets are routed to them." icon="users">
        @if (canManage) {
          <button sdButton sdPageActions variant="primary" icon="plus" (click)="edit(null)">New team</button>
        }
      </sd-page-header>
      <desk-module-tabs [tabs]="tabs" ariaLabel="Teams & Agents sections" />
      <div class="desk-page__body">
        <div class="toolbar">
          <label class="sd-visually-hidden" for="team-dept">Department</label>
          <select id="team-dept" class="sd-input" [ngModel]="departmentFilter()" (ngModelChange)="setDepartment($event)">
            <option [ngValue]="null">All departments</option>
            @for (d of departments(); track d.id) {
              <option [ngValue]="d.id">{{ d.name }}</option>
            }
          </select>
          <span class="sd-spacer"></span>
          <button sdButton size="sm" icon="play" (click)="showPreview.set(!showPreview())" [pressed]="showPreview()">Test routing</button>
          <button sdButton size="sm" variant="ghost" icon="refresh" (click)="load()" [loading]="loading()">Refresh</button>
        </div>
        @if (showPreview()) {
          <desk-routing-preview-panel [departments]="departments()" [teams]="teams()" [departmentId]="departmentFilter()" />
        }
        @if (error()) {
          <div class="error-box" role="alert"><span>{{ error() }}</span><button sdButton size="sm" (click)="load()">Try again</button></div>
        } @else if (loading() && teams().length === 0) {
          <div class="grid" aria-busy="true">
            @for (i of [1, 2, 3]; track i) {
              <sd-card><sd-skeleton [lines]="6" /></sd-card>
            }
          </div>
        } @else if (visible().length === 0) {
          <sd-empty-state icon="users" title="No teams" message="Create a team to group agents and route tickets to them." />
        } @else {
          <div class="grid">
            @for (t of visible(); track t.id) {
              <sd-card [title]="t.name" [subtitle]="t.departmentName">
                <div sdCardActions>
                  @if (canManage) {
                    <button sdButton size="sm" variant="ghost" icon="sliders" iconOnly [attr.aria-label]="'Routing of ' + t.name" (click)="routing(t)"></button>
                    <button sdButton size="sm" variant="ghost" icon="pencil" iconOnly [attr.aria-label]="'Edit ' + t.name" (click)="edit(t)"></button>
                    @if (!t.isDepartmentDefault) {
                      <button sdButton size="sm" variant="ghost" icon="trash" iconOnly [attr.aria-label]="'Delete ' + t.name" (click)="remove(t)"></button>
                    }
                  }
                </div>
                <div class="pills">
                  <sd-badge size="sm" tone="primary" icon="workflow">{{ modeLabel(t.routing.mode) }}</sd-badge>
                  @if (t.isDepartmentDefault) {
                    <sd-badge size="sm" tone="info">Department routing team</sd-badge>
                  }
                  @if (t.routing.considerShifts) {
                    <sd-badge size="sm" icon="clock">Shift-aware</sd-badge>
                  }
                  @if (t.routing.onlyOnlineAgents) {
                    <sd-badge size="sm" icon="activity">Online only</sd-badge>
                  }
                  @if (t.routing.maxOpenTicketsPerAgent) {
                    <sd-badge size="sm">Max {{ t.routing.maxOpenTicketsPerAgent }} per agent</sd-badge>
                  }
                  @if (t.routing.fallbackTeamName) {
                    <sd-badge size="sm" icon="arrow-right">Overflow → {{ t.routing.fallbackTeamName }}</sd-badge>
                  }
                  @if (!t.isActive) {
                    <sd-badge size="sm" tone="neutral">Inactive</sd-badge>
                  }
                </div>
                <p class="small muted counts">{{ t.openTickets }} open · {{ t.unassignedTickets }} unassigned in queue@if (t.leadName) { · Lead: {{ t.leadName }}}</p>
                @if (t.members.length === 0) {
                  <p class="empty-note">No members yet.</p>
                } @else {
                  <ul class="members">
                    @for (m of t.members; track m.userId) {
                      <li>
                        <span class="who">
                          <sd-avatar [name]="m.name" size="sm" [presence]="$any(m.availability)" />
                          <span>
                            <span class="sd-truncate">{{ m.name }}@if (m.isLead) { <sd-badge size="sm" tone="accent">Lead</sd-badge>}</span>
                            <span class="xs muted">{{ m.availability }}{{ m.isActive ? '' : ' · deactivated' }}</span>
                          </span>
                        </span>
                        <span class="load" [attr.aria-label]="m.openTickets + ' of ' + m.capacity + ' tickets'">
                          <span class="bar" [class.is-warn]="pct(m.openTickets, m.capacity) >= 75" [class.is-danger]="pct(m.openTickets, m.capacity) >= 100">
                            <span [style.width.%]="pct(m.openTickets, m.capacity)"></span>
                          </span>
                          <span class="xs muted">{{ m.openTickets }}/{{ m.capacity }}</span>
                        </span>
                      </li>
                    }
                  </ul>
                }
              </sd-card>
            }
          </div>
        }
      </div>
    </div>
  `,
  styles: [
    SECTION_STYLES,
    PAGE_STYLES,
    `
      .counts { margin: var(--sd-space-2) 0; }
      .members { list-style: none; padding: 0; margin: 0; display: grid; gap: 6px; }
      .members li { display: flex; justify-content: space-between; gap: var(--sd-space-2); align-items: center; }
    `,
  ],
})
export class TeamsPage {
  private readonly api = inject(OrganisationApi);
  private readonly dialogs = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  private readonly me = inject(MeStore);
  /** Query param ?departmentId= (route inputs). */
  readonly departmentId = input<string | null>(null);
  protected readonly canManage = this.me.canAny(['admin.organisation', 'agents.manage']);
  protected readonly tabs = TEAMS_TABS;
  protected readonly departments = signal<DepartmentDto[]>([]);
  protected readonly teams = signal<TeamDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly showPreview = signal(false);
  private readonly selectedDepartment = signal<string | null | undefined>(undefined);
  protected readonly departmentFilter = computed(() => (this.selectedDepartment() === undefined ? this.departmentId() : this.selectedDepartment()) ?? null);
  protected readonly visible = computed(() => {
    const d = this.departmentFilter();
    return d ? this.teams().filter((t) => t.departmentId === d) : this.teams();
  });

  constructor() {
    this.api.departments().subscribe({ next: (d) => this.departments.set(d) });
    this.load();
  }

  protected modeLabel(mode: string): string {
    return routingModeLabel(mode);
  }

  protected pct(open: number, capacity: number): number {
    return loadPercent(open, capacity);
  }

  protected setDepartment(id: string | null): void {
    this.selectedDepartment.set(id);
    void this.router.navigate([], { queryParams: { departmentId: id }, replaceUrl: true });
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.teams(null, true).subscribe({
      next: (list) => {
        this.teams.set(list);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.error.set(errorText(err));
        this.loading.set(false);
      },
    });
  }

  protected edit(team: TeamDto | null): void {
    this.dialogs
      .open<TeamDto, TeamFormData>(TeamFormDialog, { data: { team, departments: this.departments(), departmentId: this.departmentFilter() }, size: 'lg' })
      .closed.subscribe((saved) => {
        if (!saved) return;
        this.toast.success(team ? 'Team updated' : 'Team created', saved.name);
        this.load();
      });
  }

  protected routing(team: TeamDto): void {
    this.dialogs
      .open<TeamDto, RoutingFormData>(TeamRoutingDialog, { data: { team, teams: this.teams().filter((t) => t.departmentId === team.departmentId && t.id !== team.id && t.isActive) } })
      .closed.subscribe((saved) => {
        if (!saved) return;
        this.toast.success('Routing updated', `${saved.name}: ${routingModeLabel(saved.routing.mode)}`);
        this.load();
      });
  }

  protected async remove(team: TeamDto): Promise<void> {
    const ok = await this.dialogs.confirm({
      title: `Delete ${team.name}?`,
      message: 'Teams with ticket history are deactivated instead of deleted. Open tickets must be moved first.',
      confirmLabel: 'Delete',
      danger: true,
    });
    if (!ok) return;
    this.api.deleteTeam(team.id).subscribe({
      next: () => {
        this.toast.success('Team removed', team.name);
        this.load();
      },
      error: (err: unknown) => this.toast.error('Could not delete the team', saveErrorMessage(err)),
    });
  }
}

interface TeamFormData {
  team: TeamDto | null;
  departments: DepartmentDto[];
  departmentId: Guid | null;
}

/** Create / edit a team: department, name, members (agents of the department), lead. */
@Component({
  selector: 'desk-team-form-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdCheckbox, SdCombobox],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="t ? 'Edit ' + t.name : 'New team'">
      <form id="team-form" class="form" (ngSubmit)="save()" novalidate>
        <div class="two">
          <sd-form-field label="Department" required [error]="errors()['departmentId']">
            <select sdInput name="dept" [ngModel]="departmentId()" (ngModelChange)="changeDepartment($event)" [disabled]="!!t" required>
              <option [ngValue]="null" disabled>Choose…</option>
              @for (d of data.departments; track d.id) {
                <option [ngValue]="d.id">{{ d.name }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Name" required [error]="errors()['name']">
            <input sdInput name="name" [(ngModel)]="name" required maxlength="120" />
          </sd-form-field>
        </div>
        <sd-form-field label="Description" optional>
          <textarea sdInput name="description" rows="2" [(ngModel)]="description" maxlength="1000"></textarea>
        </sd-form-field>
        <sd-form-field label="Members" hint="Active agents of the department (light agents cannot own tickets but may be members)." [error]="errors()['memberUserIds']">
          <sd-combobox multiple [options]="memberOptions()" [value]="members()" (valueChange)="setMembers($any($event) ?? [])" placeholder="Add agents…" [maxChips]="8"
            ariaLabel="Team members" />
        </sd-form-field>
        <sd-form-field label="Team lead" optional [error]="errors()['leadUserId']">
          <select sdInput name="lead" [(ngModel)]="leadUserId">
            <option [ngValue]="null">—</option>
            @for (o of leadOptions(); track o.value) {
              <option [ngValue]="o.value">{{ o.label }}</option>
            }
          </select>
        </sd-form-field>
        <sd-checkbox [(value)]="active">Active (receives routed tickets)</sd-checkbox>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="team-form" [loading]="saving()">Save</button>
      </div>
    </sd-dialog>
  `,
  styles: FORM_STYLES,
})
export class TeamFormDialog {
  protected readonly data = inject<TeamFormData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<TeamDto>>(DialogRef);
  private readonly api = inject(OrganisationApi);
  private readonly agentsApi = inject(AgentsAdminApi);
  protected readonly t = this.data.team;
  protected readonly departmentId = signal<Guid | null>(this.t?.departmentId ?? this.data.departmentId ?? this.data.departments[0]?.id ?? null);
  protected name = this.t?.name ?? '';
  protected description = this.t?.description ?? '';
  protected leadUserId: Guid | null = this.t?.leadUserId ?? null;
  protected readonly active = signal(this.t?.isActive ?? true);
  protected readonly members = signal<string[]>(this.t?.members.map((m) => m.userId) ?? []);
  private readonly candidates = signal<AgentListItemDto[]>([]);
  protected readonly memberOptions = computed<SdOption[]>(() =>
    this.candidates().map((a) => ({ value: a.userId, label: a.name, avatar: a.name, description: a.isLightAgent ? 'Light agent' : a.title ?? a.email })),
  );
  protected readonly leadOptions = computed(() => this.memberOptions().filter((o) => this.members().includes(o.value)));
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  constructor() {
    this.loadCandidates();
  }

  protected changeDepartment(id: Guid | null): void {
    this.departmentId.set(id);
    this.members.set([]);
    this.leadUserId = null;
    this.loadCandidates();
  }

  protected setMembers(ids: string[]): void {
    this.members.set(ids);
    if (this.leadUserId && !ids.includes(this.leadUserId)) this.leadUserId = null;
  }

  private loadCandidates(): void {
    const dept = this.departmentId();
    if (!dept) return;
    this.agentsApi.list({ departmentId: dept, status: 'Active,Invited', limit: 200 }).subscribe({ next: (r) => this.candidates.set(r.items) });
  }

  protected save(): void {
    const dept = this.departmentId();
    if (!dept) {
      this.errors.set({ departmentId: 'Choose a department.' });
      return;
    }
    this.saving.set(true);
    this.formError.set(null);
    this.api
      .saveTeam(this.t?.id ?? null, {
        departmentId: dept,
        name: this.name.trim(),
        description: this.description.trim() || null,
        leadUserId: this.leadUserId,
        isActive: this.active(),
        memberUserIds: this.members(),
        routing: this.t ? null : { mode: 'LoadBalanced', onlyOnlineAgents: true, considerShifts: false, maxOpenTicketsPerAgent: null, fallbackTeamId: null },
        rowVersion: this.t?.rowVersion ?? null,
      })
      .subscribe({
        next: (saved) => this.ref.close(saved),
        error: (err: unknown) => {
          this.saving.set(false);
          this.errors.set(validationMessages(err));
          this.formError.set(saveErrorMessage(err));
        },
      });
  }
}

interface RoutingFormData {
  team: TeamDto;
  teams: TeamDto[];
}

/** Routing configuration of one team: mode, availability/shift awareness, capacity cap, fallback queue. */
@Component({
  selector: 'desk-team-routing-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdCheckbox, SdRadioGroup, SdRadio],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="'Routing — ' + data.team.name" description="Applies to new tickets routed to this team (manual assignment is never blocked).">
      <form id="routing-form" class="form" (ngSubmit)="save()" novalidate>
        <sd-form-field label="Assignment mode" [error]="errors()['routing.mode']">
          <sd-radio-group [(value)]="mode" ariaLabel="Assignment mode">
            @for (m of modes; track m) {
              <sd-radio [value]="m">
                <strong>{{ info[m].label }}</strong>
                <span class="hint"> — {{ info[m].help }}</span>
              </sd-radio>
            }
          </sd-radio-group>
        </sd-form-field>
        <div class="checks">
          <sd-checkbox [(value)]="onlyOnline">Only agents who are Online</sd-checkbox>
          <sd-checkbox [(value)]="considerShifts">Skip agents who are off shift</sd-checkbox>
        </div>
        <p class="hint">Agents on approved leave or delegated away are always skipped; light agents never receive tickets.</p>
        <div class="two">
          <sd-form-field label="Max open tickets per agent" optional hint="Team cap; the agent's own capacity also applies." [error]="errors()['routing.maxOpenTicketsPerAgent']">
            <input sdInput name="max" type="number" min="1" max="1000" [(ngModel)]="maxLoad" />
          </sd-form-field>
          <sd-form-field label="Fallback queue" optional hint="Team that receives tickets when nobody here is eligible." [error]="errors()['routing.fallbackTeamId']">
            <select sdInput name="fallback" [(ngModel)]="fallbackTeamId">
              <option [ngValue]="null">None — wait in this queue</option>
              @for (t of data.teams; track t.id) {
                <option [ngValue]="t.id">{{ t.name }}</option>
              }
            </select>
          </sd-form-field>
        </div>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="routing-form" [loading]="saving()">Save routing</button>
      </div>
    </sd-dialog>
  `,
  styles: FORM_STYLES,
})
export class TeamRoutingDialog {
  protected readonly data = inject<RoutingFormData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<TeamDto>>(DialogRef);
  private readonly api = inject(OrganisationApi);
  protected readonly modes = ROUTING_MODES;
  protected readonly info = ROUTING_MODE_INFO;
  protected readonly mode = signal<string | null>(this.data.team.routing.mode);
  protected readonly onlyOnline = signal(this.data.team.routing.onlyOnlineAgents);
  protected readonly considerShifts = signal(this.data.team.routing.considerShifts);
  protected maxLoad: number | null = this.data.team.routing.maxOpenTicketsPerAgent;
  protected fallbackTeamId: Guid | null = this.data.team.routing.fallbackTeamId;
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  protected save(): void {
    this.saving.set(true);
    this.formError.set(null);
    const max = this.maxLoad === null || (this.maxLoad as unknown) === '' ? null : Number(this.maxLoad);
    this.api
      .saveRouting(
        this.data.team.id,
        { mode: this.mode() as RoutingMode, onlyOnlineAgents: this.onlyOnline(), considerShifts: this.considerShifts(), maxOpenTicketsPerAgent: max, fallbackTeamId: this.fallbackTeamId },
        this.data.team.rowVersion,
      )
      .subscribe({
        next: (saved) => this.ref.close(saved),
        error: (err: unknown) => {
          this.saving.set(false);
          this.errors.set(validationMessages(err));
          this.formError.set(saveErrorMessage(err));
        },
      });
  }
}
