import { ChangeDetectionStrategy, Component, booleanAttribute, computed, inject, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { FlowNodeDto, FlowNodeType, FlowOptionDto, FlowValidationIssueDto, KnowledgeApi } from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCombobox,
  SdDialogService,
  SdDropdownMenu,
  SdFormField,
  SdIcon,
  SdInput,
  SdMenuItem,
  SdOption,
  SdOptionSource,
  SdRichTextEditor,
} from '@sd/ui';
import { map, tap } from 'rxjs';
import { FLOW_NODE_META, comboOne, truncate } from '../kb-common';
import { nodeLabel } from './flow-layout';

const TYPES: FlowNodeType[] = ['question', 'solution', 'article', 'escalate'];

/**
 * Editor for the selected flow step: type, label, text, linked article, answers (label → next step),
 * add child step, set as start and delete. Emits patches; the designer owns the definition.
 */
@Component({
  selector: 'desk-kb-flow-node-editor',
  imports: [FormsModule, SdFormField, SdInput, SdButton, SdIcon, SdBadge, SdCombobox, SdRichTextEditor, SdDropdownMenu],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="editor" [attr.aria-label]="'Edit step: ' + label()">
      <header class="head">
        <span [class]="'type-dot type-dot--' + node().type" aria-hidden="true"></span>
        <h3 class="sd-truncate">{{ label() }}</h3>
        @if (isStart()) {
          <sd-badge tone="primary" size="sm">Start</sd-badge>
        }
        <span class="sd-xs sd-muted sd-mono">{{ node().id }}</span>
      </header>

      @if (issues().length) {
        <ul class="issues" aria-label="Problems with this step">
          @for (i of issues(); track $index) {
            <li><sd-icon name="triangle-alert" [size]="13" /> {{ i.message }}</li>
          }
        </ul>
      }

      <div class="grid">
        <sd-form-field label="Step type" [hint]="meta[node().type].description">
          <select sdInput [disabled]="readonly()" [ngModel]="node().type" (ngModelChange)="setType($event)">
            @for (t of types; track t) {
              <option [value]="t">{{ meta[t].label }}</option>
            }
          </select>
        </sd-form-field>
        <sd-form-field label="Short label" optional hint="Shown in the diagram only.">
          <input sdInput maxlength="80" [disabled]="readonly()" [ngModel]="node().title ?? ''" (ngModelChange)="patch({ title: $event || null })" />
        </sd-form-field>
      </div>

      @switch (node().type) {
        @case ('solution') {
          <sd-form-field label="Solution shown to the customer" required>
            <sd-rich-text-editor
              ariaLabel="Solution text"
              toolbar="compact"
              minHeight="120px"
              maxHeight="320px"
              placeholder="Explain the fix step by step…"
              [disabled]="readonly()"
              [value]="node().text"
              (valueChange)="patch({ text: $event })"
            />
          </sd-form-field>
        }
        @case ('article') {
          <sd-form-field label="Article" required hint="Only live articles can be linked.">
            <sd-combobox
              placeholder="Search live articles"
              [options]="articleSeed()"
              [search]="searchArticles"
              [disabled]="readonly()"
              [value]="node().articleId"
              (valueChange)="setArticle(comboOne($event))"
            />
          </sd-form-field>
          <sd-form-field label="Intro text" optional hint="Shown above the article link.">
            <textarea sdInput rows="2" maxlength="1000" [disabled]="readonly()" [ngModel]="node().text" (ngModelChange)="patch({ text: $event })"></textarea>
          </sd-form-field>
        }
        @case ('escalate') {
          <sd-form-field label="Message before handing over" required hint="The customer can then open a ticket with their answers attached.">
            <textarea sdInput rows="3" maxlength="2000" [disabled]="readonly()" [ngModel]="node().text" (ngModelChange)="patch({ text: $event })"></textarea>
          </sd-form-field>
        }
        @default {
          <sd-form-field label="Question" required>
            <textarea sdInput rows="2" maxlength="1000" placeholder="e.g. Does the device power on?" [disabled]="readonly()" [ngModel]="node().text" (ngModelChange)="patch({ text: $event })"></textarea>
          </sd-form-field>
        }
      }

      @if (node().type === 'question') {
        <fieldset class="answers">
          <legend>Answers</legend>
          @for (opt of node().options; track $index; let i = $index, first = $first, last = $last) {
            <div class="answer" [class.is-dangling]="!opt.next || !nodeIds().has(opt.next)">
              <input
                sdInput
                size="sm"
                class="answer-label"
                maxlength="120"
                placeholder="Answer text"
                [attr.aria-label]="'Answer ' + (i + 1) + ' text'"
                [disabled]="readonly()"
                [ngModel]="opt.label"
                (ngModelChange)="setOption(i, { label: $event })"
              />
              <sd-icon name="arrow-right" [size]="14" class="arrow" />
              <select
                sdInput
                size="sm"
                class="answer-next"
                [attr.aria-label]="'Answer ' + (i + 1) + ' leads to'"
                [disabled]="readonly()"
                [ngModel]="opt.next"
                (ngModelChange)="setOption(i, { next: $event })"
              >
                <option value="">— Not connected —</option>
                @for (t of targets(); track t.value) {
                  <option [value]="t.value">{{ t.label }}</option>
                }
              </select>
              <span class="answer-tools">
                @if (opt.next && nodeIds().has(opt.next)) {
                  <button sdButton size="sm" variant="ghost" icon="arrow-right" iconOnly type="button" [attr.aria-label]="'Go to the step answer ' + (i + 1) + ' leads to'" (click)="goToNode.emit(opt.next)"></button>
                }
                @if (!readonly()) {
                  <button sdButton size="sm" variant="ghost" icon="chevron-up" iconOnly type="button" [disabled]="first" [attr.aria-label]="'Move answer ' + (i + 1) + ' up'" (click)="moveOption(i, -1)"></button>
                  <button sdButton size="sm" variant="ghost" icon="chevron-down" iconOnly type="button" [disabled]="last" [attr.aria-label]="'Move answer ' + (i + 1) + ' down'" (click)="moveOption(i, 1)"></button>
                  <button sdButton size="sm" variant="ghost" icon="trash" iconOnly type="button" [attr.aria-label]="'Remove answer ' + (i + 1)" (click)="removeOption(i)"></button>
                }
              </span>
            </div>
          } @empty {
            <p class="sd-small sd-muted">No answers yet — add the choices the customer can pick.</p>
          }
          @if (!readonly()) {
            <div class="answer-actions">
              <button sdButton size="sm" variant="ghost" icon="plus" type="button" (click)="addOption()">Add answer</button>
              <sd-dropdown-menu label="Add answer with new step" icon="git-branch" size="sm" variant="subtle" [items]="childItems" />
            </div>
          }
        </fieldset>
      }

      @if (!readonly()) {
        <div class="footer">
          <button sdButton size="sm" variant="ghost" icon="flag" type="button" [disabled]="isStart()" (click)="makeStart.emit()">Set as start</button>
          <span class="sd-spacer"></span>
          <button sdButton size="sm" variant="ghost" icon="trash" type="button" [disabled]="!canDelete()" (click)="confirmRemove()">Delete step</button>
        </div>
      }
    </section>
  `,
  styles: `
    .editor {
      display: grid;
      gap: var(--sd-space-3);
      padding: var(--sd-space-4);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    .head {
      display: flex;
      align-items: center;
      gap: 8px;
      min-width: 0;
    }
    .head h3 {
      flex: 1;
      font-size: var(--sd-text-md);
      font-weight: var(--sd-weight-semibold);
    }
    .type-dot {
      flex: none;
      width: 12px;
      height: 12px;
      border-radius: 4px;
    }
    .type-dot--question {
      background: var(--sd-primary);
    }
    .type-dot--solution {
      background: var(--sd-success);
    }
    .type-dot--article {
      background: var(--sd-info);
    }
    .type-dot--escalate {
      background: var(--sd-warning);
    }
    .issues {
      display: grid;
      gap: 4px;
      margin: 0;
      padding: 8px 10px;
      list-style: none;
      border: 1px solid var(--sd-danger-border);
      border-radius: var(--sd-radius-sm);
      background: var(--sd-danger-bg);
      color: var(--sd-danger);
      font-size: var(--sd-text-xs);
    }
    .issues li {
      display: flex;
      align-items: center;
      gap: 6px;
    }
    .grid {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: var(--sd-space-3);
    }
    @media (max-width: 520px) {
      .grid {
        grid-template-columns: 1fr;
      }
    }
    .answers {
      display: grid;
      gap: 6px;
      min-width: 0;
      margin: 0;
      padding: var(--sd-space-3);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
    }
    legend {
      padding: 0 4px;
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-semibold);
    }
    .answer {
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto minmax(0, 1fr) auto;
      align-items: center;
      gap: 6px;
      padding: 4px;
      border-radius: var(--sd-radius-sm);
    }
    .answer.is-dangling {
      background: var(--sd-warning-bg);
    }
    @media (max-width: 640px) {
      .answer {
        grid-template-columns: minmax(0, 1fr);
      }
      .answer .arrow {
        display: none;
      }
    }
    .arrow {
      color: var(--sd-text-3);
    }
    .answer-tools {
      display: inline-flex;
      gap: 0;
    }
    .answer-actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--sd-space-2);
      margin-top: 4px;
    }
    .footer {
      display: flex;
      align-items: center;
      gap: var(--sd-space-2);
      padding-top: var(--sd-space-2);
      border-top: 1px solid var(--sd-divider);
    }
  `,
})
export class FlowNodeEditor {
  readonly node = input.required<FlowNodeDto>();
  readonly nodes = input.required<readonly FlowNodeDto[]>();
  readonly isStart = input(false, { transform: booleanAttribute });
  readonly readonly = input(false, { transform: booleanAttribute });
  readonly issues = input<readonly FlowValidationIssueDto[]>([]);
  readonly articleTitles = input<Readonly<Record<string, string>>>({});

  readonly nodeChange = output<Partial<FlowNodeDto>>();
  readonly addChildNode = output<FlowNodeType>();
  readonly removeNode = output<void>();
  readonly makeStart = output<void>();
  readonly goToNode = output<string>();
  readonly articleTitle = output<{ id: string; title: string }>();

  private readonly api = inject(KnowledgeApi);
  private readonly dialogs = inject(SdDialogService);
  protected readonly meta = FLOW_NODE_META;
  protected readonly types = TYPES;
  protected readonly comboOne = comboOne;
  private readonly seenTitles = new Map<string, string>();

  protected readonly label = computed(() => nodeLabel(this.node()));
  protected readonly nodeIds = computed(() => new Set(this.nodes().map((n) => n.id)));
  protected readonly canDelete = computed(() => this.nodes().length > 1);
  /** Every other step, as answer targets. */
  protected readonly targets = computed<SdOption[]>(() =>
    this.nodes()
      .filter((n) => n.id !== this.node().id)
      .map((n) => ({ value: n.id, label: `${truncate(nodeLabel(n), 40)} (${FLOW_NODE_META[n.type].label})` })),
  );
  protected readonly articleSeed = computed<SdOption[]>(() => {
    const id = this.node().articleId;
    if (!id) return [];
    return [{ value: id, label: this.articleTitles()[id] ?? 'Linked article', icon: 'file-text' }];
  });

  protected readonly childItems: SdMenuItem[] = TYPES.map((t) => ({
    id: t,
    label: FLOW_NODE_META[t].label,
    icon: FLOW_NODE_META[t].icon,
    run: () => this.addChildNode.emit(t),
  }));

  protected readonly searchArticles: SdOptionSource = (q: string) =>
    this.api.articles({ q: q || null, live: true, pageSize: 15 }).pipe(
      tap((page) => page.items.forEach((a) => this.seenTitles.set(a.id, a.title))),
      map((page) => page.items.map((a) => ({ value: a.id, label: a.title, icon: 'file-text', description: a.categoryName }))),
    );

  protected patch(changes: Partial<FlowNodeDto>): void {
    this.nodeChange.emit(changes);
  }

  protected async setType(type: FlowNodeType): Promise<void> {
    const n = this.node();
    if (type === n.type) return;
    if (type !== 'question' && n.options.length) {
      const ok = await this.dialogs.confirm({
        title: `Change to ${FLOW_NODE_META[type].label.toLowerCase()}?`,
        message: `Only questions have answers — the ${n.options.length} answer${n.options.length === 1 ? '' : 's'} of this step will be removed (the steps they lead to stay).`,
        confirmLabel: 'Change type',
      });
      if (!ok) return;
    }
    this.patch({ type, options: type === 'question' ? n.options : [], articleId: type === 'article' ? n.articleId : null });
  }

  protected setArticle(id: string | null): void {
    this.patch({ articleId: id });
    const title = id ? this.seenTitles.get(id) : undefined;
    if (id && title) this.articleTitle.emit({ id, title });
  }

  protected setOption(index: number, changes: Partial<FlowOptionDto>): void {
    this.patch({ options: this.node().options.map((o, i) => (i === index ? { ...o, ...changes } : o)) });
  }

  protected addOption(): void {
    this.patch({ options: [...this.node().options, { label: '', next: '' }] });
  }

  protected removeOption(index: number): void {
    this.patch({ options: this.node().options.filter((_, i) => i !== index) });
  }

  protected moveOption(index: number, delta: number): void {
    const list = [...this.node().options];
    const target = index + delta;
    if (target < 0 || target >= list.length) return;
    [list[index], list[target]] = [list[target]!, list[index]!];
    this.patch({ options: list });
  }

  protected async confirmRemove(): Promise<void> {
    const incoming = this.nodes().reduce((sum, n) => sum + n.options.filter((o) => o.next === this.node().id).length, 0);
    const ok = await this.dialogs.confirm({
      title: `Delete “${truncate(this.label(), 40)}”?`,
      message: incoming ? `${incoming} answer${incoming === 1 ? ' leads' : 's lead'} here and will be removed too.` : 'This step is removed from the flow.',
      confirmLabel: 'Delete step',
      danger: true,
    });
    if (ok) this.removeNode.emit();
  }
}
