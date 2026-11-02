import { ChangeDetectionStrategy, Component, OnInit, computed, inject, linkedSignal, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AiAdminApi, PromptTemplateDto, isApiError } from '@sd/core';
import { SdBadge, SdButton, SdCard, SdDialogService, SdFormField, SdInput, SdRelativeTimePipe, SdSkeleton, ToastService } from '@sd/ui';
import { fieldErrors, humanize } from '../../ai/shared/ai-ui';

/**
 * Versioned prompt templates per AI feature. Saving creates a new active version; any version (or the built-in
 * default, version 0) can be re-activated. The gateway always appends its security rules and wraps customer content
 * in data blocks, whatever the template says.
 */
@Component({
  selector: 'desk-ai-prompts',
  imports: [FormsModule, SdCard, SdButton, SdBadge, SdFormField, SdInput, SdSkeleton, SdRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (templates(); as list) {
      <div class="cols">
        <sd-card padding="none" title="Templates">
          <ul class="list" role="listbox" aria-label="Prompt templates">
            @for (t of list; track t.feature) {
              <li>
                <button type="button" role="option" [attr.aria-selected]="t.feature === selectedKey()" [class.active]="t.feature === selectedKey()" (click)="select(t.feature)">
                  <span>{{ label(t.feature) }}</span>
                  <sd-badge size="sm" [tone]="t.source === 'custom' ? 'primary' : 'neutral'">{{ t.source === 'custom' ? 'v' + t.activeVersion : 'default' }}</sd-badge>
                </button>
              </li>
            }
          </ul>
        </sd-card>

        @if (selected(); as t) {
          <div class="editor">
            <sd-card [title]="label(t.feature)" [subtitle]="t.source === 'custom' ? 'Custom version ' + t.activeVersion + ' is active' : 'The built-in default is active'">
              <div sdCardActions>
                @if (t.source === 'custom') {
                  <button sdButton size="sm" variant="ghost" icon="history" (click)="reset(t)">Reset to default</button>
                }
              </div>
              <form class="form" (ngSubmit)="save(t)">
                <sd-form-field label="System prompt" [error]="errors()['systemPrompt']">
                  <textarea sdInput name="system" rows="8" [ngModel]="systemPrompt()" (ngModelChange)="systemPrompt.set($event)"></textarea>
                </sd-form-field>
                <sd-form-field label="User message template" [error]="errors()['userTemplate']" [hint]="'Placeholders: ' + placeholders(t)">
                  <textarea sdInput name="user" rows="6" class="mono" [ngModel]="userTemplate()" (ngModelChange)="userTemplate.set($event)"></textarea>
                </sd-form-field>
                <p class="sd-xs sd-muted">
                  Security rules are always appended to the system prompt, customer content is wrapped in delimited data blocks and outputs are validated
                  — a template cannot switch these off.
                </p>
                <div class="row">
                  <button sdButton type="button" variant="ghost" (click)="loadDefault(t)">Start from default</button>
                  <button sdButton type="submit" variant="primary" icon="check" [loading]="saving()" [disabled]="!changed(t)">Save as version {{ nextVersion(t) }}</button>
                </div>
              </form>
            </sd-card>
            <sd-card title="Versions" padding="none">
              <ul class="versions">
                <li>
                  <span><strong>Default</strong><span class="sd-xs sd-muted"> · built in</span></span>
                  @if (t.source === 'custom') {
                    <button sdButton size="sm" (click)="activate(t, 0)">Activate</button>
                  } @else {
                    <sd-badge size="sm" tone="success">Active</sd-badge>
                  }
                </li>
                @for (v of t.versions; track v.id) {
                  <li>
                    <span><strong>Version {{ v.version }}</strong><span class="sd-xs sd-muted"> · {{ v.createdAt | sdRelativeTime }}</span></span>
                    <span class="row">
                      <button sdButton size="sm" variant="ghost" (click)="loadVersion(v.systemPrompt, v.userTemplate)">Load</button>
                      @if (v.isActive) {
                        <sd-badge size="sm" tone="success">Active</sd-badge>
                      } @else {
                        <button sdButton size="sm" (click)="activate(t, v.version)">Activate</button>
                      }
                    </span>
                  </li>
                }
              </ul>
            </sd-card>
          </div>
        }
      </div>
    } @else {
      <sd-card><sd-skeleton [lines]="10" /></sd-card>
    }
  `,
  styles: `
    .cols {
      display: grid;
      grid-template-columns: minmax(220px, 280px) minmax(0, 1fr);
      gap: var(--sd-space-4);
      align-items: start;
    }
    @media (max-width: 900px) {
      .cols {
        grid-template-columns: 1fr;
      }
    }
    .list,
    .versions {
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .list button {
      display: flex;
      justify-content: space-between;
      align-items: center;
      width: 100%;
      padding: 10px 14px;
      border: 0;
      border-top: 1px solid var(--sd-divider);
      background: transparent;
      color: var(--sd-text);
      font: inherit;
      text-align: start;
      cursor: pointer;
    }
    .list button:hover {
      background: var(--sd-row-hover);
    }
    .list button.active {
      background: var(--sd-row-selected);
      font-weight: var(--sd-weight-semibold);
    }
    .list button:focus-visible {
      outline: none;
      box-shadow: inset var(--sd-focus-ring);
    }
    .editor {
      display: grid;
      gap: var(--sd-space-4);
    }
    .form {
      display: grid;
      gap: 12px;
    }
    .mono {
      font-family: var(--sd-font-mono);
    }
    .row {
      display: flex;
      gap: 8px;
      align-items: center;
      justify-content: flex-end;
    }
    .versions li {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 8px;
      padding: 10px 14px;
      border-top: 1px solid var(--sd-divider);
    }
  `,
})
export class AiPromptsPage implements OnInit {
  private readonly api = inject(AiAdminApi);
  private readonly dialogs = inject(SdDialogService);
  private readonly toast = inject(ToastService);

  protected readonly templates = signal<PromptTemplateDto[] | null>(null);
  protected readonly selectedKey = signal<string | null>(null);
  protected readonly selected = computed(() => this.templates()?.find((t) => t.feature === this.selectedKey()) ?? null);
  protected readonly systemPrompt = linkedSignal(() => this.selected()?.systemPrompt ?? '');
  protected readonly userTemplate = linkedSignal(() => this.selected()?.userTemplate ?? '');
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly saving = signal(false);
  protected readonly label = humanize;

  ngOnInit(): void {
    this.api.prompts().subscribe((list) => {
      this.templates.set(list);
      if (!this.selectedKey() && list.length) this.selectedKey.set(list[0].feature);
    });
  }

  protected select(feature: string): void {
    this.selectedKey.set(feature);
    this.errors.set({});
  }

  protected placeholders(t: PromptTemplateDto): string {
    return t.placeholders.map((p) => `{{${p}}}`).join(', ');
  }

  protected changed(t: PromptTemplateDto): boolean {
    return this.systemPrompt() !== t.systemPrompt || this.userTemplate() !== t.userTemplate;
  }

  protected nextVersion(t: PromptTemplateDto): number {
    return Math.max(0, ...t.versions.map((v) => v.version)) + 1;
  }

  protected loadDefault(t: PromptTemplateDto): void {
    this.loadVersion(t.defaultSystemPrompt, t.defaultUserTemplate);
  }

  protected loadVersion(system: string, user: string): void {
    this.systemPrompt.set(system);
    this.userTemplate.set(user);
  }

  private replace(updated: PromptTemplateDto): void {
    this.templates.update((list) => (list ?? []).map((t) => (t.feature === updated.feature ? updated : t)));
  }

  protected save(t: PromptTemplateDto): void {
    this.saving.set(true);
    this.errors.set({});
    this.api.savePrompt(t.feature, { systemPrompt: this.systemPrompt(), userTemplate: this.userTemplate() }).subscribe({
      next: (updated) => {
        this.saving.set(false);
        this.replace(updated);
        this.toast.success(`Saved ${humanize(t.feature)} as version ${updated.activeVersion}`);
      },
      error: (err: unknown) => {
        this.saving.set(false);
        this.errors.set(fieldErrors(err));
        this.toast.warning('The template was not saved', isApiError(err) ? err.userMessage : undefined);
      },
    });
  }

  protected activate(t: PromptTemplateDto, version: number): void {
    this.api.activatePrompt(t.feature, version).subscribe((updated) => {
      this.replace(updated);
      this.toast.success(version === 0 ? 'Built-in default activated' : `Version ${version} activated`);
    });
  }

  protected async reset(t: PromptTemplateDto): Promise<void> {
    const ok = await this.dialogs.confirm({
      title: `Reset ${humanize(t.feature)} to the default?`,
      message: 'Saved versions are kept and can be activated again later.',
      confirmLabel: 'Reset',
    });
    if (!ok) return;
    this.api.resetPrompt(t.feature).subscribe((updated) => {
      this.replace(updated);
      this.toast.success('Built-in default activated');
    });
  }
}
