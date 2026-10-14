import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import {
  BlueprintDefinition,
  BlueprintDetailDto,
  BlueprintStateDef,
  BlueprintTransitionDef,
  BlueprintsApi,
  SaveBlueprintRequest,
  ValidationIssueDto,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCard,
  SdCombobox,
  SdDialogService,
  SdFormField,
  SdIcon,
  SdInput,
  SdSkeleton,
  SdTab,
  SdTabs,
  SdToggle,
  ToastService,
} from '@sd/ui';
import { AutomationContext } from '../shared/automation-context.service';
import { IssueMap, clone, errorMessage, issuesFromError, issuesFromList, issuesUnder } from '../shared/automation-ui';
import { VersionHistory } from '../shared/version-history.component';
import { BlueprintCanvas, CanvasSelection, StatusLook, autoPosition } from './blueprint-canvas.component';
import { TransitionEditor } from './transition-editor.component';

const EMPTY: BlueprintDefinition = { states: [], transitions: [] };

/**
 * Blueprint designer: drag states on the canvas, drag from a state's handle to another state to create a transition,
 * edit states/transitions in the inspector, validate, save drafts, publish, activate and browse versions.
 */
@Component({
  selector: 'desk-blueprint-designer',
  imports: [
    FormsModule,
    RouterLink,
    SdButton,
    SdBadge,
    SdCard,
    SdCombobox,
    SdFormField,
    SdIcon,
    SdInput,
    SdSkeleton,
    SdTabs,
    SdTab,
    SdToggle,
    BlueprintCanvas,
    TransitionEditor,
    VersionHistory,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (!ctx.ready() || loading()) {
      <sd-skeleton [lines]="8" height="28px" />
    } @else {
      <div class="bar">
        <a sdButton variant="ghost" size="sm" icon="arrow-left" routerLink="/automation/blueprints">Blueprints</a>
        <span class="sd-spacer"></span>
        @if (detail(); as d) {
          @if (d.blueprint.publishedVersionNumber) {
            <sd-badge tone="success" variant="outline">Live: v{{ d.blueprint.publishedVersionNumber }}</sd-badge>
          } @else {
            <sd-badge variant="outline">Not published</sd-badge>
          }
          @if (dirty()) {
            <sd-badge tone="warning">Unsaved changes</sd-badge>
          }
          <sd-toggle [value]="d.blueprint.isActive" [disabled]="!d.blueprint.publishedVersionNumber" (valueChange)="setActive($event)">Active</sd-toggle>
        }
        <button sdButton icon="list-checks" (click)="check()" [loading]="checking()">Check</button>
        <button sdButton icon="file-plus" (click)="save(false)" [loading]="saving() === 'draft'" [disabled]="!dirty() && !!id()">Save draft</button>
        <button sdButton variant="primary" icon="rocket" (click)="publish()" [loading]="saving() === 'publish'" [disabled]="!canPublish()">Publish</button>
      </div>

      @if (generalIssues().length) {
        <div class="issues" role="alert">
          <strong>The blueprint cannot be published yet:</strong>
          <ul>
            @for (i of generalIssues(); track i.path + i.message) {
              <li>{{ i.message }} <code>{{ i.path }}</code></li>
            }
          </ul>
        </div>
      }
      @if (warnings().length) {
        <div class="warnings" role="status">
          @for (w of warnings(); track w.path + w.message) {
            <p><sd-icon name="triangle-alert" [size]="13" /> {{ w.message }}</p>
          }
        </div>
      }

      <sd-card padding="sm" class="settings">
        <div class="settings-grid">
          <sd-form-field label="Blueprint name" required [error]="issues()['name']">
            <input sdInput [ngModel]="name()" (ngModelChange)="name.set($event)" maxlength="200" />
          </sd-form-field>
          <sd-form-field label="Department" hint="Applies to new tickets in this department." [error]="issues()['departmentId']">
            <sd-combobox [options]="ctx.departmentOptions()" [value]="departmentId()" (valueChange)="departmentId.set($any($event))" placeholder="All departments" />
          </sd-form-field>
          <sd-form-field label="Category" hint="…and/or with this category." [error]="issues()['categoryId']">
            <sd-combobox [options]="ctx.allCategoryOptions()" [value]="categoryId()" (valueChange)="categoryId.set($any($event))" placeholder="Any category" />
          </sd-form-field>
          <sd-form-field label="Description" optional>
            <input sdInput [ngModel]="description()" (ngModelChange)="description.set($event)" />
          </sd-form-field>
        </div>
      </sd-card>

      <div class="layout">
        <div class="board">
          <div class="tools" role="toolbar" aria-label="Canvas tools">
            <button sdButton size="sm" icon="plus" (click)="addState()">Add state</button>
            <button
              sdButton
              size="sm"
              icon="arrow-right"
              [pressed]="!!connectFrom()"
              [disabled]="selected()?.kind !== 'state' && !connectFrom()"
              (click)="toggleConnect()"
            >
              {{ connectFrom() ? 'Pick the target state…' : 'Connect from selected' }}
            </button>
            <button sdButton size="sm" variant="ghost" icon="trash" [disabled]="!selected()" (click)="deleteSelected()">Delete selected</button>
            <span class="sd-spacer"></span>
            <span class="sd-muted hint">Drag states to arrange; drag the → handle onto another state to add a transition. Focused state: arrow keys move.</span>
          </div>
          <desk-blueprint-canvas
            class="canvas"
            [definition]="def()"
            [statuses]="statusLooks()"
            [(selected)]="selected"
            [(connectFrom)]="connectFrom"
            (stateMoved)="moveState($event.key, $event.x, $event.y)"
            (connect)="addTransition($event.from, $event.to)"
          />
          @if (anyStateTransitions().length) {
            <p class="sd-muted hint">From any state: {{ anyStateTransitions().join(', ') }}</p>
          }
        </div>

        <aside class="side" aria-label="Inspector">
          <sd-card padding="sm">
            <sd-tabs [(selected)]="sideTab" variant="pill" ariaLabel="Blueprint inspector">
              <sd-tab id="inspect" label="Inspector" icon="sliders">
                @switch (selected()?.kind) {
                  @case ('state') {
                    @if (selectedState(); as s) {
                      <div class="form">
                        <h3>State</h3>
                        @if (stateIssues()['']; as e) {
                          <p class="err">{{ e }}</p>
                        }
                        <sd-form-field label="Name" required [error]="stateIssues()['name']">
                          <input sdInput [ngModel]="s.name" (ngModelChange)="patchState(s.key, { name: $event })" />
                        </sd-form-field>
                        <sd-form-field label="Key" hint="Stable id (lowercase, digits, - or _)." [error]="stateIssues()['key']">
                          <input sdInput class="mono" [ngModel]="s.key" (change)="renameState(s.key, $any($event.target).value)" />
                        </sd-form-field>
                        <sd-form-field label="Ticket status in this state" hint="Empty keeps the ticket's status." [error]="stateIssues()['statusId']">
                          <sd-combobox [options]="ctx.statusOptions()" [value]="s.statusId ?? null" (valueChange)="patchState(s.key, { statusId: $any($event), substatus: null })" placeholder="Keep current status" />
                        </sd-form-field>
                        @if (ctx.substatusOptions(s.statusId ?? null).length) {
                          <sd-form-field label="Substatus" [error]="stateIssues()['substatus']">
                            <select sdInput [ngModel]="s.substatus ?? ''" (ngModelChange)="patchState(s.key, { substatus: $event || null })">
                              <option value="">—</option>
                              @for (o of ctx.substatusOptions(s.statusId ?? null); track o) {
                                <option [value]="o">{{ o }}</option>
                              }
                            </select>
                          </sd-form-field>
                        }
                        <sd-toggle [value]="!!s.isInitial" (valueChange)="setInitial(s.key, $event)">Initial state (new tickets start here)</sd-toggle>
                        <sd-toggle [value]="!!s.isFinal" (valueChange)="patchState(s.key, { isFinal: $event })">Final state</sd-toggle>
                        <div class="row">
                          <button sdButton size="sm" icon="arrow-right" (click)="connectFrom.set(s.key)">Add transition from here</button>
                          <button sdButton size="sm" variant="danger" icon="trash" (click)="deleteSelected()">Delete state</button>
                        </div>
                        @if (outgoing(s.key).length) {
                          <h4>Transitions from here</h4>
                          <ul class="links">
                            @for (t of outgoing(s.key); track t.key) {
                              <li><button type="button" class="link" (click)="selected.set({ kind: 'transition', key: t.key })">{{ t.name }} → {{ stateName(t.to) }}</button></li>
                            }
                          </ul>
                        }
                      </div>
                    }
                  }
                  @case ('transition') {
                    @if (selectedTransition(); as t) {
                      <h3>Transition</h3>
                      <desk-transition-editor
                        [transition]="t"
                        [definition]="def()"
                        [issues]="transitionIssues()"
                        (changed)="patchTransition(t.key, $event)"
                        (renamed)="renameTransition($event.from, $event.to)"
                        (remove)="deleteSelected()"
                      />
                    }
                  }
                  @default {
                    <div class="overview">
                      <p class="sd-muted">Select a state or a transition on the canvas to edit it.</p>
                      <dl>
                        <dt>States</dt>
                        <dd>{{ def().states.length }}</dd>
                        <dt>Transitions</dt>
                        <dd>{{ def().transitions.length }}</dd>
                        @if (detail(); as d) {
                          <dt>Open tickets on it</dt>
                          <dd>{{ d.blueprint.activeTickets }}</dd>
                        }
                      </dl>
                      @if (detail(); as d) {
                        <button sdButton variant="danger" size="sm" icon="trash" [disabled]="d.blueprint.activeTickets > 0" (click)="remove(d)">Delete blueprint</button>
                      }
                    </div>
                  }
                }
              </sd-tab>
              <sd-tab id="versions" label="Versions" icon="history" [badge]="detail()?.versions?.length ?? null">
                @if (detail(); as d) {
                  <desk-version-history
                    [versions]="d.versions"
                    [loadedId]="loadedVersionId()"
                    [current]="def()"
                    [loader]="versionLoader"
                    [canRollback]="false"
                    (open)="openVersion($event)"
                    (publish)="publishVersion($event)"
                  />
                } @else {
                  <p class="sd-muted">Versions appear after the first save.</p>
                }
              </sd-tab>
            </sd-tabs>
          </sd-card>
        </aside>
      </div>
    }
  `,
  styles: `
    .bar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      margin-bottom: 12px;
    }
    .issues,
    .warnings {
      margin-bottom: 12px;
      padding: 10px 12px;
      border-radius: var(--sd-radius-md);
      font-size: var(--sd-text-sm);
    }
    .issues {
      border: 1px solid var(--sd-danger-border);
      background: var(--sd-danger-bg);
      color: var(--sd-danger);
    }
    .issues ul {
      margin: 6px 0 0;
      padding-left: 18px;
    }
    .warnings {
      border: 1px solid var(--sd-warning-border);
      background: var(--sd-warning-bg);
    }
    .warnings p {
      margin: 0;
      display: flex;
      align-items: center;
      gap: 6px;
    }
    .settings {
      display: block;
      margin-bottom: 12px;
    }
    .settings-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 220px), 1fr));
      gap: 10px 14px;
    }
    .layout {
      display: grid;
      grid-template-columns: minmax(0, 1fr) 420px;
      gap: 14px;
      align-items: start;
    }
    @media (max-width: 1100px) {
      .layout {
        grid-template-columns: minmax(0, 1fr);
      }
    }
    .board {
      display: flex;
      flex-direction: column;
      gap: 8px;
      min-width: 0;
    }
    .tools {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
    }
    .hint {
      font-size: var(--sd-text-xs);
    }
    .canvas {
      height: 560px;
    }
    .side {
      max-height: calc(100vh - 120px);
      overflow: auto;
    }
    .form {
      display: flex;
      flex-direction: column;
      gap: 10px;
    }
    h3 {
      margin: 0 0 8px;
      font-size: var(--sd-text-md);
    }
    h4 {
      margin: 6px 0 0;
      font-size: var(--sd-text-sm);
    }
    .mono {
      font-family: var(--sd-font-mono);
    }
    .row {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    .links {
      margin: 0;
      padding-left: 16px;
      font-size: var(--sd-text-sm);
    }
    .link {
      border: 0;
      padding: 0;
      background: none;
      color: var(--sd-primary);
      cursor: pointer;
      text-align: left;
    }
    .err {
      margin: 0;
      color: var(--sd-danger);
      font-size: var(--sd-text-sm);
    }
    .overview dl {
      display: grid;
      grid-template-columns: auto 1fr;
      gap: 4px 12px;
      font-size: var(--sd-text-sm);
    }
    .overview dt {
      color: var(--sd-text-3);
    }
    .overview dd {
      margin: 0;
    }
  `,
})
export class BlueprintDesignerPage {
  protected readonly ctx = inject(AutomationContext);
  private readonly api = inject(BlueprintsApi);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(SdDialogService);

  readonly id = input<string | undefined>();

  protected readonly loading = signal(false);
  protected readonly detail = signal<BlueprintDetailDto | null>(null);
  protected readonly loadedVersionId = signal<string | null>(null);
  protected readonly name = signal('');
  protected readonly description = signal('');
  protected readonly departmentId = signal<string | null>(null);
  protected readonly categoryId = signal<string | null>(null);
  protected readonly def = signal<BlueprintDefinition>(clone(EMPTY));
  protected readonly selected = signal<CanvasSelection | null>(null);
  protected readonly connectFrom = signal<string | null>(null);
  protected readonly sideTab = signal<string | null>('inspect');
  protected readonly issues = signal<IssueMap>({});
  protected readonly issueList = signal<ValidationIssueDto[]>([]);
  protected readonly warnings = signal<ValidationIssueDto[]>([]);
  protected readonly saving = signal<'draft' | 'publish' | null>(null);
  protected readonly checking = signal(false);
  private readonly baseline = signal('');

  private readonly snapshot = computed(() => JSON.stringify([this.name(), this.description(), this.departmentId(), this.categoryId(), this.def()]));
  protected readonly dirty = computed(() => this.snapshot() !== this.baseline());
  protected readonly canPublish = computed(() => {
    const d = this.detail();
    if (!d || this.dirty()) return true;
    return d.latest?.state === 'Draft';
  });

  protected readonly statusLooks = computed<ReadonlyMap<string, StatusLook>>(
    () => new Map((this.ctx.meta()?.statuses ?? []).map((s) => [s.id, { name: s.name, color: s.color }])),
  );
  protected readonly selectedState = computed(() => {
    const s = this.selected();
    return s?.kind === 'state' ? (this.def().states.find((x) => x.key === s.key) ?? null) : null;
  });
  protected readonly selectedTransition = computed(() => {
    const s = this.selected();
    return s?.kind === 'transition' ? (this.def().transitions.find((x) => x.key === s.key) ?? null) : null;
  });
  protected readonly stateIssues = computed(() => {
    const s = this.selectedState();
    const i = s ? this.def().states.indexOf(s) : -1;
    return i < 0 ? {} : issuesUnder(this.issues(), `states[${i}]`);
  });
  protected readonly transitionIssues = computed(() => {
    const t = this.selectedTransition();
    const i = t ? this.def().transitions.indexOf(t) : -1;
    return i < 0 ? {} : issuesUnder(this.issues(), `transitions[${i}]`);
  });
  /** Issues not attached to a selectable item (reachability, initial state…) plus a count of the others. */
  protected readonly generalIssues = computed(() => {
    const list = this.issueList();
    const general = list.filter((i) => i.path === 'states' || i.path === 'transitions' || !/^(states|transitions)\[/.test(i.path));
    const itemCount = list.length - general.length;
    return itemCount > 0 ? [...general, { path: '', message: `${itemCount} issue(s) on states or transitions — select the highlighted item to see them.` }] : general;
  });
  protected readonly anyStateTransitions = computed(() =>
    this.def()
      .transitions.filter((t) => (Array.isArray(t.from) ? t.from : [t.from]).includes('*'))
      .map((t) => `${t.name} → ${this.stateName(t.to)}`),
  );

  protected readonly versionLoader = (versionId: string) => this.api.version(this.id()!, versionId);

  constructor() {
    effect(() => {
      const id = this.id();
      const ready = this.ctx.ready();
      untracked(() => {
        if (!ready) return;
        if (id) this.load(id);
        else this.resetNew();
      });
    });
  }

  private resetNew(): void {
    this.detail.set(null);
    this.name.set('');
    this.description.set('');
    this.departmentId.set(null);
    this.categoryId.set(null);
    const open = this.ctx.statusOptions().find((s) => s.description === 'Open')?.value ?? null;
    this.def.set({
      states: [{ key: 'new', name: 'New', statusId: open, isInitial: true, x: 60, y: 120 }],
      transitions: [],
    });
    this.baseline.set('');
  }

  private load(id: string): void {
    if (this.detail()?.blueprint.id === id) return;
    this.loading.set(true);
    this.api.get(id).subscribe({
      next: (d) => {
        this.apply(d);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  private apply(d: BlueprintDetailDto, versionId?: string): void {
    this.detail.set(d);
    this.name.set(d.blueprint.name);
    this.description.set(d.blueprint.description ?? '');
    this.departmentId.set(d.blueprint.departmentId);
    this.categoryId.set(d.blueprint.categoryId);
    const version = versionId === d.published?.id ? d.published : d.latest;
    this.def.set(normalize(version?.definition));
    this.loadedVersionId.set(version?.id ?? null);
    this.issues.set({});
    this.issueList.set([]);
    this.warnings.set([]);
    this.baseline.set(this.snapshot());
  }

  // ───── Editing ─────

  private mutate(fn: (d: BlueprintDefinition) => void): void {
    const next = clone(this.def());
    fn(next);
    this.def.set(next);
  }

  protected stateName(key: string): string {
    return this.def().states.find((s) => s.key === key)?.name ?? key;
  }

  protected outgoing(key: string): BlueprintTransitionDef[] {
    return this.def().transitions.filter((t) => (Array.isArray(t.from) ? t.from : [t.from]).includes(key));
  }

  protected addState(): void {
    const keys = new Set(this.def().states.map((s) => s.key));
    let n = this.def().states.length + 1;
    while (keys.has(`state_${n}`)) n++;
    const pos = autoPosition(this.def().states.length);
    const key = `state_${n}`;
    this.mutate((d) => d.states.push({ key, name: `State ${n}`, statusId: null, x: pos.x, y: pos.y, isInitial: d.states.length === 0 }));
    this.selected.set({ kind: 'state', key });
    this.sideTab.set('inspect');
  }

  protected moveState(key: string, x: number, y: number): void {
    this.mutate((d) => {
      const s = d.states.find((v) => v.key === key);
      if (s) {
        s.x = x;
        s.y = y;
      }
    });
  }

  protected patchState(key: string, change: Partial<BlueprintStateDef>): void {
    this.mutate((d) => {
      const s = d.states.find((v) => v.key === key);
      if (s) Object.assign(s, change);
    });
  }

  protected setInitial(key: string, initial: boolean): void {
    this.mutate((d) => {
      for (const s of d.states) s.isInitial = initial ? s.key === key : s.key === key ? false : !!s.isInitial;
    });
  }

  protected renameState(oldKey: string, raw: string): void {
    const key = slug(raw);
    if (!key || key === oldKey) return;
    if (this.def().states.some((s) => s.key === key)) {
      this.toast.warning('Key already used', key);
      return;
    }
    this.mutate((d) => {
      for (const s of d.states) if (s.key === oldKey) s.key = key;
      for (const t of d.transitions) {
        t.from = (Array.isArray(t.from) ? t.from : [t.from]).map((f) => (f === oldKey ? key : f));
        if (t.to === oldKey) t.to = key;
      }
    });
    this.selected.set({ kind: 'state', key });
  }

  protected addTransition(from: string, to: string): void {
    const keys = new Set(this.def().transitions.map((t) => t.key));
    let key = slug(`${from}_to_${to}`).slice(0, 46);
    let n = 2;
    while (keys.has(key)) key = `${key.slice(0, 44)}_${n++}`;
    this.mutate((d) => d.transitions.push({ key, name: `To ${this.stateName(to)}`, from: [from], to, allowedRoleIds: [], requiredFields: [], actions: [] }));
    this.selected.set({ kind: 'transition', key });
    this.sideTab.set('inspect');
  }

  protected patchTransition(key: string, next: BlueprintTransitionDef): void {
    this.mutate((d) => {
      const i = d.transitions.findIndex((t) => t.key === key);
      if (i >= 0) d.transitions[i] = clone(next);
    });
  }

  protected renameTransition(oldKey: string, key: string): void {
    if (this.def().transitions.some((t) => t.key === key)) {
      this.toast.warning('Key already used', key);
      return;
    }
    this.mutate((d) => {
      const t = d.transitions.find((x) => x.key === oldKey);
      if (t) t.key = key;
    });
    this.selected.set({ kind: 'transition', key });
  }

  protected toggleConnect(): void {
    if (this.connectFrom()) this.connectFrom.set(null);
    else if (this.selected()?.kind === 'state') this.connectFrom.set(this.selected()!.key);
  }

  protected deleteSelected(): void {
    const sel = this.selected();
    if (!sel) return;
    this.mutate((d) => {
      if (sel.kind === 'state') {
        d.states = d.states.filter((s) => s.key !== sel.key);
        d.transitions = d.transitions
          .map((t) => ({ ...t, from: (Array.isArray(t.from) ? t.from : [t.from]).filter((f) => f !== sel.key) }))
          .filter((t) => t.to !== sel.key && (t.from as string[]).length > 0);
      } else d.transitions = d.transitions.filter((t) => t.key !== sel.key);
    });
    this.selected.set(null);
  }

  // ───── Persistence ─────

  private request(publish: boolean): SaveBlueprintRequest {
    return {
      name: this.name().trim(),
      description: this.description().trim() || null,
      departmentId: this.departmentId(),
      categoryId: this.categoryId(),
      definition: this.def(),
      publish,
    };
  }

  protected save(publish: boolean): void {
    this.saving.set(publish ? 'publish' : 'draft');
    const id = this.id();
    const call = id ? this.api.update(id, this.request(publish)) : this.api.create(this.request(publish));
    call.subscribe({
      next: (d) => {
        this.saving.set(null);
        this.apply(d);
        this.ctx.refreshBlueprints();
        this.toast.success(publish ? `Published v${d.blueprint.publishedVersionNumber}` : `Draft v${d.blueprint.latestVersionNumber} saved`, d.blueprint.name);
        if (!id) void this.router.navigate(['/automation/blueprints', d.blueprint.id], { replaceUrl: true });
      },
      error: (err: unknown) => this.fail(err, publish ? 'Could not publish the blueprint' : 'Could not save the blueprint'),
    });
  }

  protected publish(): void {
    const d = this.detail();
    if (!d || this.dirty()) this.save(true);
    else if (d.latest) this.publishVersion(d.latest.id);
  }

  protected publishVersion(versionId: string): void {
    const id = this.id();
    if (!id) return;
    this.saving.set('publish');
    this.api.publish(id, versionId).subscribe({
      next: (d) => {
        this.saving.set(null);
        this.apply(d, d.published?.id);
        this.toast.success(`Published v${d.blueprint.publishedVersionNumber}`);
      },
      error: (err: unknown) => this.fail(err, 'Could not publish'),
    });
  }

  protected openVersion(versionId: string): void {
    const id = this.id();
    if (!id) return;
    this.api.version(id, versionId).subscribe((v) => {
      this.def.set(normalize(v.definition));
      this.loadedVersionId.set(v.id);
      this.selected.set(null);
      this.toast.info(`Version ${v.versionNumber} loaded`, 'Save it as a new draft or publish it to make it live again.');
    });
  }

  protected check(): void {
    this.checking.set(true);
    this.api.validate(this.def()).subscribe({
      next: (r) => {
        this.checking.set(false);
        this.issueList.set(r.errors);
        this.issues.set(issuesFromList(r.errors));
        this.warnings.set(r.warnings);
        if (r.valid) this.toast.success('Blueprint is valid', r.warnings.length ? `${r.warnings.length} warning(s).` : 'Ready to publish.');
      },
      error: (err: unknown) => this.fail(err, 'Could not check the blueprint'),
    });
  }

  protected setActive(active: boolean): void {
    const id = this.id();
    if (!id) return;
    this.api.setActive(id, active).subscribe({
      next: (d) => {
        this.detail.set(d);
        this.ctx.refreshBlueprints();
        this.toast.success(active ? 'Blueprint activated' : 'Blueprint deactivated');
      },
      error: (err: unknown) => this.fail(err, 'Could not change the blueprint'),
    });
  }

  protected async remove(d: BlueprintDetailDto): Promise<void> {
    const ok = await this.dialogs.confirm({ title: `Delete “${d.blueprint.name}”?`, message: 'The blueprint and all its versions are deleted.', danger: true, confirmLabel: 'Delete' });
    if (!ok) return;
    this.api.delete(d.blueprint.id).subscribe({
      next: () => {
        this.ctx.refreshBlueprints();
        void this.router.navigate(['/automation/blueprints']);
      },
      error: (err: unknown) => this.toast.error('Could not delete', errorMessage(err)),
    });
  }

  private fail(err: unknown, title: string): void {
    this.saving.set(null);
    this.checking.set(false);
    const issues = issuesFromError(err);
    this.issues.set(issues);
    this.issueList.set(Object.entries(issues).map(([path, message]) => ({ path, message })));
    this.toast.error(title, Object.keys(issues).length ? 'Some states or transitions need attention.' : errorMessage(err));
  }
}

function slug(raw: string): string {
  return raw
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9_-]+/g, '_')
    .replace(/^[_-]+/, '')
    .slice(0, 50);
}

/** Stored definition → editable definition (arrays present, from always a list). */
function normalize(raw: unknown): BlueprintDefinition {
  const d = (raw && typeof raw === 'object' ? raw : {}) as Partial<BlueprintDefinition>;
  return {
    states: (d.states ?? []).map((s) => ({ ...s, statusId: s.statusId ?? null, substatus: s.substatus ?? null })),
    transitions: (d.transitions ?? []).map((t) => ({
      ...t,
      from: Array.isArray(t.from) ? t.from : t.from ? [t.from] : [],
      allowedRoleIds: t.allowedRoleIds ?? [],
      requiredFields: t.requiredFields ?? [],
      actions: (t.actions ?? []).map((a) => ({ type: a.type, params: a.params ?? {} })),
    })),
  };
}
