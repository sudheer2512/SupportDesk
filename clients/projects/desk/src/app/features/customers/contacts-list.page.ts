import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ContactDetailDto, ContactListItemDto, ContactsApi, MeStore, PORTAL_ACCESS_SCOPES, cursorList } from '@sd/core';
import {
  SdAvatar,
  SdBadge,
  SdButton,
  SdCellDef,
  SdColumn,
  SdDataTable,
  SdDialogService,
  SdIcon,
  SdInput,
  SdPageHeader,
  SdRelativeTimePipe,
  SdToggle,
  ToastService,
} from '@sd/ui';
import { ContactFormData, ContactFormDialog } from './contact-form.dialog';
import { CUSTOMER_TABS, ModuleTabsComponent } from './shared/module-tabs.component';

/** /customers/contacts — all contacts (search by name, e-mail, phone or title; status, VIP and portal filters). */
@Component({
  selector: 'desk-contacts-list-page',
  imports: [FormsModule, SdPageHeader, SdButton, SdDataTable, SdCellDef, SdBadge, SdAvatar, SdIcon, SdInput, SdToggle, SdRelativeTimePipe, ModuleTabsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Contacts" subtitle="People at your customers, their identities, consent and portal access." icon="users">
        @if (me.can('customers.manage')) {
          <button sdButton sdPageActions variant="primary" icon="user-plus" (click)="create()">New contact</button>
        }
      </sd-page-header>
      <desk-module-tabs [tabs]="tabs" ariaLabel="Customers sections" />
      <div class="desk-page__body">
        <div class="toolbar">
          <label class="search">
            <sd-icon name="search" [size]="16" />
            <input sdInput type="search" placeholder="Search name, e-mail, phone or title" aria-label="Search contacts" [ngModel]="q()" (ngModelChange)="onSearch($event)" />
          </label>
          <select sdInput size="sm" class="f" aria-label="Status" [ngModel]="status()" (ngModelChange)="status.set($event)">
            <option value="Active">Active</option>
            <option value="Inactive">Inactive</option>
            <option value="Merged">Merged</option>
            <option value="all">All</option>
          </select>
          <select sdInput size="sm" class="f" aria-label="Portal access" [ngModel]="portal()" (ngModelChange)="portal.set($event)">
            <option value="">Any portal access</option>
            @for (p of portalScopes; track p) {
              <option [value]="p">{{ p }}</option>
            }
          </select>
          <sd-toggle [value]="vipOnly()" (valueChange)="vipOnly.set($event)">VIP only</sd-toggle>
        </div>
        <div class="table-host">
          <sd-data-table
            [columns]="columns"
            [rows]="list.items()"
            storageKey="contacts.list"
            [loading]="list.loading()"
            [loadingMore]="list.loadingMore()"
            [hasMore]="list.hasMore()"
            [total]="list.total()"
            [error]="list.error()?.userMessage ?? null"
            emptyTitle="No contacts match"
            emptyIcon="users"
            ariaLabel="Contacts"
            (loadMore)="list.loadMore()"
            (rowClick)="open($event)"
          >
            <ng-template sdCell="fullName" [sdCellRows]="list.items()" let-row>
              <sd-avatar [name]="row.fullName" size="xs" />
              <span class="sd-truncate"><strong>{{ row.fullName }}</strong></span>
              @if (row.isVip) {
                <sd-badge tone="warning" size="sm">VIP</sd-badge>
              }
            </ng-template>
            <ng-template sdCell="status" [sdCellRows]="list.items()" let-row>
              <sd-badge [tone]="row.status === 'Active' ? 'success' : row.status === 'Merged' ? 'info' : 'neutral'" size="sm" dot>{{ row.status }}</sd-badge>
            </ng-template>
            <ng-template sdCell="portalAccess" [sdCellRows]="list.items()" let-row>
              <span class="sd-small">{{ row.portalAccess }}@if (row.hasPortalPassword) { <sd-icon name="key" [size]="12" aria-label="Password set" /> }</span>
            </ng-template>
            <ng-template sdCell="updatedAt" [sdCellRows]="list.items()" let-row>
              <span class="sd-small sd-muted">{{ row.updatedAt | sdRelativeTime }}</span>
            </ng-template>
          </sd-data-table>
        </div>
      </div>
    </div>
  `,
  styles: `
    .toolbar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--sd-space-2);
      margin-bottom: var(--sd-space-3);
    }
    .search {
      position: relative;
      flex: 1 1 260px;
      max-width: 380px;
    }
    .search sd-icon {
      position: absolute;
      left: 10px;
      top: 50%;
      transform: translateY(-50%);
      color: var(--sd-text-3);
    }
    .search input {
      padding-left: 32px;
    }
    .f {
      width: 170px;
    }
    .table-host {
      height: max(420px, calc(100dvh - 330px));
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      overflow: hidden;
      background: var(--sd-surface);
    }
  `,
})
export class ContactsListPage {
  /** Optional ?customerId= filter. */
  readonly customerId = input<string | null>(null);
  protected readonly me = inject(MeStore);
  private readonly api = inject(ContactsApi);
  private readonly router = inject(Router);
  private readonly dialog = inject(SdDialogService);
  private readonly toast = inject(ToastService);

  protected readonly tabs = CUSTOMER_TABS;
  protected readonly portalScopes = PORTAL_ACCESS_SCOPES;
  protected readonly q = signal('');
  protected readonly status = signal('Active');
  protected readonly portal = signal('');
  protected readonly vipOnly = signal(false);
  private timer: ReturnType<typeof setTimeout> | null = null;

  protected readonly columns: SdColumn<ContactListItemDto>[] = [
    { key: 'fullName', header: 'Name', flex: 2, minWidth: 200, locked: true },
    { key: 'customerName', header: 'Customer', flex: 2, minWidth: 160 },
    { key: 'email', header: 'E-mail', flex: 2, minWidth: 180 },
    { key: 'phone', header: 'Phone', width: 150 },
    { key: 'jobTitle', header: 'Title', flex: 1, minWidth: 120, hiddenByDefault: true },
    { key: 'locationName', header: 'Location', flex: 1, minWidth: 120, hiddenByDefault: true },
    { key: 'status', header: 'Status', width: 110 },
    { key: 'portalAccess', header: 'Portal', width: 120 },
    { key: 'openTickets', header: 'Open', width: 70, align: 'end' },
    { key: 'updatedAt', header: 'Updated', width: 110 },
  ];

  private readonly query = computed(() => ({
    q: this.q() || null,
    status: this.status(),
    portalAccess: this.portal() || null,
    isVip: this.vipOnly() || null,
    customerId: this.customerId(),
  }));

  protected readonly list = cursorList<ContactListItemDto>((cursor) => this.api.list({ ...this.query(), cursor, limit: 50, includeTotal: !cursor }));

  constructor() {
    effect(() => {
      this.query();
      untracked(() => this.list.load());
    });
  }

  protected onSearch(value: string): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => this.q.set(value), 250);
  }

  protected open(row: ContactListItemDto): void {
    void this.router.navigate(['/customers/contacts', row.id]);
  }

  protected create(): void {
    this.dialog
      .open<ContactDetailDto, ContactFormData>(ContactFormDialog, { data: {}, size: 'lg', disableClose: true })
      .closed.subscribe((saved) => {
        if (!saved) return;
        this.toast.success('Contact created', saved.fullName);
        void this.router.navigate(['/customers/contacts', saved.id]);
      });
  }
}
