import { ChangeDetectionStrategy, Component, computed, inject, linkedSignal, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AI_PROVIDERS, AiAdminApi, AiConfigurationDto, AiProvider, BudgetAction, ReindexResultDto, TestProviderResultDto, isApiError } from '@sd/core';
import { SdBadge, SdButton, SdCard, SdCheckbox, SdFormField, SdIcon, SdInput, SdRadio, SdRadioGroup, SdSkeleton, SdToggle, ToastService } from '@sd/ui';
import { AiMeterComponent, compact, fieldErrors, money } from '../../ai/shared/ai-ui';
import { AiConfigStore } from './ai-config.store';

interface Draft {
  isEnabled: boolean;
  provider: AiProvider;
  model: string;
  endpoint: string;
  apiKey: string;
  clearApiKey: boolean;
  embeddingProvider: AiProvider;
  embeddingModel: string;
  monthlyBudget: number;
  currency: string;
  budgetAction: BudgetAction;
  inputCostPer1K: number;
  outputCostPer1K: number;
  redactPii: boolean;
  executionRetentionDays: number;
}

const PROVIDER_INFO: Record<AiProvider, string> = {
  Local: 'Built in, offline and free. Nothing leaves your servers. Translation is not available.',
  OpenAI: 'OpenAI API (api.openai.com). Needs an API key.',
  AzureOpenAI: 'Your Azure OpenAI resource. The model is the deployment name; needs the resource endpoint and key.',
  OpenAICompatible: 'Any OpenAI-compatible server (Ollama, vLLM, LM Studio, gateways). Needs the base URL, e.g. https://llm.example.com/v1.',
  Anthropic: 'Anthropic Messages API. Needs an API key. Use Local or another provider for embeddings.',
};

function toDraft(c: AiConfigurationDto | null): Draft | null {
  if (!c) return null;
  return {
    isEnabled: c.isEnabled,
    provider: c.provider,
    model: c.model,
    endpoint: c.endpoint ?? '',
    apiKey: '',
    clearApiKey: false,
    embeddingProvider: c.embeddingProvider,
    embeddingModel: c.embeddingModel,
    monthlyBudget: c.monthlyBudget,
    currency: c.currency,
    budgetAction: c.budgetAction,
    inputCostPer1K: c.inputCostPer1K,
    outputCostPer1K: c.outputCostPer1K,
    redactPii: c.redactPii,
    executionRetentionDays: c.executionRetentionDays,
  };
}

/** Provider, model, endpoint and write-only API key; embeddings and reindexing; budget, costs, privacy and retention. */
@Component({
  selector: 'desk-ai-settings',
  imports: [FormsModule, SdCard, SdButton, SdBadge, SdIcon, SdFormField, SdInput, SdToggle, SdCheckbox, SdRadioGroup, SdRadio, SdSkeleton, AiMeterComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (draft(); as d) {
      <form class="grid" (ngSubmit)="save()">
        <sd-card title="Model provider" subtitle="Every AI call goes through the gateway: permissions, budget, PII redaction and an audit record." class="wide">
          <div sdCardActions>
            <sd-toggle [value]="d.isEnabled" (valueChange)="patch({ isEnabled: $event })">AI enabled</sd-toggle>
          </div>
          <sd-radio-group [value]="d.provider" (valueChange)="setProvider($any($event))" ariaLabel="Provider" class="providers">
            @for (p of providers; track p) {
              <sd-radio [value]="p" [description]="info[p]">{{ p === 'Local' ? 'Local (built-in)' : p === 'OpenAICompatible' ? 'OpenAI-compatible' : p === 'AzureOpenAI' ? 'Azure OpenAI' : p }}</sd-radio>
            }
          </sd-radio-group>
          @if (d.provider !== 'Local') {
            <div class="fields">
              <sd-form-field [label]="d.provider === 'AzureOpenAI' ? 'Deployment name' : 'Model'" [error]="errors()['model']" required>
                <input sdInput name="model" [ngModel]="d.model" (ngModelChange)="patch({ model: $event })" placeholder="e.g. gpt-4o-mini" autocomplete="off" />
              </sd-form-field>
              <sd-form-field label="Endpoint" [error]="errors()['endpoint']" [hint]="endpointHint()" [required]="d.provider === 'OpenAICompatible' || d.provider === 'AzureOpenAI'" [optional]="d.provider === 'OpenAI' || d.provider === 'Anthropic'">
                <input sdInput name="endpoint" type="url" [ngModel]="d.endpoint" (ngModelChange)="patch({ endpoint: $event })" placeholder="https://…" autocomplete="off" />
              </sd-form-field>
              <sd-form-field label="API key" [error]="errors()['apiKey']" [hint]="config()?.hasApiKey ? 'A key is stored (never shown). Type a new one to replace it.' : 'Stored encrypted; it can never be read back.'">
                <input sdInput name="apiKey" type="password" [ngModel]="d.apiKey" (ngModelChange)="patch({ apiKey: $event, clearApiKey: false })" [placeholder]="config()?.hasApiKey ? '•••••••• stored' : 'Paste the API key'" autocomplete="new-password" />
              </sd-form-field>
              @if (config()?.hasApiKey) {
                <sd-checkbox [value]="d.clearApiKey" (valueChange)="patch({ clearApiKey: $event, apiKey: '' })">Remove the stored key</sd-checkbox>
              }
            </div>
          } @else {
            <p class="local sd-small"><sd-icon name="shield-check" [size]="16" />Runs offline: naive-Bayes classification trained on your tickets, extractive summaries, lexicon sentiment, grounded template replies and rule-based analytics.</p>
          }
          <div class="row">
            <button sdButton type="button" icon="zap" [loading]="testing()" (click)="test()">Test saved provider</button>
            @if (testResult(); as t) {
              <sd-badge [tone]="t.success ? 'success' : 'danger'" [icon]="t.success ? 'circle-check' : 'circle-x'">
                {{ t.success ? 'Chat OK · ' + t.latencyMs + ' ms' : 'Chat failed' }}
              </sd-badge>
              <sd-badge [tone]="t.embeddingError ? 'danger' : 'success'">{{ t.embeddingError ? 'Embeddings failed' : 'Embeddings OK · ' + t.embeddingDimensions + ' dims' }}</sd-badge>
            }
          </div>
          @if (testResult(); as t) {
            <p class="sd-small result">{{ t.provider }} · {{ t.model }} — {{ t.output ?? t.error }}</p>
            @if (t.embeddingError) {
              <p class="sd-small err">{{ t.embeddingError }}</p>
            }
          }
        </sd-card>

        <sd-card title="Embeddings & knowledge index" subtitle="Used for semantic search, similar tickets, duplicates and grounded answers.">
          <sd-form-field label="Embedding provider" [error]="errors()['embeddingProvider']">
            <select sdInput name="embeddingProvider" [ngModel]="d.embeddingProvider" (ngModelChange)="patch({ embeddingProvider: $event })">
              <option value="Local">Local (feature hashing, 384 dims)</option>
              @if (d.provider !== 'Local' && d.provider !== 'Anthropic') {
                <option [value]="d.provider">{{ d.provider }} (same endpoint and key)</option>
              }
              @if (d.provider === 'Local') {
                <option value="OpenAI">OpenAI</option>
                <option value="AzureOpenAI">Azure OpenAI</option>
                <option value="OpenAICompatible">OpenAI-compatible</option>
              }
            </select>
          </sd-form-field>
          @if (d.embeddingProvider !== 'Local') {
            <sd-form-field label="Embedding model" [error]="errors()['embeddingModel']" hint="Changing the model re-embeds the whole index in the background.">
              <input sdInput name="embeddingModel" [ngModel]="d.embeddingModel" (ngModelChange)="patch({ embeddingModel: $event })" placeholder="e.g. text-embedding-3-small" />
            </sd-form-field>
          }
          <div class="row">
            <button sdButton type="button" icon="refresh" [loading]="reindexing()" (click)="reindex()">Reindex knowledge now</button>
          </div>
          @if (reindexResult(); as r) {
            <p class="sd-small result">Indexed {{ r.articles }} article(s) and {{ r.tickets }} resolved ticket(s), re-embedded {{ r.reembedded }} chunk(s), removed {{ r.removed }}.</p>
          }
        </sd-card>

        <sd-card title="Budget & cost" subtitle="Applies to paid (external) providers; the built-in provider is free.">
          @if (config(); as c) {
            <div class="usage">
              <span class="sd-small">Spent this month: <strong>{{ fmtMoney(c.spentThisMonth, c.currency) }}</strong> of {{ fmtMoney(c.monthlyBudget, c.currency) }}</span>
              <desk-ai-meter [value]="c.monthlyBudget > 0 ? c.spentThisMonth / c.monthlyBudget : 0" label="Budget used" />
              <span class="sd-small">Metered tokens this month (external providers): {{ fmtCompact(c.tokensThisMonth) }}{{ c.tokenAllowance ? ' of ' + fmtCompact(c.tokenAllowance) + ' in your plan' : ' (no plan limit)' }}</span>
            </div>
          }
          <div class="fields two">
            <sd-form-field label="Monthly budget" [error]="errors()['monthlyBudget']" hint="0 = no budget limit">
              <input sdInput name="budget" type="number" min="0" step="1" [ngModel]="d.monthlyBudget" (ngModelChange)="patch({ monthlyBudget: +$event })" />
            </sd-form-field>
            <sd-form-field label="Currency" [error]="errors()['currency']">
              <input sdInput name="currency" maxlength="3" [ngModel]="d.currency" (ngModelChange)="patch({ currency: $event })" />
            </sd-form-field>
            <sd-form-field label="Input cost per 1K tokens" [error]="errors()['inputCostPer1K']">
              <input sdInput name="inCost" type="number" min="0" step="0.0001" [ngModel]="d.inputCostPer1K" (ngModelChange)="patch({ inputCostPer1K: +$event })" />
            </sd-form-field>
            <sd-form-field label="Output cost per 1K tokens" [error]="errors()['outputCostPer1K']">
              <input sdInput name="outCost" type="number" min="0" step="0.0001" [ngModel]="d.outputCostPer1K" (ngModelChange)="patch({ outputCostPer1K: +$event })" />
            </sd-form-field>
          </div>
          <sd-radio-group [value]="d.budgetAction" (valueChange)="patch({ budgetAction: $any($event) })" ariaLabel="When the budget is used up" direction="row">
            <sd-radio value="Block" description="Refuse paid AI calls until next month">Block</sd-radio>
            <sd-radio value="Warn" description="Keep working and notify AI administrators once">Warn</sd-radio>
          </sd-radio-group>
        </sd-card>

        <sd-card title="Privacy & retention">
          <sd-toggle [value]="d.redactPii" (valueChange)="patch({ redactPii: $event })">Redact personal data before calling external providers</sd-toggle>
          <p class="sd-xs sd-muted indent">E-mail addresses, phone numbers, payment-card, Aadhaar- and PAN-like numbers are replaced with placeholders; card and ID numbers are never restored in answers.</p>
          <sd-form-field label="Keep the AI execution log for (days)" [error]="errors()['executionRetentionDays']">
            <input sdInput name="retention" type="number" min="7" max="3650" [ngModel]="d.executionRetentionDays" (ngModelChange)="patch({ executionRetentionDays: +$event })" />
          </sd-form-field>
        </sd-card>

        <div class="bar wide">
          @if (dirty()) {
            <span class="sd-small sd-muted">Unsaved changes</span>
            <button sdButton type="button" variant="ghost" (click)="discard()">Discard</button>
          }
          <button sdButton type="submit" variant="primary" icon="check" [loading]="saving()" [disabled]="!dirty()">Save settings</button>
        </div>
      </form>
    } @else {
      <sd-card><sd-skeleton [lines]="10" /></sd-card>
    }
  `,
  styles: `
    .grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 460px), 1fr));
      gap: var(--sd-space-4);
    }
    .wide {
      grid-column: 1 / -1;
    }
    .providers {
      display: block;
      margin-bottom: 14px;
    }
    .fields {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 260px), 1fr));
      gap: 12px;
      align-items: start;
      margin-bottom: 12px;
    }
    .fields.two {
      margin-top: 12px;
    }
    .row {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      align-items: center;
      margin-top: 8px;
    }
    .local {
      display: flex;
      gap: 8px;
      align-items: center;
      color: var(--sd-text-2);
    }
    .result {
      margin-top: 8px;
      color: var(--sd-text-2);
    }
    .err {
      color: var(--sd-danger);
    }
    .usage {
      display: grid;
      gap: 6px;
      padding: 10px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface-2);
    }
    .indent {
      margin: 4px 0 12px 44px;
    }
    .bar {
      position: sticky;
      bottom: 0;
      display: flex;
      justify-content: flex-end;
      align-items: center;
      gap: 10px;
      padding: 12px 0;
      background: var(--sd-bg);
    }
  `,
})
export class AiSettingsPage {
  private readonly store = inject(AiConfigStore);
  private readonly api = inject(AiAdminApi);
  private readonly toast = inject(ToastService);

  protected readonly providers = AI_PROVIDERS;
  protected readonly info = PROVIDER_INFO;
  protected readonly config = this.store.config;
  protected readonly draft = linkedSignal<Draft | null>(() => toDraft(this.store.config()));
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly saving = signal(false);
  protected readonly testing = signal(false);
  protected readonly reindexing = signal(false);
  protected readonly testResult = signal<TestProviderResultDto | null>(null);
  protected readonly reindexResult = signal<ReindexResultDto | null>(null);
  protected readonly fmtMoney = money;
  protected readonly fmtCompact = compact;

  protected readonly dirty = computed(() => {
    const d = this.draft();
    const base = toDraft(this.store.config());
    return !!d && !!base && JSON.stringify(d) !== JSON.stringify(base);
  });

  protected readonly endpointHint = computed(() => {
    switch (this.draft()?.provider) {
      case 'OpenAI':
        return 'Leave empty for https://api.openai.com/v1.';
      case 'AzureOpenAI':
        return 'Resource URL, e.g. https://my-resource.openai.azure.com.';
      case 'Anthropic':
        return 'Leave empty for https://api.anthropic.com.';
      default:
        return 'Base URL that serves /chat/completions and /embeddings.';
    }
  });

  protected patch(change: Partial<Draft>): void {
    this.draft.update((d) => (d ? { ...d, ...change } : d));
  }

  protected setProvider(provider: AiProvider): void {
    const d = this.draft();
    if (!d) return;
    // A remote embedding provider must match the chat provider (shared endpoint and key).
    const embeddingProvider = d.embeddingProvider === 'Local' || provider === 'Local' || provider === 'Anthropic' ? 'Local' : provider;
    this.patch({ provider, embeddingProvider, model: provider === 'Local' ? 'local-heuristic-v1' : d.model === 'local-heuristic-v1' ? '' : d.model });
  }

  protected discard(): void {
    this.draft.set(toDraft(this.store.config()));
    this.errors.set({});
  }

  protected async save(): Promise<void> {
    const d = this.draft();
    if (!d || this.saving()) return;
    this.saving.set(true);
    this.errors.set({});
    try {
      await this.store.save({
        isEnabled: d.isEnabled,
        provider: d.provider,
        model: d.model,
        endpoint: d.endpoint.trim(),
        apiKey: d.apiKey.trim() || null,
        clearApiKey: d.clearApiKey,
        embeddingProvider: d.embeddingProvider,
        embeddingModel: d.embeddingModel,
        monthlyBudget: d.monthlyBudget,
        currency: d.currency,
        budgetAction: d.budgetAction,
        inputCostPer1K: d.inputCostPer1K,
        outputCostPer1K: d.outputCostPer1K,
        redactPii: d.redactPii,
        executionRetentionDays: d.executionRetentionDays,
      });
      this.testResult.set(null);
    } catch (err: unknown) {
      if (isApiError(err) && err.code === 'validation_failed') {
        this.errors.set(fieldErrors(err));
        this.toast.warning('Check the highlighted fields', err.userMessage);
      }
    } finally {
      this.saving.set(false);
    }
  }

  protected test(): void {
    this.testing.set(true);
    this.api.testProvider().subscribe({
      next: (r) => {
        this.testResult.set(r);
        this.testing.set(false);
      },
      error: () => this.testing.set(false),
    });
  }

  protected reindex(): void {
    this.reindexing.set(true);
    this.api.reindex().subscribe({
      next: (r) => {
        this.reindexResult.set(r);
        this.reindexing.set(false);
        this.toast.success('Knowledge index updated');
      },
      error: () => this.reindexing.set(false),
    });
  }
}
