import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { DepartmentDto, MetaApi, OrganisationApi, RealtimeService, SlaAdminApi, SlaBreachItemDto, SlaBreachSummaryDto, cursorList } from '@sd/core';
import { SdBadge, SdButton, SdEmptyState, SdKpiTile, SdPageHeader, SdRelativeTimePipe, SdSkeleton } from '@sd/ui';
import { catchError, debounceTime, merge, of } from 'rxjs';
import { ModuleTabsComponent } from '../customers/shared/module-tabs.component';
import { SECTION_STYLES, TABLE_STYLES } from '../customers/shared/styles';
import { PAGE_STYLES, formatMinutes, loadPercent } from '../teams/shared/org-ui';
import { MILESTONE_LABELS, SLA_TABS } from './sla-ui';

/** /sla/breaches — running SLA milestones at risk or breached (within your ticket visibility); refreshes on SLA alerts. */
@Component({
  selector: 'desk-sla-breaches-page',
  imports: [FormsModule, RouterLink, SdPageHeader, SdButton, SdBadge, SdKpiTile, SdSkeleton, SdEmptyState, SdRelativeTimePipe, ModuleTabsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="SLA" subtitle="Tickets at risk of breaching or already breached, soonest first." icon="timer">
        <button sdButton sdPageActions size="sm" variant="ghost" icon="refresh" (click)="reload()" [loading]="list.loading()">Refresh</button>
      </sd-page-header>
      <desk-module-tabs [tabs]="tabs" ariaLabel="SLA sections" />
      <div class="desk-page__body">
        @if (summary(); as s) {
          <div class="kpis">
            <sd-kpi-tile label="At risk" icon="hourglass" [value]="s.atRisk" interactive (activate)="state.set('AtRisk'); reload()" />
            <sd-kpi-tile label="Breached (open)" icon="octagon-alert" [value]="s.breached" interactive (activate)="state.set('Breached'); reload()" />
            <sd-kpi-tile label="Breached today" icon="flag" [value]="s.breachedToday" />
            <sd-kpi-tile label="Due in the next hour" icon="timer" [value]="s.dueNextHour" />
            <sd-kpi-tile label="Paused" icon="pause" [value]="s.paused" />
          </div>
        }
        <div class="toolbar">
          <label class="sd-visually-hidden" for="br-state">State</label>
          <select id="br-state" class="sd-input" [ngModel]="state()" (ngModelChange)="state.set($event); reload()">
            <option value="All">At risk and breached</option>
            <option value="AtRisk">At risk</option>
            <option value="Breached">Breached</option>
          </select>
          <label class="sd-visually-hidden" for="br-ms">Milestone</label>
          <select id="br-ms" class="sd-input" [ngModel]="milestone()" (ngModelChange)="milestone.set($event); reload()">
            <option value="">All milestones</option>
            <option value="FirstResponse">First response</option>
            <option value="NextResponse">Next response</option>
            <option value="Resolution">Resolution</option>
          </select>
          <label class="sd-visually-hidden" for="br-dept">Department</label>
          <select id="br-dept" class="sd-input" [ngModel]="department()" (ngModelChange)="department.set($event); reload()">
            <option [ngValue]="null">All departments</option>
            @for (d of departments(); track d.id) {
              <option [ngValue]="d.id">{{ d.name }}</option>
            }
          </select>
          <span class="sd-spacer"></span>
          @if (list.total() !== null) {
            <span class="small muted">{{ list.total() }} milestones</span>
          }
        </div>
        @if (list.error(); as e) {
          <div class="error-box" role="alert"><span>{{ e.userMessage }}</span><button sdButton size="sm" (click)="reload()">Try again</button></div>
        } @else if (list.loading() && list.items().length === 0) {
          <div aria-busy="true"><sd-skeleton [lines]="8" /></div>
        } @else if (list.isEmpty()) {
          <sd-empty-state icon="circle-check" title="All clear" message="No running SLA milestones are at risk or breached." />
        } @else {
          <div class="dt-wrap">
            <table class="dt">
              <caption class="sd-visually-hidden">SLA milestones at risk or breached</caption>
              <thead>
                <tr>
                  <th scope="col">Ticket</th>
                  <th scope="col">Milestone</th>
                  <th scope="col">Due</th>
                  <th scope="col">Elapsed</th>
                  <th scope="col">Owner</th>
                  <th scope="col">Department</th>
                  <th scope="col">Policy</th>
                </tr>
              </thead>
              <tbody>
                @for (b of list.items(); track b.milestoneId) {
                  <tr>
                    <td>
                      <a [routerLink]="['/tickets', b.number]">{{ prefix() }}{{ b.number }}</a>
                      <div class="small sd-truncate subject">{{ b.subject }}</div>
                    </td>
                    <td>
                      <sd-badge size="sm" [tone]="b.milestoneStatus === 'Breached' ? 'danger' : 'warning'" dot>{{ label(b.milestone) }}</sd-badge>
                      <span class="xs muted"> {{ b.priority }}</span>
                      @if (b.escalationLevel > 0) {
                        <sd-badge size="sm" tone="accent">L{{ b.escalationLevel }}</sd-badge>
                      }
                    </td>
                    <td class="nowrap small">{{ b.dueAt | sdRelativeTime }}</td>
                    <td>
                      <span class="load" [attr.aria-label]="minutes(b.elapsedMinutes) + ' of ' + minutes(b.targetMinutes)">
                        <span class="bar" [class.is-warn]="pct(b) >= 75" [class.is-danger]="pct(b) >= 100"><span [style.width.%]="pct(b)"></span></span>
                        <span class="xs muted">{{ minutes(b.elapsedMinutes) }} / {{ minutes(b.targetMinutes) }}</span>
                      </span>
                    </td>
                    <td class="small">{{ b.assigneeName ?? 'Unassigned' }}</td>
                    <td class="small">{{ b.departmentName }}</td>
                    <td class="small">{{ b.policyName ?? '—' }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          @if (list.hasMore()) {
            <div class="more"><button sdButton (click)="list.loadMore()" [loading]="list.loadingMore()">Load more</button></div>
          }
        }
      </div>
    </div>
  `,
  styles: [
    TABLE_STYLES,
    SECTION_STYLES,
    PAGE_STYLES,
    `
      .subject { max-width: 360px; }
      .more { display: flex; justify-content: center; margin-top: var(--sd-space-3); }
      a { color: var(--sd-primary); }
    `,
  ],
})
export class BreachesPage {
  private readonly api = inject(SlaAdminApi);
  private readonly org = inject(OrganisationApi);
  private readonly meta = inject(MetaApi);
  private readonly realtime = inject(RealtimeService);
  protected readonly tabs = SLA_TABS;
  protected readonly state = signal('All');
  protected readonly milestone = signal('');
  protected readonly department = signal<string | null>(null);
  protected readonly departments = signal<DepartmentDto[]>([]);
  protected readonly summary = signal<SlaBreachSummaryDto | null>(null);
  protected readonly prefix = computed(() => this.meta.ticketForm()?.ticketNumberPrefix ?? '#');
  protected readonly list = cursorList<SlaBreachItemDto>(
    (cursor) => this.api.breaches({ state: this.state(), milestone: this.milestone() || null, departmentId: this.department(), cursor, limit: 50 }),
    (b) => b.milestoneId,
  );

  constructor() {
    this.meta.load();
    this.org
      .departments()
      .pipe(catchError(() => of([] as DepartmentDto[])))
      .subscribe((d) => this.departments.set(d));
    const sub = merge(this.realtime.slaAlert$, this.realtime.ticketChanged$, this.realtime.reconnected$)
      .pipe(debounceTime(2000))
      .subscribe(() => this.reload());
    inject(DestroyRef).onDestroy(() => sub.unsubscribe());
    this.reload();
  }

  protected reload(): void {
    this.list.load();
    this.api.breachSummary(this.department()).subscribe({ next: (s) => this.summary.set(s) });
  }

  protected label(m: string): string {
    return MILESTONE_LABELS[m] ?? m;
  }

  protected minutes(m: number): string {
    return formatMinutes(m);
  }

  protected pct(b: SlaBreachItemDto): number {
    return b.targetMinutes > 0 ? Math.min(100, Math.round((b.elapsedMinutes / b.targetMinutes) * 100)) : loadPercent(1, 1);
  }
}
