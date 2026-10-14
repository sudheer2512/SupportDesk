import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import {
  AgentListItemDto,
  AgentsAdminApi,
  DepartmentDto,
  InviteResultDto,
  MeStore,
  OrganisationApi,
  RoleOptionDto,
  SeatUsageDto,
  TeamDto,
  cursorList,
  saveErrorMessage,
  validationMessages,
} from '@sd/core';
import {
  SdAvatar,
  SdBadge,
  SdButton,
  SdCheckbox,
  SdCombobox,
  SdDialog,
  SdDialogService,
  SdEmptyState,
  SdFormField,
  SdInput,
  SdOption,
  SdPageHeader,
  SdRelativeTimePipe,
  SdSkeleton,
  ToastService,
} from '@sd/ui';
import { forkJoin } from 'rxjs';
import { ModuleTabsComponent } from '../customers/shared/module-tabs.component';
import { SECTION_STYLES, TABLE_STYLES } from '../customers/shared/styles';
import { FORM_STYLES, PAGE_STYLES, TEAMS_TABS, agentStatusTone, availabilityTone, loadPercent, timeZoneOptions } from './shared/org-ui';

/** /teams/agents — agents with status, roles, departments and load; plan seats; invite new agents. */
@Component({
  selector: 'desk-agents-page',
  imports: [FormsModule, SdPageHeader, SdButton, SdBadge, SdAvatar, SdSkeleton, SdEmptyState, SdRelativeTimePipe, ModuleTabsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Teams & Agents" subtitle="Agents, their roles, departments, capacity and status." icon="users">
        @if (canInvite) {
          <button sdButton sdPageActions variant="primary" icon="user-plus" (click)="invite()">Invite agent</button>
        }
      </sd-page-header>
      <desk-module-tabs [tabs]="tabs" ariaLabel="Teams & Agents sections" />
      <div class="desk-page__body">
        @if (seats(); as s) {
          <div class="seats" aria-label="Plan seats">
            <div>
              <span class="small muted">Agents ({{ s.planName }})</span>
              <div class="load">
                <span class="bar" [class.is-warn]="seatPct(s.agents, s.maxAgents) >= 90"><span [style.width.%]="seatPct(s.agents, s.maxAgents)"></span></span>
                <span class="small">{{ s.agents }} / {{ s.maxAgents ?? '∞' }}</span>
              </div>
            </div>
            <div>
              <span class="small muted">Light agents</span>
              <div class="load">
                <span class="bar" [class.is-warn]="seatPct(s.lightAgents, s.maxLightAgents) >= 90"><span [style.width.%]="seatPct(s.lightAgents, s.maxLightAgents)"></span></span>
                <span class="small">{{ s.lightAgents }} / {{ s.maxLightAgents ?? '∞' }}</span>
              </div>
            </div>
            <div><span class="small muted">Pending invitations</span><div class="small"><strong>{{ s.pendingInvitations }}</strong></div></div>
          </div>
        }
        <div class="toolbar">
          <label class="sd-visually-hidden" for="agent-q">Search agents</label>
          <input id="agent-q" class="sd-input" type="search" placeholder="Search name or e-mail" [ngModel]="q()" (ngModelChange)="q.set($event)" />
          <label class="sd-visually-hidden" for="agent-status">Status</label>
          <select id="agent-status" class="sd-input" [ngModel]="status()" (ngModelChange)="status.set($event)">
            <option value="">All statuses</option>
            <option value="Active">Active</option>
            <option value="Invited">Invited</option>
            <option value="Deactivated">Deactivated</option>
          </select>
          <label class="sd-visually-hidden" for="agent-dept">Department</label>
          <select id="agent-dept" class="sd-input" [ngModel]="dept()" (ngModelChange)="dept.set($event)">
            <option [ngValue]="null">All departments</option>
            @for (d of departments(); track d.id) {
              <option [ngValue]="d.id">{{ d.name }}</option>
            }
          </select>
          <label class="sd-visually-hidden" for="agent-kind">Agent type</label>
          <select id="agent-kind" class="sd-input" [ngModel]="kind()" (ngModelChange)="kind.set($event)">
            <option value="">All agent types</option>
            <option value="full">Full agents</option>
            <option value="light">Light agents</option>
          </select>
          <span class="sd-spacer"></span>
          @if (list.total() !== null) {
            <span class="small muted">{{ list.total() }} agents</span>
          }
        </div>
        @if (list.error(); as e) {
          <div class="error-box" role="alert"><span>{{ e.userMessage }}</span><button sdButton size="sm" (click)="list.load()">Try again</button></div>
        } @else if (list.loading() && list.items().length === 0) {
          <div aria-busy="true"><sd-skeleton [lines]="8" /></div>
        } @else if (list.isEmpty()) {
          <sd-empty-state icon="users" title="No agents match" message="Adjust the filters or invite a new agent." />
        } @else {
          <div class="dt-wrap">
            <table class="dt">
              <caption class="sd-visually-hidden">Agents</caption>
              <thead>
                <tr>
                  <th scope="col">Agent</th>
                  <th scope="col">Status</th>
                  <th scope="col">Roles</th>
                  <th scope="col">Departments</th>
                  <th scope="col">Availability</th>
                  <th scope="col">Load</th>
                  <th scope="col">Last sign-in</th>
                </tr>
              </thead>
              <tbody>
                @for (a of list.items(); track a.userId) {
                  <tr class="clickable" tabindex="0" (click)="open(a)" (keydown.enter)="open(a)" [attr.aria-label]="'Open ' + a.name">
                    <td>
                      <span class="who">
                        <sd-avatar [name]="a.name" [src]="a.avatarUrl" size="sm" />
                        <span>
                          <strong class="sd-truncate">{{ a.name }}</strong>
                          <span class="xs muted sd-truncate">{{ a.title ?? a.email }}</span>
                        </span>
                      </span>
                    </td>
                    <td>
                      <span class="pills">
                        <sd-badge size="sm" [tone]="statusTone(a.status)" dot>{{ a.status }}</sd-badge>
                        @if (a.isLightAgent) {
                          <sd-badge size="sm" tone="info">Light</sd-badge>
                        }
                        @if (a.onLeave) {
                          <sd-badge size="sm" tone="warning" icon="calendar">On leave</sd-badge>
                        }
                      </span>
                    </td>
                    <td class="small">{{ names(a.roles) }}</td>
                    <td class="small">{{ names(a.departments) }}</td>
                    <td><sd-badge size="sm" [tone]="tone(a.availability)" dot>{{ a.availability }}</sd-badge></td>
                    <td>
                      <span class="load" [attr.aria-label]="a.openTickets + ' of ' + a.capacity + ' open tickets'">
                        <span class="bar" [class.is-warn]="pct(a) >= 75" [class.is-danger]="pct(a) >= 100"><span [style.width.%]="pct(a)"></span></span>
                        <span class="xs muted">{{ a.openTickets }}/{{ a.capacity }}</span>
                      </span>
                    </td>
                    <td class="small nowrap">{{ a.lastLoginAt ? (a.lastLoginAt | sdRelativeTime) : 'Never' }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          @if (list.hasMore()) {
            <div class="more"><button sdButton (click)="list.loadMore()" [loading]="list.loadingMore()">Load more</button></div>
          }
        }
      </div>
    </div>
  `,
  styles: [
    TABLE_STYLES,
    SECTION_STYLES,
    PAGE_STYLES,
    `
      .seats { display: grid; gap: var(--sd-space-4); grid-template-columns: repeat(auto-fill, minmax(min(100%, 200px), 1fr)); margin-bottom: var(--sd-space-4);
        padding: var(--sd-space-3); border: 1px solid var(--sd-border); border-radius: var(--sd-radius-md); background: var(--sd-surface); }
      .more { display: flex; justify-content: center; margin-top: var(--sd-space-3); }
    `,
  ],
})
export class AgentsPage {
  private readonly api = inject(AgentsAdminApi);
  private readonly org = inject(OrganisationApi);
  private readonly dialogs = inject(SdDialogService);
  private readonly router = inject(Router);
  private readonly me = inject(MeStore);
  /** ?departmentId= from the departments page. */
  readonly departmentId = input<string | null>(null);
  protected readonly tabs = TEAMS_TABS;
  protected readonly canInvite = this.me.can('admin.users');
  protected readonly departments = signal<DepartmentDto[]>([]);
  protected readonly seats = signal<SeatUsageDto | null>(null);
  protected readonly q = signal('');
  protected readonly status = signal('');
  protected readonly dept = signal<string | null>(null);
  protected readonly kind = signal('');
  protected readonly list = cursorList<AgentListItemDto>((cursor) => this.api.list(this.query(cursor)), (a) => a.userId);
  private debounce: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    this.org.departments().subscribe({ next: (d) => this.departments.set(d) });
    this.loadSeats();
    let first = true;
    effect(() => {
      // Reload when a filter changes (search is debounced).
      this.q();
      this.status();
      this.dept();
      this.kind();
      if (first) {
        first = false;
        const fromRoute = this.departmentId();
        if (fromRoute) {
          this.dept.set(fromRoute);
          return;
        }
        this.list.load();
        return;
      }
      if (this.debounce) clearTimeout(this.debounce);
      this.debounce = setTimeout(() => this.list.load(), 250);
    });
  }

  private query(cursor: string | null) {
    const kind = this.kind();
    return {
      q: this.q().trim() || null,
      status: this.status() || null,
      departmentId: this.dept(),
      light: kind === 'light' ? true : kind === 'full' ? false : null,
      cursor,
      limit: 50,
    };
  }

  private loadSeats(): void {
    this.api.seats().subscribe({ next: (s) => this.seats.set(s) });
  }

  protected names(list: { name: string }[]): string {
    return list.map((x) => x.name).join(', ') || '—';
  }

  protected statusTone(status: string) {
    return agentStatusTone(status);
  }

  protected tone(availability: string) {
    return availabilityTone(availability);
  }

  protected pct(a: AgentListItemDto): number {
    return loadPercent(a.openTickets, a.capacity);
  }

  protected seatPct(used: number, max: number | null): number {
    return max === null ? 0 : loadPercent(used, max);
  }

  protected open(a: AgentListItemDto): void {
    void this.router.navigate(['/teams/agents', a.userId]);
  }

  protected invite(): void {
    this.dialogs
      .open<InviteResultDto, InviteData>(InviteAgentDialog, { data: { departments: this.departments().filter((d) => d.isActive) }, size: 'lg' })
      .closed.subscribe((result) => {
        if (!result) return;
        this.list.load();
        this.loadSeats();
        this.dialogs.open(InvitationSentDialog, { data: result });
      });
  }
}

interface InviteData {
  departments: DepartmentDto[];
}

/** Invite an agent: e-mail, name, role(s), departments (and supervised ones), teams, light agent, time zone. */
@Component({
  selector: 'desk-invite-agent-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdCheckbox, SdCombobox, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Invite an agent" description="They receive an e-mail with a code to choose their password. Seats are checked against your plan.">
      @if (loading()) {
        <sd-skeleton [lines]="6" />
      } @else {
        <form id="invite-form" class="form" (ngSubmit)="send()" novalidate>
          <div class="two">
            <sd-form-field label="Work e-mail" required [error]="errors()['email']">
              <input sdInput name="email" type="email" [(ngModel)]="email" required maxlength="254" autocomplete="off" />
            </sd-form-field>
            <sd-form-field label="Full name" required [error]="errors()['name']">
              <input sdInput name="name" [(ngModel)]="name" required maxlength="120" />
            </sd-form-field>
          </div>
          <div class="two">
            <sd-form-field label="Title" optional>
              <input sdInput name="title" [(ngModel)]="title" maxlength="120" />
            </sd-form-field>
            <sd-form-field label="Time zone" optional [error]="errors()['timeZone']">
              <select sdInput name="tz" [(ngModel)]="timeZone">
                <option [ngValue]="null">Organisation default</option>
                @for (z of zones; track z) {
                  <option [ngValue]="z">{{ z }}</option>
                }
              </select>
            </sd-form-field>
          </div>
          <sd-checkbox [value]="light()" (valueChange)="setLight($event)">Light agent (collaborator: views tickets and adds internal notes only)</sd-checkbox>
          <sd-form-field label="Roles" [hint]="light() ? 'Light agents never reply to customers or own tickets, whatever the role.' : 'Empty = the Agent role.'" [error]="errors()['roleIds']">
            <sd-combobox multiple [options]="roleOptions()" [value]="roles()" (valueChange)="roles.set($any($event) ?? [])" placeholder="Choose roles…" ariaLabel="Roles" />
          </sd-form-field>
          <sd-form-field label="Departments" required [error]="errors()['departmentIds']">
            <sd-combobox multiple [options]="deptOptions" [value]="depts()" (valueChange)="setDepts($any($event) ?? [])" placeholder="Choose departments…" ariaLabel="Departments" />
          </sd-form-field>
          <div class="two">
            <sd-form-field label="Supervises" optional hint="Departments where the agent is a supervisor.">
              <sd-combobox multiple [options]="supervisedOptions()" [value]="supervised()" (valueChange)="supervised.set($any($event) ?? [])" placeholder="None" ariaLabel="Supervised departments" />
            </sd-form-field>
            <sd-form-field label="Teams" optional [error]="errors()['teamIds']">
              <sd-combobox multiple [options]="teamOptions()" [value]="teams()" (valueChange)="teams.set($any($event) ?? [])" placeholder="None" ariaLabel="Teams" />
            </sd-form-field>
          </div>
          @if (formError()) {
            <p class="error" role="alert">{{ formError() }}</p>
          }
        </form>
      }
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="invite-form" icon="send" [loading]="saving()" [disabled]="loading()">Send invitation</button>
      </div>
    </sd-dialog>
  `,
  styles: FORM_STYLES,
})
export class InviteAgentDialog {
  private readonly data = inject<InviteData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<InviteResultDto>>(DialogRef);
  private readonly api = inject(AgentsAdminApi);
  private readonly org = inject(OrganisationApi);
  protected email = '';
  protected name = '';
  protected title = '';
  protected timeZone: string | null = null;
  protected readonly zones = timeZoneOptions();
  protected readonly light = signal(false);
  protected readonly roles = signal<string[]>([]);
  protected readonly depts = signal<string[]>([]);
  protected readonly supervised = signal<string[]>([]);
  protected readonly teams = signal<string[]>([]);
  private readonly allRoles = signal<RoleOptionDto[]>([]);
  private readonly allTeams = signal<TeamDto[]>([]);
  protected readonly deptOptions: SdOption[] = this.data.departments.map((d) => ({ value: d.id, label: d.name }));
  protected readonly roleOptions = computed<SdOption[]>(() =>
    this.allRoles().map((r) => ({ value: r.id, label: r.name, description: r.description ?? `${r.ticketScope} scope` })),
  );
  protected readonly supervisedOptions = computed(() => this.deptOptions.filter((o) => this.depts().includes(o.value)));
  protected readonly teamOptions = computed<SdOption[]>(() =>
    this.allTeams()
      .filter((t) => t.isActive && this.depts().includes(t.departmentId))
      .map((t) => ({ value: t.id, label: t.name, group: t.departmentName })),
  );
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  constructor() {
    forkJoin({ roles: this.org.roleOptions(), teams: this.org.teams() }).subscribe({
      next: (r) => {
        this.allRoles.set(r.roles);
        this.allTeams.set(r.teams);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.formError.set(saveErrorMessage(err));
        this.loading.set(false);
      },
    });
  }

  protected setLight(on: boolean): void {
    this.light.set(on);
    const lightRole = this.allRoles().find((r) => r.isLightAgentRole);
    if (on && lightRole && this.roles().length === 0) this.roles.set([lightRole.id]);
  }

  protected setDepts(ids: string[]): void {
    this.depts.set(ids);
    this.supervised.update((s) => s.filter((x) => ids.includes(x)));
    const allowed = new Set(this.allTeams().filter((t) => ids.includes(t.departmentId)).map((t) => t.id));
    this.teams.update((t) => t.filter((x) => allowed.has(x)));
  }

  protected send(): void {
    this.saving.set(true);
    this.formError.set(null);
    this.api
      .invite({
        email: this.email.trim(),
        name: this.name.trim(),
        title: this.title.trim() || null,
        isLightAgent: this.light(),
        roleIds: this.roles(),
        departmentIds: this.depts(),
        supervisorDepartmentIds: this.supervised(),
        teamIds: this.teams(),
        timeZone: this.timeZone,
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

/** Shows where the invitation went and, in demo mode, the code (like the MFA code on the login screen). */
@Component({
  selector: 'desk-invitation-sent-dialog',
  imports: [SdDialog, SdButton, SdBadge],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Invitation sent" [description]="'We e-mailed ' + data.sentTo + ' a code to choose a password.'">
      <div class="body">
        <p>The code expires {{ expires }}. The invitee opens the link in the e-mail, enters the code and sets a password.</p>
        @if (data.demoCode) {
          <div class="demo" role="status">
            <sd-badge tone="info" icon="info">Demo mode</sd-badge>
            <span>Invitation code:</span>
            <strong class="code">{{ data.demoCode }}</strong>
          </div>
        }
        <p class="link">
          Accept link: <a [href]="data.acceptUrl" target="_blank" rel="noopener">{{ data.acceptUrl }}</a>
          <button sdButton size="sm" variant="ghost" icon="copy" iconOnly aria-label="Copy the accept link" (click)="copy()"></button>
        </p>
      </div>
      <div sdDialogFooter>
        <button sdButton variant="primary" type="button" (click)="ref.close()">Done</button>
      </div>
    </sd-dialog>
  `,
  styles: `
    .body { display: grid; gap: var(--sd-space-3); }
    .demo { display: flex; flex-wrap: wrap; align-items: center; gap: var(--sd-space-2); padding: var(--sd-space-3); border-radius: var(--sd-radius-md);
      background: var(--sd-info-bg); border: 1px solid var(--sd-info-border); }
    .code { font-family: var(--sd-font-mono); font-size: var(--sd-text-xl); letter-spacing: 3px; }
    .link { overflow-wrap: anywhere; font-size: var(--sd-text-sm); }
  `,
})
export class InvitationSentDialog {
  protected readonly data = inject<InviteResultDto>(DIALOG_DATA);
  protected readonly ref = inject(DialogRef);
  private readonly toast = inject(ToastService);
  protected readonly expires = new Date(this.data.expiresAt).toLocaleString();

  protected copy(): void {
    const url = this.data.acceptUrl.startsWith('http') ? this.data.acceptUrl : `${location.origin}${this.data.acceptUrl}`;
    void navigator.clipboard?.writeText(url).then(() => this.toast.success('Link copied'));
  }
}
