import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { AiAdminApi, AiAgentDto, MetaApi, isApiError } from '@sd/core';
import { SdBadge, SdButton, SdCard, SdDialogService, SdEmptyState, SdRelativeTimePipe, SdSkeleton, SdToggle, ToastService } from '@sd/ui';
import { humanize } from '../../ai/shared/ai-ui';
import { AiAgentEditorDialog, AgentEditorData } from './agent-editor.dialog';
import { AiConfigStore } from './ai-config.store';

/** AI agents (triage, info collector, knowledge resolver, auto-responder): create, edit, enable/disable, delete. */
@Component({
  selector: 'desk-ai-agents',
  imports: [SdCard, SdButton, SdBadge, SdToggle, SdEmptyState, SdSkeleton, SdRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-card padding="none" title="AI agents" subtitle="Agents run when tickets arrive or customers reply. Consequential requests (refunds, contracts, account or data changes) always go to a person.">
      <div sdCardActions>
        <button sdButton variant="primary" icon="plus" (click)="edit(null)">New agent</button>
      </div>
      @if (agents(); as list) {
        @if (list.length === 0) {
          <sd-empty-state icon="bot" title="No AI agents yet" message="Create a triage agent in suggest-only mode to start receiving proposals." />
        } @else {
          <div class="table-wrap">
            <table class="grid">
              <caption class="sd-visually-hidden">AI agents</caption>
              <thead>
                <tr>
                  <th scope="col">Agent</th>
                  <th scope="col">Kind</th>
                  <th scope="col">Mode</th>
                  <th scope="col">Scope</th>
                  <th scope="col" class="num">Runs</th>
                  <th scope="col">Enabled</th>
                  <th scope="col"><span class="sd-visually-hidden">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                @for (a of list; track a.id) {
                  <tr>
                    <td>
                      <strong>{{ a.name }}</strong>
                      <p class="sd-xs sd-muted">updated {{ a.updatedAt | sdRelativeTime }}</p>
                    </td>
                    <td>{{ label(a.kind) }}</td>
                    <td>
                      <sd-badge size="sm" [tone]="a.mode === 'AutoLowRisk' ? 'warning' : 'info'">{{ a.mode === 'AutoLowRisk' ? 'Acts on low-risk' : 'Suggest only' }}</sd-badge>
                    </td>
                    <td class="sd-small">{{ scope(a) }}</td>
                    <td class="num">{{ a.runCount }}</td>
                    <td><sd-toggle [value]="a.isEnabled" (valueChange)="setEnabled(a, $event)" [ariaLabel]="'Enable ' + a.name" /></td>
                    <td class="actions">
                      <button sdButton size="sm" variant="ghost" icon="pencil" (click)="edit(a)">Edit</button>
                      <button sdButton size="sm" variant="ghost" icon="trash" iconOnly [attr.aria-label]="'Delete ' + a.name" (click)="remove(a)"></button>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      } @else {
        <div class="pad"><sd-skeleton [lines]="5" /></div>
      }
    </sd-card>
  `,
  styles: `
    .table-wrap {
      overflow: auto;
    }
    .grid {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--sd-text-sm);
    }
    .grid th,
    .grid td {
      padding: 10px 14px;
      border-top: 1px solid var(--sd-divider);
      text-align: start;
      vertical-align: middle;
    }
    .grid th {
      background: var(--sd-surface-2);
      color: var(--sd-text-2);
      font-weight: var(--sd-weight-semibold);
    }
    .num {
      text-align: end !important;
      font-variant-numeric: tabular-nums;
    }
    .actions {
      display: flex;
      gap: 4px;
      justify-content: flex-end;
      white-space: nowrap;
      min-width: 120px;
    }
    .pad {
      padding: 16px;
    }
  `,
})
export class AiAgentsPage implements OnInit {
  private readonly api = inject(AiAdminApi);
  private readonly store = inject(AiConfigStore);
  private readonly meta = inject(MetaApi);
  private readonly dialogs = inject(SdDialogService);
  private readonly toast = inject(ToastService);

  protected readonly agents = signal<AiAgentDto[] | null>(null);
  protected readonly label = humanize;

  ngOnInit(): void {
    this.meta.ticketForm$().subscribe({ error: () => undefined });
    this.load();
  }

  private load(): void {
    this.api.agents().subscribe((list) => this.agents.set(list));
  }

  protected scope(a: AiAgentDto): string {
    const depts = (a.config.departments ?? []).map((id) => this.meta.ticketForm()?.departments.find((d) => d.id === id)?.name ?? 'department');
    const parts = [depts.length ? depts.join(', ') : 'All departments', a.config.channels?.length ? a.config.channels.join(', ') : 'all channels'];
    if (a.kind === 'AutoResponder') parts.push(a.config.scenarios?.length ? `scenarios: ${a.config.scenarios.join(', ')}` : 'all approved scenarios');
    if (a.config.minConfidence !== undefined) parts.push(`≥ ${Math.round(a.config.minConfidence * 100)}%`);
    return parts.join(' · ');
  }

  protected edit(agent: AiAgentDto | null): void {
    const ref = this.dialogs.open<AiAgentDto | null, AgentEditorData, AiAgentEditorDialog>(AiAgentEditorDialog, {
      data: { agent, scenarios: this.store.config()?.autoReplyScenarios ?? [] },
      size: 'lg',
      disableClose: true,
    });
    ref.closed.subscribe((saved) => {
      if (!saved) return;
      this.toast.success(`Saved agent “${saved.name}”`);
      this.load();
    });
  }

  protected setEnabled(a: AiAgentDto, enabled: boolean): void {
    this.api
      .updateAgent(a.id, { name: a.name, kind: a.kind, mode: a.mode, isEnabled: enabled, config: a.config, instructions: a.instructions })
      .subscribe({
        next: (saved) => {
          this.agents.update((list) => (list ?? []).map((x) => (x.id === saved.id ? saved : x)));
          this.toast.success(`${saved.name} ${saved.isEnabled ? 'enabled' : 'disabled'}`);
        },
        error: (err: unknown) => {
          this.toast.error('The agent was not updated', isApiError(err) ? err.userMessage : undefined);
          this.load();
        },
      });
  }

  protected async remove(a: AiAgentDto): Promise<void> {
    const ok = await this.dialogs.confirm({ title: `Delete ${a.name}?`, message: 'Its past proposals and audit records are kept.', danger: true, confirmLabel: 'Delete' });
    if (!ok) return;
    this.api.deleteAgent(a.id).subscribe(() => {
      this.toast.success(`Deleted ${a.name}`);
      this.load();
    });
  }
}
