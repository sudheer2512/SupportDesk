import { ChangeDetectionStrategy, Component, OnInit, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ApiError, PlatformApi, PlatformTenantDto } from '@sd/core';
import { SdBadge, SdButton, SdDateTimePipe, SdEmptyState, SdFormField, SdInput, SdPageHeader, SdRelativeTimePipe, SdSkeleton } from '@sd/ui';
import { SAAS_CSS, bytes, tenantTone } from '../subscription/saas-ui';

/** Tenants: search by name or workspace, filter by status, keyset paging; opens the tenant detail. */
@Component({
  selector: 'desk-platform-tenants-page',
  imports: [FormsModule, SdBadge, SdButton, SdDateTimePipe, SdEmptyState, SdFormField, SdInput, SdPageHeader, SdRelativeTimePipe, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Tenants" icon="building" [subtitle]="total() !== null ? total() + ' workspaces' : 'All workspaces'" />
      <div class="desk-page__body">
        <form class="toolbar" (ngSubmit)="load(false)" role="search">
          <sd-form-field label="Search">
            <input sdInput name="q" placeholder="Name or workspace" [(ngModel)]="search" />
          </sd-form-field>
          <sd-form-field label="Status">
            <select sdInput name="status" [ngModel]="statusFilter()" (ngModelChange)="statusFilter.set($event); load(false)">
              <option value="">All</option>
              <option value="Trial">Trial</option>
              <option value="Active">Active</option>
              <option value="Suspended">Suspended</option>
              <option value="Cancelled">Cancelled</option>
              <option value="PendingDeletion">Pending deletion</option>
            </select>
          </sd-form-field>
          <button sdButton type="submit" icon="search">Search</button>
        </form>

        @if (error(); as e) {
          <sd-empty-state icon="circle-alert" title="Tenants could not be loaded" [message]="e"><button sdButton (click)="load(false)">Try again</button></sd-empty-state>
        } @else if (rows() === null) {
          <sd-skeleton [lines]="8" height="36px" />
        } @else if (rows()!.length === 0) {
          <sd-empty-state icon="building" title="No tenants match" message="Clear the search or status filter." />
        } @else {
          <div class="table-wrap">
            <table class="data">
              <caption class="sd-visually-hidden">Tenants</caption>
              <thead>
                <tr>
                  <th scope="col">Workspace</th>
                  <th scope="col">Status</th>
                  <th scope="col">Plan</th>
                  <th scope="col" class="num hide-sm">Agents</th>
                  <th scope="col" class="num hide-sm">Storage</th>
                  <th scope="col" class="hide-sm">Created</th>
                  <th scope="col" class="hide-sm">Last activity</th>
                </tr>
              </thead>
              <tbody>
                @for (t of rows(); track t.id) {
                  <tr class="clickable" tabindex="0" (click)="open(t)" (keydown.enter)="open(t)" [attr.aria-label]="'Open ' + t.name">
                    <td>
                      <strong>{{ t.name }}</strong>
                      <div class="muted small">{{ t.slug }}{{ t.databaseMode === 'Dedicated' ? ' · dedicated DB' : '' }}</div>
                    </td>
                    <td>
                      <sd-badge size="sm" [tone]="tone(t.status)" [dot]="true">{{ t.status }}</sd-badge>
                      @if (t.trialEndsAt && t.status === 'Trial') {
                        <div class="muted small">ends {{ t.trialEndsAt | sdRelativeTime }}</div>
                      }
                    </td>
                    <td>{{ t.planName }}<div class="muted small">{{ t.subscriptionStatus }}</div></td>
                    <td class="num hide-sm">{{ t.agents }}</td>
                    <td class="num hide-sm">{{ bytes(t.storageBytes) }}</td>
                    <td class="hide-sm">{{ t.createdAt | sdDateTime: 'date' }}</td>
                    <td class="hide-sm">{{ t.lastActivityAt ? (t.lastActivityAt | sdRelativeTime) : '—' }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          @if (cursor()) {
            <div class="more"><button sdButton size="sm" [loading]="loading()" (click)="load(true)">Load more</button></div>
          }
        }
      </div>
    </div>
  `,
  styles: SAAS_CSS,
})
export class PlatformTenantsPage implements OnInit {
  /** ?status= query param. */
  readonly status = input<string | undefined>();
  private readonly api = inject(PlatformApi);
  private readonly router = inject(Router);

  protected search = '';
  protected readonly statusFilter = signal('');
  protected readonly rows = signal<PlatformTenantDto[] | null>(null);
  protected readonly cursor = signal<string | null>(null);
  protected readonly total = signal<number | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly tone = tenantTone;
  protected readonly bytes = bytes;

  ngOnInit(): void {
    this.statusFilter.set(this.status() ?? '');
    this.load(false);
  }

  protected load(more: boolean): void {
    this.loading.set(true);
    this.error.set(null);
    if (!more) this.rows.set(null);
    this.api.tenants({ search: this.search, status: this.statusFilter() || null, cursor: more ? this.cursor() : null, limit: 50 }).subscribe({
      next: (p) => {
        this.rows.set(more ? [...(this.rows() ?? []), ...p.items] : p.items);
        this.cursor.set(p.nextCursor);
        this.total.set(p.total ?? null);
        this.loading.set(false);
      },
      error: (e: ApiError) => {
        this.error.set(e.userMessage);
        this.loading.set(false);
      },
    });
  }

  protected open(t: PlatformTenantDto): void {
    void this.router.navigate(['/platform/tenants', t.id]);
  }
}
