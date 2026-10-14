import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import {
  AVAILABILITIES,
  AgentDetailDto,
  AgentListItemDto,
  AgentsAdminApi,
  CustomersApi,
  DepartmentDto,
  Guid,
  InviteResultDto,
  MeStore,
  MetaApi,
  OrganisationApi,
  ROUTING_ATTRIBUTE_KINDS,
  RoleOptionDto,
  SkillDto,
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
  SdChipsInput,
  SdCombobox,
  SdDateTimePipe,
  SdDialog,
  SdDialogService,
  SdFormField,
  SdInput,
  SdOption,
  SdPageHeader,
  SdRadio,
  SdRadioGroup,
  SdRelativeTimePipe,
  SdSkeleton,
  ToastService,
} from '@sd/ui';
import { forkJoin, map } from 'rxjs';
import { SECTION_STYLES } from '../customers/shared/styles';
import { InvitationSentDialog } from './agents.page';
import { FORM_STYLES, PAGE_STYLES, agentStatusTone, availabilityTone, errorText, timeZoneOptions } from './shared/org-ui';

/** /teams/agents/:id — one agent: profile, access, routing profile (capacity, skills, attributes), shifts, workload and lifecycle actions. */
@Component({
  selector: 'desk-agent-detail-page',
  imports: [FormsModule, RouterLink, SdPageHeader, SdButton, SdBadge, SdCard, SdAvatar, SdSkeleton, SdDateTimePipe, SdRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      @if (agent(); as a) {
        <sd-page-header [title]="a.name" [subtitle]="a.title ?? a.email" icon="user" [breadcrumbs]="crumbs">
          <div sdPageActions class="actions">
            @if (isAdmin && a.status === 'Invited') {
              <button sdButton size="sm" icon="send" (click)="resend(a)" [loading]="busy()">Resend invitation</button>
              <button sdButton size="sm" variant="ghost" icon="x" (click)="revoke(a)">Revoke</button>
            }
            @if (isAdmin && a.status !== 'Deactivated' && !isSelf(a)) {
              <button sdButton size="sm" variant="danger" icon="lock" (click)="deactivate(a)">Deactivate</button>
            }
            @if (isAdmin && a.status === 'Deactivated') {
              <button sdButton size="sm" variant="primary" icon="lock-open" (click)="reactivate(a)" [loading]="busy()">Reactivate</button>
            }
          </div>
        </sd-page-header>
      } @else {
        <sd-page-header title="Agent" icon="user" [breadcrumbs]="crumbs" />
      }
      <div class="desk-page__body">
        @if (error()) {
          <div class="error-box" role="alert"><span>{{ error() }}</span><button sdButton size="sm" (click)="load()">Try again</button></div>
        } @else if (!agent()) {
          <div class="cards" aria-busy="true">
            @for (i of [1, 2, 3]; track i) {
              <sd-card><sd-skeleton [lines]="6" /></sd-card>
            }
          </div>
        } @else {
          @let a = agent()!;
          <div class="hero">
            <div class="avatar">
              <sd-avatar [name]="a.name" [src]="a.avatarUrl" size="xl" [presence]="$any(a.availability)" />
              @if (canEditAvatar(a)) {
                <label class="upload">
                  <input type="file" accept="image/png,image/jpeg,image/gif,image/webp" class="sd-visually-hidden" (change)="upload(a, $event)" />
                  <span>{{ a.avatarUrl ? 'Change photo' : 'Add photo' }}</span>
                </label>
                @if (a.avatarUrl) {
                  <button sdButton size="sm" variant="link" (click)="removeAvatar(a)">Remove</button>
                }
              }
            </div>
            <div class="pills">
              <sd-badge [tone]="statusTone(a.status)" dot>{{ a.status }}</sd-badge>
              @if (a.isLightAgent) {
                <sd-badge tone="info">Light agent</sd-badge>
              }
              @if (a.mfaEnabled) {
                <sd-badge tone="success" icon="shield-check">MFA</sd-badge>
              }
              @if (a.invitation; as inv) {
                <sd-badge [tone]="inv.expired ? 'danger' : 'warning'" icon="mail">Invitation {{ inv.expired ? 'expired' : 'pending' }} · {{ inv.sentAt | sdRelativeTime }}</sd-badge>
              }
            </div>
            <div class="avail">
              <label for="avail" class="small muted">Availability</label>
              @if (canSetAvailability(a)) {
                <select id="avail" class="sd-input sd-input-sm" [ngModel]="a.availability" (ngModelChange)="setAvailability(a, $event)">
                  @for (v of availabilities; track v) {
                    <option [value]="v">{{ v }}</option>
                  }
                </select>
              } @else {
                <sd-badge id="avail" [tone]="tone(a.availability)" dot>{{ a.availability }}</sd-badge>
              }
              @if (a.availabilityChangedAt) {
                <span class="xs muted">since {{ a.availabilityChangedAt | sdRelativeTime }}</span>
              }
            </div>
          </div>

          <div class="cards">
            <sd-card title="Profile">
              <div sdCardActions>
                @if (isAdmin) {
                  <button sdButton size="sm" variant="ghost" icon="pencil" (click)="editProfile(a)">Edit</button>
                }
              </div>
              <dl class="meta">
                <dt>E-mail</dt>
                <dd>{{ a.email }}</dd>
                <dt>Phone</dt>
                <dd>{{ a.phone ?? '—' }}</dd>
                <dt>Time zone</dt>
                <dd>{{ a.timeZone }}</dd>
                <dt>Locale</dt>
                <dd>{{ a.locale }}</dd>
                <dt>Last sign-in</dt>
                <dd>{{ a.lastLoginAt ? (a.lastLoginAt | sdDateTime) : 'Never' }}</dd>
                <dt>Signature</dt>
                <dd>@if (a.signature) {<span class="sig" [innerHTML]="a.signature"></span>} @else {—}</dd>
              </dl>
            </sd-card>

            <sd-card title="Access">
              <div sdCardActions>
                @if (isAdmin) {
                  <button sdButton size="sm" variant="ghost" icon="pencil" (click)="editAccess(a)">Edit</button>
                }
              </div>
              <dl class="meta">
                <dt>Roles</dt>
                <dd><span class="pills">@for (r of a.roles; track r.id) {<sd-badge size="sm">{{ r.name }}</sd-badge>}</span></dd>
                <dt>Departments</dt>
                <dd>
                  <span class="pills">
                    @for (d of a.departments; track d.id) {
                      <sd-badge size="sm" [tone]="d.isSupervisor ? 'accent' : 'neutral'">{{ d.name }}{{ d.isSupervisor ? ' · supervisor' : '' }}</sd-badge>
                    }
                  </span>
                </dd>
                <dt>Teams</dt>
                <dd>{{ joined(a.teams) }}</dd>
                <dt>Reports to</dt>
                <dd>@if (a.reportsToUserId) {<a [routerLink]="['/teams/agents', a.reportsToUserId]">{{ a.reportsToName }}</a>} @else {—}</dd>
              </dl>
            </sd-card>

            <sd-card title="Routing profile" subtitle="Used by skill, territory and load-balanced routing.">
              <div sdCardActions>
                @if (canManageRouting) {
                  <button sdButton size="sm" variant="ghost" icon="pencil" (click)="editRouting(a)">Edit</button>
                }
              </div>
              <dl class="meta">
                <dt>Capacity</dt>
                <dd>{{ a.openTickets }} of {{ a.maxOpenTickets }} open tickets · {{ a.maxConcurrentChats }} chats</dd>
                <dt>Languages</dt>
                <dd>{{ a.languages.join(', ') || '—' }}</dd>
                <dt>Skills</dt>
                <dd>
                  @if (a.skills.length === 0) {
                    —
                  } @else {
                    <span class="pills">
                      @for (s of a.skills; track s.skillId) {
                        <sd-badge size="sm" [attr.title]="'Proficiency ' + s.level + ' of 5'">{{ s.name }} · {{ s.level }}/5</sd-badge>
                      }
                    </span>
                  }
                </dd>
                <dt>Attributes</dt>
                <dd>
                  @if (a.routingAttributes.length === 0) {
                    —
                  } @else {
                    <span class="pills">
                      @for (r of a.routingAttributes; track r.id) {
                        <sd-badge size="sm" tone="info">{{ r.kind }}: {{ r.label ?? r.value }}</sd-badge>
                      }
                    </span>
                  }
                </dd>
              </dl>
            </sd-card>

            <sd-card title="Shifts">
              @if (a.shifts.length === 0) {
                <p class="empty-note">No shift assigned — always considered on shift.</p>
              } @else {
                <ul class="list">
                  @for (s of a.shifts; track s.assignmentId) {
                    <li><strong>{{ s.shiftName }}</strong> <span class="small muted">from {{ s.effectiveFrom }}{{ s.effectiveTo ? ' to ' + s.effectiveTo : '' }}</span></li>
                  }
                </ul>
              }
              <a class="small" routerLink="/teams/shifts">Manage shifts &amp; leave</a>
            </sd-card>

            <sd-card title="Account-managed customers">
              @if (a.accountManagedCustomers.length === 0) {
                <p class="empty-note">None. Account managers are set on the customer record.</p>
              } @else {
                <ul class="list">
                  @for (c of a.accountManagedCustomers; track c.id) {
                    <li><a [routerLink]="['/customers', c.id]">{{ c.name }}</a></li>
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
    SECTION_STYLES,
    PAGE_STYLES,
    `
      .actions { display: flex; flex-wrap: wrap; gap: var(--sd-space-2); }
      .hero { display: flex; flex-wrap: wrap; gap: var(--sd-space-5); align-items: center; margin-bottom: var(--sd-space-5); }
      .avatar { display: flex; align-items: center; gap: var(--sd-space-3); flex-wrap: wrap; }
      .upload { cursor: pointer; color: var(--sd-primary); font-size: var(--sd-text-sm); }
      .upload:focus-within { box-shadow: var(--sd-focus-ring); border-radius: var(--sd-radius-sm); }
      .avail { display: flex; align-items: center; gap: var(--sd-space-2); flex-wrap: wrap; }
      .list { margin: 0 0 var(--sd-space-2); padding-left: 1.1em; display: grid; gap: 4px; }
      .sig { font-size: var(--sd-text-sm); }
      a { color: var(--sd-primary); }
    `,
  ],
})
export class AgentDetailPage {
  private readonly api = inject(AgentsAdminApi);
  private readonly dialogs = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  private readonly me = inject(MeStore);
  readonly id = input.required<string>();
  protected readonly agent = signal<AgentDetailDto | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly availabilities = AVAILABILITIES;
  protected readonly isAdmin = this.me.can('admin.users');
  protected readonly canManageRouting = this.me.canAny(['admin.users', 'agents.manage']);
  protected readonly crumbs = [{ label: 'Teams & Agents', link: '/teams' }, { label: 'Agents', link: '/teams/agents' }];

  constructor() {
    effect(() => {
      this.id();
      this.load();
    });
  }

  protected load(): void {
    this.error.set(null);
    this.api.get(this.id()).subscribe({
      next: (a) => this.agent.set(a),
      error: (err: unknown) => this.error.set(errorText(err)),
    });
  }

  protected isSelf(a: AgentDetailDto): boolean {
    return a.userId === this.me.user()?.id;
  }

  protected canEditAvatar(a: AgentDetailDto): boolean {
    return this.isAdmin || this.isSelf(a);
  }

  protected canSetAvailability(a: AgentDetailDto): boolean {
    return a.status !== 'Deactivated' && (this.canManageRouting || this.isSelf(a));
  }

  protected statusTone(s: string) {
    return agentStatusTone(s);
  }

  protected tone(s: string) {
    return availabilityTone(s);
  }

  protected joined(list: { name: string }[]): string {
    return list.map((x) => x.name).join(', ') || '—';
  }

  private apply(a: AgentDetailDto, message?: string): void {
    this.agent.set(a);
    this.busy.set(false);
    if (message) this.toast.success(message);
  }

  private fail(err: unknown, title = 'Could not save'): void {
    this.busy.set(false);
    this.toast.error(title, saveErrorMessage(err));
  }

  protected setAvailability(a: AgentDetailDto, value: string): void {
    this.api.setAvailability(a.userId, value).subscribe({ next: (r) => this.apply(r, `Availability set to ${value}`), error: (e: unknown) => this.fail(e) });
  }

  protected upload(a: AgentDetailDto, event: Event): void {
    const file = (event.target as HTMLInputElement).files?.[0];
    (event.target as HTMLInputElement).value = '';
    if (!file) return;
    if (file.size > 2 * 1024 * 1024) {
      this.toast.error('Image too large', 'Avatars may be at most 2 MB.');
      return;
    }
    this.api.uploadAvatar(a.userId, file).subscribe({ next: (r) => this.apply(r, 'Photo updated'), error: (e: unknown) => this.fail(e, 'Upload failed') });
  }

  protected removeAvatar(a: AgentDetailDto): void {
    this.api.removeAvatar(a.userId).subscribe({ next: (r) => this.apply(r, 'Photo removed'), error: (e: unknown) => this.fail(e) });
  }

  protected resend(a: AgentDetailDto): void {
    this.busy.set(true);
    this.api.resendInvitation(a.userId).subscribe({
      next: (r) => {
        this.apply(r.agent);
        this.dialogs.open<void, InviteResultDto>(InvitationSentDialog, { data: r });
      },
      error: (e: unknown) => this.fail(e, 'Could not resend'),
    });
  }

  protected async revoke(a: AgentDetailDto): Promise<void> {
    const ok = await this.dialogs.confirm({ title: 'Revoke invitation?', message: `${a.name} will not be able to join and the seat is freed.`, danger: true, confirmLabel: 'Revoke' });
    if (!ok) return;
    this.api.revokeInvitation(a.userId).subscribe({ next: (r) => this.apply(r, 'Invitation revoked'), error: (e: unknown) => this.fail(e) });
  }

  protected reactivate(a: AgentDetailDto): void {
    this.busy.set(true);
    this.api.reactivate(a.userId).subscribe({ next: (r) => this.apply(r, `${a.name} reactivated`), error: (e: unknown) => this.fail(e, 'Could not reactivate') });
  }

  protected deactivate(a: AgentDetailDto): void {
    this.dialogs.open<AgentDetailDto, AgentDetailDto>(DeactivateAgentDialog, { data: a }).closed.subscribe((r) => {
      if (r) this.apply(r);
    });
  }

  protected editProfile(a: AgentDetailDto): void {
    this.dialogs.open<AgentDetailDto, AgentDetailDto>(AgentProfileDialog, { data: a, size: 'lg' }).closed.subscribe((r) => {
      if (r) this.apply(r, 'Profile saved');
    });
  }

  protected editAccess(a: AgentDetailDto): void {
    this.dialogs.open<AgentDetailDto, AgentDetailDto>(AgentAccessDialog, { data: a, size: 'lg' }).closed.subscribe((r) => {
      if (r) this.apply(r, 'Access saved');
    });
  }

  protected editRouting(a: AgentDetailDto): void {
    this.dialogs.open<AgentDetailDto, AgentDetailDto>(AgentRoutingDialog, { data: a, size: 'lg' }).closed.subscribe((r) => {
      if (r) this.apply(r, 'Routing profile saved');
    });
  }
}

/** Edit name, title, phone, time zone, locale and signature. */
@Component({
  selector: 'desk-agent-profile-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="'Profile — ' + a.name">
      <form id="profile-form" class="form" (ngSubmit)="save()" novalidate>
        <div class="three">
          <sd-form-field label="Display name" required [error]="errors()['displayName']">
            <input sdInput name="dn" [(ngModel)]="displayName" required maxlength="120" />
          </sd-form-field>
          <sd-form-field label="First name" optional>
            <input sdInput name="fn" [(ngModel)]="firstName" maxlength="80" />
          </sd-form-field>
          <sd-form-field label="Last name" optional>
            <input sdInput name="ln" [(ngModel)]="lastName" maxlength="80" />
          </sd-form-field>
        </div>
        <div class="two">
          <sd-form-field label="Title" optional>
            <input sdInput name="title" [(ngModel)]="title" maxlength="120" />
          </sd-form-field>
          <sd-form-field label="Phone" optional [error]="errors()['phone']">
            <input sdInput name="phone" type="tel" [(ngModel)]="phone" maxlength="40" />
          </sd-form-field>
        </div>
        <div class="two">
          <sd-form-field label="Time zone" [error]="errors()['timeZone']">
            <select sdInput name="tz" [(ngModel)]="timeZone">
              @for (z of zones; track z) {
                <option [ngValue]="z">{{ z }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Locale" [error]="errors()['locale']" hint="e.g. en, en-IN, hi">
            <input sdInput name="locale" [(ngModel)]="locale" maxlength="16" />
          </sd-form-field>
        </div>
        <sd-form-field label="E-mail signature" optional hint="Basic HTML is allowed; it is sanitised by the server.">
          <textarea sdInput name="sig" rows="4" [(ngModel)]="signature" maxlength="4000"></textarea>
        </sd-form-field>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="profile-form" [loading]="saving()">Save</button>
      </div>
    </sd-dialog>
  `,
  styles: FORM_STYLES,
})
export class AgentProfileDialog {
  protected readonly a = inject<AgentDetailDto>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<AgentDetailDto>>(DialogRef);
  private readonly api = inject(AgentsAdminApi);
  protected displayName = this.a.name;
  protected firstName = this.a.firstName ?? '';
  protected lastName = this.a.lastName ?? '';
  protected title = this.a.title ?? '';
  protected phone = this.a.phone ?? '';
  protected timeZone = this.a.timeZone;
  protected locale = this.a.locale;
  protected signature = this.a.signature ?? '';
  protected readonly zones = timeZoneOptions(this.a.timeZone);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  protected save(): void {
    this.saving.set(true);
    this.api
      .updateProfile(this.a.userId, {
        displayName: this.displayName.trim(),
        firstName: this.firstName.trim() || null,
        lastName: this.lastName.trim() || null,
        title: this.title.trim() || null,
        phone: this.phone.trim() || null,
        timeZone: this.timeZone,
        locale: this.locale.trim() || null,
        signature: this.signature.trim() || null,
        rowVersion: this.a.rowVersion,
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

/** Roles, departments (supervised), teams, reporting line and light-agent flag. */
@Component({
  selector: 'desk-agent-access-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdCheckbox, SdCombobox, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="'Access — ' + a.name" description="Role permissions apply within the agent's departments (or the role's data scope).">
      @if (loading()) {
        <sd-skeleton [lines]="6" />
      } @else {
        <form id="access-form" class="form" (ngSubmit)="save()" novalidate>
          <sd-form-field label="Roles" required [error]="errors()['roleIds']">
            <sd-combobox multiple [options]="roleOptions()" [value]="roles()" (valueChange)="roles.set($any($event) ?? [])" ariaLabel="Roles" />
          </sd-form-field>
          <sd-form-field label="Departments" required [error]="errors()['departmentIds']">
            <sd-combobox multiple [options]="deptOptions()" [value]="depts()" (valueChange)="setDepts($any($event) ?? [])" ariaLabel="Departments" />
          </sd-form-field>
          <div class="two">
            <sd-form-field label="Supervises" optional [error]="errors()['supervisorDepartmentIds']">
              <sd-combobox multiple [options]="supervisedOptions()" [value]="supervised()" (valueChange)="supervised.set($any($event) ?? [])" placeholder="None"
                ariaLabel="Supervised departments" />
            </sd-form-field>
            <sd-form-field label="Teams" optional [error]="errors()['teamIds']">
              <sd-combobox multiple [options]="teamOptions()" [value]="teams()" (valueChange)="teams.set($any($event) ?? [])" placeholder="None" ariaLabel="Teams" />
            </sd-form-field>
          </div>
          <sd-form-field label="Reports to" optional hint="Receives this agent's leave requests." [error]="errors()['reportsToUserId']">
            <select sdInput name="reports" [(ngModel)]="reportsTo">
              <option [ngValue]="null">—</option>
              @for (u of managers(); track u.userId) {
                <option [ngValue]="u.userId">{{ u.name }}</option>
              }
            </select>
          </sd-form-field>
          <sd-checkbox [(value)]="light">Light agent (cannot reply to customers or own tickets)</sd-checkbox>
          @if (formError()) {
            <p class="error" role="alert">{{ formError() }}</p>
          }
        </form>
      }
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="access-form" [loading]="saving()" [disabled]="loading()">Save</button>
      </div>
    </sd-dialog>
  `,
  styles: FORM_STYLES,
})
export class AgentAccessDialog {
  protected readonly a = inject<AgentDetailDto>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<AgentDetailDto>>(DialogRef);
  private readonly api = inject(AgentsAdminApi);
  private readonly org = inject(OrganisationApi);
  protected readonly roles = signal<string[]>(this.a.roles.map((r) => r.id));
  protected readonly depts = signal<string[]>(this.a.departments.map((d) => d.id));
  protected readonly supervised = signal<string[]>(this.a.departments.filter((d) => d.isSupervisor).map((d) => d.id));
  protected readonly teams = signal<string[]>(this.a.teams.map((t) => t.id));
  protected readonly light = signal(this.a.isLightAgent);
  protected reportsTo: Guid | null = this.a.reportsToUserId;
  private readonly allRoles = signal<RoleOptionDto[]>([]);
  private readonly allDepts = signal<DepartmentDto[]>([]);
  private readonly allTeams = signal<TeamDto[]>([]);
  protected readonly managers = signal<AgentListItemDto[]>([]);
  protected readonly roleOptions = computed<SdOption[]>(() => this.allRoles().map((r) => ({ value: r.id, label: r.name, description: r.description })));
  protected readonly deptOptions = computed<SdOption[]>(() => this.allDepts().map((d) => ({ value: d.id, label: d.name })));
  protected readonly supervisedOptions = computed(() => this.deptOptions().filter((o) => this.depts().includes(o.value)));
  protected readonly teamOptions = computed<SdOption[]>(() =>
    this.allTeams()
      .filter((t) => this.depts().includes(t.departmentId))
      .map((t) => ({ value: t.id, label: t.name, group: t.departmentName })),
  );
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  constructor() {
    forkJoin({ roles: this.org.roleOptions(), depts: this.org.departments(), teams: this.org.teams(), managers: this.api.list({ status: 'Active', limit: 200 }) }).subscribe({
      next: (r) => {
        this.allRoles.set(r.roles);
        this.allDepts.set(r.depts);
        this.allTeams.set(r.teams);
        this.managers.set(r.managers.items.filter((m) => m.userId !== this.a.userId && !m.isLightAgent));
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.formError.set(saveErrorMessage(err));
        this.loading.set(false);
      },
    });
  }

  protected setDepts(ids: string[]): void {
    this.depts.set(ids);
    this.supervised.update((s) => s.filter((x) => ids.includes(x)));
    const allowed = new Set(this.allTeams().filter((t) => ids.includes(t.departmentId)).map((t) => t.id));
    this.teams.update((t) => t.filter((x) => allowed.has(x)));
  }

  protected save(): void {
    this.saving.set(true);
    this.formError.set(null);
    this.api
      .updateAccess(this.a.userId, {
        roleIds: this.roles(),
        departmentIds: this.depts(),
        supervisorDepartmentIds: this.supervised(),
        teamIds: this.teams(),
        isLightAgent: this.light(),
        reportsToUserId: this.reportsTo,
        rowVersion: this.a.rowVersion,
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

const NO_OPTIONS: SdOption[] = [];

interface SkillRow {
  skillId: string | null;
  level: number;
}

interface AttributeRow {
  kind: string;
  value: string;
  refId: string | null;
}

/** Capacity, languages, skills with proficiency and routing attributes (territory, product, customer, language, category, tier). */
@Component({
  selector: 'desk-agent-routing-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdChipsInput, SdCombobox, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="'Routing profile — ' + a.name">
      @if (loading()) {
        <sd-skeleton [lines]="6" />
      } @else {
        <form id="routing-profile-form" class="form" (ngSubmit)="save()" novalidate>
          <div class="three">
            <sd-form-field label="Max open tickets" required [error]="errors()['maxOpenTickets']">
              <input sdInput name="cap" type="number" min="1" max="1000" [(ngModel)]="maxOpen" required />
            </sd-form-field>
            <sd-form-field label="Max concurrent chats" [error]="errors()['maxConcurrentChats']">
              <input sdInput name="chats" type="number" min="0" max="50" [(ngModel)]="maxChats" />
            </sd-form-field>
            <sd-form-field label="Languages" [error]="errors()['languages']" hint="Codes such as en, hi, ta">
              <sd-chips-input [value]="languages()" (valueChange)="languages.set($event)" ariaLabel="Languages" placeholder="Add a code" />
            </sd-form-field>
          </div>
          <fieldset class="rows">
            <legend>Skills <span class="hint">(1 basic – 5 expert)</span></legend>
            @for (row of skills(); track $index; let i = $index) {
              <div class="row">
                <select class="sd-input sd-input-sm" [attr.aria-label]="'Skill ' + (i + 1)" [ngModel]="row.skillId" [ngModelOptions]="{ standalone: true }"
                  (ngModelChange)="updateSkill(i, { skillId: $event })">
                  <option [ngValue]="null" disabled>Choose a skill…</option>
                  @for (s of allSkills(); track s.id) {
                    <option [ngValue]="s.id">{{ s.category ? s.category + ' › ' : '' }}{{ s.name }}</option>
                  }
                </select>
                <select class="sd-input sd-input-sm level" [attr.aria-label]="'Proficiency of skill ' + (i + 1)" [ngModel]="row.level" [ngModelOptions]="{ standalone: true }"
                  (ngModelChange)="updateSkill(i, { level: +$event })">
                  @for (l of [1, 2, 3, 4, 5]; track l) {
                    <option [ngValue]="l">{{ l }}</option>
                  }
                </select>
                <button sdButton type="button" size="sm" variant="ghost" icon="x" iconOnly [attr.aria-label]="'Remove skill ' + (i + 1)" (click)="removeSkill(i)"></button>
              </div>
            }
            <button sdButton type="button" size="sm" variant="ghost" icon="plus" (click)="addSkill()">Add skill</button>
            @if (errors()['skills']) {
              <p class="error">{{ errors()['skills'] }}</p>
            }
          </fieldset>
          <fieldset class="rows">
            <legend>Routing attributes</legend>
            @for (row of attributes(); track $index; let i = $index) {
              <div class="row">
                <select class="sd-input sd-input-sm" [attr.aria-label]="'Attribute ' + (i + 1) + ' kind'" [ngModel]="row.kind" [ngModelOptions]="{ standalone: true }"
                  (ngModelChange)="updateAttribute(i, { kind: $event, value: '', refId: null })">
                  @for (k of kinds; track k) {
                    <option [ngValue]="k">{{ k }}</option>
                  }
                </select>
                @switch (row.kind) {
                  @case ('Customer') {
                    <sd-combobox size="sm" [search]="searchCustomers" [options]="knownOption(row.refId)" [value]="row.refId"
                      (valueChange)="updateAttribute(i, { refId: $any($event) })" placeholder="Search customers…" [ariaLabel]="'Attribute ' + (i + 1) + ' customer'" />
                  }
                  @case ('Product') {
                    <select class="sd-input sd-input-sm" [attr.aria-label]="'Attribute ' + (i + 1) + ' product'" [ngModel]="row.refId" [ngModelOptions]="{ standalone: true }"
                      (ngModelChange)="updateAttribute(i, { refId: $event })">
                      <option [ngValue]="null" disabled>Choose…</option>
                      @for (p of products(); track p.id) {
                        <option [ngValue]="p.id">{{ p.name }}</option>
                      }
                    </select>
                  }
                  @case ('Category') {
                    <select class="sd-input sd-input-sm" [attr.aria-label]="'Attribute ' + (i + 1) + ' category'" [ngModel]="row.refId" [ngModelOptions]="{ standalone: true }"
                      (ngModelChange)="updateAttribute(i, { refId: $event })">
                      <option [ngValue]="null" disabled>Choose…</option>
                      @for (c of categories(); track c.id) {
                        <option [ngValue]="c.id">{{ c.name }}</option>
                      }
                    </select>
                  }
                  @default {
                    <input class="sd-input sd-input-sm" [attr.aria-label]="'Attribute ' + (i + 1) + ' value'" [ngModel]="row.value" [ngModelOptions]="{ standalone: true }"
                      (ngModelChange)="updateAttribute(i, { value: $event })" [placeholder]="row.kind === 'Territory' ? 'e.g. South' : row.kind === 'Language' ? 'e.g. ta' : 'e.g. Platinum'" />
                  }
                }
                <button sdButton type="button" size="sm" variant="ghost" icon="x" iconOnly [attr.aria-label]="'Remove attribute ' + (i + 1)" (click)="removeAttribute(i)"></button>
              </div>
            }
            <button sdButton type="button" size="sm" variant="ghost" icon="plus" (click)="addAttribute()">Add attribute</button>
          </fieldset>
          @if (formError()) {
            <p class="error" role="alert">{{ formError() }}</p>
          }
        </form>
      }
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="routing-profile-form" [loading]="saving()" [disabled]="loading()">Save</button>
      </div>
    </sd-dialog>
  `,
  styles: [
    FORM_STYLES,
    `
      .rows { border: 1px solid var(--sd-divider); border-radius: var(--sd-radius-md); padding: var(--sd-space-3); display: grid; gap: 6px; margin: 0; }
      legend { font-weight: var(--sd-weight-medium); padding: 0 4px; }
      .row { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1.4fr) auto; gap: 6px; align-items: center; }
      .row .level { max-width: 90px; }
      @media (max-width: 640px) { .row { grid-template-columns: 1fr auto; } }
    `,
  ],
})
export class AgentRoutingDialog {
  protected readonly a = inject<AgentDetailDto>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<AgentDetailDto>>(DialogRef);
  private readonly api = inject(AgentsAdminApi);
  private readonly org = inject(OrganisationApi);
  private readonly meta = inject(MetaApi);
  private readonly customersApi = inject(CustomersApi);
  protected maxOpen = this.a.maxOpenTickets;
  protected maxChats = this.a.maxConcurrentChats;
  protected readonly kinds = ROUTING_ATTRIBUTE_KINDS;
  protected readonly languages = signal<string[]>(this.a.languages);
  protected readonly skills = signal<SkillRow[]>(this.a.skills.map((s) => ({ skillId: s.skillId, level: s.level })));
  protected readonly attributes = signal<AttributeRow[]>(this.a.routingAttributes.map((r) => ({ kind: r.kind, value: r.value ?? '', refId: r.refId })));
  private readonly labels = new Map(this.a.routingAttributes.filter((r) => r.refId).map((r) => [r.refId as string, r.label ?? '']));
  protected readonly allSkills = signal<SkillDto[]>([]);
  protected readonly products = computed(() => this.meta.ticketForm()?.products ?? []);
  protected readonly categories = computed(() => this.meta.ticketForm()?.categories ?? []);
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly searchCustomers = (q: string) =>
    this.customersApi.list({ q, limit: 15 }).pipe(map((r) => r.items.map((c): SdOption => ({ value: c.id, label: c.name, description: c.serviceRegion }))));

  constructor() {
    this.meta.load();
    this.org.skills().subscribe({
      next: (s) => {
        this.allSkills.set(s);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.formError.set(saveErrorMessage(err));
        this.loading.set(false);
      },
    });
  }

  /** The current reference as a known option so the async combobox shows its name instead of the id. */
  protected knownOption(id: string | null): SdOption[] {
    if (!id) return NO_OPTIONS;
    let cached = this.optionCache.get(id);
    if (!cached) {
      const label = this.labels.get(id);
      cached = label ? [{ value: id, label }] : NO_OPTIONS;
      this.optionCache.set(id, cached);
    }
    return cached;
  }

  private readonly optionCache = new Map<string, SdOption[]>();

  protected addSkill(): void {
    this.skills.update((s) => [...s, { skillId: null, level: 3 }]);
  }

  protected removeSkill(i: number): void {
    this.skills.update((s) => s.filter((_, x) => x !== i));
  }

  protected updateSkill(i: number, patch: Partial<SkillRow>): void {
    this.skills.update((s) => s.map((row, x) => (x === i ? { ...row, ...patch } : row)));
  }

  protected addAttribute(): void {
    this.attributes.update((a) => [...a, { kind: 'Territory', value: '', refId: null }]);
  }

  protected removeAttribute(i: number): void {
    this.attributes.update((a) => a.filter((_, x) => x !== i));
  }

  protected updateAttribute(i: number, patch: Partial<AttributeRow>): void {
    this.attributes.update((a) => a.map((row, x) => (x === i ? { ...row, ...patch } : row)));
  }

  protected save(): void {
    this.saving.set(true);
    this.formError.set(null);
    this.api
      .updateRoutingProfile(this.a.userId, {
        maxOpenTickets: Number(this.maxOpen),
        maxConcurrentChats: Number(this.maxChats),
        languages: this.languages(),
        skills: this.skills()
          .filter((s) => s.skillId)
          .map((s) => ({ skillId: s.skillId as string, level: s.level })),
        attributes: this.attributes().map((a) => ({ kind: a.kind, value: a.value.trim() || null, refId: a.refId })),
        rowVersion: this.a.rowVersion,
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

/** Deactivate: sessions end at once; open tickets go to another agent, back to the queue, or through routing. */
@Component({
  selector: 'desk-deactivate-agent-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdRadioGroup, SdRadio],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="'Deactivate ' + a.name" description="The agent is signed out everywhere and can no longer sign in. The seat is freed.">
      <form id="deactivate-form" class="form" (ngSubmit)="confirm()">
        <p>
          <strong>{{ a.openTickets }}</strong> open tickets are owned by {{ a.name }}.
        </p>
        <sd-form-field label="Their open tickets">
          <sd-radio-group [(value)]="mode" ariaLabel="What happens to open tickets">
            <sd-radio value="route" description="Automatic routing picks a new owner (or leaves them in the team queue).">Route again</sd-radio>
            <sd-radio value="reassign" description="Only where the agent belongs to the ticket's department; others go back to the queue.">Give to an agent</sd-radio>
            <sd-radio value="unassign" description="Supervisors assign them later.">Return to the team queue</sd-radio>
          </sd-radio-group>
        </sd-form-field>
        @if (mode() === 'reassign') {
          <sd-form-field label="New owner" required [error]="errors()['reassignToUserId']">
            <select sdInput name="target" [(ngModel)]="target" required>
              <option [ngValue]="null" disabled>Choose an agent…</option>
              @for (u of candidates(); track u.userId) {
                <option [ngValue]="u.userId">{{ u.name }}</option>
              }
            </select>
          </sd-form-field>
        }
        <sd-form-field label="Reason" optional>
          <input sdInput name="reason" [(ngModel)]="reason" maxlength="500" />
        </sd-form-field>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="danger" type="submit" form="deactivate-form" [loading]="saving()" [disabled]="mode() === 'reassign' && !target">Deactivate</button>
      </div>
    </sd-dialog>
  `,
  styles: FORM_STYLES,
})
export class DeactivateAgentDialog {
  protected readonly a = inject<AgentDetailDto>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<AgentDetailDto>>(DialogRef);
  private readonly api = inject(AgentsAdminApi);
  private readonly toast = inject(ToastService);
  protected readonly mode = signal<string | null>('route');
  protected target: Guid | null = null;
  protected reason = '';
  protected readonly candidates = signal<AgentListItemDto[]>([]);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  constructor() {
    this.api.list({ status: 'Active', light: false, limit: 200 }).subscribe({
      next: (r) => this.candidates.set(r.items.filter((x) => x.userId !== this.a.userId)),
    });
  }

  protected confirm(): void {
    this.saving.set(true);
    this.formError.set(null);
    const mode = (this.mode() ?? 'route') as 'route' | 'reassign' | 'unassign';
    this.api.deactivate(this.a.userId, { mode, reassignToUserId: mode === 'reassign' ? this.target : null, reason: this.reason.trim() || null }).subscribe({
      next: (r) => {
        this.toast.success(`${this.a.name} deactivated`, r.openTickets > 0 ? `${r.openTickets} open tickets are being reassigned.` : undefined);
        this.ref.close(r.agent);
      },
      error: (err: unknown) => {
        this.saving.set(false);
        this.errors.set(validationMessages(err));
        this.formError.set(saveErrorMessage(err));
      },
    });
  }
}
