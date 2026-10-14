import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, OnInit, WritableSignal, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AI_AGENT_ACTIONS, AI_AGENT_KINDS, AI_INFO_ITEMS, AiAdminApi, AiAgentDto, AiAgentKind, AiAgentMode, AutoReplyScenarioDto, MetaApi, SaveAiAgentRequest, isApiError } from '@sd/core';
import { SdButton, SdCheckbox, SdCombobox, SdDialog, SdFormField, SdInput, SdOption, SdRadio, SdRadioGroup, SdToggle } from '@sd/ui';
import { fieldErrors, humanize } from '../../ai/shared/ai-ui';

export interface AgentEditorData {
  agent: AiAgentDto | null;
  scenarios: AutoReplyScenarioDto[];
}

const KIND_INFO: Record<AiAgentKind, string> = {
  Triage: 'Classifies new tickets: category, product, department, priority and tags.',
  InfoCollector: 'Spots missing version, environment, steps or error details and drafts a request to the customer.',
  KnowledgeResolver: 'Drafts a resolution grounded in published knowledge and similar resolved tickets.',
  AutoResponder: 'Replies automatically — only inside approved low-risk scenarios, above the threshold, on allowed channels.',
};

/** Create/edit an AI agent: kind, mode, scope (departments, channels), confidence, allowed actions and instructions. */
@Component({
  selector: 'desk-ai-agent-editor',
  imports: [FormsModule, SdDialog, SdButton, SdFormField, SdInput, SdCombobox, SdCheckbox, SdToggle, SdRadioGroup, SdRadio],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="data.agent ? 'Edit ' + data.agent.name : 'New AI agent'" description="Agents propose actions; only low-risk internal changes (or approved auto-reply scenarios) happen without a person.">
      <form id="agent-form" class="form" (ngSubmit)="save()">
        <sd-form-field label="Name" required [error]="errors()['name']">
          <input sdInput name="name" [(ngModel)]="name" maxlength="100" />
        </sd-form-field>
        <sd-form-field label="Kind" [error]="errors()['kind']" [hint]="kindInfo[kind()]">
          <select sdInput name="kind" [ngModel]="kind()" (ngModelChange)="kind.set($event)" [disabled]="!!data.agent">
            @for (k of kinds; track k) {
              <option [value]="k">{{ label(k) }}</option>
            }
          </select>
        </sd-form-field>
        <sd-radio-group [value]="mode()" (valueChange)="mode.set($any($event))" ariaLabel="Mode" class="span2">
          <sd-radio value="SuggestOnly" description="Everything becomes a proposal for a person to approve.">Suggest only</sd-radio>
          <sd-radio value="AutoLowRisk" [description]="kind() === 'AutoResponder' ? 'Replies automatically inside approved scenarios; everything else is proposed.' : 'Applies low-risk internal changes above the threshold (fills empty fields, adds tags); the rest is proposed.'">
            Act on low-risk
          </sd-radio>
        </sd-radio-group>
        <sd-form-field label="Departments" hint="Empty = all departments" [error]="errors()['config.departments']">
          <sd-combobox multiple [options]="departmentOptions()" [(value)]="departments" placeholder="All departments" />
        </sd-form-field>
        <sd-form-field label="Channels" hint="Empty = all channels" [error]="errors()['config.channels']">
          <sd-combobox multiple [options]="channelOptions()" [(value)]="channels" placeholder="All channels" />
        </sd-form-field>
        <sd-form-field label="Minimum confidence" hint="Defaults to the agents feature setting">
          <input sdInput name="min" type="number" min="0" max="1" step="0.05" [(ngModel)]="minConfidence" placeholder="0.75" />
        </sd-form-field>
        @if (kind() === 'AutoResponder') {
          <sd-form-field label="Max automatic replies per ticket">
            <input sdInput name="max" type="number" min="0" max="5" [(ngModel)]="maxReplies" />
          </sd-form-field>
          <sd-form-field label="Approved scenarios" class="span2" [error]="errors()['config.scenarios']" hint="Empty = every enabled scenario in AI settings">
            <sd-combobox multiple [options]="scenarioOptions" [(value)]="scenarios" placeholder="All approved scenarios" />
          </sd-form-field>
        }
        @if (kind() === 'InfoCollector') {
          <fieldset class="span2">
            <legend>Information to ask for</legend>
            <div class="checks">
              @for (item of infoItems; track item) {
                <sd-checkbox [value]="requiredInfo().includes(item)" (valueChange)="toggle(requiredInfo, item, $event)">{{ label(item) }}</sd-checkbox>
              }
            </div>
          </fieldset>
        }
        <fieldset class="span2">
          <legend>Allowed actions <span class="sd-muted sd-xs">(none ticked = all)</span></legend>
          <div class="checks">
            @for (a of actions; track a) {
              <sd-checkbox [value]="allowed().includes(a)" (valueChange)="toggle(allowed, a, $event)">{{ label(a) }}</sd-checkbox>
            }
          </div>
          @if (errors()['config.allowedActions']; as e) {
            <p class="err">{{ e }}</p>
          }
        </fieldset>
        <sd-form-field label="Instructions" class="span2" hint="Guidance for remote models (tone, what to mention). The built-in provider uses fixed, reviewed wording.">
          <textarea sdInput name="instructions" rows="3" [(ngModel)]="instructions" maxlength="2000"></textarea>
        </sd-form-field>
        <sd-toggle class="span2" [(value)]="enabled">Enabled</sd-toggle>
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close(null)">Cancel</button>
        <button sdButton type="submit" form="agent-form" variant="primary" [loading]="saving()" [disabled]="!name.trim()">Save agent</button>
      </div>
    </sd-dialog>
  `,
  styles: `
    .form {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 12px;
      /* Long forms scroll inside the dialog so the footer buttons stay on screen. */
      max-height: calc(100dvh - 240px);
      overflow: auto;
      padding: 2px 4px 2px 2px;
    }
    .span2 {
      grid-column: 1 / -1;
    }
    fieldset {
      margin: 0;
      padding: 0;
      border: 0;
    }
    legend {
      margin-bottom: 6px;
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-medium);
    }
    .checks {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(170px, 1fr));
      gap: 6px 12px;
    }
    .err {
      color: var(--sd-danger);
      font-size: var(--sd-text-sm);
    }
    @media (max-width: 640px) {
      .form {
        grid-template-columns: 1fr;
      }
    }
  `,
})
export class AiAgentEditorDialog implements OnInit {
  protected readonly data = inject<AgentEditorData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<AiAgentDto | null>>(DialogRef);
  private readonly api = inject(AiAdminApi);
  private readonly meta = inject(MetaApi);

  protected readonly kinds = AI_AGENT_KINDS;
  protected readonly kindInfo = KIND_INFO;
  protected readonly actions = AI_AGENT_ACTIONS;
  protected readonly infoItems = AI_INFO_ITEMS;
  protected readonly label = humanize;

  private readonly agent = this.data.agent;
  protected name = this.agent?.name ?? '';
  protected readonly kind = signal<AiAgentKind>(this.agent?.kind ?? 'Triage');
  protected readonly mode = signal<AiAgentMode>(this.agent?.mode ?? 'SuggestOnly');
  protected readonly enabled = signal(this.agent?.isEnabled ?? true);
  protected readonly departments = signal<string | string[] | null>(this.agent?.config.departments ?? []);
  protected readonly channels = signal<string | string[] | null>(this.agent?.config.channels ?? []);
  protected readonly scenarios = signal<string | string[] | null>(this.agent?.config.scenarios ?? []);
  protected readonly allowed = signal<string[]>(this.agent?.config.allowedActions ?? []);
  protected readonly requiredInfo = signal<string[]>(this.agent?.config.requiredInfo ?? ['version', 'environment', 'steps', 'error_message']);
  protected minConfidence: number | null = this.agent?.config.minConfidence ?? null;
  protected maxReplies = this.agent?.config.maxRepliesPerTicket ?? 1;
  protected instructions = this.agent?.instructions ?? '';
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly saving = signal(false);

  protected readonly scenarioOptions: SdOption[] = this.data.scenarios.map((s) => ({ value: s.key, label: s.name, description: s.enabled ? null : 'disabled' }));
  protected readonly departmentOptions = computed<SdOption[]>(() => (this.meta.ticketForm()?.departments ?? []).map((d) => ({ value: d.id, label: d.name })));
  protected readonly channelOptions = computed<SdOption[]>(() => (this.meta.ticketForm()?.channels ?? []).map((c) => ({ value: c, label: c })));

  ngOnInit(): void {
    this.meta.ticketForm$().subscribe({ error: () => undefined });
  }

  protected toggle(list: WritableSignal<string[]>, value: string, on: boolean): void {
    list.update((l) => (on ? [...new Set([...l, value])] : l.filter((x) => x !== value)));
  }

  private asList(v: string | string[] | null): string[] {
    return v === null ? [] : Array.isArray(v) ? v : [v];
  }

  protected save(): void {
    if (!this.name.trim() || this.saving()) return;
    const kind = this.kind();
    const config: SaveAiAgentRequest['config'] = {
      departments: this.asList(this.departments()),
      channels: this.asList(this.channels()),
      allowedActions: this.allowed(),
      ...(this.minConfidence !== null && `${this.minConfidence}` !== '' ? { minConfidence: Math.min(1, Math.max(0, Number(this.minConfidence))) } : {}),
      ...(kind === 'AutoResponder' ? { scenarios: this.asList(this.scenarios()), maxRepliesPerTicket: Number(this.maxReplies) || 1 } : {}),
      ...(kind === 'InfoCollector' ? { requiredInfo: this.requiredInfo() } : {}),
    };
    const request: SaveAiAgentRequest = {
      name: this.name.trim(),
      kind,
      mode: this.mode(),
      isEnabled: this.enabled(),
      config,
      instructions: this.instructions.trim() || null,
    };
    this.saving.set(true);
    this.errors.set({});
    const call = this.agent ? this.api.updateAgent(this.agent.id, request) : this.api.createAgent(request);
    call.subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.ref.close(saved);
      },
      error: (err: unknown) => {
        this.saving.set(false);
        const fe = fieldErrors(err);
        this.errors.set(Object.keys(fe).length ? fe : { name: isApiError(err) ? err.userMessage : 'The agent was not saved.' });
      },
    });
  }
}
