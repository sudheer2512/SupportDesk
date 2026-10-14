import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { AgentRefDto, ApiError, Availability, MeStore, MetaApi, RealtimeService } from '@sd/core';
import { SdAvatar, SdButton, SdChart, SdEmptyState, SdPresence, SdSkeleton } from '@sd/ui';
import type { EChartsCoreOption } from 'echarts/core';
import { debounceTime } from 'rxjs';

interface WorkloadRow {
  agent: AgentRefDto;
  availability: Availability;
  load: number | null;
}

/**
 * Team workload from GET /meta/ticket-form agents: open tickets against capacity with live availability.
 * Supervisors see their departments; agents see their teams.
 */
@Component({
  selector: 'desk-team-workload',
  imports: [SdAvatar, SdButton, SdChart, SdEmptyState, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading() && rows().length === 0) {
      <sd-skeleton [lines]="5" />
    } @else if (error()) {
      <p class="error" role="alert">{{ error() }} <button sdButton size="sm" variant="link" type="button" (click)="load()">Retry</button></p>
    } @else if (rows().length === 0) {
      <sd-empty-state icon="users" title="No colleagues to show" message="You are not a member of a team or department yet." compact />
    } @else {
      <p class="sd-small sd-muted scope">{{ scopeLabel() }} · {{ online() }} of {{ rows().length }} online</p>
      <sd-chart [options]="chart()" [height]="chartHeight()" ariaLabel="Open tickets per agent compared with capacity (also listed below)" />
      <table class="list">
        <caption class="sd-visually-hidden">Agents, availability and open tickets</caption>
        <thead>
          <tr><th scope="col">Agent</th><th scope="col">Load</th><th scope="col" class="num">Open / capacity</th></tr>
        </thead>
        <tbody>
          @for (r of rows(); track r.agent.userId) {
            <tr>
              <td>
                <span class="who">
                  <sd-avatar [name]="r.agent.name" size="xs" [presence]="presence(r.availability)" />
                  <span class="sd-truncate">{{ r.agent.name }}</span>
                  @if (r.agent.isLightAgent) {
                    <span class="sd-xs sd-muted">light</span>
                  }
                </span>
              </td>
              <td>
                <span class="bar" [class.is-high]="(r.load ?? 0) >= 0.9" [class.is-mid]="(r.load ?? 0) >= 0.7 && (r.load ?? 0) < 0.9">
                  <span [style.width.%]="r.load === null ? 0 : r.load > 1 ? 100 : r.load * 100"></span>
                </span>
              </td>
              <td class="num">{{ r.agent.openTickets }} / {{ r.agent.capacity || '∞' }}</td>
            </tr>
          }
        </tbody>
      </table>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: 8px;
    }
    .scope {
      margin: 0;
    }
    .list {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--sd-text-sm);
    }
    th {
      padding: 4px 6px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-medium);
      text-align: left;
    }
    td {
      padding: 5px 6px;
      border-top: 1px solid var(--sd-divider);
    }
    .num {
      text-align: right;
      font-variant-numeric: tabular-nums;
      white-space: nowrap;
    }
    .who {
      display: flex;
      align-items: center;
      gap: 6px;
      min-width: 0;
    }
    .bar {
      display: block;
      width: 100%;
      min-width: 60px;
      height: 6px;
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface-3);
      overflow: hidden;
    }
    .bar span {
      display: block;
      height: 100%;
      background: var(--sd-success);
    }
    .bar.is-mid span {
      background: var(--sd-warning);
    }
    .bar.is-high span {
      background: var(--sd-danger);
    }
    .error {
      color: var(--sd-danger);
    }
  `,
})
export class TeamWorkloadComponent {
  private readonly meta = inject(MetaApi);
  private readonly me = inject(MeStore);
  private readonly realtime = inject(RealtimeService);

  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  private readonly supervisor = computed(() => this.me.can('supervisor.monitor'));

  protected readonly rows = computed<WorkloadRow[]>(() => {
    const meta = this.meta.ticketForm();
    const user = this.me.user();
    if (!meta || !user) return [];
    const depts = new Set(user.departments.map((d) => d.id));
    const teams = new Set(user.teams.map((t) => t.id));
    const byTeam = !this.supervisor() && teams.size > 0;
    const live = this.realtime.agentAvailability();
    return meta.agents
      .filter((a) => (byTeam ? a.teamIds.some((t) => teams.has(t)) : a.departmentIds.some((d) => depts.has(d))))
      .map((a) => ({ agent: a, availability: live.get(a.userId) ?? a.availability, load: a.capacity > 0 ? a.openTickets / a.capacity : null }))
      .sort((x, y) => (y.load ?? 0) - (x.load ?? 0) || y.agent.openTickets - x.agent.openTickets);
  });
  protected readonly online = computed(() => this.rows().filter((r) => r.availability === 'Online').length);
  protected readonly scopeLabel = computed(() => (this.supervisor() || this.me.user()?.teams.length === 0 ? 'Your departments' : 'Your teams'));
  protected readonly chartHeight = computed(() => `${Math.max(140, Math.min(12, this.rows().length) * 26 + 50)}px`);
  protected readonly chart = computed<EChartsCoreOption>(() => {
    const rows = this.rows().slice(0, 12).reverse();
    return {
      grid: { left: 8, right: 16, top: 24, bottom: 8, containLabel: true },
      legend: { top: 0, right: 0, itemWidth: 10, itemHeight: 10 },
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
      xAxis: { type: 'value', minInterval: 1 },
      yAxis: { type: 'category', data: rows.map((r) => r.agent.name) },
      series: [
        { name: 'Open tickets', type: 'bar', data: rows.map((r) => r.agent.openTickets), barMaxWidth: 12 },
        { name: 'Capacity', type: 'bar', data: rows.map((r) => r.agent.capacity), barMaxWidth: 12 },
      ],
    };
  });

  constructor() {
    this.load();
    const sub = this.realtime.ticketChanged$.pipe(debounceTime(15_000)).subscribe(() => this.load(true));
    inject(DestroyRef).onDestroy(() => sub.unsubscribe());
  }

  load(quiet = false): void {
    if (!quiet) this.loading.set(true);
    this.meta.ticketForm$(true).subscribe({
      next: () => {
        this.loading.set(false);
        this.error.set(null);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        if (!quiet) this.error.set(ApiError.from(err).userMessage);
      },
    });
  }

  protected presence(a: Availability): SdPresence {
    return a;
  }
}
