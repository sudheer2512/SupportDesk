import { DecimalPipe, PercentPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, signal, untracked } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiError, Guid, RealtimeService, SlaMilestoneDto, TicketContextDto, TicketListItemDto, TicketsApi } from '@sd/core';
import { SdBadge, SdButton, SdDateTimePipe, SdIcon, SdRelativeTimePipe, SdSkeleton, SdSlaCountdown, SdStatusPill } from '@sd/ui';
import { debounceTime, filter, merge } from 'rxjs';
import { TicketWorkbenchStore } from '../workspace/workbench.store';

const MILESTONE_LABELS: Record<string, string> = { FirstResponse: 'First response', NextResponse: 'Next response', Resolution: 'Resolution' };

/**
 * Right-hand context of the open ticket (GET /tickets/{id}/context): customer 360, contact, previous tickets,
 * live SLA milestones, contracts (covering flag), installations, ERP references, suggested articles, AI
 * recommendations and engineering links. Refreshes on realtime changes to the ticket.
 */
@Component({
  selector: 'desk-context-panel',
  imports: [RouterLink, DecimalPipe, PercentPipe, SdBadge, SdButton, SdDateTimePipe, SdIcon, SdRelativeTimePipe, SdSkeleton, SdSlaCountdown, SdStatusPill],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="panel" aria-label="Customer context" role="region">
      @if (!ticketId()) {
        <p class="hint sd-muted">Open a ticket to see the customer's history, SLA and contracts here.</p>
      } @else if (loading() && !ctx()) {
        <div class="pad"><sd-skeleton [lines]="4" /><br /><sd-skeleton [lines]="3" /><br /><sd-skeleton [lines]="3" /></div>
      } @else if (error()) {
        <p class="pad error" role="alert">{{ error() }} <button sdButton size="sm" variant="link" type="button" (click)="load()">Retry</button></p>
      } @else if (ctx(); as c) {
        <details open>
          <summary><sd-icon name="building" [size]="15" />Customer</summary>
          @if (c.customer; as cu) {
            <div class="card">
              <div class="title-row">
                <strong class="sd-truncate">{{ cu.name }}</strong>
                <sd-badge size="sm" [tone]="cu.tier === 'Platinum' || cu.tier === 'Gold' ? 'accent' : 'neutral'">{{ cu.tier }}</sd-badge>
              </div>
              <p class="sub sd-small sd-muted">
                {{ joinParts([cu.code, cu.industry, cu.serviceRegion]) }}
                @if (cu.status !== 'Active') {
                  · <span class="warn">{{ cu.status }}</span>
                }
              </p>
              @if (cu.accountManagerName) {
                <p class="sd-small">Account manager: {{ cu.accountManagerName }}</p>
              }
              <dl class="stats">
                <div><dt>Open</dt><dd>{{ cu.openTickets }}</dd></div>
                <div><dt>90 days</dt><dd>{{ cu.ticketsLast90Days }}</dd></div>
                <div><dt>CSAT</dt><dd>{{ cu.csatAverage !== null ? (cu.csatAverage | number: '1.1-1') : '—' }}</dd></div>
                <div [class.is-bad]="cu.slaBreachesLast90Days > 0"><dt>Breaches</dt><dd>{{ cu.slaBreachesLast90Days }}</dd></div>
              </dl>
              <a class="more" [routerLink]="['/customers', cu.id]">Open customer <sd-icon name="arrow-right" [size]="13" /></a>
            </div>
          } @else {
            <p class="sd-small sd-muted">No customer is linked to this ticket.</p>
          }
        </details>

        @if (c.contact; as co) {
          <details open>
            <summary><sd-icon name="user" [size]="15" />Contact</summary>
            <div class="card">
              <div class="title-row">
                <strong class="sd-truncate">{{ co.fullName }}</strong>
                @if (co.isVip) {
                  <sd-badge size="sm" tone="accent" icon="star">VIP</sd-badge>
                }
              </div>
              @if (co.jobTitle || co.locationName) {
                <p class="sub sd-small sd-muted">{{ joinParts([co.jobTitle, co.locationName]) }}</p>
              }
              <ul class="kv">
                @if (co.email) {
                  <li><sd-icon name="mail" [size]="13" /><a [href]="'mailto:' + co.email">{{ co.email }}</a></li>
                }
                @if (co.phone) {
                  <li><sd-icon name="phone" [size]="13" /><a [href]="'tel:' + co.phone">{{ co.phone }}</a></li>
                }
                <li><sd-icon name="languages" [size]="13" />{{ co.preferredLanguage }}{{ co.preferredChannel ? ' · prefers ' + co.preferredChannel : '' }}</li>
                <li><sd-icon name="ticket" [size]="13" />{{ co.openTickets }} open ticket(s)</li>
              </ul>
            </div>
          </details>
        }

        @if (c.sla; as sla) {
          <details open>
            <summary><sd-icon name="timer" [size]="15" />SLA · {{ sla.policyName }}</summary>
            <ul class="milestones">
              @for (m of sla.milestones; track m.id) {
                <li>
                  <div class="title-row">
                    <span>{{ milestoneLabel(m) }}</span>
                    @if (m.status === 'Active' || m.status === 'Paused') {
                      <sd-sla-countdown [dueAt]="m.dueAt" [state]="milestoneState(m)" compact />
                    } @else {
                      <sd-badge size="sm" [tone]="m.status === 'Met' ? 'success' : m.status === 'Breached' ? 'danger' : 'neutral'">{{ m.status }}</sd-badge>
                    }
                  </div>
                  <div class="bar" [class.is-bad]="m.status === 'Breached' || m.percentElapsed >= 100" [class.is-warn]="m.percentElapsed >= 75 && m.percentElapsed < 100">
                    <span [style.width.%]="m.percentElapsed > 100 ? 100 : m.percentElapsed"></span>
                  </div>
                  <p class="sd-xs sd-muted">
                    Due {{ m.dueAt | sdDateTime }} · target {{ m.targetMinutes }} min{{ m.businessHours ? ' (business hours)' : '' }}
                    @if (m.escalationLevel > 0) {
                      · escalation L{{ m.escalationLevel }}
                    }
                  </p>
                </li>
              } @empty {
                <li class="sd-small sd-muted">No milestones.</li>
              }
            </ul>
          </details>
        }

        @if (c.contracts.length) {
          <details open>
            <summary><sd-icon name="file-text" [size]="15" />Contracts ({{ c.contracts.length }})</summary>
            <ul class="list">
              @for (k of c.contracts; track k.id) {
                <li class="card">
                  <div class="title-row">
                    <strong class="sd-truncate">{{ k.number }} · {{ k.name }}</strong>
                    @if (k.coversTicket) {
                      <sd-badge size="sm" tone="success" icon="check">Covers</sd-badge>
                    } @else {
                      <sd-badge size="sm" tone="neutral">Not covering</sd-badge>
                    }
                  </div>
                  <p class="sd-xs sd-muted">{{ k.type }} · {{ k.status }} · {{ k.startDate }} → {{ k.endDate }}{{ k.isBillable ? ' · billable' : '' }}</p>
                  @if (k.prepaidHours) {
                    <div class="bar" [class.is-warn]="k.consumedHours / k.prepaidHours >= 0.8">
                      <span [style.width.%]="k.consumedHours / k.prepaidHours > 1 ? 100 : (k.consumedHours / k.prepaidHours) * 100"></span>
                    </div>
                    <p class="sd-xs sd-muted">{{ k.consumedHours | number: '1.0-1' }} of {{ k.prepaidHours }} hours used</p>
                  }
                </li>
              }
            </ul>
          </details>
        }

        @if (c.installations.length) {
          <details>
            <summary><sd-icon name="server" [size]="15" />Installations ({{ c.installations.length }})</summary>
            <ul class="list">
              @for (i of c.installations; track i.id) {
                <li class="card">
                  <strong class="sd-truncate">{{ i.name }}</strong>
                  <p class="sd-xs sd-muted">{{ i.productName }} {{ i.version ?? '' }} · {{ i.environment }} · {{ i.deploymentMode }}</p>
                  @if (i.operatingSystem || i.databaseVersion) {
                    <p class="sd-xs sd-muted">{{ joinParts([i.operatingSystem, i.databaseVersion]) }}</p>
                  }
                </li>
              }
            </ul>
          </details>
        }

        @if (c.previousTickets.length) {
          <details open>
            <summary><sd-icon name="history" [size]="15" />Previous tickets ({{ c.previousTickets.length }})</summary>
            <ul class="list">
              @for (p of c.previousTickets; track p.id) {
                <li>
                  <button type="button" class="ticket" (click)="openTicket(p)">
                    <span class="num">{{ p.displayNumber }}</span>
                    <span class="sd-truncate grow">{{ p.subject }}</span>
                    <sd-status-pill [label]="p.status" [color]="p.statusColor" />
                  </button>
                  <p class="sd-xs sd-muted indent">{{ p.createdAt | sdRelativeTime }} · {{ p.assigneeName ?? 'unassigned' }}</p>
                </li>
              }
            </ul>
          </details>
        }

        @if (c.relatedTransactions.length) {
          <details>
            <summary><sd-icon name="receipt" [size]="15" />ERP references ({{ c.relatedTransactions.length }})</summary>
            <ul class="list">
              @for (r of c.relatedTransactions; track r.id) {
                <li class="row">
                  <span class="grow sd-truncate"><strong>{{ r.system }}</strong> {{ r.recordType }} {{ r.recordNumber ?? '' }}</span>
                  @if (safeUrl(r.url); as url) {
                    <a [href]="url" target="_blank" rel="noopener noreferrer" [attr.aria-label]="'Open ' + r.recordType"><sd-icon name="external-link" [size]="13" /></a>
                  }
                </li>
              }
            </ul>
          </details>
        }

        @if (c.suggestedArticles.length) {
          <details open>
            <summary><sd-icon name="book-open" [size]="15" />Suggested articles</summary>
            <ul class="list">
              @for (a of c.suggestedArticles; track a.id) {
                <li class="card">
                  <a class="article" [href]="a.url" target="_blank" rel="noopener">{{ a.title }}</a>
                  @if (a.summary) {
                    <p class="sd-xs sd-muted">{{ a.summary }}</p>
                  }
                  <p class="sd-xs sd-muted">{{ a.visibility }} · match {{ a.score | percent: '1.0-0' }}</p>
                </li>
              }
            </ul>
          </details>
        }

        @if (c.aiRecommendations.length) {
          <details open>
            <summary><sd-icon name="sparkles" [size]="15" />AI recommendations</summary>
            <ul class="list">
              @for (r of c.aiRecommendations; track $index) {
                <li class="card ai">
                  <div class="title-row">
                    <strong>{{ r.title }}</strong>
                    @if (r.confidence !== null) {
                      <sd-badge size="sm" tone="info">{{ r.confidence | percent: '1.0-0' }}</sd-badge>
                    }
                  </div>
                  <p class="sd-small">{{ r.detail }}</p>
                </li>
              }
            </ul>
          </details>
        }

        @if (c.engineeringLinks.length) {
          <details open>
            <summary><sd-icon name="git-pull-request" [size]="15" />Engineering</summary>
            <ul class="list">
              @for (e of c.engineeringLinks; track e.id) {
                <li class="card">
                  <div class="title-row">
                    <strong class="sd-truncate">{{ e.provider }} {{ e.itemType }} {{ e.externalId }}</strong>
                    <sd-badge size="sm" [tone]="e.isResolved ? 'success' : 'warning'">{{ e.state ?? (e.isResolved ? 'Resolved' : 'Open') }}</sd-badge>
                  </div>
                  <p class="sd-small">{{ e.title }}</p>
                  <p class="sd-xs sd-muted">
                    {{ e.assignedTo ?? 'Unassigned' }}{{ e.fixVersion ? ' · fix in ' + e.fixVersion : '' }}
                    @if (e.lastSyncedAt) {
                      · synced {{ e.lastSyncedAt | sdRelativeTime }}
                    }
                  </p>
                  @if (safeUrl(e.url); as url) {
                    <a class="more" [href]="url" target="_blank" rel="noopener noreferrer">Open <sd-icon name="external-link" [size]="12" /></a>
                  }
                </li>
              }
            </ul>
          </details>
        }
      }
    </div>
  `,
  styles: `
    :host {
      display: block;
      height: 100%;
      overflow-y: auto;
      background: var(--sd-surface);
    }
    .panel {
      padding: 8px 12px 16px;
    }
    .hint,
    .pad {
      padding: 8px 4px;
    }
    .error {
      color: var(--sd-danger);
    }
    details {
      border-bottom: 1px solid var(--sd-divider);
      padding: 6px 0 10px;
    }
    summary {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 6px 4px;
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-semibold);
      cursor: pointer;
      list-style: none;
      border-radius: var(--sd-radius-sm);
    }
    summary::-webkit-details-marker {
      display: none;
    }
    summary::after {
      content: '';
      margin-left: auto;
      width: 7px;
      height: 7px;
      border-right: 1.5px solid var(--sd-text-3);
      border-bottom: 1.5px solid var(--sd-text-3);
      transform: rotate(-45deg);
      transition: transform var(--sd-dur-fast) var(--sd-ease);
    }
    details[open] > summary::after {
      transform: rotate(45deg);
    }
    summary:hover {
      background: var(--sd-surface-2);
    }
    summary:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .card {
      display: grid;
      gap: 3px;
      padding: 8px 10px;
      border: 1px solid var(--sd-divider);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      min-width: 0;
    }
    .card.ai {
      border-color: var(--sd-info-border);
      background: var(--sd-info-bg);
    }
    .title-row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
      min-width: 0;
    }
    .sub {
      margin: 0;
    }
    .warn {
      color: var(--sd-warning);
    }
    .stats {
      display: grid;
      grid-template-columns: repeat(4, 1fr);
      gap: 6px;
      margin: 6px 0 0;
    }
    .stats div {
      padding: 4px 6px;
      border-radius: var(--sd-radius-sm);
      background: var(--sd-surface-2);
      text-align: center;
    }
    .stats .is-bad {
      background: var(--sd-danger-bg);
      color: var(--sd-danger);
    }
    dt {
      font-size: 11px;
      color: var(--sd-text-3);
    }
    dd {
      margin: 0;
      font-weight: var(--sd-weight-semibold);
      font-variant-numeric: tabular-nums;
    }
    .kv {
      display: grid;
      gap: 3px;
      margin: 4px 0 0;
      padding: 0;
      list-style: none;
      font-size: var(--sd-text-sm);
    }
    .kv li {
      display: flex;
      align-items: center;
      gap: 6px;
      min-width: 0;
      overflow-wrap: anywhere;
    }
    .more {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      font-size: var(--sd-text-sm);
    }
    .milestones,
    .list {
      display: grid;
      gap: 8px;
      margin: 4px 0 0;
      padding: 0;
      list-style: none;
    }
    .milestones li {
      display: grid;
      gap: 3px;
    }
    .bar {
      height: 5px;
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface-3);
      overflow: hidden;
    }
    .bar span {
      display: block;
      height: 100%;
      background: var(--sd-sla-ontrack);
    }
    .bar.is-warn span {
      background: var(--sd-sla-atrisk);
    }
    .bar.is-bad span {
      background: var(--sd-sla-breached);
    }
    .ticket {
      display: flex;
      align-items: center;
      gap: 6px;
      width: 100%;
      padding: 4px 6px;
      border: 0;
      border-radius: var(--sd-radius-sm);
      background: transparent;
      color: var(--sd-text);
      font: inherit;
      font-size: var(--sd-text-sm);
      text-align: left;
      cursor: pointer;
    }
    .ticket:hover {
      background: var(--sd-surface-2);
    }
    .num {
      flex: none;
      color: var(--sd-text-2);
      font-variant-numeric: tabular-nums;
    }
    .grow {
      flex: 1;
      min-width: 0;
    }
    .indent {
      padding-left: 6px;
    }
    .row {
      display: flex;
      align-items: center;
      gap: 6px;
      font-size: var(--sd-text-sm);
    }
    .article {
      font-weight: var(--sd-weight-medium);
    }
  `,
})
export class ContextPanelComponent {
  readonly ticketId = input<Guid | null>(null);

  private readonly tickets = inject(TicketsApi);
  private readonly realtime = inject(RealtimeService);
  private readonly store = inject(TicketWorkbenchStore);
  private readonly router = inject(Router);

  protected readonly ctx = signal<TicketContextDto | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  private seq = 0;

  constructor() {
    effect(() => {
      const id = this.ticketId();
      untracked(() => {
        this.ctx.set(null);
        this.error.set(null);
        if (id) this.load();
      });
    });
    const sub = merge(this.realtime.ticketChanged$, this.realtime.slaAlert$)
      .pipe(
        filter((e) => e.ticketId === this.ticketId()),
        debounceTime(2000),
      )
      .subscribe(() => this.load(true));
    inject(DestroyRef).onDestroy(() => sub.unsubscribe());
  }

  load(quiet = false): void {
    const id = this.ticketId();
    if (!id) return;
    const seq = ++this.seq;
    if (!quiet) this.loading.set(true);
    this.tickets.context(id).subscribe({
      next: (c) => {
        if (seq !== this.seq) return;
        this.ctx.set(c);
        this.loading.set(false);
        this.error.set(null);
      },
      error: (err: unknown) => {
        if (seq !== this.seq) return;
        this.loading.set(false);
        if (!quiet) this.error.set(ApiError.from(err).userMessage);
      },
    });
  }

  protected milestoneLabel(m: SlaMilestoneDto): string {
    return `${MILESTONE_LABELS[m.kind] ?? m.kind}${m.cycle > 1 ? ` #${m.cycle}` : ''}`;
  }

  protected milestoneState(m: SlaMilestoneDto): string {
    if (m.status === 'Paused') return 'Paused';
    if (m.status === 'Breached' || m.percentElapsed >= 100) return 'Breached';
    return m.percentElapsed >= 75 ? 'AtRisk' : 'OnTrack';
  }

  protected joinParts(parts: (string | null | undefined)[]): string {
    return parts.filter((p) => !!p).join(' · ');
  }

  protected safeUrl(url: string | null): string | null {
    return url && /^https?:\/\//i.test(url) ? url : null;
  }

  protected openTicket(t: TicketListItemDto): void {
    this.store.openTab({ id: t.id, displayNumber: t.displayNumber, subject: t.subject });
    void this.router.navigate(['/tickets', t.id]);
  }
}
