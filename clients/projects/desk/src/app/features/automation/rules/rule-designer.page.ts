import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import {
  AutomationApi,
  FilterGroup,
  RuleActionDef,
  RuleDefinition,
  RuleDetailDto,
  SaveRuleRequest,
  ValidationIssueDto,
  emptyFilterGroup,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCard,
  SdCombobox,
  SdDialogService,
  SdFilterBuilder,
  SdFormField,
  SdIcon,
  SdInput,
  SdOption,
  SdRelativeTimePipe,
  SdSkeleton,
  SdTab,
  SdTabs,
  SdToggle,
  ToastService,
} from '@sd/ui';
import { ActionListEditor } from '../shared/action-list-editor.component';
import { AutomationContext, webhookFieldsIn } from '../shared/automation-context.service';
import { IssueMap, asFilterGroup, errorMessage, issuesFromError, issuesFromList } from '../shared/automation-ui';
import { VersionHistory } from '../shared/version-history.component';
import { SimulationPanel } from './simulation-panel.component';

/**
 * Visual rule designer: trigger → conditions (nested AND/OR filter builder over the server's field catalog) →
 * ordered actions with schema-driven editors. Saving creates an immutable draft version; publishing validates on
 * the server and makes it live. Side panel: simulation (dry run), version history with diff/roll back, conflicts
 * and validation.
 */
@Component({
  selector: 'desk-rule-designer',
  imports: [
    FormsModule,
    RouterLink,
    SdButton,
    SdCard,
    SdBadge,
    SdIcon,
    SdInput,
    SdFormField,
    SdCombobox,
    SdToggle,
    SdTabs,
    SdTab,
    SdSkeleton,
    SdFilterBuilder,
    SdRelativeTimePipe,
    ActionListEditor,
    SimulationPanel,
    VersionHistory,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (!ctx.ready() || loading()) {
      <sd-skeleton [lines]="8" height="28px" />
    } @else {
      <div class="bar">
        <a sdButton variant="ghost" size="sm" icon="arrow-left" routerLink="/automation/rules">Rules</a>
        <span class="sd-spacer"></span>
        @if (detail(); as d) {
          @if (d.rule.publishedVersionNumber) {
            <sd-badge tone="success" variant="outline">Live: v{{ d.rule.publishedVersionNumber }}</sd-badge>
          } @else {
            <sd-badge tone="neutral" variant="outline">Not published</sd-badge>
          }
          @if (dirty()) {
            <sd-badge tone="warning">Unsaved changes</sd-badge>
          }
          <sd-toggle [value]="d.rule.isActive" [disabled]="!d.rule.publishedVersionNumber" (valueChange)="setActive($event)">Active</sd-toggle>
        }
        <input sdInput class="note" aria-label="Change note" placeholder="Change note (optional)" [(ngModel)]="changeNote" />
        <button sdButton icon="list-checks" (click)="check()" [loading]="checking()">Check</button>
        <button sdButton icon="file-plus" (click)="save(false)" [loading]="saving() === 'draft'" [disabled]="!dirty() && !!id()">Save draft</button>
        <button sdButton variant="primary" icon="rocket" (click)="publish()" [loading]="saving() === 'publish'" [disabled]="!canPublish()">Publish</button>
      </div>

      @if (topIssues().length) {
        <div class="issues" role="alert">
          <strong>Fix these before publishing:</strong>
          <ul>
            @for (i of topIssues(); track i.path) {
              <li><code>{{ i.path }}</code> — {{ i.message }}</li>
            }
          </ul>
        </div>
      }
      @if (warnings().length) {
        <div class="warnings" role="status">
          @for (w of warnings(); track w.path) {
            <p><sd-icon name="triangle-alert" [size]="13" /> {{ w.message }}</p>
          }
        </div>
      }

      <div class="layout">
        <div class="main">
          <sd-card>
            <div class="basics">
              <sd-form-field label="Rule name" required [error]="issues()['name']">
                <input sdInput [ngModel]="name()" (ngModelChange)="name.set($event)" maxlength="200" />
              </sd-form-field>
              <sd-form-field label="Description" optional>
                <input sdInput [ngModel]="description()" (ngModelChange)="description.set($event)" />
              </sd-form-field>
              <sd-toggle [value]="stopProcessing()" (valueChange)="stopProcessing.set($event)">Stop processing later rules when this rule matches</sd-toggle>
            </div>
          </sd-card>

          <sd-card title="When" subtitle="What starts the rule">
            <div class="triggers" role="radiogroup" aria-label="Trigger">
              @for (t of ctx.catalog()!.triggers; track t.key) {
                <button type="button" role="radio" class="trigger" [attr.aria-checked]="trigger() === t.key" (click)="trigger.set(t.key)">
                  <sd-icon [name]="t.kind === 'scheduled' ? 'clock' : t.kind === 'webhook' ? 'webhook' : 'zap'" [size]="15" />
                  <span class="t-label">{{ t.label }}</span>
                  <span class="t-desc">{{ t.description }}</span>
                </button>
              }
            </div>
            @if (trigger() === 'ticket.updated') {
              <sd-form-field label="Only when these fields change" hint="Leave empty to run on any update." optional>
                <sd-combobox multiple [options]="fieldOptions()" [value]="triggerFields()" (valueChange)="triggerFields.set($any($event) ?? [])" placeholder="Any field" />
              </sd-form-field>
            }
            @if (trigger() === 'scheduled') {
              <sd-form-field label="Run every (minutes)" hint="Tickets matching the conditions are processed once per interval." [error]="issues()['scheduleIntervalMinutes']">
                <input sdInput type="number" min="5" max="10080" class="interval" [ngModel]="interval()" (ngModelChange)="interval.set(+$event)" />
              </sd-form-field>
              @if (detail()?.rule?.nextScheduledRunAt; as next) {
                <p class="sd-muted small">Next run {{ next | sdRelativeTime }}.</p>
              }
            }
            @if (trigger() === 'webhook') {
              @if (detail()?.rule?.webhookUrl; as path) {
                <div class="webhook">
                  <sd-form-field label="Webhook URL" hint="POST JSON with ticketId or ticketNumber; send the secret in the X-Webhook-Secret header.">
                    <input sdInput readonly [value]="origin + path" />
                  </sd-form-field>
                  <button sdButton size="sm" icon="refresh" (click)="rotateSecret()">New secret</button>
                </div>
              } @else {
                <p class="sd-muted small">The webhook URL and secret are issued when the rule is saved.</p>
              }
              @if (secret(); as s) {
                <div class="secret" role="status">
                  <strong>Copy this secret now — it is shown only once:</strong>
                  <code>{{ s }}</code>
                  <button sdButton size="sm" icon="copy" (click)="copy(s)">Copy</button>
                </div>
              }
            }
          </sd-card>

          <sd-card title="If" subtitle="Conditions on the ticket (and the triggering change)">
            @if (conditionIssues().length) {
              <ul class="inline-issues" role="alert">
                @for (i of conditionIssues(); track i.path) {
                  <li><code>{{ i.path.replace('conditions.', '') }}</code> {{ i.message }}</li>
                }
              </ul>
            }
            <sd-filter-builder [fields]="filterFields()" [value]="conditions()" (valueChange)="conditions.set($event)" [maxDepth]="4" />
            @if (trigger() === 'webhook') {
              <div class="payload-field">
                <input sdInput aria-label="Payload field" placeholder="Payload field, e.g. event or order.total" [(ngModel)]="newPayloadField" />
                <button sdButton size="sm" icon="plus" (click)="addPayloadField()">Add payload field</button>
              </div>
            }
          </sd-card>

          <sd-card title="Then" subtitle="Actions run in this order as “Automation: rule name”">
            <desk-action-list [actions]="actions()" (actionsChange)="actions.set($event)" [issues]="issues()" pathPrefix="actions" />
          </sd-card>

          @if (detail(); as d) {
            <div class="danger">
              <button sdButton variant="danger" size="sm" icon="trash" (click)="remove(d)">Delete rule</button>
            </div>
          }
        </div>

        <aside class="side" aria-label="Rule tools">
          <sd-card padding="sm">
            <sd-tabs [(selected)]="sideTab" variant="pill" ariaLabel="Rule tools">
              <sd-tab id="simulate" label="Simulate" icon="play">
                <desk-simulation-panel
                  [ruleId]="id() ?? null"
                  [versionId]="loadedVersionId()"
                  [trigger]="trigger()"
                  [draft]="definition()"
                  [hasDraft]="dirty() || !id()"
                />
              </sd-tab>
              <sd-tab id="versions" label="Versions" icon="history" [badge]="detail()?.versions?.length ?? null">
                @if (detail(); as d) {
                  <desk-version-history
                    [versions]="d.versions"
                    [loadedId]="loadedVersionId()"
                    [current]="definition()"
                    [loader]="versionLoader"
                    (open)="openVersion($event)"
                    (publish)="publishVersion($event)"
                    (rollback)="rollback($event)"
                  />
                } @else {
                  <p class="sd-muted small">Versions appear after the first save.</p>
                }
              </sd-tab>
              <sd-tab id="conflicts" label="Conflicts" icon="triangle-alert" [badge]="detail()?.conflicts?.length || null">
                @if (detail()?.conflicts?.length) {
                  <ul class="conflicts">
                    @for (c of detail()!.conflicts; track $index) {
                      <li>
                        <sd-badge size="sm" tone="warning">{{ c.kind.replace('_', ' ') }}</sd-badge>
                        <p>{{ c.explanation }}</p>
                        <a [routerLink]="['/automation/rules', c.ruleId === id() ? c.otherRuleId : c.ruleId]">Open “{{ c.ruleId === id() ? c.otherRuleName : c.ruleName }}”</a>
                      </li>
                    }
                  </ul>
                } @else {
                  <p class="sd-muted small">No conflicts with other active rules on this trigger.</p>
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
      position: sticky;
      top: 0;
      z-index: var(--sd-z-sticky);
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      margin: -4px 0 12px;
      padding: 8px 0;
      background: var(--sd-bg);
    }
    .note {
      width: 220px;
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
      color: var(--sd-text);
    }
    .warnings p {
      margin: 0;
      display: flex;
      gap: 6px;
      align-items: center;
    }
    .layout {
      display: grid;
      grid-template-columns: minmax(0, 1fr) 400px;
      gap: 16px;
      align-items: start;
    }
    @media (max-width: 1100px) {
      .layout {
        grid-template-columns: minmax(0, 1fr);
      }
    }
    .main {
      display: flex;
      flex-direction: column;
      gap: 14px;
      min-width: 0;
    }
    .side {
      position: sticky;
      top: 56px;
      max-height: calc(100vh - 140px);
      overflow: auto;
    }
    .basics {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 12px;
      align-items: end;
    }
    .basics sd-toggle {
      grid-column: 1 / -1;
    }
    @media (max-width: 720px) {
      .basics {
        grid-template-columns: 1fr;
      }
    }
    .triggers {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(190px, 1fr));
      gap: 8px;
      margin-bottom: 12px;
    }
    .trigger {
      display: grid;
      grid-template-columns: auto 1fr;
      gap: 2px 8px;
      align-items: center;
      padding: 8px 10px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      color: var(--sd-text);
      text-align: left;
      cursor: pointer;
    }
    .trigger:hover {
      border-color: var(--sd-border-strong);
    }
    .trigger[aria-checked='true'] {
      border-color: var(--sd-primary);
      background: var(--sd-primary-soft);
    }
    .trigger:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .t-label {
      font-weight: var(--sd-weight-semibold);
      font-size: var(--sd-text-sm);
    }
    .t-desc {
      grid-column: 1 / -1;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      line-height: 1.35;
    }
    .interval {
      width: 140px;
    }
    .small {
      font-size: var(--sd-text-xs);
    }
    .webhook {
      display: flex;
      align-items: flex-end;
      gap: 8px;
    }
    .webhook sd-form-field {
      flex: 1;
    }
    .secret {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      margin-top: 10px;
      padding: 10px;
      border: 1px solid var(--sd-warning-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-warning-bg);
    }
    .secret code {
      font-family: var(--sd-font-mono);
      word-break: break-all;
    }
    .inline-issues {
      margin: 0 0 10px;
      padding-left: 18px;
      color: var(--sd-danger);
      font-size: var(--sd-text-sm);
    }
    .payload-field {
      display: flex;
      gap: 8px;
      margin-top: 10px;
    }
    .payload-field input {
      max-width: 320px;
    }
    .conflicts {
      display: flex;
      flex-direction: column;
      gap: 10px;
      margin: 0;
      padding: 0;
      list-style: none;
      font-size: var(--sd-text-sm);
    }
    .conflicts p {
      margin: 4px 0;
    }
    .danger {
      display: flex;
      justify-content: flex-end;
    }
  `,
})
export class RuleDesignerPage {
  protected readonly ctx = inject(AutomationContext);
  private readonly api = inject(AutomationApi);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(SdDialogService);

  /** Route parameter (absent for /rules/new). */
  readonly id = input<string | undefined>();

  protected readonly origin = typeof location !== 'undefined' ? location.origin : '';
  protected readonly loading = signal(false);
  protected readonly detail = signal<RuleDetailDto | null>(null);
  protected readonly loadedVersionId = signal<string | null>(null);
  protected readonly name = signal('');
  protected readonly description = signal('');
  protected readonly trigger = signal('ticket.created');
  protected readonly stopProcessing = signal(false);
  protected readonly interval = signal(60);
  protected readonly triggerFields = signal<string[]>([]);
  protected readonly conditions = signal<FilterGroup>(emptyFilterGroup());
  protected readonly actions = signal<RuleActionDef[]>([]);
  protected readonly extraWebhookFields = signal<string[]>([]);
  protected readonly newPayloadField = signal('');
  protected readonly changeNote = signal('');
  protected readonly issues = signal<IssueMap>({});
  protected readonly warnings = signal<ValidationIssueDto[]>([]);
  protected readonly saving = signal<'draft' | 'publish' | null>(null);
  protected readonly checking = signal(false);
  protected readonly secret = signal<string | null>(null);
  protected readonly sideTab = signal<string | null>('simulate');
  private readonly baseline = signal('');

  protected readonly definition = computed<RuleDefinition>(() => ({
    trigger: this.trigger() === 'ticket.updated' && this.triggerFields().length ? { fields: this.triggerFields() } : null,
    conditions: this.conditions(),
    actions: this.actions(),
  }));

  private readonly snapshot = computed(() =>
    JSON.stringify([this.name(), this.description(), this.trigger(), this.stopProcessing(), this.trigger() === 'scheduled' ? this.interval() : null, this.definition()]),
  );
  protected readonly dirty = computed(() => this.snapshot() !== this.baseline());
  protected readonly canPublish = computed(() => {
    const d = this.detail();
    if (!d || this.dirty()) return true;
    return d.latest !== null && d.latest.state === 'Draft';
  });

  protected readonly filterFields = computed(() => {
    const used = webhookFieldsIn(this.conditions());
    const extra = [...new Set([...this.extraWebhookFields(), ...used])];
    return this.ctx.conditionFields(this.trigger(), extra);
  });
  protected readonly fieldOptions = computed<SdOption[]>(() =>
    (this.ctx.catalog()?.fields ?? []).map((f) => ({ value: f.field, label: f.label })).concat([{ value: 'tags', label: 'Tags' }, { value: 'customFields', label: 'Any custom field' }]).filter((o, i, a) => a.findIndex((x) => x.value === o.value) === i),
  );
  protected readonly conditionIssues = computed(() =>
    Object.entries(this.issues())
      .filter(([k]) => k.startsWith('conditions'))
      .map(([path, message]) => ({ path, message })),
  );
  protected readonly topIssues = computed(() =>
    Object.entries(this.issues())
      .filter(([k]) => !k.startsWith('conditions') && !k.startsWith('actions[') && k !== 'name')
      .map(([path, message]) => ({ path, message })),
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
    this.loadedVersionId.set(null);
    this.name.set('');
    this.description.set('');
    this.trigger.set('ticket.created');
    this.stopProcessing.set(false);
    this.interval.set(60);
    this.triggerFields.set([]);
    this.conditions.set(emptyFilterGroup());
    this.actions.set([]);
    this.issues.set({});
    this.warnings.set([]);
    this.baseline.set('');
  }

  private load(id: string): void {
    if (this.detail()?.rule.id === id) return;
    this.loading.set(true);
    this.api.get(id).subscribe({
      next: (d) => {
        this.apply(d, d.latest?.id ?? null);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  /** Puts a rule (and one of its versions) into the editor and marks it clean. */
  private apply(d: RuleDetailDto, versionId: string | null): void {
    this.detail.set(d);
    if (d.webhookSecret) this.secret.set(d.webhookSecret);
    this.name.set(d.rule.name);
    this.description.set(d.rule.description ?? '');
    this.trigger.set(d.rule.trigger);
    this.stopProcessing.set(d.rule.stopProcessing);
    this.interval.set(d.rule.scheduleIntervalMinutes ?? 60);
    const version = versionId === d.latest?.id ? d.latest : versionId === d.published?.id ? d.published : d.latest;
    this.loadDefinition(version?.definition);
    this.loadedVersionId.set(version?.id ?? null);
    this.issues.set({});
    this.warnings.set([]);
    this.baseline.set(this.snapshot());
  }

  private loadDefinition(raw: unknown): void {
    const def = (raw && typeof raw === 'object' ? raw : {}) as Partial<RuleDefinition>;
    this.conditions.set(asFilterGroup(def.conditions));
    this.actions.set(Array.isArray(def.actions) ? def.actions.map((a) => ({ type: a.type, params: a.params ?? {} })) : []);
    this.triggerFields.set(def.trigger?.fields ?? []);
  }

  protected openVersion(versionId: string): void {
    const id = this.id();
    if (!id) return;
    this.api.version(id, versionId).subscribe((v) => {
      this.loadDefinition(v.definition);
      this.loadedVersionId.set(v.id);
      this.toast.info(`Version ${v.versionNumber} loaded`, 'Save it as a new draft or publish it to make it live.');
    });
  }

  private request(publish: boolean): SaveRuleRequest {
    return {
      name: this.name().trim(),
      description: this.description().trim() || null,
      trigger: this.trigger(),
      definition: this.definition(),
      stopProcessing: this.stopProcessing(),
      scheduleIntervalMinutes: this.trigger() === 'scheduled' ? this.interval() : null,
      publish,
      changeNote: this.changeNote().trim() || null,
    };
  }

  protected save(publish: boolean): void {
    this.saving.set(publish ? 'publish' : 'draft');
    this.issues.set({});
    const id = this.id();
    const call = id ? this.api.update(id, this.request(publish)) : this.api.create(this.request(publish));
    call.subscribe({
      next: (d) => {
        this.saving.set(null);
        this.changeNote.set('');
        this.apply(d, d.latest?.id ?? null);
        this.toast.success(publish ? `Published v${d.rule.publishedVersionNumber}` : `Draft v${d.rule.latestVersionNumber} saved`, d.rule.name);
        if (!id) void this.router.navigate(['/automation/rules', d.rule.id], { replaceUrl: true });
      },
      error: (err: unknown) => this.fail(err, publish ? 'Could not publish the rule' : 'Could not save the rule'),
    });
  }

  protected publish(): void {
    const d = this.detail();
    if (!d || this.dirty()) {
      this.save(true);
      return;
    }
    if (d.latest) this.publishVersion(d.latest.id);
  }

  protected publishVersion(versionId: string): void {
    const id = this.id();
    if (!id) return;
    this.saving.set('publish');
    this.api.publish(id, versionId, this.changeNote().trim() || null).subscribe({
      next: (d) => {
        this.saving.set(null);
        this.apply(d, d.published?.id ?? null);
        this.toast.success(`Published v${d.rule.publishedVersionNumber}`, d.rule.name);
      },
      error: (err: unknown) => this.fail(err, 'Could not publish the version'),
    });
  }

  protected async rollback(versionId: string): Promise<void> {
    const id = this.id();
    const v = this.detail()?.versions.find((x) => x.id === versionId);
    if (!id || !v) return;
    const ok = await this.dialogs.confirm({
      title: `Roll back to v${v.versionNumber}?`,
      message: `Its definition is published again as a new version; v${v.versionNumber} itself stays unchanged in the history.`,
      confirmLabel: 'Roll back',
    });
    if (!ok) return;
    this.api.rollback(id, versionId).subscribe({
      next: (d) => {
        this.apply(d, d.published?.id ?? null);
        this.toast.success(`Rolled back — now live as v${d.rule.publishedVersionNumber}`);
      },
      error: (err: unknown) => this.fail(err, 'Could not roll back'),
    });
  }

  protected check(): void {
    this.checking.set(true);
    this.api.validate(this.trigger(), this.definition()).subscribe({
      next: (r) => {
        this.checking.set(false);
        this.issues.set(issuesFromList(r.errors));
        this.warnings.set(r.warnings);
        if (r.valid) this.toast.success('Looks good', r.warnings.length ? `${r.warnings.length} warning(s) to review.` : 'The rule can be published.');
      },
      error: (err: unknown) => this.fail(err, 'Could not check the rule'),
    });
  }

  protected setActive(active: boolean): void {
    const id = this.id();
    if (!id) return;
    this.api.setActive(id, active).subscribe({
      next: (d) => {
        this.detail.set({ ...d, webhookSecret: null });
        this.toast.success(active ? 'Rule activated' : 'Rule deactivated');
      },
      error: (err: unknown) => this.fail(err, 'Could not change the rule'),
    });
  }

  protected rotateSecret(): void {
    const id = this.id();
    if (!id) return;
    this.api.rotateWebhookSecret(id).subscribe((d) => {
      this.secret.set(d.webhookSecret);
      this.detail.set(d);
    });
  }

  protected copy(text: string): void {
    void navigator.clipboard?.writeText(text).then(() => this.toast.success('Copied'));
  }

  protected addPayloadField(): void {
    const raw = this.newPayloadField().trim().replace(/^webhook\./, '');
    if (!raw) return;
    this.extraWebhookFields.update((list) => [...new Set([...list, `webhook.${raw}`])]);
    this.newPayloadField.set('');
  }

  protected async remove(d: RuleDetailDto): Promise<void> {
    const ok = await this.dialogs.confirm({ title: `Delete “${d.rule.name}”?`, message: 'The rule and its versions are deleted. Execution history stays.', danger: true, confirmLabel: 'Delete' });
    if (!ok) return;
    this.api.delete(d.rule.id).subscribe(() => {
      this.toast.success('Rule deleted');
      void this.router.navigate(['/automation/rules']);
    });
  }

  private fail(err: unknown, title: string): void {
    this.saving.set(null);
    this.checking.set(false);
    const issues = issuesFromError(err);
    this.issues.set(issues);
    this.toast.error(title, Object.keys(issues).length ? 'Some settings need attention — see the highlighted fields.' : errorMessage(err));
  }
}
