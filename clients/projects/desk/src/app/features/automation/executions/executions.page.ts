import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AutomationApi, PageResult, RuleExecutionDto, RuleSummaryDto, TicketsApi } from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdDateTimePipe,
  SdDrawer,
  SdEmptyState,
  SdIcon,
  SdInput,
  SdRelativeTimePipe,
  SdSkeleton,
  ToastService,
} from '@sd/ui';
import { AutomationContext } from '../shared/automation-context.service';
import { actionTone, errorMessage, executionTone, humanStatus } from '../shared/automation-ui';

const STATUSES = ['Succeeded', 'PartiallyFailed', 'Failed', 'SkippedLoop', 'NotMatched', 'Simulated'];

/** Rule execution history: filters (rule, ticket, status, trigger), paging, detail with condition trace and action results, retry. */
@Component({
  selector: 'desk-executions',
  imports: [FormsModule, RouterLink, SdBadge, SdButton, SdDrawer, SdIcon, SdInput, SdEmptyState, SdSkeleton, SdDateTimePipe, SdRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form class="filters" (ngSubmit)="search(1)" aria-label="Filter executions">
      <select sdInput name="rule" aria-label="Rule" [(ngModel)]="ruleFilter">
        <option value="">All rules</option>
        @for (r of rules(); track r.id) {
          <option [value]="r.id">{{ r.name }}</option>
        }
      </select>
      <select sdInput name="status" aria-label="Status" [(ngModel)]="statusFilter">
        <option value="">Any status</option>
        @for (s of statuses; track s) {
          <option [value]="s">{{ human(s) }}</option>
        }
      </select>
      <select sdInput name="trigger" aria-label="Trigger" [(ngModel)]="triggerFilter">
        <option value="">Any trigger</option>
        @for (t of ctx.catalog()?.triggers ?? []; track t.key) {
          <option [value]="t.key">{{ t.label }}</option>
        }
      </select>
      <input sdInput name="ticket" class="ticket" inputmode="numeric" aria-label="Ticket number" placeholder="Ticket #" [(ngModel)]="ticketNumber" />
      <button sdButton type="submit" icon="search">Apply</button>
      <button sdButton type="button" variant="ghost" (click)="clear()">Clear</button>
      <span class="sd-spacer"></span>
      <button sdButton type="button" variant="ghost" icon="refresh" (click)="search(page()?.page ?? 1)" aria-label="Refresh"></button>
    </form>

    @if (loading() && !page()) {
      <sd-skeleton [lines]="8" height="32px" />
    } @else if (page(); as p) {
      @if (p.items.length === 0) {
        <sd-empty-state icon="activity" title="No executions match" message="Rules record every evaluation here: what matched, what each action did, and why rules were skipped." />
      } @else {
        <div class="table-wrap">
          <table class="table">
            <caption class="sd-visually-hidden">Rule executions</caption>
            <thead>
              <tr>
                <th scope="col">When</th>
                <th scope="col">Rule</th>
                <th scope="col">Trigger</th>
                <th scope="col">Ticket</th>
                <th scope="col">Status</th>
                <th scope="col">Actions</th>
                <th scope="col" class="num">Depth</th>
                <th scope="col"><span class="sd-visually-hidden">Details</span></th>
              </tr>
            </thead>
            <tbody>
              @for (e of p.items; track e.id) {
                <tr [class.selected]="selected()?.id === e.id">
                  <td><span [title]="e.startedAt | sdDateTime">{{ e.startedAt | sdRelativeTime }}</span></td>
                  <td>
                    <a [routerLink]="['/automation/rules', e.ruleId]">{{ e.ruleName ?? 'Deleted rule' }}</a>
                    @if (e.versionNumber) {
                      <span class="sd-muted"> v{{ e.versionNumber }}</span>
                    }
                  </td>
                  <td class="sd-muted">{{ ctx.triggerLabel(e.trigger) }}</td>
                  <td>
                    @if (e.ticketNumber) {
                      <a [routerLink]="['/automation/tickets', e.entityId]">#{{ e.ticketNumber }}</a>
                    }
                  </td>
                  <td><sd-badge size="sm" [tone]="tone(e.status)">{{ human(e.status) }}</sd-badge></td>
                  <td class="sd-muted">{{ actionSummary(e) }}</td>
                  <td class="num">{{ e.chainDepth }}</td>
                  <td><button sdButton size="sm" variant="ghost" (click)="open(e)">Details</button></td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <div class="pager">
          <span class="sd-muted">{{ rangeText(p) }}</span>
          <span class="sd-spacer"></span>
          <button sdButton size="sm" icon="chevron-left" [disabled]="p.page <= 1" (click)="search(p.page - 1)">Previous</button>
          <button sdButton size="sm" iconRight="chevron-right" [disabled]="p.page * p.pageSize >= p.total" (click)="search(p.page + 1)">Next</button>
        </div>
      }
    }

    <sd-drawer [open]="!!selected()" (openChange)="!$event && selected.set(null)" title="Execution details" width="560px">
      @if (selected(); as e) {
        <div class="detail">
          <div class="row">
            <sd-badge [tone]="tone(e.status)">{{ human(e.status) }}</sd-badge>
            <span class="sd-muted">attempt {{ e.attempt }} · depth {{ e.chainDepth }}</span>
            <span class="sd-spacer"></span>
            @if (e.status === 'Failed' || e.status === 'PartiallyFailed') {
              <button sdButton size="sm" variant="primary" icon="refresh" [loading]="retrying()" (click)="retry(e)">Retry failed actions</button>
            }
          </div>
          <dl class="facts">
            <dt>Rule</dt>
            <dd><a [routerLink]="['/automation/rules', e.ruleId]">{{ e.ruleName ?? e.ruleId }}</a>{{ e.versionNumber ? ' (v' + e.versionNumber + ')' : '' }}</dd>
            <dt>Trigger</dt>
            <dd>{{ ctx.triggerLabel(e.trigger) }}</dd>
            <dt>Ticket</dt>
            <dd>{{ e.ticketNumber ? '#' + e.ticketNumber : e.entityId }}</dd>
            <dt>Started</dt>
            <dd>{{ e.startedAt | sdDateTime }}</dd>
            <dt>Completed</dt>
            <dd>{{ e.completedAt | sdDateTime }}</dd>
            <dt>Event</dt>
            <dd class="mono">{{ e.eventId }}</dd>
            @if (e.correlationId) {
              <dt>Correlation</dt>
              <dd class="mono">{{ e.correlationId }}</dd>
            }
          </dl>
          @if (e.error) {
            <p class="error" role="alert"><sd-icon name="circle-alert" [size]="14" /> {{ e.error }}</p>
          }
          <h3>Conditions</h3>
          @if (e.trace.length) {
            <table class="trace">
              <thead>
                <tr><th scope="col">Field</th><th scope="col">Check</th><th scope="col">Value</th><th scope="col"><span class="sd-visually-hidden">Result</span></th></tr>
              </thead>
              <tbody>
                @for (c of e.trace; track $index) {
                  <tr>
                    <td>{{ ctx.fieldLabel(c.field) }}</td>
                    <td><span class="mono">{{ c.op }}</span> {{ c.expected === null ? '' : ctx.describeValue(c.expected) }}</td>
                    <td>{{ ctx.describeValue(c.actual) }}</td>
                    <td><sd-icon [name]="c.result ? 'circle-check' : 'circle-x'" [size]="14" [class]="c.result ? 'ok' : 'no'" /><span class="sd-visually-hidden">{{ c.result }}</span></td>
                  </tr>
                }
              </tbody>
            </table>
          } @else {
            <p class="sd-muted">No conditions — the rule matches every ticket for its trigger.</p>
          }
          <h3>Actions</h3>
          @if (e.actions.length) {
            <ol class="actions">
              @for (a of e.actions; track a.index) {
                <li>
                  <div class="row">
                    <strong>{{ ctx.actionLabel(a.type) }}</strong>
                    <sd-badge size="sm" [tone]="actTone(a.status)">{{ a.status }}</sd-badge>
                    @if (a.attempts > 1) {
                      <span class="sd-muted">{{ a.attempts }} attempts</span>
                    }
                    @if (a.transient) {
                      <sd-badge size="sm" variant="outline" tone="info">retryable</sd-badge>
                    }
                  </div>
                  @if (a.message) {
                    <p>{{ a.message }}</p>
                  }
                  @if (a.error) {
                    <p class="error">{{ a.error }}</p>
                  }
                </li>
              }
            </ol>
          } @else {
            <p class="sd-muted">No actions ran.</p>
          }
        </div>
      }
    </sd-drawer>
  `,
  styles: `
    .filters {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      margin-bottom: 14px;
    }
    .filters select {
      width: 190px;
    }
    .ticket {
      width: 110px;
    }
    .table-wrap {
      overflow-x: auto;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
    }
    .table {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--sd-text-sm);
    }
    .table th {
      padding: 8px 10px;
      text-align: left;
      color: var(--sd-text-3);
      font-weight: var(--sd-weight-medium);
      border-bottom: 1px solid var(--sd-border);
      white-space: nowrap;
    }
    .table td {
      padding: 6px 10px;
      border-bottom: 1px solid var(--sd-divider);
      white-space: nowrap;
    }
    .table tr:last-child td {
      border-bottom: 0;
    }
    .table tr:hover td,
    .table tr.selected td {
      background: var(--sd-row-hover);
    }
    .table a {
      color: var(--sd-primary);
      text-decoration: none;
    }
    .num {
      text-align: right;
    }
    .pager {
      display: flex;
      align-items: center;
      gap: 8px;
      margin-top: 10px;
      font-size: var(--sd-text-sm);
    }
    .detail {
      display: flex;
      flex-direction: column;
      gap: 12px;
      font-size: var(--sd-text-sm);
    }
    .row {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
    }
    .facts {
      display: grid;
      grid-template-columns: 110px 1fr;
      gap: 4px 10px;
      margin: 0;
    }
    .facts dt {
      color: var(--sd-text-3);
    }
    .facts dd {
      margin: 0;
      word-break: break-word;
    }
    .mono {
      font-family: var(--sd-font-mono);
      font-size: var(--sd-text-xs);
    }
    h3 {
      margin: 6px 0 0;
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-semibold);
    }
    .trace {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--sd-text-xs);
    }
    .trace th {
      text-align: left;
      color: var(--sd-text-3);
      font-weight: var(--sd-weight-medium);
    }
    .trace td:first-child {
      white-space: nowrap;
    }
    .trace td {
      padding: 3px 4px;
      border-top: 1px solid var(--sd-divider);
      vertical-align: top;
      word-break: break-word;
    }
    .ok {
      color: var(--sd-success);
    }
    .no {
      color: var(--sd-danger);
    }
    .error {
      display: flex;
      gap: 6px;
      align-items: flex-start;
      margin: 0;
      color: var(--sd-danger);
    }
    .actions {
      display: flex;
      flex-direction: column;
      gap: 8px;
      margin: 0;
      padding-left: 18px;
    }
    .actions p {
      margin: 2px 0 0;
    }
  `,
})
export class ExecutionsPage implements OnInit {
  protected readonly ctx = inject(AutomationContext);
  private readonly api = inject(AutomationApi);
  private readonly tickets = inject(TicketsApi);
  private readonly toast = inject(ToastService);

  /** Optional query parameters (?ruleId=…&status=…). */
  readonly ruleId = input<string | undefined>();
  readonly status = input<string | undefined>();

  protected readonly statuses = STATUSES;
  protected readonly rules = signal<RuleSummaryDto[]>([]);
  protected readonly page = signal<PageResult<RuleExecutionDto> | null>(null);
  protected readonly loading = signal(false);
  protected readonly selected = signal<RuleExecutionDto | null>(null);
  protected readonly retrying = signal(false);
  protected readonly ruleFilter = signal('');
  protected readonly statusFilter = signal('');
  protected readonly triggerFilter = signal('');
  protected readonly ticketNumber = signal('');
  private ticketId: string | null = null;

  protected readonly tone = executionTone;
  protected readonly actTone = actionTone;
  protected readonly human = humanStatus;
  protected readonly hasFilters = computed(() => !!(this.ruleFilter() || this.statusFilter() || this.triggerFilter() || this.ticketNumber()));

  ngOnInit(): void {
    this.ruleFilter.set(this.ruleId() ?? '');
    this.statusFilter.set(this.status() ?? '');
    this.api.list().subscribe((r) => this.rules.set(r.sort((a, b) => a.name.localeCompare(b.name))));
    this.search(1);
  }

  protected clear(): void {
    this.ruleFilter.set('');
    this.statusFilter.set('');
    this.triggerFilter.set('');
    this.ticketNumber.set('');
    this.search(1);
  }

  protected search(pageNumber: number): void {
    const number = this.ticketNumber().replace(/[^0-9]/g, '');
    if (number) {
      this.tickets.getByNumber(number).subscribe({
        next: (t) => {
          this.ticketId = t.id;
          this.fetch(pageNumber);
        },
        error: () => this.toast.warning('Ticket not found', `#${number}`),
      });
      return;
    }
    this.ticketId = null;
    this.fetch(pageNumber);
  }

  private fetch(pageNumber: number): void {
    this.loading.set(true);
    this.api
      .executions({
        ruleId: this.ruleFilter() || null,
        status: this.statusFilter() || null,
        trigger: this.triggerFilter() || null,
        ticketId: this.ticketId,
        page: pageNumber,
        pageSize: 50,
      })
      .subscribe({
        next: (p) => {
          this.page.set(p);
          this.loading.set(false);
        },
        error: () => this.loading.set(false),
      });
  }

  protected open(e: RuleExecutionDto): void {
    this.selected.set(e);
  }

  protected retry(e: RuleExecutionDto): void {
    this.retrying.set(true);
    this.api.retry(e.id).subscribe({
      next: (updated) => {
        this.retrying.set(false);
        this.selected.set(updated);
        this.page.update((p) => (p ? { ...p, items: p.items.map((x) => (x.id === updated.id ? updated : x)) } : p));
        this.toast.success('Retried', humanStatus(updated.status));
      },
      error: (err: unknown) => {
        this.retrying.set(false);
        this.toast.error('Retry failed', errorMessage(err));
      },
    });
  }

  protected actionSummary(e: RuleExecutionDto): string {
    if (e.actions.length === 0) return '—';
    const count = (s: string) => e.actions.filter((a) => a.status === s).length;
    const parts: [number, string][] = [
      [count('succeeded'), 'done'],
      [count('would_run'), 'would run'],
      [count('queued'), 'queued'],
      [count('skipped'), 'skipped'],
      [count('failed'), 'failed'],
    ];
    return parts.filter(([n]) => n > 0).map(([n, label]) => `${n} ${label}`).join(' · ');
  }

  protected rangeText(p: PageResult<RuleExecutionDto>): string {
    const from = (p.page - 1) * p.pageSize + 1;
    return `${from}–${from + p.items.length - 1} of ${p.total}`;
  }
}
