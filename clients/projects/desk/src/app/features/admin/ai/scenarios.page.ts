import { ChangeDetectionStrategy, Component, OnInit, computed, inject, linkedSignal, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AutoReplyScenarioDto, MetaApi, isApiError } from '@sd/core';
import { SdButton, SdCard, SdChipsInput, SdCombobox, SdDialogService, SdEmptyState, SdFormField, SdInput, SdOption, SdSkeleton, SdToggle, ToastService } from '@sd/ui';
import { fieldErrors } from '../../ai/shared/ai-ui';
import { AiConfigStore } from './ai-config.store';

function emptyScenario(): AutoReplyScenarioDto {
  return { key: '', name: '', description: '', keywords: [], categoryIds: [], articleIds: [], channels: ['Email', 'Portal'], minConfidence: 0.8, enabled: true };
}

/**
 * Explicitly approved low-risk scenarios in which the auto-responder agent may reply without a person (e.g.
 * "password reset how-to"). Anything outside them — or below the confidence threshold, on another channel, about
 * money/contracts/accounts, or from a frustrated customer — becomes a proposal for review instead.
 */
@Component({
  selector: 'desk-ai-scenarios',
  imports: [FormsModule, SdCard, SdButton, SdFormField, SdInput, SdChipsInput, SdCombobox, SdToggle, SdEmptyState, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (scenarios(); as list) {
      <div class="head">
        <p class="sd-small sd-muted">
          The auto-responder only answers inside these scenarios, from published customer-visible articles, above the confidence threshold and on the listed channels.
        </p>
        <div class="actions">
          <button sdButton icon="plus" (click)="add()">Add scenario</button>
          <button sdButton variant="primary" icon="check" [loading]="saving()" [disabled]="!dirty()" (click)="save()">Save scenarios</button>
        </div>
      </div>
      @if (errors()['autoReplyScenarios']; as e) {
        <p class="err" role="alert">{{ e }}</p>
      }
      @if (list.length === 0) {
        <sd-card><sd-empty-state icon="send" title="No approved scenarios" message="Without a scenario the auto-responder never replies on its own; it only proposes replies for review." /></sd-card>
      }
      <div class="list">
        @for (s of list; track $index; let i = $index) {
          <sd-card [title]="s.name || 'New scenario'" [subtitle]="s.key ? 'Key: ' + s.key : null">
            <div sdCardActions>
              <sd-toggle [value]="s.enabled" (valueChange)="update(i, { enabled: $event })">Enabled</sd-toggle>
              <button sdButton size="sm" variant="ghost" icon="trash" iconOnly [attr.aria-label]="'Remove ' + (s.name || 'scenario')" (click)="remove(i)"></button>
            </div>
            <div class="fields">
              <sd-form-field label="Name" required [error]="errors()['autoReplyScenarios.' + s.key + '.name']">
                <input sdInput [ngModel]="s.name" (ngModelChange)="update(i, { name: $event })" placeholder="Password reset how-to" />
              </sd-form-field>
              <sd-form-field label="Key" hint="Stable id agents refer to (letters, digits, _)">
                <input sdInput [ngModel]="s.key" (ngModelChange)="update(i, { key: $event })" placeholder="password_reset_howto" />
              </sd-form-field>
              <sd-form-field label="Description" class="span2" hint="What the customer is asking; also used for semantic matching.">
                <input sdInput [ngModel]="s.description ?? ''" (ngModelChange)="update(i, { description: $event })" />
              </sd-form-field>
              <sd-form-field label="Keywords" class="span2" hint="Phrases that identify the scenario, e.g. “reset password”.">
                <sd-chips-input [value]="s.keywords" (valueChange)="update(i, { keywords: $event })" placeholder="Add a phrase…" />
              </sd-form-field>
              <sd-form-field label="Channels" [error]="errors()['autoReplyScenarios.' + s.key + '.channels']">
                <sd-combobox multiple [options]="channelOptions()" [value]="s.channels" (valueChange)="update(i, { channels: asList($event) })" placeholder="Any channel" />
              </sd-form-field>
              <sd-form-field label="Categories (optional)">
                <sd-combobox multiple [options]="categoryOptions()" [value]="s.categoryIds" (valueChange)="update(i, { categoryIds: asList($event) })" placeholder="Any category" />
              </sd-form-field>
              <sd-form-field label="Minimum confidence" hint="0.5 – 1">
                <input sdInput type="number" min="0.5" max="1" step="0.05" [ngModel]="s.minConfidence" (ngModelChange)="update(i, { minConfidence: +$event })" />
              </sd-form-field>
              <sd-form-field label="Approved article ids (optional)" hint="Replies must cite one of these articles.">
                <sd-chips-input [value]="s.articleIds" (valueChange)="update(i, { articleIds: $event })" placeholder="Article id…" />
              </sd-form-field>
            </div>
          </sd-card>
        }
      </div>
    } @else {
      <sd-card><sd-skeleton [lines]="8" /></sd-card>
    }
  `,
  styles: `
    .head {
      display: flex;
      flex-wrap: wrap;
      justify-content: space-between;
      align-items: center;
      gap: 12px;
      margin-bottom: 12px;
    }
    .head p {
      max-width: 720px;
    }
    .actions {
      display: flex;
      gap: 8px;
    }
    .list {
      display: grid;
      gap: var(--sd-space-4);
    }
    .fields {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 260px), 1fr));
      gap: 12px;
    }
    .span2 {
      grid-column: span 2;
    }
    @media (max-width: 640px) {
      .span2 {
        grid-column: auto;
      }
    }
    .err {
      color: var(--sd-danger);
      margin-bottom: 8px;
    }
  `,
})
export class AiScenariosPage implements OnInit {
  private readonly store = inject(AiConfigStore);
  private readonly meta = inject(MetaApi);
  private readonly dialogs = inject(SdDialogService);
  private readonly toast = inject(ToastService);

  protected readonly scenarios = linkedSignal<AutoReplyScenarioDto[] | null>(() => this.store.config()?.autoReplyScenarios.map((s) => ({ ...s })) ?? null);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly saving = signal(false);
  protected readonly channelOptions = computed<SdOption[]>(() =>
    (this.meta.ticketForm()?.channels ?? ['Email', 'Portal', 'WebForm', 'Chat', 'WhatsApp', 'Phone']).map((c) => ({ value: c, label: c })),
  );
  protected readonly categoryOptions = computed<SdOption[]>(() =>
    (this.meta.ticketForm()?.categories ?? []).map((c) => ({ value: c.id, label: c.name, group: c.parentId ? 'Subcategory' : 'Category' })),
  );
  protected readonly dirty = computed(() => JSON.stringify(this.scenarios()) !== JSON.stringify(this.store.config()?.autoReplyScenarios ?? null));

  ngOnInit(): void {
    this.meta.ticketForm$().subscribe({ error: () => undefined });
  }

  protected asList(value: string | string[] | null): string[] {
    return value === null ? [] : Array.isArray(value) ? value : [value];
  }

  protected add(): void {
    this.scenarios.update((list) => [...(list ?? []), emptyScenario()]);
  }

  protected async remove(index: number): Promise<void> {
    const s = this.scenarios()?.[index];
    if (s?.name && !(await this.dialogs.confirm({ title: `Remove “${s.name}”?`, message: 'The auto-responder stops replying for this scenario once you save.', danger: true, confirmLabel: 'Remove' })))
      return;
    this.scenarios.update((list) => (list ?? []).filter((_, i) => i !== index));
  }

  protected update(index: number, change: Partial<AutoReplyScenarioDto>): void {
    this.scenarios.update((list) => (list ?? []).map((s, i) => (i === index ? { ...s, ...change } : s)));
  }

  protected async save(): Promise<void> {
    const list = this.scenarios();
    if (!list) return;
    const missing = list.find((s) => !s.name.trim());
    if (missing) {
      this.toast.warning('Every scenario needs a name');
      return;
    }
    this.saving.set(true);
    this.errors.set({});
    try {
      await this.store.save(
        { autoReplyScenarios: list.map((s) => ({ ...s, key: (s.key || s.name).trim().toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_|_$/g, '') })) },
        'Auto-reply scenarios saved',
      );
    } catch (err: unknown) {
      if (isApiError(err) && err.code === 'validation_failed') {
        this.errors.set(fieldErrors(err));
        this.toast.warning('Some scenarios need attention', err.userMessage);
      }
    } finally {
      this.saving.set(false);
    }
  }
}
