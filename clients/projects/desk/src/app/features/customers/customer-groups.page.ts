import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ApiError, CustomerGroupDto, CustomersApi, MeStore, saveErrorMessage } from '@sd/core';
import { SdButton, SdDialogService, SdEmptyState, SdPageHeader, SdSkeleton, ToastService } from '@sd/ui';
import { CUSTOMER_TABS, ModuleTabsComponent } from './shared/module-tabs.component';
import { NameDescriptionData, NameDescriptionDialog } from './shared/name-description.dialog';
import { TABLE_STYLES } from './shared/styles';

/**
 * /customers/groups — customer groups (GET/POST/PUT/DELETE /customer-groups) with the number of (visible) member
 * customers. Anyone with customers.view sees them; creating, renaming and deleting needs customers.manage. Customers
 * join a group from their own form (Group field).
 */
@Component({
  selector: 'desk-customer-groups-page',
  imports: [SdPageHeader, SdButton, SdSkeleton, SdEmptyState, ModuleTabsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Customer groups" subtitle="Group organisations to filter the customer list and to limit knowledge-base articles to a group." icon="layers">
        @if (canManage) {
          <button sdButton sdPageActions variant="primary" icon="plus" (click)="edit(null)">New group</button>
        }
      </sd-page-header>
      <desk-module-tabs [tabs]="tabs" ariaLabel="Customers sections" />
      <div class="desk-page__body">
        @if (error()) {
          <sd-empty-state icon="triangle-alert" title="Customer groups could not be loaded" [message]="error()">
            <button sdButton icon="refresh" (click)="load()">Try again</button>
          </sd-empty-state>
        } @else if (loading()) {
          <sd-skeleton [lines]="5" />
        } @else if (groups().length === 0) {
          <sd-empty-state icon="layers" title="No customer groups yet" message="Groups bundle organisations — for example by sales channel or partner — for filters, reports and automation.">
            @if (canManage) {
              <button sdButton variant="primary" icon="plus" (click)="edit(null)">New group</button>
            }
          </sd-empty-state>
        } @else {
          <p class="sd-small sd-muted summary">{{ groups().length }} group{{ groups().length === 1 ? '' : 's' }} · {{ members() }} customers in groups</p>
          <div class="dt-wrap">
            <table class="dt">
              <caption class="sd-visually-hidden">Customer groups</caption>
              <thead>
                <tr>
                  <th scope="col">Group</th>
                  <th scope="col" class="hide-sm">Description</th>
                  <th scope="col" class="num">Customers</th>
                  @if (canManage) {
                    <th scope="col"><span class="sd-visually-hidden">Actions</span></th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (g of groups(); track g.id) {
                  <tr>
                    <td>
                      <strong>{{ g.name }}</strong>
                      @if (g.description) {
                        <span class="sd-xs sd-muted show-sm">{{ g.description }}</span>
                      }
                    </td>
                    <td class="hide-sm sd-muted">{{ g.description ?? '—' }}</td>
                    <td class="num">{{ g.customerCount }}</td>
                    @if (canManage) {
                      <td class="row-actions">
                        <button sdButton size="sm" variant="ghost" icon="pencil" iconOnly [attr.aria-label]="'Edit ' + g.name" (click)="edit(g)"></button>
                        <button sdButton size="sm" variant="ghost" icon="trash" iconOnly [attr.aria-label]="'Delete ' + g.name" (click)="remove(g)"></button>
                      </td>
                    }
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </div>
    </div>
  `,
  styles: [
    TABLE_STYLES,
    `
      .summary {
        margin-bottom: var(--sd-space-3);
      }
      .show-sm {
        display: none;
      }
      @media (max-width: 640px) {
        .hide-sm {
          display: none;
        }
        .show-sm {
          display: block;
        }
      }
    `,
  ],
})
export class CustomerGroupsPage {
  private readonly api = inject(CustomersApi);
  private readonly dialog = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  protected readonly canManage = inject(MeStore).can('customers.manage');
  protected readonly tabs = CUSTOMER_TABS;

  protected readonly groups = signal<CustomerGroupDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly members = computed(() => this.groups().reduce((sum, g) => sum + g.customerCount, 0));

  constructor() {
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.groups().subscribe({
      next: (list) => {
        this.groups.set(list);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.error.set(ApiError.from(err).userMessage);
      },
    });
  }

  protected edit(group: CustomerGroupDto | null): void {
    if (!this.canManage) return;
    this.dialog
      .open<CustomerGroupDto, NameDescriptionData<CustomerGroupDto>>(NameDescriptionDialog, {
        data: {
          title: group ? `Edit ${group.name}` : 'New customer group',
          name: group?.name,
          text: group?.description,
          nameLabel: 'Group name',
          confirmLabel: group ? 'Save' : 'Create group',
          save: (name, description) => this.api.saveGroup(group?.id ?? null, { name, description }),
        },
        size: 'sm',
      })
      .closed.subscribe((saved) => {
        if (!saved) return;
        this.toast.success(group ? 'Group saved' : 'Group created', saved.name);
        this.load();
      });
  }

  protected async remove(group: CustomerGroupDto): Promise<void> {
    const ok = await this.dialog.confirm({
      title: `Delete ${group.name}?`,
      message:
        group.customerCount > 0
          ? `Its ${group.customerCount} customer${group.customerCount === 1 ? '' : 's'} stay but no longer belong to a group.`
          : 'No customer belongs to this group.',
      confirmLabel: 'Delete group',
      danger: true,
    });
    if (!ok) return;
    this.api.deleteGroup(group.id).subscribe({
      next: () => {
        this.toast.success('Group deleted', group.name);
        this.load();
      },
      error: (err: unknown) => this.toast.error('Could not delete the group', saveErrorMessage(err)),
    });
  }
}
