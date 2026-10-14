import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import {
  ActiveTrackerDto,
  ApiError,
  EngineeringAffectedTicketsDto,
  EngineeringApi,
  EngineeringAssigneeDto,
  EngineeringLinkViewDto,
  EscalationResultDto,
  Guid,
  TicketEngineeringDto,
  saveErrorMessage,
  validationMessages,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCheckbox,
  SdChipsInput,
  SdDialogService,
  SdEmptyState,
  SdFormField,
  SdIcon,
  SdInput,
  SdRelativeTimePipe,
  SdRichTextEditor,
  SdSkeleton,
  SdTone,
  ToastService,
} from '@sd/ui';

type Mode = 'list' | 'escalate' | 'link';

interface EscalateForm {
  trackerId: Guid | null;
  itemType: string;
  title: string;
  reproStepsHtml: string;
  severity: string;
  priority: string;
  areaPath: string;
  iterationPath: string;
  tags: string[];
  assigneeUserId: Guid | null;
  setSubstatus: boolean;
}

/**
 * Engineering side panel of a ticket: linked work items with live state (fix version, release, last sync, orphaned
 * items keep their last known data), other tickets affected, refresh / open in tracker / unlink, and the forms to
 * escalate (new work item prefilled from the ticket) or link an existing item by id or URL.
 */
@Component({
  selector: 'desk-engineering-panel',
  imports: [FormsModule, RouterLink, SdBadge, SdButton, SdCheckbox, SdChipsInput, SdEmptyState, SdFormField, SdIcon, SdInput, SdRelativeTimePipe, SdRichTextEditor, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel" aria-label="Engineering">
      @if (loading() && !data()) {
        <sd-skeleton [lines]="4" height="18px" />
      } @else if (error(); as e) {
        <div class="error" role="alert">
          <sd-icon name="circle-alert" [size]="16" />
          <span>{{ e }}</span>
          <button sdButton size="sm" variant="ghost" icon="refresh" (click)="load(true)">Retry</button>
        </div>
      } @else if (data(); as d) {
        @switch (mode()) {
          @case ('list') {
            <div class="head">
              <h3>Engineering work items</h3>
              <button sdButton size="sm" variant="ghost" icon="refresh" iconOnly aria-label="Refresh state from the trackers" [loading]="loading()" (click)="load(true)"></button>
            </div>
            @if (d.canLink) {
              @if (d.trackers.length > 0) {
                <div class="actions">
                  <button sdButton size="sm" variant="primary" icon="bug" (click)="openEscalate()">Escalate</button>
                  <button sdButton size="sm" icon="link" (click)="openLink()">Link existing</button>
                </div>
              } @else {
                <p class="sd-muted small">No engineering tracker is set up. An administrator can connect Azure DevOps, GitHub or the built-in board under Administration → Engineering.</p>
              }
            }
            @if (d.links.length === 0) {
              <sd-empty-state compact icon="bug" title="Not escalated" message="Escalate a product defect to engineering or link an existing work item to follow its fix here." />
            } @else {
              <ul class="links">
                @for (l of d.links; track l.id) {
                  <li class="link" [class.orphaned]="l.isOrphaned">
                    <div class="top">
                      <sd-icon [name]="l.itemType === 'Feature' ? 'lightbulb' : l.itemType === 'Task' ? 'list-checks' : 'bug'" [size]="15" />
                      @if (l.provider === 'Local') {
                        <a class="key" [routerLink]="['/engineering']" [queryParams]="{ item: l.externalId }">{{ l.displayId }}</a>
                      } @else if (l.url) {
                        <a class="key" [href]="l.url" target="_blank" rel="noopener noreferrer">{{ l.displayId }}</a>
                      } @else {
                        <span class="key">{{ l.displayId }}</span>
                      }
                      <span class="title">{{ l.title }}</span>
                    </div>
                    <div class="badges">
                      <sd-badge size="sm" [tone]="stateTone(l)" [dot]="true">{{ l.state ?? 'Unknown' }}</sd-badge>
                      @if (l.isReleased) {
                        <sd-badge size="sm" tone="success" icon="rocket">Released{{ l.releaseName ? ' ' + l.releaseName : '' }}</sd-badge>
                      } @else if (l.fixVersion) {
                        <sd-badge size="sm" variant="outline">Fix in {{ l.fixVersion }}</sd-badge>
                      }
                      @if (l.isOrphaned) {
                        <sd-badge size="sm" tone="warning" icon="triangle-alert">{{ l.orphanReason === 'moved' ? 'Moved' : 'Deleted' }} in tracker</sd-badge>
                      }
                      @if (l.customerNotified) {
                        <sd-badge size="sm" tone="info" icon="mail">Customer notified</sd-badge>
                      }
                    </div>
                    <div class="meta sd-muted">
                      <span>{{ l.trackerName }}</span>
                      @if (l.assignedTo) {
                        <span>· {{ l.assignedTo }}</span>
                      }
                      @if (l.lastSyncedAt) {
                        <span>· synced {{ l.lastSyncedAt | sdRelativeTime }}</span>
                      }
                    </div>
                    @if (l.isOrphaned) {
                      <p class="note">The work item was {{ l.orphanReason === 'moved' ? 'moved out of the project' : 'deleted' }}; the last known data is kept.</p>
                    }
                    @if (l.syncError) {
                      <p class="sync-error" role="status"><sd-icon name="circle-alert" [size]="13" /> Sync failed: {{ l.syncError }}</p>
                    }
                    @if (l.artifacts.length > 0) {
                      <ul class="artifacts" aria-label="Linked changes">
                        @for (a of l.artifacts; track a.kind + a.id) {
                          <li>
                            <sd-icon [name]="a.kind === 'Release' ? 'rocket' : 'git-pull-request'" [size]="13" />
                            @if (a.url) {
                              <a [href]="a.url" target="_blank" rel="noopener noreferrer">{{ artifactLabel(a.kind) }} {{ a.id }}</a>
                            } @else {
                              <span>{{ artifactLabel(a.kind) }} {{ a.id }}</span>
                            }
                            @if (a.state) {
                              <span class="sd-muted">({{ a.state }})</span>
                            }
                          </li>
                        }
                      </ul>
                    }
                    <div class="row-actions">
                      <button
                        sdButton
                        size="sm"
                        variant="ghost"
                        [icon]="expanded() === l.id ? 'chevron-up' : 'chevron-down'"
                        [attr.aria-expanded]="expanded() === l.id"
                        [disabled]="l.ticketCount <= 1"
                        (click)="toggleAffected(l)"
                      >
                        {{ l.ticketCount }} ticket{{ l.ticketCount === 1 ? '' : 's' }} · {{ l.customerCount }} customer{{ l.customerCount === 1 ? '' : 's' }}
                      </button>
                      <span class="sd-spacer"></span>
                      <button sdButton size="sm" variant="ghost" icon="refresh" iconOnly [attr.aria-label]="'Refresh ' + l.displayId" [disabled]="l.isOrphaned" [loading]="busy() === l.id" (click)="refresh(l)"></button>
                      @if (l.url && l.provider !== 'Local') {
                        <a sdButton size="sm" variant="ghost" icon="external-link" iconOnly [href]="l.url" target="_blank" rel="noopener noreferrer" [attr.aria-label]="'Open ' + l.displayId + ' in ' + l.trackerName"></a>
                      }
                      @if (d.canLink) {
                        <button sdButton size="sm" variant="ghost" icon="x" iconOnly [attr.aria-label]="'Unlink ' + l.displayId" (click)="unlink(l)"></button>
                      }
                    </div>
                    @if (expanded() === l.id) {
                      <div class="affected">
                        @if (affected(); as a) {
                          @if (a.tickets.length === 0 && a.hidden === 0) {
                            <p class="sd-muted small">No other tickets.</p>
                          }
                          <ul>
                            @for (t of a.tickets; track t.id) {
                              <li>
                                <a [routerLink]="['/tickets', t.id]">{{ t.displayNumber }}</a>
                                <span class="sd-truncate">{{ t.subject }}</span>
                                <span class="sd-muted small">{{ t.customerName ?? '' }} · {{ t.status }}</span>
                              </li>
                            }
                          </ul>
                          @if (a.hidden > 0) {
                            <p class="sd-muted small">+{{ a.hidden }} ticket{{ a.hidden === 1 ? '' : 's' }} outside your access.</p>
                          }
                        } @else {
                          <sd-skeleton [lines]="2" />
                        }
                      </div>
                    }
                  </li>
                }
              </ul>
            }
          }
          @case ('escalate') {
            <form class="form" (ngSubmit)="escalate()" aria-label="Escalate to engineering">
              <div class="head">
                <h3>Escalate to engineering</h3>
                <button sdButton type="button" size="sm" variant="ghost" icon="x" iconOnly aria-label="Cancel escalation" (click)="mode.set('list')"></button>
              </div>
              <sd-form-field label="Tracker" required [error]="errors()['trackerId']">
                <select sdInput name="tracker" [ngModel]="form().trackerId" (ngModelChange)="patch({ trackerId: $event })">
                  @for (t of d.trackers; track t.id) {
                    <option [ngValue]="t.id">{{ t.name }}{{ t.project ? ' — ' + t.project : '' }}</option>
                  }
                </select>
              </sd-form-field>
              @if (prefillLoading()) {
                <sd-skeleton [lines]="5" height="16px" />
              } @else {
                <div class="two">
                  <sd-form-field label="Type">
                    <select sdInput name="type" [ngModel]="form().itemType" (ngModelChange)="patch({ itemType: $event })">
                      @for (t of selectedTracker()?.itemTypes ?? ['Bug']; track t) {
                        <option [value]="t">{{ t }}</option>
                      }
                    </select>
                  </sd-form-field>
                  <sd-form-field label="Priority">
                    <select sdInput name="priority" [ngModel]="form().priority" (ngModelChange)="patch({ priority: $event })">
                      @for (p of priorities; track p) {
                        <option [value]="p">{{ p }}</option>
                      }
                    </select>
                  </sd-form-field>
                </div>
                <sd-form-field label="Title" required [error]="errors()['title']">
                  <input sdInput name="title" maxlength="255" [ngModel]="form().title" (ngModelChange)="patch({ title: $event })" />
                </sd-form-field>
                @if (form().itemType === 'Bug') {
                  <sd-form-field label="Severity">
                    <select sdInput name="severity" [ngModel]="form().severity" (ngModelChange)="patch({ severity: $event })">
                      @for (s of severities; track s) {
                        <option [value]="s">{{ s }}</option>
                      }
                    </select>
                  </sd-form-field>
                }
                <sd-form-field label="Repro steps" hint="Prefilled from the ticket: description, customer messages, analysis notes, product context and impact.">
                  <sd-rich-text-editor toolbar="compact" minHeight="140px" maxHeight="300px" ariaLabel="Repro steps" [value]="form().reproStepsHtml" (valueChange)="patch({ reproStepsHtml: $event })" />
                </sd-form-field>
                @if (selectedTracker()?.supportsAssignee) {
                  <sd-form-field label="Assign to engineer" optional [error]="errors()['assigneeUserId']">
                    <select sdInput name="assignee" [ngModel]="form().assigneeUserId" (ngModelChange)="patch({ assigneeUserId: $event })">
                      <option [ngValue]="null">Unassigned</option>
                      @for (a of assignees(); track a.id) {
                        <option [ngValue]="a.id">{{ a.name }}{{ a.isLightAgent ? ' (engineer)' : '' }}</option>
                      }
                    </select>
                  </sd-form-field>
                } @else {
                  <div class="two">
                    <sd-form-field label="Area path" optional>
                      <input sdInput name="area" maxlength="300" [ngModel]="form().areaPath" (ngModelChange)="patch({ areaPath: $event })" />
                    </sd-form-field>
                    <sd-form-field label="Iteration path" optional>
                      <input sdInput name="iteration" maxlength="300" [ngModel]="form().iterationPath" (ngModelChange)="patch({ iterationPath: $event })" />
                    </sd-form-field>
                  </div>
                }
                <sd-form-field label="Tags" optional [error]="errors()['tags']">
                  <sd-chips-input ariaLabel="Tags" placeholder="Add tag" [value]="form().tags" (valueChange)="patch({ tags: $event })" />
                </sd-form-field>
                @if (impact() > 0) {
                  <p class="sd-muted small">Customer impact: {{ impact() }} customer{{ impact() === 1 ? '' : 's' }} (this ticket and linked tickets).</p>
                }
                <sd-checkbox [value]="form().setSubstatus" (valueChange)="patch({ setSubstatus: $event })">Set the ticket substatus “Engineering”</sd-checkbox>
              }
              @if (formError()) {
                <p class="form-error" role="alert">{{ formError() }}</p>
              }
              <div class="footer">
                <button sdButton type="button" variant="ghost" (click)="mode.set('list')">Cancel</button>
                <button sdButton type="submit" variant="primary" icon="send" [loading]="saving()" [disabled]="prefillLoading()">Create work item</button>
              </div>
            </form>
          }
          @case ('link') {
            <form class="form" (ngSubmit)="link()" aria-label="Link an existing work item">
              <div class="head">
                <h3>Link existing work item</h3>
                <button sdButton type="button" size="sm" variant="ghost" icon="x" iconOnly aria-label="Cancel linking" (click)="mode.set('list')"></button>
              </div>
              <sd-form-field label="Tracker" required>
                <select sdInput name="tracker" [ngModel]="linkTrackerId()" (ngModelChange)="linkTrackerId.set($event)">
                  @for (t of d.trackers; track t.id) {
                    <option [ngValue]="t.id">{{ t.name }}{{ t.project ? ' — ' + t.project : '' }}</option>
                  }
                </select>
              </sd-form-field>
              <sd-form-field label="Work item id or URL" required hint="e.g. 1234, #1234, ENG-12 or the work item / issue URL." [error]="errors()['reference']">
                <input sdInput name="reference" autocomplete="off" [ngModel]="reference()" (ngModelChange)="reference.set($event)" />
              </sd-form-field>
              <sd-checkbox [value]="linkSubstatus()" (valueChange)="linkSubstatus.set($event)">Set the ticket substatus “Engineering”</sd-checkbox>
              @if (formError()) {
                <p class="form-error" role="alert">{{ formError() }}</p>
              }
              <div class="footer">
                <button sdButton type="button" variant="ghost" (click)="mode.set('list')">Cancel</button>
                <button sdButton type="submit" variant="primary" icon="link" [loading]="saving()" [disabled]="!reference().trim()">Link</button>
              </div>
            </form>
          }
        }
      }
    </section>
  `,
  styles: `
    .panel {
      display: flex;
      flex-direction: column;
      gap: 10px;
    }
    .head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
    }
    h3 {
      margin: 0;
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-semibold);
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    .small {
      font-size: var(--sd-text-xs);
      margin: 0;
    }
    .error {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 10px;
      border: 1px solid var(--sd-danger-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-danger-bg);
      color: var(--sd-danger);
      font-size: var(--sd-text-sm);
    }
    .links {
      display: flex;
      flex-direction: column;
      gap: 8px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .link {
      display: flex;
      flex-direction: column;
      gap: 6px;
      padding: 10px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
    }
    .link.orphaned {
      border-style: dashed;
      background: var(--sd-surface-sunken);
    }
    .top {
      display: flex;
      align-items: baseline;
      gap: 6px;
      min-width: 0;
    }
    .top sd-icon {
      align-self: center;
      color: var(--sd-text-3);
    }
    .key {
      flex: none;
      font-family: var(--sd-font-mono);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      color: var(--sd-primary);
    }
    .title {
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-medium);
      overflow-wrap: anywhere;
    }
    .badges {
      display: flex;
      flex-wrap: wrap;
      gap: 4px;
    }
    .meta {
      display: flex;
      flex-wrap: wrap;
      gap: 4px;
      font-size: var(--sd-text-xs);
    }
    .note,
    .sync-error {
      margin: 0;
      font-size: var(--sd-text-xs);
    }
    .sync-error {
      display: flex;
      align-items: center;
      gap: 4px;
      color: var(--sd-warning);
    }
    .artifacts {
      display: flex;
      flex-direction: column;
      gap: 2px;
      margin: 0;
      padding: 0;
      list-style: none;
      font-size: var(--sd-text-xs);
    }
    .artifacts li {
      display: flex;
      align-items: center;
      gap: 4px;
    }
    .row-actions {
      display: flex;
      align-items: center;
      gap: 2px;
    }
    .affected ul {
      display: flex;
      flex-direction: column;
      gap: 4px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .affected li {
      display: grid;
      grid-template-columns: auto minmax(0, 1fr);
      gap: 0 6px;
      font-size: var(--sd-text-sm);
    }
    .affected li .small {
      grid-column: 2;
    }
    .form {
      display: flex;
      flex-direction: column;
      gap: 10px;
    }
    .two {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 8px;
    }
    @media (max-width: 480px) {
      .two {
        grid-template-columns: 1fr;
      }
    }
    .form-error {
      margin: 0;
      color: var(--sd-danger);
      font-size: var(--sd-text-sm);
    }
    .footer {
      display: flex;
      justify-content: flex-end;
      gap: 8px;
    }
  `,
})
export class EngineeringPanelComponent {
  readonly ticketId = input.required<Guid>();
  /** Emitted after an escalation/link/unlink changed the ticket (status, timeline). */
  readonly ticketChanged = output<void>();

  private readonly api = inject(EngineeringApi);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(SdDialogService);

  protected readonly priorities = ['Low', 'Medium', 'High', 'Critical'];
  protected readonly severities = ['Cosmetic', 'Minor', 'Major', 'Critical'];

  protected readonly data = signal<TicketEngineeringDto | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly mode = signal<Mode>('list');
  protected readonly busy = signal<Guid | null>(null);
  protected readonly expanded = signal<Guid | null>(null);
  protected readonly affected = signal<EngineeringAffectedTicketsDto | null>(null);

  protected readonly form = signal<EscalateForm>(this.emptyForm(null));
  protected readonly prefillLoading = signal(false);
  protected readonly impact = signal(0);
  protected readonly assignees = signal<EngineeringAssigneeDto[]>([]);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);

  protected readonly linkTrackerId = signal<Guid | null>(null);
  protected readonly reference = signal('');
  protected readonly linkSubstatus = signal(true);

  protected readonly selectedTracker = computed<ActiveTrackerDto | null>(
    () => this.data()?.trackers.find((t) => t.id === this.form().trackerId) ?? null,
  );

  private requestKey = 0;

  constructor() {
    effect(() => {
      this.ticketId();
      untracked(() => {
        this.mode.set('list');
        this.expanded.set(null);
        this.data.set(null);
        this.load(true);
      });
    });
  }

  load(refresh = false): void {
    const ticketId = this.ticketId();
    const key = ++this.requestKey;
    this.loading.set(true);
    this.error.set(null);
    this.api.ticket(ticketId, refresh).subscribe({
      next: (d) => {
        if (key !== this.requestKey) return;
        this.data.set(d);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        if (key !== this.requestKey) return;
        this.loading.set(false);
        this.error.set(err instanceof ApiError ? err.userMessage : 'Engineering links could not be loaded.');
      },
    });
  }

  private emptyForm(trackerId: Guid | null): EscalateForm {
    return {
      trackerId,
      itemType: 'Bug',
      title: '',
      reproStepsHtml: '',
      severity: 'Minor',
      priority: 'Medium',
      areaPath: '',
      iterationPath: '',
      tags: [],
      assigneeUserId: null,
      setSubstatus: true,
    };
  }

  protected patch(change: Partial<EscalateForm>): void {
    this.form.update((f) => ({ ...f, ...change }));
  }

  protected stateTone(l: EngineeringLinkViewDto): SdTone {
    if (l.isOrphaned) return 'neutral';
    if (l.isReleased) return 'success';
    if (l.isResolved) return 'info';
    return 'warning';
  }

  protected artifactLabel(kind: string): string {
    return kind === 'PullRequest' ? 'PR' : kind;
  }

  // ───── Escalate ─────

  protected openEscalate(): void {
    const d = this.data();
    if (!d) return;
    const tracker = d.trackers.find((t) => t.isDefault) ?? d.trackers[0] ?? null;
    this.form.set({ ...this.emptyForm(tracker?.id ?? null), itemType: tracker?.defaultItemType ?? 'Bug' });
    this.errors.set({});
    this.formError.set(null);
    this.mode.set('escalate');
    this.prefillLoading.set(true);
    this.api.prefill(this.ticketId()).subscribe({
      next: (p) => {
        this.patch({ title: p.title, reproStepsHtml: p.reproStepsHtml, severity: p.severity ?? 'Minor', priority: p.priority, tags: p.tags });
        this.impact.set(p.customerImpactCount);
        this.prefillLoading.set(false);
      },
      error: (err: unknown) => {
        this.prefillLoading.set(false);
        this.formError.set(err instanceof ApiError ? err.userMessage : 'The ticket details could not be loaded.');
      },
    });
    if (this.assignees().length === 0) this.api.assignees().subscribe({ next: (a) => this.assignees.set(a), error: () => this.assignees.set([]) });
  }

  protected escalate(): void {
    const f = this.form();
    if (!f.trackerId) {
      this.errors.set({ trackerId: 'Choose a tracker.' });
      return;
    }
    this.saving.set(true);
    this.formError.set(null);
    const tracker = this.selectedTracker();
    this.api
      .escalate(this.ticketId(), {
        trackerId: f.trackerId,
        itemType: f.itemType,
        title: f.title.trim(),
        reproStepsHtml: f.reproStepsHtml || null,
        severity: f.itemType === 'Bug' ? f.severity : null,
        priority: f.priority,
        areaPath: tracker?.supportsAssignee ? null : f.areaPath.trim() || null,
        iterationPath: tracker?.supportsAssignee ? null : f.iterationPath.trim() || null,
        tags: f.tags,
        assigneeUserId: tracker?.supportsAssignee ? f.assigneeUserId : null,
        setSubstatus: f.setSubstatus,
        clientRequestId: `esc-${this.ticketId()}-${Date.now()}`,
      })
      .subscribe({
        next: (r) => this.done(r, 'Escalated to engineering'),
        error: (err: unknown) => this.failed(err),
      });
  }

  // ───── Link ─────

  protected openLink(): void {
    const d = this.data();
    this.linkTrackerId.set(d?.trackers.find((t) => t.isDefault)?.id ?? d?.trackers[0]?.id ?? null);
    this.reference.set('');
    this.linkSubstatus.set(true);
    this.errors.set({});
    this.formError.set(null);
    this.mode.set('link');
  }

  protected link(): void {
    const trackerId = this.linkTrackerId();
    if (!trackerId) return;
    this.saving.set(true);
    this.formError.set(null);
    this.api.link(this.ticketId(), { trackerId, reference: this.reference().trim(), setSubstatus: this.linkSubstatus() }).subscribe({
      next: (r) => this.done(r, 'Work item linked'),
      error: (err: unknown) => this.failed(err),
    });
  }

  private done(r: EscalationResultDto, title: string): void {
    this.saving.set(false);
    this.mode.set('list');
    this.toast.success(title, `${r.link.displayId} · ${r.link.title}`);
    if (r.message) this.toast.info('Ticket status unchanged', r.message);
    this.load();
    this.ticketChanged.emit();
  }

  private failed(err: unknown): void {
    this.saving.set(false);
    const fields = validationMessages(err);
    this.errors.set(fields);
    this.formError.set(Object.keys(fields).length > 0 ? null : saveErrorMessage(err));
  }

  // ───── Row actions ─────

  protected refresh(l: EngineeringLinkViewDto): void {
    this.busy.set(l.id);
    this.api.refreshLink(this.ticketId(), l.id).subscribe({
      next: (updated) => {
        this.busy.set(null);
        this.data.update((d) => (d ? { ...d, links: d.links.map((x) => (x.id === updated.id ? updated : x)) } : d));
        if (updated.syncError) this.toast.warning('Sync failed', updated.syncError);
      },
      error: (err: unknown) => {
        this.busy.set(null);
        this.toast.error('Refresh failed', err instanceof ApiError ? err.userMessage : undefined);
      },
    });
  }

  protected toggleAffected(l: EngineeringLinkViewDto): void {
    if (this.expanded() === l.id) {
      this.expanded.set(null);
      return;
    }
    this.expanded.set(l.id);
    this.affected.set(null);
    this.api.affectedTickets(this.ticketId(), l.id).subscribe({
      next: (a) => {
        if (this.expanded() === l.id) this.affected.set(a);
      },
      error: (err: unknown) => {
        this.expanded.set(null);
        this.toast.error('Could not load the other tickets', err instanceof ApiError ? err.userMessage : undefined);
      },
    });
  }

  protected async unlink(l: EngineeringLinkViewDto): Promise<void> {
    const ok = await this.dialogs.confirm({
      title: `Unlink ${l.displayId}?`,
      message: 'The ticket stops following this work item. The timeline keeps a record of the link.',
      confirmLabel: 'Unlink',
      danger: true,
    });
    if (!ok) return;
    this.api.unlink(this.ticketId(), l.id).subscribe({
      next: () => {
        this.toast.success('Work item unlinked', l.displayId);
        this.load();
        this.ticketChanged.emit();
      },
      error: (err: unknown) => this.toast.error('Could not unlink', err instanceof ApiError ? err.userMessage : undefined),
    });
  }
}
