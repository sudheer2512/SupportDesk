import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AdminRoleListItemDto, AdminRolesApi, ApiError } from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdDialogService,
  SdDrawer,
  SdDropdownMenu,
  SdEmptyState,
  SdFormField,
  SdIcon,
  SdInput,
  SdMenuItem,
  SdPageHeader,
  SdSkeleton,
  SdTooltip,
  ToastService,
} from '@sd/ui';
import { ADMIN_EDITOR_STYLES } from '../admin-shared';

const SCOPE_LABEL: Record<string, string> = { Own: 'Own tickets', Team: 'Team', Departments: 'Departments', All: 'All tickets' };

/** Roles & permissions (admin.roles): system and custom roles with member counts; create, clone and delete custom roles. */
@Component({
  selector: 'desk-admin-roles-list',
  imports: [FormsModule, RouterLink, SdPageHeader, SdButton, SdBadge, SdIcon, SdDrawer, SdFormField, SdInput, SdDropdownMenu, SdEmptyState, SdSkeleton, SdTooltip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Roles & permissions" icon="shield-check" [breadcrumbs]="crumbs" subtitle="What each role may do and which tickets and customers its members see.">
        <a sdButton sdPageActions variant="primary" icon="plus" routerLink="new" [disabled]="!customRolesAllowed()"
          [sdTooltip]="customRolesAllowed() ? null : 'Custom roles are not included in your plan.'">New role</a>
      </sd-page-header>
      <div class="desk-page__body">
        <div class="toolbar">
          <input sdInput class="search" type="search" placeholder="Filter roles" aria-label="Filter roles" [ngModel]="query()" (ngModelChange)="query.set($event)" />
          <span class="sd-spacer"></span>
          <span class="sd-muted small">{{ filtered().length }} of {{ roles().length }} roles</span>
        </div>
        @if (loading()) {
          <sd-skeleton [lines]="6" height="52px" />
        } @else if (error()) {
          <sd-empty-state icon="circle-alert" title="Roles could not be loaded" [message]="error()">
            <button sdButton icon="refresh" (click)="load()">Try again</button>
          </sd-empty-state>
        } @else if (filtered().length === 0) {
          <sd-empty-state icon="shield-check" title="No matching roles" message="Change the filter or create a custom role." />
        } @else {
          <ul class="rows">
            @for (r of filtered(); track r.id) {
              <li>
                <span class="icon" [class.system]="r.isSystem"><sd-icon [name]="r.isSystem ? 'lock' : 'shield-check'" [size]="16" /></span>
                <div class="grow">
                  <div class="title">
                    <a [routerLink]="r.id" class="name">{{ r.name }}</a>
                    <sd-badge size="sm" [tone]="r.isSystem ? 'neutral' : 'primary'" variant="outline">{{ r.isSystem ? 'System' : 'Custom' }}</sd-badge>
                    @if (r.isAdministrative) {
                      <sd-badge size="sm" tone="warning" icon="key">Administrator</sd-badge>
                    }
                    @if (r.isLightAgent) {
                      <sd-badge size="sm" tone="info">Light agent</sd-badge>
                    }
                  </div>
                  @if (r.description) {
                    <span class="sd-muted small sd-truncate">{{ r.description }}</span>
                  }
                </div>
                <div class="meta small">
                  <span><sd-icon name="eye" [size]="13" /> {{ scopeLabel(r.ticketScope) }}</span>
                  <span>{{ r.permissionCount }} permissions</span>
                  <span><sd-icon name="users" [size]="13" /> {{ r.userCount }}</span>
                </div>
                <sd-dropdown-menu iconOnly icon="ellipsis" variant="ghost" size="sm" align="end" [ariaLabel]="'Actions for ' + r.name" [items]="menuFor(r)" (select)="onMenu(r, $event)" />
              </li>
            }
          </ul>
        }
      </div>
    </div>

    <sd-drawer [(open)]="cloneOpen" [title]="'Clone ' + (target()?.name ?? 'role')" width="440px">
      <form class="form" id="clone-form" (ngSubmit)="clone()">
        <p class="sd-muted small">The copy starts with the same permissions, data scope and light-agent setting. You can then change it.</p>
        <sd-form-field label="Name of the new role" required [error]="cloneError()">
          <input sdInput name="cloneName" [ngModel]="cloneName()" (ngModelChange)="cloneName.set($event)" maxlength="100" />
        </sd-form-field>
      </form>
      <div sdDrawerFooter class="footer">
        <button sdButton type="button" variant="ghost" (click)="cloneOpen.set(false)">Cancel</button>
        <button sdButton type="submit" form="clone-form" variant="primary" [loading]="busy()">Clone role</button>
      </div>
    </sd-drawer>

    <sd-drawer [(open)]="deleteOpen" [title]="'Delete ' + (target()?.name ?? 'role')" width="440px">
      <form class="form" id="delete-form" (ngSubmit)="remove()">
        <p>{{ target()?.userCount }} user(s) hold this role. Choose the role they move to.</p>
        <sd-form-field label="Move members to" required [error]="deleteError()">
          <select sdInput name="reassign" [ngModel]="reassignTo()" (ngModelChange)="reassignTo.set($event)">
            <option value="">Choose a role…</option>
            @for (r of reassignOptions(); track r.id) {
              <option [value]="r.id">{{ r.name }}</option>
            }
          </select>
        </sd-form-field>
      </form>
      <div sdDrawerFooter class="footer">
        <button sdButton type="button" variant="ghost" (click)="deleteOpen.set(false)">Cancel</button>
        <button sdButton type="submit" form="delete-form" variant="danger" [loading]="busy()" [disabled]="!reassignTo()">Delete role</button>
      </div>
    </sd-drawer>
  `,
  styles: `
    ${ADMIN_EDITOR_STYLES}
    .search { max-width: 320px; }
    .icon { display: grid; place-items: center; width: 32px; height: 32px; flex: none; border-radius: var(--sd-radius-sm); background: var(--sd-primary-soft); color: var(--sd-primary); }
    .icon.system { background: var(--sd-surface-sunken); color: var(--sd-text-2); }
    .title { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
    .name { font-weight: var(--sd-weight-semibold); color: var(--sd-text); text-decoration: none; }
    .name:hover, .name:focus-visible { color: var(--sd-primary); text-decoration: underline; }
    .meta { display: flex; gap: var(--sd-space-4); color: var(--sd-text-2); white-space: nowrap; }
    .meta span { display: inline-flex; align-items: center; gap: 4px; }
    @media (max-width: 760px) { .meta { width: 100%; padding-left: 44px; flex-wrap: wrap; gap: var(--sd-space-2) var(--sd-space-3); } }
  `,
})
export class RolesListPage implements OnInit {
  private readonly api = inject(AdminRolesApi);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(SdDialogService);

  protected readonly crumbs = [{ label: 'Administration', link: '/admin' }, { label: 'Roles & permissions' }];
  protected readonly roles = signal<AdminRoleListItemDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly query = signal('');
  protected readonly customRolesAllowed = signal(true);

  protected readonly target = signal<AdminRoleListItemDto | null>(null);
  protected readonly cloneOpen = signal(false);
  protected readonly cloneName = signal('');
  protected readonly cloneError = signal<string | null>(null);
  protected readonly deleteOpen = signal(false);
  protected readonly reassignTo = signal('');
  protected readonly deleteError = signal<string | null>(null);
  protected readonly busy = signal(false);

  protected readonly filtered = computed(() => {
    const q = this.query().trim().toLowerCase();
    return q ? this.roles().filter((r) => `${r.name} ${r.description ?? ''}`.toLowerCase().includes(q)) : this.roles();
  });
  protected readonly reassignOptions = computed(() => this.roles().filter((r) => r.id !== this.target()?.id));

  ngOnInit(): void {
    this.load();
    this.api.catalog().subscribe({ next: (c) => this.customRolesAllowed.set(c.customRolesAllowed), error: () => undefined });
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.list().subscribe({
      next: (list) => {
        this.roles.set(list);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.error.set(err instanceof ApiError ? err.userMessage : 'Please try again.');
        this.loading.set(false);
      },
    });
  }

  protected scopeLabel(scope: string): string {
    return SCOPE_LABEL[scope] ?? scope;
  }

  protected menuFor(r: AdminRoleListItemDto): SdMenuItem[] {
    return [
      { id: 'open', label: r.isSystem ? 'View' : 'Edit', icon: r.isSystem ? 'eye' : 'pencil' },
      { id: 'clone', label: 'Clone', icon: 'copy', disabled: !this.customRolesAllowed() },
      ...(r.isSystem ? [] : [{ id: 'divider', divider: true }, { id: 'delete', label: 'Delete', icon: 'trash', danger: true }]),
    ];
  }

  protected async onMenu(r: AdminRoleListItemDto, item: SdMenuItem): Promise<void> {
    this.target.set(r);
    if (item.id === 'open') void this.router.navigate(['/admin/roles', r.id]);
    if (item.id === 'clone') {
      this.cloneName.set(`${r.name} (copy)`);
      this.cloneError.set(null);
      this.cloneOpen.set(true);
    }
    if (item.id === 'delete') {
      if (r.userCount > 0) {
        this.reassignTo.set('');
        this.deleteError.set(null);
        this.deleteOpen.set(true);
        return;
      }
      const ok = await this.dialogs.confirm({ title: `Delete “${r.name}”?`, message: 'Nobody holds this role. This cannot be undone.', danger: true, confirmLabel: 'Delete' });
      if (ok) this.doDelete(r, null);
    }
  }

  protected clone(): void {
    const r = this.target();
    if (!r) return;
    this.busy.set(true);
    this.api.clone(r.id, { name: this.cloneName().trim() }).subscribe({
      next: (created) => {
        this.busy.set(false);
        this.cloneOpen.set(false);
        this.toast.success('Role cloned', created.name);
        void this.router.navigate(['/admin/roles', created.id]);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        const e = ApiError.from(err);
        this.cloneError.set(e.fieldError('name') ?? e.userMessage);
      },
    });
  }

  protected remove(): void {
    const r = this.target();
    if (r && this.reassignTo()) this.doDelete(r, this.reassignTo());
  }

  private doDelete(r: AdminRoleListItemDto, reassignTo: string | null): void {
    this.busy.set(true);
    this.api.delete(r.id, reassignTo).subscribe({
      next: () => {
        this.busy.set(false);
        this.deleteOpen.set(false);
        this.toast.success('Role deleted', r.name);
        this.load();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        const message = ApiError.from(err).userMessage;
        if (this.deleteOpen()) this.deleteError.set(message);
        else this.toast.error('Could not delete the role', message);
      },
    });
  }
}
