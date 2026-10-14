import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { forkJoin, of } from 'rxjs';
import {
  AdminCustomerScope,
  AdminRoleDto,
  AdminRolesApi,
  AdminTicketScope,
  ApiError,
  MeApi,
  MeStore,
  MetaApi,
  PermissionCatalogDto,
  PermissionGroupDto,
  SaveRoleRequest,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCheckbox,
  SdCombobox,
  SdEmptyState,
  SdFormField,
  SdIcon,
  SdInput,
  SdOption,
  SdPageHeader,
  SdRadio,
  SdRadioGroup,
  SdSkeleton,
  SdToggle,
  SdTooltip,
  ToastService,
} from '@sd/ui';
import { ADMIN_EDITOR_STYLES, AdminLeaveGuarded, sameValue, saveErrors, warnOnUnload } from '../admin-shared';

interface RoleModel {
  name: string;
  description: string;
  ticketScope: AdminTicketScope;
  customerScope: AdminCustomerScope;
  isLightAgent: boolean;
  permissions: string[];
}

const SCOPE_ORDER: Record<string, number> = { Own: 1, Team: 2, Departments: 3, All: 4, AccountManaged: 1 };

/**
 * Role editor (admin.roles): name, data scope, light-agent flag and the permission matrix grouped by area, plus role
 * members. System roles are shown read-only (clone them to customise). Permissions and scopes the signed-in
 * administrator does not hold are disabled — the API refuses privilege escalation anyway.
 */
@Component({
  selector: 'desk-admin-role-editor',
  imports: [
    FormsModule,
    RouterLink,
    SdPageHeader,
    SdButton,
    SdBadge,
    SdIcon,
    SdFormField,
    SdInput,
    SdCheckbox,
    SdToggle,
    SdRadioGroup,
    SdRadio,
    SdCombobox,
    SdSkeleton,
    SdEmptyState,
    SdTooltip,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header [title]="title()" icon="shield-check" [breadcrumbs]="crumbs()" [subtitle]="subtitle()">
        @if (role()?.isSystem) {
          <button sdButton sdPageActions icon="copy" [disabled]="!catalog()?.customRolesAllowed" (click)="cloneSystem()">Clone to customise</button>
        }
      </sd-page-header>
      <div class="desk-page__body">
        @if (loading()) {
          <sd-skeleton [lines]="8" height="40px" />
        } @else if (loadError()) {
          <sd-empty-state icon="circle-alert" title="This role could not be loaded" [message]="loadError()">
            <a sdButton routerLink="/admin/roles" icon="arrow-left">Back to roles</a>
          </sd-empty-state>
        } @else {
          @if (role()?.isSystem) {
            <div class="callout" role="note"><sd-icon name="lock" [size]="16" /><div><strong>System role — read-only.</strong><p>Clone it to create a custom role you can change.</p></div></div>
          }
          @if (formError()) {
            <div class="callout danger" role="alert"><sd-icon name="circle-alert" [size]="16" /><div>{{ formError() }}</div></div>
          }

          <section class="card" aria-labelledby="role-general">
            <h2 id="role-general">General</h2>
            <p class="sd-muted">How the role appears in pickers and on agent profiles.</p>
            <div class="grid2">
              <sd-form-field label="Name" required [error]="errors()['name']">
                <input sdInput name="name" [ngModel]="model().name" (ngModelChange)="patch({ name: $event })" maxlength="100" [disabled]="readOnly()" />
              </sd-form-field>
              <sd-form-field label="Description" optional [error]="errors()['description']">
                <input sdInput name="description" [ngModel]="model().description" (ngModelChange)="patch({ description: $event })" maxlength="500" [disabled]="readOnly()" />
              </sd-form-field>
            </div>
          </section>

          <section class="card" aria-labelledby="role-scope">
            <h2 id="role-scope">Data scope</h2>
            <p class="sd-muted">The broadest scope across a user's roles applies. Out-of-scope tickets and customers are invisible to them.</p>
            <div class="grid2">
              <fieldset>
                <legend>Tickets</legend>
                <sd-radio-group [value]="model().ticketScope" (valueChange)="patch({ ticketScope: $any($event) })" ariaLabel="Ticket scope">
                  @for (o of catalog()?.ticketScopes ?? []; track o.value) {
                    <sd-radio [value]="o.value" [disabled]="readOnly() || !scopeAllowed(o.value, 'ticket')">{{ o.label }}</sd-radio>
                  }
                </sd-radio-group>
              </fieldset>
              <fieldset>
                <legend>Customers</legend>
                <sd-radio-group [value]="model().customerScope" (valueChange)="patch({ customerScope: $any($event) })" ariaLabel="Customer scope">
                  @for (o of catalog()?.customerScopes ?? []; track o.value) {
                    <sd-radio [value]="o.value" [disabled]="readOnly() || !scopeAllowed(o.value, 'customer')">{{ o.label }}</sd-radio>
                  }
                </sd-radio-group>
              </fieldset>
            </div>
            <div class="light">
              <sd-toggle [value]="model().isLightAgent" (valueChange)="patch({ isLightAgent: $event })" [disabled]="readOnly()" ariaLabel="Light agent role">Light agent role</sd-toggle>
              <span class="sd-muted small">Members can view tickets and add internal notes but never reply to customers, own, transfer, bulk-edit or delete tickets — whatever the permissions below say.</span>
            </div>
          </section>

          <section class="card" aria-labelledby="role-perms">
            <div class="perm-head">
              <div>
                <h2 id="role-perms">Permissions</h2>
                <p class="sd-muted">{{ model().permissions.length }} of {{ totalPermissions() }} selected. <sd-badge size="sm" tone="warning">Sensitive</sd-badge> marks high-impact permissions.</p>
              </div>
              <input sdInput class="perm-filter" type="search" placeholder="Filter permissions" aria-label="Filter permissions" [ngModel]="permFilter()" (ngModelChange)="permFilter.set($event)" />
            </div>
            @if (errors()['permissions']) {
              <p class="err" role="alert">{{ errors()['permissions'] }}</p>
            }
            <div class="groups">
              @for (g of visibleGroups(); track g.module) {
                <fieldset class="group">
                  <legend>
                    <sd-checkbox [value]="groupState(g) === 'all'" [indeterminate]="groupState(g) === 'some'" [disabled]="readOnly()"
                      (valueChange)="toggleGroup(g, $event)" [ariaLabel]="'All ' + g.module + ' permissions'">{{ g.module }}</sd-checkbox>
                    <span class="sd-muted small">{{ selectedIn(g) }}/{{ g.permissions.length }}</span>
                  </legend>
                  @for (p of g.permissions; track p.code) {
                    <div class="perm" [class.locked]="!grantable().has(p.code)">
                      <sd-checkbox [value]="selected().has(p.code)" [disabled]="readOnly() || !grantable().has(p.code)" (valueChange)="toggle(p.code, $event)">
                        <span class="pdesc">{{ p.description }}</span>
                      </sd-checkbox>
                      <span class="code mono">{{ p.code }}</span>
                      @if (p.sensitive) {
                        <sd-badge size="sm" tone="warning">Sensitive</sd-badge>
                      }
                      @if (!grantable().has(p.code)) {
                        <sd-icon name="lock" [size]="13" [sdTooltip]="'You do not hold this permission, so you cannot grant it.'" />
                      }
                    </div>
                  }
                </fieldset>
              } @empty {
                <p class="sd-muted">No permission matches “{{ permFilter() }}”.</p>
              }
            </div>
          </section>

          @if (role(); as r) {
            <section class="card" aria-labelledby="role-members">
              <h2 id="role-members">Members ({{ r.members.length }})</h2>
              <p class="sd-muted">Changes apply immediately to the members' open sessions.</p>
              <div class="add-member">
                <sd-combobox class="grow" [options]="memberOptions()" [(value)]="newMember" placeholder="Add a user to this role…" ariaLabel="User to add" />
                <button sdButton icon="user-plus" [disabled]="!newMember()" [loading]="memberBusy()" (click)="addMember()">Add</button>
              </div>
              @if (r.members.length === 0) {
                <p class="sd-muted small">Nobody holds this role yet.</p>
              } @else {
                <ul class="rows members">
                  @for (m of r.members; track m.userId) {
                    <li>
                      <div class="grow">
                        <strong>{{ m.name }}</strong>
                        @if (!m.isActive) {
                          <sd-badge size="sm">Inactive</sd-badge>
                        }
                        <span class="sd-muted small sd-truncate">{{ m.email }}{{ m.otherRoles.length ? ' · also ' + m.otherRoles.join(', ') : '' }}</span>
                      </div>
                      <button sdButton size="sm" variant="ghost" icon="x" [attr.aria-label]="'Remove ' + m.name + ' from ' + r.name" [disabled]="memberBusy()" (click)="removeMember(m.userId, m.name)">Remove</button>
                    </li>
                  }
                </ul>
              }
            </section>
          }

          @if (!readOnly()) {
            <div class="savebar">
              @if (dirty()) {
                <span class="sd-muted small"><sd-icon name="circle-dot" [size]="12" /> Unsaved changes</span>
              } @else {
                <span class="sd-muted small">All changes saved</span>
              }
              <span class="sd-spacer"></span>
              <button sdButton variant="ghost" [disabled]="!dirty() || saving()" (click)="discard()">Discard</button>
              <button sdButton variant="primary" icon="check" [loading]="saving()" [disabled]="!dirty() && !!role()" (click)="save()">{{ role() ? 'Save role' : 'Create role' }}</button>
            </div>
          }
        }
      </div>
    </div>
  `,
  styles: `
    ${ADMIN_EDITOR_STYLES}
    .light { display: flex; flex-direction: column; gap: 4px; margin-top: var(--sd-space-4); }
    .perm-head { display: flex; flex-wrap: wrap; align-items: flex-start; gap: var(--sd-space-3); justify-content: space-between; }
    .perm-head p { margin: 0 0 var(--sd-space-3); font-size: var(--sd-text-sm); }
    .perm-filter { max-width: 260px; }
    .groups { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(100%, 360px), 1fr)); gap: var(--sd-space-3); }
    .group legend { display: flex; align-items: center; gap: 8px; }
    .perm { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; padding: 5px 0; border-bottom: 1px dashed var(--sd-divider); }
    .perm:last-child { border-bottom: 0; }
    .perm.locked { opacity: 0.75; }
    .pdesc { font-size: var(--sd-text-sm); }
    .code { color: var(--sd-text-3); margin-left: 26px; flex-basis: 100%; }
    .add-member { display: flex; gap: var(--sd-space-2); margin-bottom: var(--sd-space-3); max-width: 560px; }
    .members strong { margin-right: 6px; }
    .members .grow { display: flex; flex-direction: column; }
  `,
})
export class RoleEditorPage implements OnInit, AdminLeaveGuarded {
  private readonly api = inject(AdminRolesApi);
  private readonly meta = inject(MetaApi);
  private readonly me = inject(MeStore);
  private readonly meApi = inject(MeApi);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  /** Route param (":id"); absent for /admin/roles/new. */
  readonly id = input<string | undefined>();

  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);
  protected readonly catalog = signal<PermissionCatalogDto | null>(null);
  protected readonly role = signal<AdminRoleDto | null>(null);
  protected readonly model = signal<RoleModel>({ name: '', description: '', ticketScope: 'Departments', customerScope: 'All', isLightAgent: false, permissions: [] });
  private readonly original = signal<RoleModel | null>(null);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly permFilter = signal('');
  protected readonly newMember = signal<string | string[] | null>(null);
  protected readonly memberBusy = signal(false);

  protected readonly readOnly = computed(() => !!this.role()?.isSystem);
  protected readonly selected = computed(() => new Set(this.model().permissions));
  protected readonly grantable = computed(() => new Set(this.catalog()?.grantable ?? []));
  protected readonly totalPermissions = computed(() => (this.catalog()?.groups ?? []).reduce((n, g) => n + g.permissions.length, 0));
  protected readonly dirty = computed(() => !this.readOnly() && !sameValue(this.normalized(this.model()), this.normalized(this.original())));
  protected readonly title = computed(() => (this.role() ? this.role()!.name : this.loading() ? 'Role' : 'New role'));
  protected readonly subtitle = computed(() =>
    this.role()?.isSystem ? 'Built-in role' : this.role() ? 'Custom role' : 'Create a role with exactly the permissions your team needs.',
  );
  protected readonly crumbs = computed(() => [
    { label: 'Administration', link: '/admin' },
    { label: 'Roles & permissions', link: '/admin/roles' },
    { label: this.title() },
  ]);
  protected readonly visibleGroups = computed<PermissionGroupDto[]>(() => {
    const q = this.permFilter().trim().toLowerCase();
    const groups = this.catalog()?.groups ?? [];
    if (!q) return groups;
    return groups
      .map((g) => ({ ...g, permissions: g.permissions.filter((p) => `${g.module} ${p.code} ${p.description}`.toLowerCase().includes(q)) }))
      .filter((g) => g.permissions.length > 0);
  });
  protected readonly memberOptions = computed<SdOption[]>(() => {
    const members = new Set((this.role()?.members ?? []).map((m) => m.userId));
    return (this.meta.ticketForm()?.agents ?? []).filter((a) => !members.has(a.userId)).map((a) => ({ value: a.userId, label: a.name, description: a.email, avatar: a.name }));
  });

  constructor() {
    warnOnUnload(this.dirty);
  }

  ngOnInit(): void {
    this.meta.load();
    const roleId = this.id();
    forkJoin({ catalog: this.api.catalog(), role: roleId && roleId !== 'new' ? this.api.get(roleId) : of(null) }).subscribe({
      next: ({ catalog, role }) => {
        this.catalog.set(catalog);
        if (role) this.applyRole(role);
        else {
          const start: RoleModel = {
            name: '',
            description: '',
            ticketScope: catalog.grantableTicketScope === 'All' ? 'Departments' : catalog.grantableTicketScope,
            customerScope: catalog.grantableCustomerScope,
            isLightAgent: false,
            permissions: ['tickets.view'].filter((p) => catalog.grantable.includes(p)),
          };
          this.model.set(start);
          this.original.set({ ...start, permissions: [...start.permissions] });
        }
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loadError.set(err instanceof ApiError ? err.userMessage : 'Please try again.');
        this.loading.set(false);
      },
    });
  }

  hasUnsavedChanges(): boolean {
    return this.dirty() && !this.saving();
  }

  protected patch(change: Partial<RoleModel>): void {
    this.model.update((m) => ({ ...m, ...change }));
  }

  protected scopeAllowed(value: string, kind: 'ticket' | 'customer'): boolean {
    const c = this.catalog();
    if (!c) return false;
    const limit = kind === 'ticket' ? c.grantableTicketScope : c.grantableCustomerScope;
    return (SCOPE_ORDER[value] ?? 99) <= (SCOPE_ORDER[limit] ?? 0) || (kind === 'customer' && limit === 'All');
  }

  protected toggle(code: string, on: boolean): void {
    const set = new Set(this.model().permissions);
    if (on) set.add(code);
    else set.delete(code);
    this.patch({ permissions: [...set] });
  }

  protected selectedIn(g: PermissionGroupDto): number {
    const s = this.selected();
    return g.permissions.filter((p) => s.has(p.code)).length;
  }

  protected groupState(g: PermissionGroupDto): 'none' | 'some' | 'all' {
    const n = this.selectedIn(g);
    return n === 0 ? 'none' : n === g.permissions.length ? 'all' : 'some';
  }

  /** Selects or clears every permission of the group the caller may grant. */
  protected toggleGroup(g: PermissionGroupDto, on: boolean): void {
    const set = new Set(this.model().permissions);
    for (const p of g.permissions) {
      if (!this.grantable().has(p.code)) continue;
      if (on) set.add(p.code);
      else set.delete(p.code);
    }
    this.patch({ permissions: [...set] });
  }

  protected discard(): void {
    const o = this.original();
    if (o) this.model.set({ ...o, permissions: [...o.permissions] });
    this.errors.set({});
    this.formError.set(null);
  }

  protected save(): void {
    const m = this.model();
    const request: SaveRoleRequest = {
      name: m.name.trim(),
      description: m.description.trim() || null,
      ticketScope: m.ticketScope,
      customerScope: m.customerScope,
      isLightAgent: m.isLightAgent,
      permissions: m.permissions,
      rowVersion: this.role()?.rowVersion ?? null,
    };
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    const existing = this.role();
    (existing ? this.api.update(existing.id, request) : this.api.create(request)).subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.applyRole(saved);
        this.toast.success(existing ? 'Role saved' : 'Role created', saved.name);
        if (saved.members.some((x) => x.userId === this.me.user()?.id)) this.meApi.get().subscribe((u) => this.me.set(u));
        if (!existing) void this.router.navigate(['/admin/roles', saved.id], { replaceUrl: true });
      },
      error: (err: unknown) => {
        this.saving.set(false);
        const e = ApiError.from(err);
        if (e.is('concurrency_conflict')) {
          this.formError.set('Someone else changed this role. Reload the page to see their version, then reapply your changes.');
          return;
        }
        const { fields, message } = saveErrors(err, 'The role could not be saved.');
        this.errors.set(fields);
        this.formError.set(message);
      },
    });
  }

  protected cloneSystem(): void {
    const r = this.role();
    if (!r) return;
    this.api.clone(r.id, { name: `${r.name} (custom)` }).subscribe({
      next: (created) => {
        this.toast.success('Role cloned', created.name);
        void this.router.navigate(['/admin/roles', created.id]);
      },
      error: (err: unknown) => this.toast.error('Could not clone the role', ApiError.from(err).userMessage),
    });
  }

  protected addMember(): void {
    const r = this.role();
    const userId = this.newMember();
    if (!r || typeof userId !== 'string') return;
    this.memberBusy.set(true);
    this.api.addMember(r.id, userId).subscribe({
      next: (updated) => {
        this.memberBusy.set(false);
        this.newMember.set(null);
        this.role.set(updated);
        this.toast.success('Member added');
      },
      error: (err: unknown) => {
        this.memberBusy.set(false);
        this.toast.error('Could not add the member', ApiError.from(err).userMessage);
      },
    });
  }

  protected removeMember(userId: string, name: string): void {
    const r = this.role();
    if (!r) return;
    this.memberBusy.set(true);
    this.api.removeMember(r.id, userId).subscribe({
      next: (updated) => {
        this.memberBusy.set(false);
        this.role.set(updated);
        this.toast.success('Member removed', name);
      },
      error: (err: unknown) => {
        this.memberBusy.set(false);
        this.toast.error('Could not remove the member', ApiError.from(err).userMessage);
      },
    });
  }

  private applyRole(role: AdminRoleDto): void {
    this.role.set(role);
    const m: RoleModel = {
      name: role.name,
      description: role.description ?? '',
      ticketScope: role.ticketScope,
      customerScope: role.customerScope,
      isLightAgent: role.isLightAgent,
      permissions: [...role.permissions],
    };
    this.model.set(m);
    this.original.set({ ...m, permissions: [...m.permissions] });
  }

  private normalized(m: RoleModel | null): unknown {
    return m ? { ...m, name: m.name.trim(), description: m.description.trim(), permissions: [...m.permissions].sort() } : null;
  }
}
