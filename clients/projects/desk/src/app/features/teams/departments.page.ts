import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import {
  AgentsAdminApi,
  AgentListItemDto,
  BusinessCalendarListItemDto,
  DepartmentDto,
  Guid,
  MeStore,
  OrganisationApi,
  SlaAdminApi,
  SlaPolicyListItemDto,
  TeamDto,
  saveErrorMessage,
  validationMessages,
} from '@sd/core';
import { SdBadge, SdButton, SdCard, SdCheckbox, SdDialog, SdDialogService, SdEmptyState, SdFormField, SdInput, SdPageHeader, SdSkeleton, ToastService } from '@sd/ui';
import { forkJoin, of, catchError } from 'rxjs';
import { ModuleTabsComponent } from '../customers/shared/module-tabs.component';
import { SECTION_STYLES } from '../customers/shared/styles';
import { FORM_STYLES, PAGE_STYLES, TEAMS_TABS, errorText } from './shared/org-ui';

/** /teams/departments — departments with routing team, calendar, SLA default, portal visibility; create, edit, archive, restore. */
@Component({
  selector: 'desk-departments-page',
  imports: [FormsModule, RouterLink, SdPageHeader, SdButton, SdBadge, SdCard, SdSkeleton, SdEmptyState, SdCheckbox, ModuleTabsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Teams & Agents" subtitle="Departments, teams, agents, skills, shifts and live supervision." icon="users">
        @if (canManage) {
          <button sdButton sdPageActions variant="primary" icon="plus" (click)="edit(null)">New department</button>
        }
      </sd-page-header>
      <desk-module-tabs [tabs]="tabs" ariaLabel="Teams & Agents sections" />
      <div class="desk-page__body">
        <div class="toolbar">
          <sd-checkbox [value]="showArchived()" (valueChange)="showArchived.set($event); load()">Show archived</sd-checkbox>
          <span class="sd-spacer"></span>
          <button sdButton size="sm" variant="ghost" icon="refresh" (click)="load()" [loading]="loading()">Refresh</button>
        </div>
        @if (error()) {
          <div class="error-box" role="alert">
            <span>{{ error() }}</span>
            <button sdButton size="sm" (click)="load()">Try again</button>
          </div>
        } @else if (loading() && departments().length === 0) {
          <div class="grid" aria-busy="true">
            @for (i of [1, 2, 3]; track i) {
              <sd-card><sd-skeleton [lines]="5" /></sd-card>
            }
          </div>
        } @else if (departments().length === 0) {
          <sd-empty-state icon="building" title="No departments" message="Create a department to route and own tickets." />
        } @else {
          <div class="grid">
            @for (d of departments(); track d.id) {
              <sd-card [title]="d.name" [subtitle]="d.description">
                <div sdCardActions>
                  @if (canManage) {
                    @if (d.isActive) {
                      <button sdButton size="sm" variant="ghost" icon="pencil" iconOnly [attr.aria-label]="'Edit ' + d.name" (click)="edit(d)"></button>
                      <button sdButton size="sm" variant="ghost" icon="archive" iconOnly [attr.aria-label]="'Archive ' + d.name" (click)="archive(d)"></button>
                    } @else {
                      <button sdButton size="sm" icon="history" (click)="restore(d)">Restore</button>
                    }
                  }
                </div>
                <div class="pills badges">
                  <sd-badge size="sm" class="sd-mono">{{ d.code }}</sd-badge>
                  @if (!d.isActive) {
                    <sd-badge size="sm" tone="neutral" icon="archive">Archived</sd-badge>
                  }
                  @if (d.visibleInPortal) {
                    <sd-badge size="sm" tone="info" icon="globe">In portal</sd-badge>
                  }
                  @if (d.transferRequiresApproval) {
                    <sd-badge size="sm" tone="warning" icon="shield-check">Transfers need approval</sd-badge>
                  }
                </div>
                <dl class="meta">
                  <dt>E-mail alias</dt>
                  <dd>{{ d.email ?? '—' }}</dd>
                  <dt>Routing team</dt>
                  <dd>{{ d.defaultTeamName ?? '—' }}</dd>
                  <dt>Business hours</dt>
                  <dd>{{ d.businessCalendarName ?? '24x7 (no calendar)' }}</dd>
                  <dt>Default SLA</dt>
                  <dd>{{ d.defaultSlaPolicyName ?? 'Policy rules decide' }}</dd>
                  <dt>Head</dt>
                  <dd>{{ d.headUserName ?? '—' }}</dd>
                </dl>
                <div class="stats">
                  <a [routerLink]="['/teams/teams']" [queryParams]="{ departmentId: d.id }"><strong>{{ d.teamCount }}</strong> teams</a>
                  <a [routerLink]="['/teams/agents']" [queryParams]="{ departmentId: d.id }"><strong>{{ d.agentCount }}</strong> agents</a>
                  <span><strong>{{ d.openTickets }}</strong> open tickets</span>
                </div>
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
      .badges { margin-bottom: var(--sd-space-3); }
      .stats { display: flex; flex-wrap: wrap; gap: var(--sd-space-4); margin-top: var(--sd-space-3); font-size: var(--sd-text-sm); color: var(--sd-text-2); }
      .stats a { color: var(--sd-primary); text-decoration: none; }
      .stats a:hover { text-decoration: underline; }
    `,
  ],
})
export class DepartmentsPage {
  private readonly api = inject(OrganisationApi);
  private readonly dialogs = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  protected readonly canManage = inject(MeStore).can('admin.organisation');
  protected readonly tabs = TEAMS_TABS;
  protected readonly departments = signal<DepartmentDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly showArchived = signal(false);

  constructor() {
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.departments(this.showArchived()).subscribe({
      next: (list) => {
        this.departments.set(list);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.error.set(errorText(err));
        this.loading.set(false);
      },
    });
  }

  protected edit(department: DepartmentDto | null): void {
    this.dialogs
      .open<DepartmentDto, DepartmentFormData>(DepartmentFormDialog, { data: { department }, size: 'lg' })
      .closed.subscribe((saved) => {
        if (!saved) return;
        this.toast.success(department ? 'Department updated' : 'Department created', saved.name);
        this.load();
      });
  }

  protected archive(department: DepartmentDto): void {
    this.dialogs
      .open<DepartmentDto, ArchiveData>(ArchiveDepartmentDialog, { data: { department, others: this.departments().filter((d) => d.isActive && d.id !== department.id) } })
      .closed.subscribe((done) => {
        if (done) this.load();
      });
  }

  protected restore(department: DepartmentDto): void {
    this.api.restoreDepartment(department.id).subscribe(() => {
      this.toast.success('Department restored', department.name);
      this.load();
    });
  }
}

interface DepartmentFormData {
  department: DepartmentDto | null;
}

/** Create / edit a department. */
@Component({
  selector: 'desk-department-form-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton, SdCheckbox, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="d ? 'Edit ' + d.name : 'New department'" description="Tickets belong to exactly one department; its routing team, calendar and SLA apply.">
      @if (loading()) {
        <sd-skeleton [lines]="6" />
      } @else {
        <form id="dept-form" class="form" (ngSubmit)="save()" novalidate>
          <div class="two">
            <sd-form-field label="Name" required [error]="errors()['name']">
              <input sdInput name="name" [(ngModel)]="name" required maxlength="120" />
            </sd-form-field>
            <sd-form-field label="Code" optional hint="2–12 letters or digits; generated from the name when empty." [error]="errors()['code']">
              <input sdInput name="code" [(ngModel)]="code" maxlength="12" />
            </sd-form-field>
          </div>
          <sd-form-field label="Description" optional>
            <textarea sdInput name="description" rows="2" [(ngModel)]="description" maxlength="2000"></textarea>
          </sd-form-field>
          <div class="two">
            <sd-form-field label="E-mail alias" optional hint="Mail to this address lands in the department." [error]="errors()['email']">
              <input sdInput name="email" type="email" [(ngModel)]="email" maxlength="254" />
            </sd-form-field>
            <sd-form-field label="Department head" optional [error]="errors()['headUserId']">
              <select sdInput name="head" [(ngModel)]="headUserId">
                <option [ngValue]="null">—</option>
                @for (a of agents(); track a.userId) {
                  <option [ngValue]="a.userId">{{ a.name }}</option>
                }
              </select>
            </sd-form-field>
          </div>
          <div class="three">
            <sd-form-field label="Routing team" [hint]="d ? 'New tickets are routed through this team.' : 'A default team is created with the department.'" [error]="errors()['defaultTeamId']">
              <select sdInput name="team" [(ngModel)]="defaultTeamId" [disabled]="!d">
                <option [ngValue]="null">{{ d ? '—' : 'Created automatically' }}</option>
                @for (t of teams(); track t.id) {
                  <option [ngValue]="t.id">{{ t.name }}</option>
                }
              </select>
            </sd-form-field>
            <sd-form-field label="Business calendar" optional [error]="errors()['businessCalendarId']">
              <select sdInput name="calendar" [(ngModel)]="calendarId">
                <option [ngValue]="null">24x7</option>
                @for (c of calendars(); track c.id) {
                  <option [ngValue]="c.id">{{ c.name }}</option>
                }
              </select>
            </sd-form-field>
            <sd-form-field label="Default SLA policy" optional [error]="errors()['defaultSlaPolicyId']">
              <select sdInput name="sla" [(ngModel)]="policyId">
                <option [ngValue]="null">Policy rules decide</option>
                @for (p of policies(); track p.id) {
                  <option [ngValue]="p.id">{{ p.name }}</option>
                }
              </select>
            </sd-form-field>
          </div>
          <div class="checks">
            <sd-checkbox [(value)]="visibleInPortal">Visible in the customer portal</sd-checkbox>
            <sd-checkbox [(value)]="transferRequiresApproval">Transfers out need supervisor approval</sd-checkbox>
          </div>
          <sd-form-field label="Sort order" optional>
            <input sdInput name="sort" type="number" min="0" max="1000" [(ngModel)]="sortOrder" />
          </sd-form-field>
          @if (formError()) {
            <p class="error" role="alert">{{ formError() }}</p>
          }
        </form>
      }
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="dept-form" [loading]="saving()" [disabled]="loading()">Save</button>
      </div>
    </sd-dialog>
  `,
  styles: FORM_STYLES,
})
export class DepartmentFormDialog {
  private readonly data = inject<DepartmentFormData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<DepartmentDto>>(DialogRef);
  private readonly api = inject(OrganisationApi);
  private readonly sla = inject(SlaAdminApi);
  private readonly agentsApi = inject(AgentsAdminApi);
  protected readonly d = this.data.department;
  protected name = this.d?.name ?? '';
  protected code = this.d?.code ?? '';
  protected description = this.d?.description ?? '';
  protected email = this.d?.email ?? '';
  protected headUserId: Guid | null = this.d?.headUserId ?? null;
  protected defaultTeamId: Guid | null = this.d?.defaultTeamId ?? null;
  protected calendarId: Guid | null = this.d?.businessCalendarId ?? null;
  protected policyId: Guid | null = this.d?.defaultSlaPolicyId ?? null;
  protected sortOrder = this.d?.sortOrder ?? 0;
  protected readonly visibleInPortal = signal(this.d?.visibleInPortal ?? true);
  protected readonly transferRequiresApproval = signal(this.d?.transferRequiresApproval ?? false);
  protected readonly teams = signal<TeamDto[]>([]);
  protected readonly calendars = signal<BusinessCalendarListItemDto[]>([]);
  protected readonly policies = signal<SlaPolicyListItemDto[]>([]);
  protected readonly agents = signal<AgentListItemDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  constructor() {
    forkJoin({
      teams: this.d ? this.api.teams(this.d.id) : of([] as TeamDto[]),
      calendars: this.sla.calendars().pipe(catchError(() => of([] as BusinessCalendarListItemDto[]))),
      policies: this.sla.policies().pipe(catchError(() => of([] as SlaPolicyListItemDto[]))),
      agents: this.agentsApi.list({ status: 'Active', limit: 200 }),
    }).subscribe({
      next: (r) => {
        this.teams.set(r.teams);
        this.calendars.set(r.calendars);
        this.policies.set(r.policies.filter((p) => p.isActive));
        this.agents.set(r.agents.items);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.formError.set(errorText(err));
        this.loading.set(false);
      },
    });
  }

  protected save(): void {
    this.saving.set(true);
    this.formError.set(null);
    this.api
      .saveDepartment(this.d?.id ?? null, {
        name: this.name.trim(),
        code: this.code.trim() || null,
        description: this.description.trim() || null,
        email: this.email.trim() || null,
        headUserId: this.headUserId,
        defaultTeamId: this.defaultTeamId,
        businessCalendarId: this.calendarId,
        defaultSlaPolicyId: this.policyId,
        visibleInPortal: this.visibleInPortal(),
        transferRequiresApproval: this.transferRequiresApproval(),
        sortOrder: Number(this.sortOrder) || 0,
        parentDepartmentId: this.d?.parentDepartmentId ?? null,
        rowVersion: this.d?.rowVersion ?? null,
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

interface ArchiveData {
  department: DepartmentDto;
  others: DepartmentDto[];
}

/** Archive a department; open tickets must move to another department (done by a background job). */
@Component({
  selector: 'desk-archive-department-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="'Archive ' + data.department.name" description="Archived departments receive no new tickets and disappear from the portal.">
      <form id="archive-form" class="form" (ngSubmit)="confirm()">
        @if (data.department.openTickets > 0) {
          <p>
            <strong>{{ data.department.openTickets }}</strong> open tickets will be moved to the department you choose (their owners are kept when they
            belong to it; otherwise the tickets are routed again).
          </p>
          <sd-form-field label="Move open tickets to" required>
            <select sdInput name="target" [(ngModel)]="target" required>
              <option [ngValue]="null" disabled>Choose a department…</option>
              @for (o of data.others; track o.id) {
                <option [ngValue]="o.id">{{ o.name }}</option>
              }
            </select>
          </sd-form-field>
        } @else {
          <p>The department has no open tickets.</p>
        }
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="danger" type="submit" form="archive-form" [loading]="saving()" [disabled]="needsTarget() && !target">Archive</button>
      </div>
    </sd-dialog>
  `,
  styles: FORM_STYLES,
})
export class ArchiveDepartmentDialog {
  protected readonly data = inject<ArchiveData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<DepartmentDto>>(DialogRef);
  private readonly api = inject(OrganisationApi);
  private readonly toast = inject(ToastService);
  protected target: Guid | null = null;
  protected readonly saving = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly needsTarget = computed(() => this.data.department.openTickets > 0);

  protected confirm(): void {
    this.saving.set(true);
    this.api.archiveDepartment(this.data.department.id, { reassignToDepartmentId: this.target, rowVersion: this.data.department.rowVersion }).subscribe({
      next: (r) => {
        this.toast.success('Department archived', r.ticketsToMove > 0 ? `${r.ticketsToMove} open tickets are being moved.` : undefined);
        this.ref.close(r.department);
      },
      error: (err: unknown) => {
        this.saving.set(false);
        this.formError.set(saveErrorMessage(err));
      },
    });
  }
}
