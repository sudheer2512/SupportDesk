import { ChangeDetectionStrategy, Component, computed, inject, linkedSignal, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AI_FEATURES, AiFeatureSettingDto, isApiError } from '@sd/core';
import { SdBadge, SdButton, SdCard, SdChipsInput, SdSkeleton, SdToggle, ToastService } from '@sd/ui';
import { fieldErrors, humanize } from '../../ai/shared/ai-ui';
import { AiConfigStore } from './ai-config.store';

const DESCRIPTIONS: Record<string, string> = {
  classification: 'Suggest category, product, priority and department. Auto-apply fills empty fields on new tickets above the confidence threshold.',
  summarization: 'Conversation summary, current status and pending actions.',
  reply_suggestion: 'Reply drafts grounded in published knowledge and similar resolved tickets, with citations.',
  semantic_search: 'Search by meaning; suggested articles and similar tickets in the workbench.',
  sentiment: 'Sentiment of every inbound customer message; frustrated customers are flagged.',
  duplicate_detection: 'Similar open tickets from the same contact or customer.',
  root_cause: 'Likely causes from similar resolved tickets.',
  knowledge_generation: 'Draft troubleshooting articles from resolved tickets.',
  translation: 'Translate text with the configured external model.',
  resolution_prediction: 'Expected resolution time from comparable historical tickets.',
  anomaly_detection: 'Hourly detection of unusual ticket volume; supervisors are notified.',
  nl_analytics: 'Answer analytics questions asked in plain language.',
  agents: 'Autonomous AI agents (triage, info collector, knowledge resolver, auto-responder).',
  portal_assistant: 'Help-center assistant and chat bot answering from customer-visible knowledge.',
};

/** Per-feature switches: enabled, role restriction, auto-apply and minimum confidence. */
@Component({
  selector: 'desk-ai-features',
  imports: [FormsModule, SdCard, SdButton, SdBadge, SdToggle, SdChipsInput, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (rows(); as list) {
      <sd-card padding="none" title="AI features" subtitle="Turn features on or off, limit them to roles and set how sure the AI must be before acting.">
        <div sdCardActions>
          <button sdButton variant="primary" icon="check" [loading]="saving()" [disabled]="!dirty()" (click)="save()">Save</button>
        </div>
        <div class="table-wrap">
          <table class="grid">
            <caption class="sd-visually-hidden">AI feature settings</caption>
            <thead>
              <tr>
                <th scope="col">Feature</th>
                <th scope="col">Enabled</th>
                <th scope="col">Limit to roles</th>
                <th scope="col">Min. confidence</th>
                <th scope="col">Auto-apply</th>
              </tr>
            </thead>
            <tbody>
              @for (f of list; track f.feature; let i = $index) {
                <tr [class.off]="!f.enabled">
                  <td class="name">
                    <strong>{{ label(f.feature) }}</strong>
                    <p class="sd-xs sd-muted">{{ describe(f.feature) }}</p>
                    @if (errors()['features.' + f.feature + '.roles']; as e) {
                      <p class="err sd-xs">{{ e }}</p>
                    }
                  </td>
                  <td><sd-toggle [value]="f.enabled" (valueChange)="update(i, { enabled: $event })" [ariaLabel]="'Enable ' + label(f.feature)" /></td>
                  <td class="roles">
                    <sd-chips-input [value]="f.roles" (valueChange)="update(i, { roles: $event })" [suggestions]="roleSuggestions" placeholder="All roles" [ariaLabel]="'Roles for ' + label(f.feature)" />
                  </td>
                  <td>
                    <input class="sd-input sd-input-sm conf" type="number" min="0" max="1" step="0.05" [ngModel]="f.minConfidence" (ngModelChange)="update(i, { minConfidence: +$event })" [attr.aria-label]="'Minimum confidence for ' + label(f.feature)" />
                  </td>
                  <td>
                    @if (f.feature === 'classification') {
                      <sd-toggle [value]="f.autoApply" (valueChange)="update(i, { autoApply: $event })" ariaLabel="Auto-apply classification" />
                    } @else {
                      <sd-badge size="sm" variant="outline">n/a</sd-badge>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </sd-card>
      <p class="sd-xs sd-muted note">
        Role limits apply to people using the feature interactively. Agents' automatic actions are controlled per agent; consequential actions always need approval.
      </p>
    } @else {
      <sd-card><sd-skeleton [lines]="10" /></sd-card>
    }
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
    .name {
      max-width: 380px;
    }
    .roles {
      min-width: 220px;
    }
    .conf {
      width: 90px;
    }
    tr.off .name strong {
      color: var(--sd-text-3);
    }
    .err {
      color: var(--sd-danger);
    }
    .note {
      margin-top: 10px;
    }
  `,
})
export class AiFeaturesPage {
  private readonly store = inject(AiConfigStore);
  private readonly toast = inject(ToastService);

  protected readonly roleSuggestions = ['Administrator', 'Supervisor', 'Agent', 'Light Agent', 'Knowledge Manager', 'Analyst'];
  protected readonly rows = linkedSignal<AiFeatureSettingDto[] | null>(() => {
    const c = this.store.config();
    if (!c) return null;
    return AI_FEATURES.map((f) => c.features.find((x) => x.feature === f) ?? { feature: f, enabled: true, roles: [], autoApply: false, minConfidence: 0.75 });
  });
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly saving = signal(false);
  protected readonly label = humanize;
  protected readonly dirty = computed(() => {
    const c = this.store.config();
    const rows = this.rows();
    if (!c || !rows) return false;
    return rows.some((r) => {
      const o = c.features.find((x) => x.feature === r.feature);
      return !o || JSON.stringify(o) !== JSON.stringify(r);
    });
  });

  protected describe(feature: string): string {
    return DESCRIPTIONS[feature] ?? '';
  }

  protected update(index: number, change: Partial<AiFeatureSettingDto>): void {
    this.rows.update((list) => (list ? list.map((r, i) => (i === index ? { ...r, ...change } : r)) : list));
  }

  protected async save(): Promise<void> {
    const rows = this.rows();
    if (!rows) return;
    this.saving.set(true);
    this.errors.set({});
    try {
      await this.store.save({ features: rows.map((r) => ({ ...r, minConfidence: Math.min(1, Math.max(0, r.minConfidence)) })) }, 'AI features saved');
    } catch (err: unknown) {
      if (isApiError(err) && err.code === 'validation_failed') {
        this.errors.set(fieldErrors(err));
        this.toast.warning('Some features need attention', err.userMessage);
      }
    } finally {
      this.saving.set(false);
    }
  }
}
