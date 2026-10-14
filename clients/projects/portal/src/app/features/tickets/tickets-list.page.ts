import { ChangeDetectionStrategy, Component, computed, effect, inject, input, linkedSignal, signal, untracked } from '@angular/core';
import { rxResource, takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import {
  PortalAuthService,
  PortalRealtimeService,
  PortalTicketListItemDto,
  PortalTicketScope,
  PortalTicketsApi,
  cursorList,
} from '@sd/core';
import { SdBadge, SdButton, SdDateTimePipe, SdEmptyState, SdIcon, SdPriorityBadge, SdRelativeTimePipe, SdSkeleton } from '@sd/ui';
import { catchError, debounceTime, distinctUntilChanged, map, merge, of } from 'rxjs';
import { BrandService } from '../../brand.service';
import { BreadcrumbComponent } from '../../shared/breadcrumb.component';
import { ErrorStateComponent } from '../../shared/error-state.component';
import { mediaQuery } from '../../shared/portal-utils';
import { StatusBadgeComponent } from '../../shared/status-badge.component';

type StatusFilter = 'open' | 'awaiting' | 'closed' | 'all';

const STATUS_FILTERS: { value: StatusFilter; label: string; icon: string }[] = [
  { value: 'open', label: 'Open', icon: 'inbox' },
  { value: 'awaiting', label: 'Awaiting you', icon: 'reply' },
  { value: 'closed', label: 'Closed', icon: 'check-check' },
  { value: 'all', label: 'All', icon: 'list' },
];

const EMPTY_TEXT: Record<StatusFilter, { title: string; message: string }> = {
  open: { title: 'No open tickets', message: "You're all caught up. Need help with something new?" },
  awaiting: { title: 'Nothing is waiting for you', message: "We'll let you know when we need your reply or confirmation." },
  closed: { title: 'No closed tickets yet', message: 'Resolved and closed tickets will appear here.' },
  all: { title: 'No tickets yet', message: 'When you contact us, your requests appear here so you can follow them up.' },
};

/**
 * "My tickets": scope switcher (own / location / organisation when the contact may see more), status
 * filter chips, search, keyset "Load more", a table on wide screens and cards on phones. Realtime
 * events flag the list as stale and offer a refresh (without losing the loaded pages unexpectedly).
 */
@Component({
  selector: 'portal-tickets-list-page',
  imports: [
    RouterLink,
    SdIcon,
    SdButton,
    SdBadge,
    SdPriorityBadge,
    SdSkeleton,
    SdEmptyState,
    SdRelativeTimePipe,
    SdDateTimePipe,
    BreadcrumbComponent,
    ErrorStateComponent,
    StatusBadgeComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="portal-container portal-page">
      <portal-breadcrumb [items]="[{ label: 'Home', link: brand.base() }, { label: 'My tickets' }]" />
      <header class="portal-page-head head">
        <div>
          <h1>{{ scope() === 'own' ? 'My tickets' : scope() === 'location' ? 'Location tickets' : 'Organisation tickets' }}</h1>
          <p class="portal-sub">{{ subtitle() }}</p>
        </div>
        <a sdButton variant="primary" icon="file-plus" [routerLink]="brand.base() + '/submit'">Submit a ticket</a>
      </header>

      @if (scopes().length > 1) {
        <div class="scopes" role="group" aria-label="Whose tickets to show">
          @for (s of scopes(); track s) {
            <button type="button" class="scope" [attr.aria-pressed]="scope() === s" (click)="setParam('scope', s === 'own' ? null : s)">
              <sd-icon [name]="s === 'own' ? 'user' : s === 'location' ? 'map-pin' : 'building'" [size]="15" />{{ scopeLabel(s) }}
            </button>
          }
        </div>
      }

      <div class="toolbar">
        <div class="chips" role="group" aria-label="Filter by status">
          @for (f of statusFilters; track f.value) {
            <button type="button" class="chip" [attr.aria-pressed]="activeStatus() === f.value" (click)="setParam('status', f.value === 'open' ? null : f.value)">
              <sd-icon [name]="f.icon" [size]="14" />{{ f.label }}
            </button>
          }
        </div>
        <form class="search" role="search" aria-label="Search tickets" (submit)="$event.preventDefault(); setParam('q', search().trim() || null)">
          <sd-icon name="search" [size]="16" />
          <label for="tickets-q" class="sd-visually-hidden">Search tickets by number or subject</label>
          <input id="tickets-q" type="search" class="field" placeholder="Search by number or subject" [value]="search()" (input)="search.set($any($event.target).value)" />
        </form>
      </div>

      @if (stale()) {
        <div class="stale" role="status">
          <sd-icon name="refresh" [size]="15" />
          <span>Some of your tickets have been updated.</span>
          <button sdButton size="sm" variant="subtle" type="button" (click)="refresh()">Refresh list</button>
        </div>
      }
      <p class="sd-visually-hidden" aria-live="polite">{{ announcement() }}</p>

      @if (list.error() && !list.items().length) {
        <portal-error-state [error]="list.error()" errorTitle="Your tickets couldn't be loaded" (retry)="list.load()" />
      } @else if (list.loading() && !list.items().length) {
        <div class="loading" aria-busy="true" aria-label="Loading tickets">
          @for (i of [1, 2, 3, 4, 5]; track i) {
            <sd-skeleton height="58px" radius="var(--sd-radius-md)" />
          }
        </div>
      } @else if (list.isEmpty()) {
        <sd-empty-state [icon]="q() ? 'search' : 'ticket'" [title]="q() ? 'No tickets match “' + q() + '”' : empty().title" [message]="q() ? 'Try a ticket number or other words.' : empty().message">
          @if (q()) {
            <button sdButton type="button" (click)="search.set(''); setParam('q', null)">Clear search</button>
          } @else if (activeStatus() !== 'all') {
            <button sdButton type="button" (click)="setParam('status', 'all')">Show all tickets</button>
          }
          <a sdButton variant="primary" icon="file-plus" [routerLink]="brand.base() + '/submit'">Submit a ticket</a>
        </sd-empty-state>
      } @else {
        @if (wide()) {
          <div class="table-wrap">
            <table class="table">
              <caption class="sd-visually-hidden">
                Tickets
              </caption>
              <thead>
                <tr>
                  <th scope="col">Ticket</th>
                  <th scope="col">Status</th>
                  @if (scope() !== 'own') {
                    <th scope="col">Raised by</th>
                  }
                  <th scope="col">Priority</th>
                  <th scope="col">Last update</th>
                  <th scope="col">Created</th>
                </tr>
              </thead>
              <tbody>
                @for (t of list.items(); track t.id) {
                  <tr [class.needs-you]="t.awaitingYourReply || t.awaitingConfirmation">
                    <td class="subject-cell">
                      <span class="num">{{ t.displayNumber }}</span>
                      <a class="subject" [routerLink]="[brand.base(), 'tickets', t.id]">{{ t.subject }}</a>
                      <span class="flags">
                        @if (t.awaitingYourReply) {
                          <sd-badge size="sm" tone="warning" icon="reply">Awaiting your reply</sd-badge>
                        }
                        @if (t.awaitingConfirmation) {
                          <sd-badge size="sm" tone="info" icon="circle-check">Please confirm resolution</sd-badge>
                        }
                        @if (t.productName) {
                          <span class="muted"><sd-icon name="package" [size]="12" />{{ t.productName }}</span>
                        }
                        @if (t.locationName && scope() !== 'own') {
                          <span class="muted"><sd-icon name="map-pin" [size]="12" />{{ t.locationName }}</span>
                        }
                      </span>
                    </td>
                    <td><portal-status-badge [status]="t.status" [category]="t.statusCategory" /></td>
                    @if (scope() !== 'own') {
                      <td>
                        @if (t.isMine) {
                          <sd-badge size="sm" tone="primary">You</sd-badge>
                        } @else {
                          {{ t.contactName || '—' }}
                        }
                      </td>
                    }
                    <td><sd-priority-badge [priority]="t.priority" /></td>
                    <td class="time" [attr.title]="t.lastActivityAt | sdDateTime">{{ t.lastActivityAt | sdRelativeTime }}</td>
                    <td class="time">{{ t.createdAt | sdDateTime: 'date' }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        } @else {
          <ul class="cards">
            @for (t of list.items(); track t.id) {
              <li>
                <a class="card" [class.needs-you]="t.awaitingYourReply || t.awaitingConfirmation" [routerLink]="[brand.base(), 'tickets', t.id]">
                  <span class="card-top">
                    <span class="num">{{ t.displayNumber }}</span>
                    <portal-status-badge [status]="t.status" [category]="t.statusCategory" />
                  </span>
                  <span class="card-subject">{{ t.subject }}</span>
                  <span class="flags">
                    @if (t.awaitingYourReply) {
                      <sd-badge size="sm" tone="warning" icon="reply">Awaiting your reply</sd-badge>
                    }
                    @if (t.awaitingConfirmation) {
                      <sd-badge size="sm" tone="info" icon="circle-check">Please confirm</sd-badge>
                    }
                    @if (!t.isMine && t.contactName) {
                      <span class="muted"><sd-icon name="user" [size]="12" />{{ t.contactName }}</span>
                    }
                  </span>
                  <span class="card-meta">Updated {{ t.lastActivityAt | sdRelativeTime }}</span>
                </a>
              </li>
            }
          </ul>
        }
        @if (list.hasMore()) {
          <div class="more">
            <button sdButton type="button" [loading]="list.loadingMore()" (click)="list.loadMore()">Load more</button>
            @if (list.total() !== null) {
              <span class="muted">Showing {{ list.items().length }} of {{ list.total() }}</span>
            }
          </div>
        }
        @if (list.error() && list.items().length) {
          <p class="portal-callout portal-callout--danger" role="alert"><sd-icon name="circle-alert" [size]="15" />More tickets couldn't be loaded. <button sdButton variant="link" type="button" (click)="list.loadMore()">Try again</button></p>
        }
      }
    </div>
  `,
  styles: `
    .head {
      justify-content: space-between;
    }
    .scopes {
      display: inline-flex;
      flex-wrap: wrap;
      gap: 4px;
      max-width: 100%;
      margin-bottom: 14px;
      padding: 3px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface-3);
    }
    .scope {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      height: 32px;
      padding: 0 12px;
      border: 0;
      border-radius: var(--sd-radius-sm);
      background: transparent;
      color: var(--sd-text-2);
      font: inherit;
      font-weight: var(--sd-weight-medium);
      cursor: pointer;
    }
    .scope[aria-pressed='true'] {
      background: var(--sd-surface);
      color: var(--sd-text);
      box-shadow: var(--sd-shadow-sm);
    }
    .scope:focus-visible,
    .chip:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .toolbar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
      margin-bottom: 14px;
    }
    .chips {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    .chip {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      height: 32px;
      padding: 0 12px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface);
      color: var(--sd-text-2);
      font: inherit;
      font-size: var(--sd-text-sm);
      cursor: pointer;
    }
    .chip:hover {
      border-color: var(--sd-primary-border);
      color: var(--sd-text);
    }
    .chip[aria-pressed='true'] {
      border-color: var(--sd-primary);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
      font-weight: var(--sd-weight-medium);
    }
    .search {
      display: flex;
      align-items: center;
      gap: 8px;
      flex: 0 1 300px;
      min-width: 200px;
      height: var(--sd-control-h);
      padding: 0 12px;
      border: 1px solid var(--sd-border-strong);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      color: var(--sd-text-3);
    }
    .search:focus-within {
      border-color: var(--sd-primary);
      box-shadow: 0 0 0 3px var(--sd-focus);
    }
    .field {
      flex: 1;
      min-width: 0;
      border: 0;
      background: transparent;
      color: var(--sd-text);
      font: inherit;
      outline: none;
    }
    .stale {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 10px;
      margin-bottom: 12px;
      padding: 8px 12px;
      border: 1px solid var(--sd-info-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-info-bg);
      font-size: var(--sd-text-sm);
    }
    .stale sd-icon {
      color: var(--sd-info);
    }
    .stale span {
      flex: 1;
    }
    .loading {
      display: grid;
      gap: 10px;
    }
    .table-wrap {
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
      overflow-x: auto;
    }
    .table {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--sd-text-sm);
    }
    th {
      padding: 10px 14px;
      border-bottom: 1px solid var(--sd-border);
      background: var(--sd-surface-2);
      color: var(--sd-text-2);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      text-align: left;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      white-space: nowrap;
    }
    td {
      padding: 12px 14px;
      border-bottom: 1px solid var(--sd-divider);
      vertical-align: top;
    }
    tbody tr:last-child td {
      border-bottom: 0;
    }
    tbody tr:hover {
      background: var(--sd-row-hover);
    }
    tr.needs-you td:first-child {
      box-shadow: inset 3px 0 0 var(--sd-warning);
    }
    .subject-cell {
      display: grid;
      gap: 3px;
      min-width: 260px;
    }
    .num {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      font-variant-numeric: tabular-nums;
    }
    .subject {
      color: var(--sd-text);
      font-size: var(--sd-text-md);
      font-weight: var(--sd-weight-semibold);
      overflow-wrap: anywhere;
    }
    .subject:hover {
      color: var(--sd-primary);
    }
    .flags {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 4px 10px;
    }
    .flags:empty {
      display: none;
    }
    .muted {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .time {
      color: var(--sd-text-2);
      white-space: nowrap;
    }
    .cards {
      display: grid;
      gap: 10px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .card {
      display: grid;
      gap: 6px;
      padding: 14px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
      color: var(--sd-text);
      text-decoration: none;
    }
    .card:hover {
      border-color: var(--sd-primary-border);
      text-decoration: none;
    }
    .card.needs-you {
      border-left: 3px solid var(--sd-warning);
    }
    .card-top {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
    }
    .card-subject {
      font-weight: var(--sd-weight-semibold);
      overflow-wrap: anywhere;
    }
    .card-meta {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .more {
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 12px;
      margin-top: 16px;
    }
    @media (max-width: 640px) {
      .search {
        flex: 1 1 100%;
      }
    }
  `,
})
export class TicketsListPage {
  readonly status = input<string | undefined>();
  readonly scopeParam = input<string | undefined>(undefined, { alias: 'scope' });
  readonly q = input<string | undefined>();

  protected readonly brand = inject(BrandService);
  private readonly auth = inject(PortalAuthService);
  private readonly api = inject(PortalTicketsApi);
  private readonly router = inject(Router);
  private readonly realtime = inject(PortalRealtimeService);

  protected readonly statusFilters = STATUS_FILTERS;
  protected readonly wide = mediaQuery('(min-width: 768px)');
  protected readonly search = linkedSignal(() => this.q() ?? '');
  protected readonly stale = signal(false);

  private readonly scopeRes = rxResource({
    params: () => this.brand.slug() ?? undefined,
    stream: ({ params }) => this.api.scope(params).pipe(catchError(() => of(null))),
  });
  protected readonly scopes = computed<PortalTicketScope[]>(() => {
    const info = this.scopeRes.hasValue() ? this.scopeRes.value() : null;
    const available = info?.available ?? ['own'];
    const order: PortalTicketScope[] = ['own', 'location', 'organisation'];
    return order.filter((s) => available.includes(s));
  });
  protected readonly scope = computed<PortalTicketScope>(() => {
    const requested = this.scopeParam() as PortalTicketScope | undefined;
    return requested && this.scopes().includes(requested) ? requested : 'own';
  });
  protected readonly activeStatus = computed<StatusFilter>(() => {
    const s = this.status() as StatusFilter | undefined;
    return s && STATUS_FILTERS.some((f) => f.value === s) ? s : 'open';
  });
  protected readonly empty = computed(() => EMPTY_TEXT[this.activeStatus()]);
  protected readonly subtitle = computed(() => {
    const c = this.auth.contact();
    switch (this.scope()) {
      case 'organisation':
        return `Every request from ${c?.customerName || 'your organisation'}.`;
      case 'location':
        return `Requests from ${c?.locationName || 'your location'}.`;
      default:
        return 'Follow up on your requests, reply to our team and confirm resolutions.';
    }
  });
  protected readonly announcement = computed(() => {
    if (this.list.loading()) return 'Loading tickets…';
    if (!this.list.loaded()) return '';
    const n = this.list.total() ?? this.list.items().length;
    return n === 1 ? '1 ticket' : `${n} tickets`;
  });

  protected readonly list = cursorList<PortalTicketListItemDto>(() => of({ items: [], nextCursor: null }));

  constructor() {
    effect(() => {
      const slug = this.brand.slug();
      if (!slug) return;
      const query = { scope: this.scope(), status: this.activeStatus(), q: this.q()?.trim() || null };
      untracked(() => {
        this.stale.set(false);
        this.list.setFetcher((cursor) => this.api.list(slug, { ...query, cursor, limit: 25 }));
      });
    });

    toObservable(this.search)
      .pipe(
        map((q) => q.trim()),
        debounceTime(400),
        distinctUntilChanged(),
        takeUntilDestroyed(),
      )
      .subscribe((q) => {
        if (q !== (this.q() ?? '')) this.setParam('q', q || null);
      });

    merge(this.realtime.ticketChanged$, this.realtime.ticketThread$, this.realtime.reconnected$)
      .pipe(debounceTime(500), takeUntilDestroyed())
      .subscribe(() => this.stale.set(true));
  }

  protected scopeLabel(s: PortalTicketScope): string {
    const c = this.auth.contact();
    if (s === 'own') return 'Mine';
    if (s === 'location') return c?.locationName ? `My location (${c.locationName})` : 'My location';
    return c?.customerName ? `My organisation (${c.customerName})` : 'My organisation';
  }

  protected refresh(): void {
    this.stale.set(false);
    this.list.load();
  }

  protected setParam(key: 'status' | 'scope' | 'q', value: string | null): void {
    void this.router.navigate([], { queryParams: { [key]: value }, queryParamsHandling: 'merge', replaceUrl: true });
  }
}
