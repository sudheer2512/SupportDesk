import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AI_FEATURES, AiAdminApi, AiExecutionDto, AiExecutionQuery, AiExecutionStatus, AiOutcome } from '@sd/core';
import {
  SdBadge,
  SdCellDef,
  SdColumn,
  SdCombobox,
  SdDataTable,
  SdDateInput,
  SdDateTimePipe,
  SdDrawer,
  SdFormField,
  SdJsonViewer,
  SdOption,
} from '@sd/ui';
import { executionTone, humanize, money, pct } from '../../ai/shared/ai-ui';

const PAGE_SIZE = 50;

/**
 * Audit trail of every AI call: feature, provider/model, entity, user/agent, tokens, cost, latency, confidence,
 * prompt-injection flags, status and the human outcome; the detail shows the validated output and citations.
 */
@Component({
  selector: 'desk-ai-executions',
  imports: [RouterLink, SdDataTable, SdCellDef, SdBadge, SdCombobox, SdFormField, SdDateInput, SdDrawer, SdJsonViewer, SdDateTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="filters">
      <sd-form-field label="Feature">
        <sd-combobox [options]="featureOptions" [(value)]="feature" placeholder="All features" (valueChange)="reload()" />
      </sd-form-field>
      <sd-form-field label="Status">
        <sd-combobox [options]="statusOptions" [(value)]="status" placeholder="Any status" [searchable]="false" (valueChange)="reload()" />
      </sd-form-field>
      <sd-form-field label="Outcome">
        <sd-combobox [options]="outcomeOptions" [(value)]="outcome" placeholder="Any outcome" [searchable]="false" (valueChange)="reload()" />
      </sd-form-field>
      <sd-form-field label="From"><sd-date-input [(value)]="from" (valueChange)="reload()" /></sd-form-field>
      <sd-form-field label="To"><sd-date-input [(value)]="to" (valueChange)="reload()" /></sd-form-field>
    </div>
    <div class="table-host">
      <sd-data-table
        [columns]="columns"
        [rows]="rows()"
        [loading]="loading()"
        [loadingMore]="loadingMore()"
        [hasMore]="rows().length < total()"
        [total]="total()"
        (loadMore)="more()"
        (rowClick)="open($event)"
        [activeKey]="selected()?.id ?? null"
        storageKey="admin.ai.executions"
        emptyTitle="No AI calls match"
        emptyIcon="list"
        ariaLabel="AI execution log"
      >
        <ng-template sdCell="feature" [sdCellRows]="rows()" let-row>{{ label(row.feature) }}</ng-template>
        <ng-template sdCell="status" [sdCellRows]="rows()" let-row><sd-badge size="sm" [tone]="tone(row.status)" dot>{{ row.status }}</sd-badge></ng-template>
        <ng-template sdCell="flags" [sdCellRows]="rows()" let-row>
          @if (row.injectionFlags.length) {
            <sd-badge size="sm" tone="danger" icon="shield-alert">{{ row.injectionFlags.length }}</sd-badge>
          }
        </ng-template>
      </sd-data-table>
    </div>

    <sd-drawer [(open)]="drawerOpen" [title]="selected() ? label(selected()!.feature) + ' · ' + selected()!.status : 'Execution'" width="560px">
      @if (selected(); as e) {
        <dl class="facts">
          <div><dt>When</dt><dd>{{ e.createdAt | sdDateTime }}</dd></div>
          <div><dt>Provider</dt><dd>{{ e.provider }} · {{ e.model }}</dd></div>
          <div><dt>Status</dt><dd><sd-badge size="sm" [tone]="tone(e.status)" dot>{{ e.status }}</sd-badge></dd></div>
          <div><dt>Outcome</dt><dd>{{ e.outcome }}</dd></div>
          <div><dt>Confidence</dt><dd>{{ fmtPct(e.confidence) }}</dd></div>
          <div><dt>Tokens</dt><dd>{{ e.inputTokens }} in · {{ e.outputTokens }} out</dd></div>
          <div><dt>Cost</dt><dd>{{ fmtMoney(e.cost) }}</dd></div>
          <div><dt>Latency</dt><dd>{{ e.latencyMs }} ms</dd></div>
          <div><dt>Prompt version</dt><dd>{{ e.promptVersion === 0 ? 'built-in default' : 'v' + e.promptVersion }}</dd></div>
          <div><dt>By</dt><dd>{{ e.userName ?? (e.agentId ? 'AI agent' : 'system') }}</dd></div>
          <div class="wide">
            <dt>Entity</dt>
            <dd>
              @if (e.entityType === 'Ticket' && e.entityId) {
                <a [routerLink]="['/ai/tickets', e.entityId]">Ticket · AI view</a>
              } @else {
                {{ e.entityType ?? '—' }} {{ e.entityId ?? '' }}
              }
            </dd>
          </div>
          <div class="wide"><dt>Input hash</dt><dd class="mono">{{ e.inputHash || '—' }}</dd></div>
        </dl>
        <h3>Prompt-injection checks</h3>
        @if (e.injectionFlags.length) {
          <div class="flags">
            @for (f of e.injectionFlags; track f) {
              <sd-badge tone="danger" icon="shield-alert">{{ label(f) }}</sd-badge>
            }
          </div>
          <p class="sd-xs sd-muted">The content was still treated strictly as data; agents send nothing automatically when flags are present.</p>
        } @else {
          <p class="sd-small sd-muted">No injection patterns detected.</p>
        }
        @if (e.error) {
          <h3>Error</h3>
          <p class="err">{{ e.error }}</p>
        }
        @if (e.citations) {
          <h3>Citations</h3>
          <sd-json-viewer [value]="e.citations" [expandDepth]="2" ariaLabel="Citations" />
        }
        <h3>Output</h3>
        @if (e.output !== null) {
          <sd-json-viewer [value]="e.output" [expandDepth]="2" ariaLabel="Validated output" />
        } @else {
          <p class="sd-small sd-muted">No output recorded.</p>
        }
      }
    </sd-drawer>
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--sd-space-4);
    }
    .filters {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
      gap: 12px;
    }
    .table-host {
      height: min(640px, calc(100dvh - 330px));
      min-height: 360px;
    }
    .facts {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 10px 16px;
      margin: 0 0 12px;
    }
    .facts .wide {
      grid-column: 1 / -1;
    }
    dt {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    dd {
      margin: 2px 0 0;
      overflow-wrap: anywhere;
    }
    .mono {
      font-family: var(--sd-font-mono);
      font-size: var(--sd-text-xs);
    }
    h3 {
      margin: 16px 0 6px;
      font-size: var(--sd-text-sm);
    }
    .flags {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    .err {
      color: var(--sd-danger);
      font-size: var(--sd-text-sm);
    }
  `,
})
export class AiExecutionsPage implements OnInit {
  private readonly api = inject(AiAdminApi);

  protected readonly featureOptions: SdOption[] = [...AI_FEATURES, 'knowledge_index', 'provider_test'].map((f) => ({ value: f, label: humanize(f) }));
  protected readonly statusOptions: SdOption[] = ['Succeeded', 'Failed', 'Blocked', 'BudgetExceeded', 'Disabled'].map((s) => ({ value: s, label: humanize(s) }));
  protected readonly outcomeOptions: SdOption[] = ['Pending', 'Accepted', 'Applied', 'Rejected', 'Reverted', 'Expired'].map((s) => ({ value: s, label: s }));

  protected readonly feature = signal<string | string[] | null>(null);
  protected readonly status = signal<string | string[] | null>(null);
  protected readonly outcome = signal<string | string[] | null>(null);
  protected readonly from = signal<string | null>(null);
  protected readonly to = signal<string | null>(null);

  protected readonly rows = signal<AiExecutionDto[]>([]);
  protected readonly total = signal(0);
  protected readonly loading = signal(false);
  protected readonly loadingMore = signal(false);
  protected readonly selected = signal<AiExecutionDto | null>(null);
  protected readonly drawerOpen = signal(false);
  private page = 1;

  protected readonly label = humanize;
  protected readonly tone = executionTone;
  protected readonly fmtPct = pct;
  protected readonly fmtMoney = (v: number) => money(v);

  protected readonly columns: SdColumn<AiExecutionDto>[] = [
    { key: 'createdAt', header: 'When', width: 170, value: (r) => new Date(r.createdAt).toLocaleString() },
    { key: 'feature', header: 'Feature', minWidth: 150, flex: 2, locked: true },
    { key: 'provider', header: 'Provider', width: 150, value: (r) => `${r.provider} · ${r.model}` },
    { key: 'user', header: 'By', minWidth: 120, value: (r) => r.userName ?? (r.agentId ? 'AI agent' : 'system') },
    { key: 'status', header: 'Status', width: 140 },
    { key: 'outcome', header: 'Outcome', width: 100 },
    { key: 'confidence', header: 'Confidence', width: 100, align: 'end', value: (r) => pct(r.confidence) },
    { key: 'tokens', header: 'Tokens', width: 90, align: 'end', value: (r) => r.inputTokens + r.outputTokens },
    { key: 'cost', header: 'Cost', width: 90, align: 'end', value: (r) => money(r.cost) },
    { key: 'latency', header: 'Latency', width: 90, align: 'end', value: (r) => `${r.latencyMs} ms`, hiddenByDefault: true },
    { key: 'flags', header: 'Injection', width: 90 },
  ];

  ngOnInit(): void {
    this.reload();
  }

  private query(page: number): AiExecutionQuery {
    const one = (v: string | string[] | null) => (Array.isArray(v) ? (v[0] ?? null) : v);
    const to = this.to();
    return {
      feature: one(this.feature()),
      status: one(this.status()) as AiExecutionStatus | null,
      outcome: one(this.outcome()) as AiOutcome | null,
      from: this.from() ? new Date(`${this.from()}T00:00:00`).toISOString() : null,
      to: to ? new Date(new Date(`${to}T00:00:00`).getTime() + 86_400_000).toISOString() : null,
      page,
      pageSize: PAGE_SIZE,
    };
  }

  protected reload(): void {
    this.page = 1;
    this.loading.set(true);
    this.api.executions(this.query(1)).subscribe({
      next: (r) => {
        this.rows.set(r.items);
        this.total.set(r.total);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  protected more(): void {
    if (this.loadingMore() || this.rows().length >= this.total()) return;
    this.loadingMore.set(true);
    this.api.executions(this.query(this.page + 1)).subscribe({
      next: (r) => {
        this.page++;
        this.rows.update((rows) => [...rows, ...r.items.filter((i) => !rows.some((x) => x.id === i.id))]);
        this.total.set(r.total);
        this.loadingMore.set(false);
      },
      error: () => this.loadingMore.set(false),
    });
  }

  protected open(row: AiExecutionDto): void {
    this.selected.set(row);
    this.drawerOpen.set(true);
  }
}
