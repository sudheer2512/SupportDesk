import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Title } from '@angular/platform-browser';
import { Router, RouterLink } from '@angular/router';
import {
  ApiError,
  FlowDefinitionDto,
  FlowDetailDto,
  FlowNodeDto,
  FlowNodeType,
  FlowValidationIssueDto,
  KbVisibility,
  KnowledgeApi,
  MeStore,
  SaveFlowRequest,
  ToastBridge,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCombobox,
  SdDialogService,
  SdDropdownMenu,
  SdEmptyState,
  SdFormField,
  SdIcon,
  SdInput,
  SdMenuItem,
  SdSkeleton,
  SdSpinner,
} from '@sd/ui';
import { Subscription, catchError, firstValueFrom, forkJoin, map, of } from 'rxjs';
import { KbStatusBadge } from '../components/kb-badges.component';
import {
  ALL_VISIBILITIES,
  KB_BASE,
  KB_STYLES,
  KbLeaveGuarded,
  VISIBILITY_META,
  comboOne,
  errorMessage,
  formatPercent,
  newNodeId,
  registerKbIcons,
  toastedGlobally,
} from '../kb-common';
import { KbStore } from '../kb.store';
import { FlowGraph } from './flow-graph.component';
import { nodeLabel } from './flow-layout';
import { FlowNodeEditor } from './flow-node-editor.component';
import { FlowPreview } from './flow-preview.component';

type FlowAction = 'publish' | 'unpublish' | 'archive';

const ACTION_DONE: Record<FlowAction, string> = {
  publish: 'Flow published',
  unpublish: 'Flow unpublished',
  archive: 'Flow archived',
};

function blankNode(id: string, type: FlowNodeType, title: string | null = null): FlowNodeDto {
  return { id, type, title, text: '', articleId: null, options: [] };
}

/**
 * /kb/flows/new and /kb/flows/:id — guided-troubleshooting designer: details, SVG graph, node editor,
 * live server validation, preview walk-through, save and publish workflow.
 */
@Component({
  selector: 'desk-kb-flow-designer',
  imports: [
    FormsModule,
    RouterLink,
    SdButton,
    SdBadge,
    SdIcon,
    SdInput,
    SdFormField,
    SdCombobox,
    SdSkeleton,
    SdSpinner,
    SdEmptyState,
    SdDropdownMenu,
    KbStatusBadge,
    FlowGraph,
    FlowNodeEditor,
    FlowPreview,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '(window:beforeunload)': 'onBeforeUnload($event)',
    '(window:keydown)': 'onKeydown($event)',
  },
  template: `
    <a class="back" [routerLink]="[base, 'flows']"><sd-icon name="arrow-left" [size]="14" /> All flows</a>

    @if (loading() && !flow()) {
      <div class="skeleton" aria-busy="true" aria-label="Loading flow">
        <sd-skeleton width="45%" height="28px" />
        <sd-skeleton height="120px" radius="var(--sd-radius-lg)" />
        <sd-skeleton height="380px" radius="var(--sd-radius-lg)" />
      </div>
    } @else if (loadError(); as err) {
      <sd-empty-state [icon]="notFound() ? 'search-x' : 'circle-alert'" [title]="notFound() ? 'Flow not found' : 'Could not load the flow'" [message]="notFound() ? 'It may have been deleted.' : err">
        @if (!notFound()) {
          <button sdButton type="button" icon="refresh" (click)="reload()">Retry</button>
        }
        <a sdButton variant="ghost" [routerLink]="[base, 'flows']">Back to flows</a>
      </sd-empty-state>
    } @else {
      <header class="head">
        <div class="titles">
          <h2 class="sd-truncate">{{ name().trim() || (isNew() ? 'New troubleshooting flow' : 'Untitled flow') }}</h2>
          <div class="badges">
            @if (flow(); as f) {
              <desk-kb-status [status]="f.status" size="sm" />
              <span class="sd-xs sd-muted">v{{ f.version }}</span>
            } @else {
              <sd-badge size="sm" dot>New</sd-badge>
            }
            <span class="sd-xs sd-muted">{{ definition().nodes.length }} step{{ definition().nodes.length === 1 ? '' : 's' }}</span>
            @if (dirty()) {
              <sd-badge tone="warning" size="sm" icon="pencil">Unsaved changes</sd-badge>
            }
          </div>
        </div>
        <div class="actions" role="toolbar" aria-label="Flow actions">
          <div class="modes" role="group" aria-label="View">
            <button sdButton size="sm" variant="ghost" icon="workflow" type="button" [pressed]="mode() === 'design'" (click)="mode.set('design')">Design</button>
            <button sdButton size="sm" variant="ghost" icon="play" type="button" [pressed]="mode() === 'preview'" (click)="mode.set('preview')">Preview</button>
          </div>
          @if (!readonly()) {
            <button sdButton variant="primary" icon="save" type="button" [loading]="saving()" [disabled]="!!busyAction()" (click)="save()" title="Save (Ctrl+S)">
              {{ isNew() ? 'Create flow' : 'Save' }}
            </button>
          }
          @if (flow(); as f) {
            @if (canPublish()) {
              @if (f.status === 'Published') {
                <button sdButton icon="eye-off" type="button" [loading]="busyAction() === 'unpublish'" [disabled]="busy()" (click)="transition('unpublish')">Unpublish</button>
              } @else {
                <button sdButton variant="subtle" icon="globe" type="button" [loading]="busyAction() === 'publish'" [disabled]="busy()" (click)="transition('publish')">Publish</button>
              }
            }
            @if (moreItems().length) {
              <sd-dropdown-menu icon="ellipsis" iconOnly [caret]="false" ariaLabel="More flow actions" [items]="moreItems()" align="end" [disabled]="busy()" />
            }
          }
        </div>
      </header>

      <section class="details" aria-label="Flow details">
        <sd-form-field label="Name" required [error]="errors()['name']">
          <input sdInput maxlength="150" placeholder="e.g. Printer will not print" [disabled]="readonly()" [ngModel]="name()" (ngModelChange)="name.set($event)" />
        </sd-form-field>
        <sd-form-field label="Visibility" [hint]="visibilityMeta[visibility()].description" [error]="errors()['visibility']">
          <select sdInput [disabled]="readonly()" [ngModel]="visibility()" (ngModelChange)="visibility.set($event)">
            @for (v of visibilities; track v) {
              <option [value]="v">{{ visibilityMeta[v].label }}</option>
            }
          </select>
        </sd-form-field>
        <sd-form-field label="Product" optional [error]="errors()['productId']">
          <sd-combobox placeholder="Any product" [options]="store.productOptions()" [disabled]="readonly()" [value]="productId()" (valueChange)="productId.set(comboOne($event))" />
        </sd-form-field>
        <sd-form-field label="Category" optional [error]="errors()['categoryId']">
          <sd-combobox placeholder="Uncategorised" [options]="store.categoryOptions()" [disabled]="readonly()" [value]="categoryId()" (valueChange)="categoryId.set(comboOne($event))" />
        </sd-form-field>
        <sd-form-field class="wide" label="Description" optional hint="Shown to customers before they start." [error]="errors()['description']">
          <textarea sdInput rows="2" maxlength="1000" [disabled]="readonly()" [ngModel]="description()" (ngModelChange)="description.set($event)"></textarea>
        </sd-form-field>
        @if (flow(); as f) {
          <dl class="usage wide">
            <div>
              <dt>Started</dt>
              <dd>{{ f.started.toLocaleString() }}</dd>
            </div>
            <div>
              <dt>Resolved</dt>
              <dd>{{ f.resolved.toLocaleString() }} <small>{{ rate(f.resolved, f.started) }}</small></dd>
            </div>
            <div>
              <dt>Escalated</dt>
              <dd>{{ f.escalated.toLocaleString() }} <small>{{ rate(f.escalated, f.started) }}</small></dd>
            </div>
          </dl>
        }
      </section>

      @if (mode() === 'preview') {
        <desk-kb-flow-preview [definition]="definition()" [name]="name()" [articleTitles]="articleTitles()" />
      } @else {
        <section class="problems" [class.ok]="!issues().length" aria-labelledby="kb-flow-problems">
          <h3 id="kb-flow-problems">
            @if (validating()) {
              <sd-spinner [size]="14" /> Checking the flow…
            } @else if (issues().length) {
              <sd-icon name="triangle-alert" [size]="15" /> {{ issues().length }} problem{{ issues().length === 1 ? '' : 's' }} to fix before publishing
            } @else {
              <sd-icon name="circle-check" [size]="15" /> No problems found
            }
          </h3>
          @if (issues().length) {
            <ul aria-live="polite">
              @for (i of issues(); track $index) {
                <li>
                  @if (i.nodeId && nodeExists(i.nodeId)) {
                    <button type="button" class="issue-link" (click)="selectNode(i.nodeId, true)">
                      <strong>{{ nodeName(i.nodeId) }}:</strong> {{ i.message }}
                    </button>
                  } @else {
                    {{ i.message }}
                  }
                </li>
              }
            </ul>
          }
        </section>

        <div class="designer">
          <div class="graph-col">
            <desk-kb-flow-graph [definition]="definition()" [selectedId]="selectedId()" [issueNodeIds]="issueNodeIds()" (nodeSelect)="selectNode($event)" />
            <div class="jump">
              <label for="kb-flow-jump" class="sd-small">Go to step</label>
              <select sdInput id="kb-flow-jump" size="sm" [ngModel]="selectedId() ?? ''" (ngModelChange)="selectNode($event, true)">
                @for (n of definition().nodes; track n.id) {
                  <option [value]="n.id">{{ nodeName(n.id) }}{{ n.id === definition().startNodeId ? ' (start)' : '' }}</option>
                }
              </select>
            </div>
          </div>
          <div class="editor-col">
            @if (selectedNode(); as n) {
              <desk-kb-flow-node-editor
                [node]="n"
                [nodes]="definition().nodes"
                [isStart]="n.id === definition().startNodeId"
                [readonly]="readonly()"
                [issues]="selectedIssues()"
                [articleTitles]="articleTitles()"
                (nodeChange)="updateNode(n.id, $event)"
                (addChildNode)="addChild(n.id, $event)"
                (removeNode)="deleteNode(n.id)"
                (makeStart)="setStart(n.id)"
                (goToNode)="selectNode($event, true)"
                (articleTitle)="rememberArticle($event.id, $event.title)"
              />
            } @else {
              <sd-empty-state icon="workflow" title="Select a step" message="Click a step in the diagram to edit it." compact />
            }
          </div>
        </div>
      }
    }
  `,
  styles: [
    KB_STYLES,
    `
      :host {
        display: block;
      }
      .back {
        display: inline-flex;
        align-items: center;
        gap: 4px;
        margin-bottom: var(--sd-space-3);
        color: var(--sd-text-2);
        font-size: var(--sd-text-sm);
      }
      .skeleton {
        display: grid;
        gap: var(--sd-space-3);
      }
      .head {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sd-space-3);
        margin-bottom: var(--sd-space-3);
      }
      .titles {
        flex: 1 1 280px;
        min-width: 0;
      }
      .titles h2 {
        font-size: var(--sd-text-xl);
        font-weight: var(--sd-weight-semibold);
      }
      .badges {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 6px;
        margin-top: 4px;
      }
      .actions {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sd-space-2);
      }
      .modes {
        display: inline-flex;
        gap: 2px;
        padding: 2px;
        border-radius: var(--sd-radius-md);
        background: var(--sd-surface-3);
      }
      .details {
        display: grid;
        grid-template-columns: repeat(4, minmax(0, 1fr));
        gap: var(--sd-space-3);
        margin-bottom: var(--sd-space-4);
        padding: var(--sd-space-4);
        border: 1px solid var(--sd-border);
        border-radius: var(--sd-radius-lg);
        background: var(--sd-surface);
      }
      .details .wide {
        grid-column: 1 / -1;
      }
      @media (max-width: 1000px) {
        .details {
          grid-template-columns: repeat(2, minmax(0, 1fr));
        }
      }
      @media (max-width: 560px) {
        .details {
          grid-template-columns: minmax(0, 1fr);
        }
      }
      .usage {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sd-space-5);
        margin: 0;
      }
      .usage dt {
        color: var(--sd-text-3);
        font-size: var(--sd-text-xs);
      }
      .usage dd {
        margin: 0;
        font-size: var(--sd-text-lg);
        font-weight: var(--sd-weight-semibold);
        font-variant-numeric: tabular-nums;
      }
      .usage small {
        color: var(--sd-text-3);
        font-size: var(--sd-text-xs);
        font-weight: var(--sd-weight-regular);
      }
      .problems {
        margin-bottom: var(--sd-space-3);
        padding: var(--sd-space-3) var(--sd-space-4);
        border: 1px solid var(--sd-warning-border);
        border-radius: var(--sd-radius-md);
        background: var(--sd-warning-bg);
      }
      .problems.ok {
        border-color: var(--sd-success-border);
        background: var(--sd-success-bg);
      }
      .problems h3 {
        display: flex;
        align-items: center;
        gap: 6px;
        font-size: var(--sd-text-sm);
        font-weight: var(--sd-weight-semibold);
      }
      .problems ul {
        display: grid;
        gap: 2px;
        margin: 6px 0 0;
        padding-left: 22px;
        font-size: var(--sd-text-sm);
      }
      .issue-link {
        padding: 0;
        border: 0;
        background: transparent;
        color: var(--sd-text);
        font: inherit;
        text-align: left;
        text-decoration: underline;
        text-decoration-color: var(--sd-border-strong);
        cursor: pointer;
      }
      .issue-link:focus-visible {
        outline: none;
        box-shadow: var(--sd-focus-ring);
        border-radius: var(--sd-radius-xs);
      }
      .designer {
        display: grid;
        grid-template-columns: minmax(0, 1fr) 400px;
        gap: var(--sd-space-4);
        align-items: start;
      }
      @media (max-width: 1150px) {
        .designer {
          grid-template-columns: minmax(0, 1fr);
        }
      }
      .graph-col,
      .editor-col {
        display: grid;
        gap: var(--sd-space-2);
        min-width: 0;
      }
      .editor-col {
        position: sticky;
        top: var(--sd-space-2);
      }
      .jump {
        display: flex;
        align-items: center;
        gap: var(--sd-space-2);
      }
      .jump select {
        width: auto;
        max-width: 360px;
      }
    `,
  ],
})
export class FlowDesignerPage implements KbLeaveGuarded {
  /** Route param (absent on /flows/new). */
  readonly id = input<string>();

  private readonly api = inject(KnowledgeApi);
  protected readonly store = inject(KbStore);
  private readonly me = inject(MeStore);
  private readonly router = inject(Router);
  private readonly dialogs = inject(SdDialogService);
  private readonly toasts = inject(ToastBridge);
  private readonly titleService = inject(Title);
  private readonly graph = viewChild(FlowGraph);

  protected readonly base = KB_BASE;
  protected readonly visibilities = ALL_VISIBILITIES;
  protected readonly visibilityMeta = VISIBILITY_META;
  protected readonly comboOne = comboOne;

  protected readonly flow = signal<FlowDetailDto | null>(null);
  protected readonly name = signal('');
  protected readonly description = signal('');
  protected readonly visibility = signal<KbVisibility>('Public');
  protected readonly productId = signal<string | null>(null);
  protected readonly categoryId = signal<string | null>(null);
  protected readonly definition = signal<FlowDefinitionDto>({ startNodeId: '', nodes: [] });
  private readonly baseline = signal('');
  protected readonly selectedId = signal<string | null>(null);
  protected readonly issues = signal<FlowValidationIssueDto[]>([]);
  protected readonly validating = signal(false);
  protected readonly mode = signal<'design' | 'preview'>('design');
  protected readonly articleTitles = signal<Record<string, string>>({});
  protected readonly errors = signal<Record<string, string | null>>({});
  protected readonly loading = signal(false);
  protected readonly loadError = signal<string | null>(null);
  protected readonly notFound = signal(false);
  protected readonly saving = signal(false);
  protected readonly busyAction = signal<FlowAction | 'delete' | null>(null);
  private readonly ready = signal(false);

  protected readonly isNew = computed(() => !this.id());
  protected readonly readonly = computed(() => !this.me.can('kb.author'));
  protected readonly canPublish = computed(() => this.me.can('kb.publish'));
  protected readonly busy = computed(() => this.saving() || this.busyAction() !== null);
  private readonly key = computed(() =>
    JSON.stringify([this.name().trim(), this.description().trim(), this.visibility(), this.productId(), this.categoryId(), this.definition()]),
  );
  protected readonly dirty = computed(() => this.ready() && !this.readonly() && this.key() !== this.baseline());
  private readonly nodesById = computed(() => new Map(this.definition().nodes.map((n) => [n.id, n])));
  protected readonly selectedNode = computed(() => {
    const id = this.selectedId();
    return id ? (this.nodesById().get(id) ?? null) : null;
  });
  protected readonly issueNodeIds = computed(() => this.issues().flatMap((i) => (i.nodeId ? [i.nodeId] : [])));
  protected readonly selectedIssues = computed(() => this.issues().filter((i) => i.nodeId === this.selectedId()));

  protected readonly moreItems = computed<SdMenuItem[]>(() => {
    const f = this.flow();
    if (!f || !this.canPublish()) return [];
    const items: SdMenuItem[] = [];
    if (f.status !== 'Archived') items.push({ id: 'archive', label: 'Archive', icon: 'archive', run: () => void this.transition('archive') });
    items.push({ id: 'delete', label: 'Delete flow…', icon: 'trash', danger: true, run: () => void this.remove() });
    return items;
  });

  private loadSub: Subscription | null = null;
  private validateSub: Subscription | null = null;
  private leaving = false;

  constructor() {
    registerKbIcons();
    this.store.ensureOptions();
    this.store.ensureCategories();
    effect(() => {
      const id = this.id();
      untracked(() => (id ? this.load(id) : this.startNew()));
    });
    // Live validation (debounced) whenever the definition changes.
    effect((onCleanup) => {
      const def = this.definition();
      if (!this.ready() || def.nodes.length === 0) return;
      const timer = setTimeout(() => untracked(() => this.validate(def)), 600);
      onCleanup(() => clearTimeout(timer));
    });
    inject(DestroyRef).onDestroy(() => {
      this.loadSub?.unsubscribe();
      this.validateSub?.unsubscribe();
    });
  }

  // ───── Loading ─────

  private startNew(): void {
    const id = newNodeId(new Set());
    const def: FlowDefinitionDto = { startNodeId: id, nodes: [blankNode(id, 'question', 'Start')] };
    this.flow.set(null);
    this.name.set('');
    this.description.set('');
    this.visibility.set('Public');
    this.productId.set(null);
    this.categoryId.set(null);
    this.definition.set(def);
    this.selectedId.set(id);
    this.issues.set([]);
    this.baseline.set(this.key());
    this.ready.set(true);
  }

  private load(id: string): void {
    this.loadSub?.unsubscribe();
    this.loading.set(true);
    this.loadError.set(null);
    this.notFound.set(false);
    this.loadSub = this.api.flow(id).subscribe({
      next: (f) => {
        this.loading.set(false);
        this.apply(f);
        this.loadArticleTitles(f.definition);
      },
      error: (err: unknown) => {
        const e = ApiError.from(err);
        this.loading.set(false);
        this.notFound.set(e.status === 404);
        this.loadError.set(e.userMessage);
      },
    });
  }

  protected reload(): void {
    const id = this.id();
    if (id) this.load(id);
  }

  private apply(f: FlowDetailDto): void {
    this.flow.set(f);
    this.name.set(f.name);
    this.description.set(f.description ?? '');
    this.visibility.set(f.visibility);
    this.productId.set(f.productId);
    this.categoryId.set(f.categoryId);
    const def: FlowDefinitionDto = {
      startNodeId: f.definition.startNodeId,
      nodes: f.definition.nodes.map((n) => ({ ...n, options: n.options.map((o) => ({ ...o })) })),
    };
    this.definition.set(def);
    this.issues.set(f.issues ?? []);
    const selected = this.selectedId();
    if (!selected || !def.nodes.some((n) => n.id === selected)) this.selectedId.set(def.startNodeId || def.nodes[0]?.id || null);
    this.baseline.set(this.key());
    this.errors.set({});
    this.ready.set(true);
    this.titleService.setTitle(`${f.name} · Troubleshooting · SupportDesk`);
  }

  private loadArticleTitles(def: FlowDefinitionDto): void {
    const known = this.articleTitles();
    const ids = [...new Set(def.nodes.map((n) => n.articleId).filter((id): id is string => !!id && !known[id]))];
    if (!ids.length) return;
    forkJoin(
      ids.map((id) =>
        this.api.article(id).pipe(
          map((a): [string, string | null] => [id, a.title]),
          catchError(() => of<[string, string | null]>([id, null])),
        ),
      ),
    ).subscribe((pairs) => {
      const next = { ...this.articleTitles() };
      for (const [id, title] of pairs) if (title) next[id] = title;
      this.articleTitles.set(next);
    });
  }

  protected rememberArticle(id: string, title: string): void {
    this.articleTitles.update((t) => ({ ...t, [id]: title }));
  }

  private validate(def: FlowDefinitionDto): void {
    this.validateSub?.unsubscribe();
    this.validating.set(true);
    this.validateSub = this.api.validateFlow(def).subscribe({
      next: (issues) => {
        this.validating.set(false);
        this.issues.set(issues);
      },
      error: () => this.validating.set(false),
    });
  }

  // ───── Graph editing ─────

  protected nodeExists(id: string): boolean {
    return this.nodesById().has(id);
  }

  protected nodeName(id: string): string {
    const n = this.nodesById().get(id);
    return n ? nodeLabel(n) : id;
  }

  protected selectNode(id: string | null, focus = false): void {
    if (!id || !this.nodeExists(id)) return;
    this.selectedId.set(id);
    if (focus) queueMicrotask(() => this.graph()?.focusNode(id));
  }

  protected updateNode(id: string, changes: Partial<FlowNodeDto>): void {
    this.definition.update((d) => ({ ...d, nodes: d.nodes.map((n) => (n.id === id ? { ...n, ...changes, id: n.id } : n)) }));
  }

  protected addChild(parentId: string, type: FlowNodeType): void {
    const def = this.definition();
    const parent = this.nodesById().get(parentId);
    if (!parent) return;
    const id = newNodeId(new Set(def.nodes.map((n) => n.id)));
    const label = `Answer ${parent.options.length + 1}`;
    this.definition.set({
      ...def,
      nodes: [
        ...def.nodes.map((n) => (n.id === parentId ? { ...n, type: 'question' as const, options: [...n.options, { label, next: id }] } : n)),
        blankNode(id, type),
      ],
    });
    this.toasts.info(`New ${type} step added`, `Linked from “${label}”. Select it in the diagram to edit.`);
  }

  protected deleteNode(id: string): void {
    const def = this.definition();
    if (def.nodes.length <= 1) return;
    const nodes = def.nodes.filter((n) => n.id !== id).map((n) => ({ ...n, options: n.options.filter((o) => o.next !== id) }));
    const startNodeId = def.startNodeId === id ? (nodes[0]?.id ?? '') : def.startNodeId;
    this.definition.set({ startNodeId, nodes });
    this.selectedId.set(startNodeId || null);
  }

  protected setStart(id: string): void {
    this.definition.update((d) => ({ ...d, startNodeId: id }));
  }

  protected rate(part: number, total: number): string {
    return total > 0 ? `(${formatPercent((part / total) * 100)})` : '';
  }

  // ───── Save & workflow ─────

  private request(): SaveFlowRequest {
    return {
      name: this.name().trim(),
      description: this.description().trim() || null,
      visibility: this.visibility(),
      productId: this.productId(),
      categoryId: this.categoryId(),
      definition: this.definition(),
    };
  }

  async save(): Promise<boolean> {
    if (this.readonly() || this.saving()) return false;
    if (!this.name().trim()) {
      this.errors.set({ name: 'Give the flow a name.' });
      this.toasts.warning('Check the highlighted fields');
      return false;
    }
    this.errors.set({});
    const current = this.flow();
    this.saving.set(true);
    try {
      const saved = await firstValueFrom(current ? this.api.updateFlow(current.id, this.request()) : this.api.createFlow(this.request()));
      this.apply(saved);
      if (!current) {
        this.toasts.success('Flow created');
        this.leaving = true;
        await this.router.navigate([KB_BASE, 'flows', saved.id], { replaceUrl: true });
      } else {
        this.toasts.success('Flow saved', saved.issues.length ? `${saved.issues.length} problem(s) still to fix before publishing.` : undefined);
      }
      return true;
    } catch (err) {
      const e = ApiError.from(err);
      const fields = {
        name: e.fieldError('name'),
        description: e.fieldError('description'),
        visibility: e.fieldError('visibility'),
        productId: e.fieldError('productId'),
        categoryId: e.fieldError('categoryId'),
      };
      this.errors.set(fields);
      this.toasts.error('Could not save the flow', Object.values(fields).some(Boolean) ? 'Check the highlighted fields.' : e.userMessage);
      return false;
    } finally {
      this.saving.set(false);
    }
  }

  protected async transition(action: FlowAction): Promise<void> {
    const f = this.flow();
    if (!f || this.busy()) return;
    if (action === 'archive') {
      const ok = await this.dialogs.confirm({ title: 'Archive this flow?', message: 'Customers can no longer start it.', confirmLabel: 'Archive', danger: true });
      if (!ok) return;
    }
    if (this.dirty()) {
      const saved = await this.save();
      if (!saved) return;
    }
    this.busyAction.set(action);
    try {
      const updated = await firstValueFrom(this.api.transitionFlow(f.id, { action }));
      this.apply(updated);
      this.toasts.success(ACTION_DONE[action]);
    } catch (err) {
      this.toasts.error(`Could not ${action} the flow`, errorMessage(err));
    } finally {
      this.busyAction.set(null);
    }
  }

  private async remove(): Promise<void> {
    const f = this.flow();
    if (!f) return;
    const ok = await this.dialogs.confirm({
      title: `Delete “${f.name}”?`,
      message: 'The flow and its usage statistics are permanently deleted.',
      confirmLabel: 'Delete flow',
      danger: true,
    });
    if (!ok) return;
    this.busyAction.set('delete');
    try {
      await firstValueFrom(this.api.deleteFlow(f.id));
      this.leaving = true;
      this.toasts.success('Flow deleted');
      await this.router.navigate([KB_BASE, 'flows']);
    } catch (err) {
      if (!toastedGlobally(err)) this.toasts.error('Could not delete the flow', errorMessage(err));
    } finally {
      this.busyAction.set(null);
    }
  }

  // ───── Leaving & shortcuts ─────

  canDeactivate(): boolean | Promise<boolean> {
    if (this.leaving || !this.dirty()) {
      this.leaving = false;
      return true;
    }
    return this.dialogs.confirm({
      title: 'Discard unsaved changes?',
      message: 'You have changes to this flow that have not been saved.',
      confirmLabel: 'Discard changes',
      cancelLabel: 'Keep editing',
      danger: true,
    });
  }

  protected onBeforeUnload(event: BeforeUnloadEvent): void {
    if (this.dirty()) {
      event.preventDefault();
      event.returnValue = '';
    }
  }

  protected onKeydown(event: KeyboardEvent): void {
    if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 's') {
      event.preventDefault();
      if (!this.readonly() && !this.busy()) void this.save();
    }
  }
}
