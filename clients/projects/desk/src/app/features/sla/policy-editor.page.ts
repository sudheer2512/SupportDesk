import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import {
  BusinessCalendarListItemDto,
  ConditionFieldDto,
  ESCALATION_ACTIONS,
  FilterGroup,
  JsonValue,
  MeStore,
  MetaApi,
  SLA_MILESTONES,
  SaveSlaPolicyRequest,
  SlaAdminApi,
  SlaEscalationDto,
  SlaEscalationNotify,
  SlaPolicyDto,
  SlaSimulationDto,
  WorkbenchApi,
  emptyFilterGroup,
  saveErrorMessage,
  validationMessages,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCard,
  SdCheckbox,
  SdCombobox,
  SdDateTimePipe,
  SdDialogService,
  SdFilterBuilder,
  SdFormField,
  SdInput,
  SdOption,
  SdPageHeader,
  SdSkeleton,
  ToastService,
} from '@sd/ui';
import { catchError, forkJoin, of } from 'rxjs';
import { ModuleTabsComponent } from '../customers/shared/module-tabs.component';
import { SECTION_STYLES, TABLE_STYLES } from '../customers/shared/styles';
import { FORM_STYLES, PAGE_STYLES, errorText, formatMinutes } from '../teams/shared/org-ui';
import { ACTION_LABELS, DurationUnit, MILESTONE_LABELS, SLA_TABS, joinMinutes, slaConditionFields, splitMinutes } from './sla-ui';

interface DurationField {
  value: number | null;
  unit: DurationUnit;
}

interface TargetRow {
  priority: string;
  enabled: boolean;
  firstResponse: DurationField;
  nextResponse: DurationField;
  resolution: DurationField;
  businessHours: boolean;
  warningPercent: number;
}

interface EscalationRow {
  milestone: string;
  level: number;
  trigger: 'percent' | 'minutes';
  percent: number;
  minutes: number;
  action: string;
  reassignToUserId: string | null;
  reassignToTeamId: string | null;
  notifyAssignee: boolean;
  notifySupervisors: boolean;
  notifyUserIds: string[];
}

const PRIORITY_ORDER = ['Critical', 'High', 'Medium', 'Low'];

/** /sla/policies/:id and /sla/policies/new — edit an SLA policy and simulate it against an existing ticket. */
@Component({
  selector: 'desk-sla-policy-editor-page',
  imports: [
    FormsModule,
    RouterLink,
    SdPageHeader,
    SdButton,
    SdBadge,
    SdCard,
    SdCheckbox,
    SdCombobox,
    SdFormField,
    SdInput,
    SdFilterBuilder,
    SdSkeleton,
    SdDateTimePipe,
    ModuleTabsComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header [title]="isNew() ? 'New SLA policy' : policy()?.name ?? 'SLA policy'" icon="timer" [breadcrumbs]="crumbs"
        [subtitle]="policy() ? 'Version ' + policy()!.version + ' · ' + policy()!.activeTickets + ' tickets running' : null">
        <div sdPageActions class="actions">
          @if (canManage && policy() && !policy()!.isDefault) {
            <button sdButton variant="ghost" icon="trash" (click)="remove()">Delete</button>
          }
          @if (canManage) {
            <button sdButton variant="primary" icon="check" (click)="save()" [loading]="saving()" [disabled]="loading()">Save policy</button>
          }
        </div>
      </sd-page-header>
      <desk-module-tabs [tabs]="tabs" ariaLabel="SLA sections" />
      <div class="desk-page__body">
        @if (loadError()) {
          <div class="error-box" role="alert"><span>{{ loadError() }}</span><button sdButton size="sm" (click)="load()">Try again</button></div>
        } @else if (loading()) {
          <div aria-busy="true"><sd-skeleton [lines]="12" /></div>
        } @else {
          @if (formError()) {
            <div class="error-box" role="alert"><span>{{ formError() }}</span></div>
          }
          <fieldset class="editor" [disabled]="!canManage">
            <sd-card title="Policy">
              <div class="form">
                <div class="two">
                  <sd-form-field label="Name" required [error]="errors()['name']">
                    <input sdInput name="name" [(ngModel)]="name" maxlength="120" />
                  </sd-form-field>
                  <sd-form-field label="Description" optional>
                    <input sdInput name="description" [(ngModel)]="description" maxlength="500" />
                  </sd-form-field>
                </div>
                <div class="checks">
                  <sd-checkbox [(value)]="isActive" [disabled]="isDefault()">Active</sd-checkbox>
                  <sd-checkbox [(value)]="isDefault" [disabled]="policy()?.isDefault ?? false">Default policy (fallback when nothing else matches)</sd-checkbox>
                </div>
                <div class="two">
                  <sd-form-field label="Business calendar" hint="Empty = the ticket department's calendar, else 24x7." [error]="errors()['calendarId']">
                    <select sdInput name="calendar" [(ngModel)]="calendarId">
                      <option [ngValue]="null">Department calendar / 24x7</option>
                      @for (c of calendars(); track c.id) {
                        <option [ngValue]="c.id">{{ c.name }}</option>
                      }
                    </select>
                  </sd-form-field>
                  <sd-form-field label="Pause statuses" optional hint="Time in these statuses is not counted (in addition to statuses flagged as pausing SLA)." [error]="errors()['pauseStatusIds']">
                    <sd-combobox multiple [options]="statusOptions()" [value]="pauseStatusIds()" (valueChange)="pauseStatusIds.set($any($event) ?? [])" placeholder="None"
                      ariaLabel="Pause statuses" />
                  </sd-form-field>
                </div>
                <sd-checkbox [(value)]="useContractCalendar">Use the contract's support hours when the ticket is covered by a contract</sd-checkbox>
              </div>
            </sd-card>

            @if (!isDefault()) {
              <sd-card title="Applies to" subtitle="Conditions on the ticket, customer, contact and contract. No conditions = every ticket (in evaluation order).">
                @if (fields().length === 0) {
                  <sd-skeleton [lines]="2" />
                } @else {
                  <sd-filter-builder [fields]="fields()" [value]="conditions()" (valueChange)="conditions.set($event)" [maxDepth]="3" />
                }
                @if (errors()['conditions']) {
                  <p class="error">{{ errors()['conditions'] }}</p>
                }
              </sd-card>
            }

            <sd-card title="Targets per priority" subtitle="Business hours count only the calendar's working time; warnings fire at the given % of a target.">
              @if (errors()['targets']) {
                <p class="error">{{ errors()['targets'] }}</p>
              }
              <div class="dt-wrap">
                <table class="dt targets">
                  <caption class="sd-visually-hidden">Targets per priority</caption>
                  <thead>
                    <tr>
                      <th scope="col">Priority</th>
                      <th scope="col">First response</th>
                      <th scope="col">Next response</th>
                      <th scope="col">Resolution</th>
                      <th scope="col">Business hours</th>
                      <th scope="col">Warn at</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (t of targets(); track t.priority; let i = $index) {
                      <tr [class.off]="!t.enabled">
                        <td>
                          <sd-checkbox [value]="t.enabled" (valueChange)="patchTarget(i, { enabled: $event })" [ariaLabel]="'Target for ' + t.priority">{{ t.priority }}</sd-checkbox>
                        </td>
                        @for (key of durationKeys; track key) {
                          <td>
                            <span class="duration">
                              <input class="sd-input sd-input-sm" type="number" min="1" [attr.aria-label]="t.priority + ' ' + keyLabel(key)" [disabled]="!t.enabled"
                                [ngModel]="t[key].value" [ngModelOptions]="{ standalone: true }" (ngModelChange)="patchDuration(i, key, { value: $event })" />
                              <select class="sd-input sd-input-sm" [attr.aria-label]="t.priority + ' ' + keyLabel(key) + ' unit'" [disabled]="!t.enabled" [ngModel]="t[key].unit"
                                [ngModelOptions]="{ standalone: true }" (ngModelChange)="patchDuration(i, key, { unit: $event })">
                                <option value="minutes">min</option>
                                <option value="hours">h</option>
                                <option value="days">d</option>
                              </select>
                            </span>
                          </td>
                        }
                        <td>
                          <sd-checkbox [value]="t.businessHours" (valueChange)="patchTarget(i, { businessHours: $event })" [disabled]="!t.enabled"
                            [ariaLabel]="t.priority + ' counts business hours only'">Business</sd-checkbox>
                        </td>
                        <td>
                          <span class="duration">
                            <input class="sd-input sd-input-sm pct" type="number" min="1" max="99" [attr.aria-label]="t.priority + ' warning percent'" [disabled]="!t.enabled"
                              [ngModel]="t.warningPercent" [ngModelOptions]="{ standalone: true }" (ngModelChange)="patchTarget(i, { warningPercent: +$event })" />
                            <span class="small">%</span>
                          </span>
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            </sd-card>

            <sd-card title="Escalation levels" subtitle="Fire at a % of the target (before or after the breach) or a number of minutes around the breach.">
              <div sdCardActions>
                <button sdButton size="sm" icon="plus" type="button" (click)="addEscalation()">Add level</button>
              </div>
              @if (escalations().length === 0) {
                <p class="empty-note">No escalations — breaches are recorded and shown on the risk boards only.</p>
              }
              <div class="esc-list">
                @for (e of escalations(); track $index; let i = $index) {
                  <div class="esc" role="group" [attr.aria-label]="'Escalation ' + (i + 1)">
                    <div class="esc-grid">
                      <sd-form-field label="Milestone" [error]="errors()['escalations[' + i + '].milestone']">
                        <select sdInput [ngModel]="e.milestone" [ngModelOptions]="{ standalone: true }" (ngModelChange)="patchEscalation(i, { milestone: $event })">
                          @for (m of milestones; track m) {
                            <option [value]="m">{{ milestoneLabel(m) }}</option>
                          }
                        </select>
                      </sd-form-field>
                      <sd-form-field label="Level" [error]="errors()['escalations[' + i + '].level']">
                        <input sdInput type="number" min="1" max="10" [ngModel]="e.level" [ngModelOptions]="{ standalone: true }" (ngModelChange)="patchEscalation(i, { level: +$event })" />
                      </sd-form-field>
                      <sd-form-field label="When">
                        <select sdInput [ngModel]="e.trigger" [ngModelOptions]="{ standalone: true }" (ngModelChange)="patchEscalation(i, { trigger: $event })">
                          <option value="percent">At % of target</option>
                          <option value="minutes">Minutes from breach</option>
                        </select>
                      </sd-form-field>
                      @if (e.trigger === 'percent') {
                        <sd-form-field label="Percent" hint="e.g. 80 = before, 150 = after" [error]="errors()['escalations[' + i + '].atPercent']">
                          <input sdInput type="number" min="1" max="1000" [ngModel]="e.percent" [ngModelOptions]="{ standalone: true }" (ngModelChange)="patchEscalation(i, { percent: +$event })" />
                        </sd-form-field>
                      } @else {
                        <sd-form-field label="Minutes" hint="Negative = before the breach" [error]="errors()['escalations[' + i + '].minutesAfterBreach']">
                          <input sdInput type="number" [ngModel]="e.minutes" [ngModelOptions]="{ standalone: true }" (ngModelChange)="patchEscalation(i, { minutes: +$event })" />
                        </sd-form-field>
                      }
                      <sd-form-field label="Action" [error]="errors()['escalations[' + i + '].action']">
                        <select sdInput [ngModel]="e.action" [ngModelOptions]="{ standalone: true }" (ngModelChange)="patchEscalation(i, { action: $event })">
                          @for (a of actions; track a) {
                            <option [value]="a">{{ actionLabel(a) }}</option>
                          }
                        </select>
                      </sd-form-field>
                    </div>
                    @if (e.action === 'Reassign' || e.action === 'NotifyAndReassign') {
                      <div class="two">
                        <sd-form-field label="Reassign to agent" optional [error]="errors()['escalations[' + i + '].reassignTo'] || errors()['escalations[' + i + '].reassignToUserId']">
                          <select sdInput [ngModel]="e.reassignToUserId" [ngModelOptions]="{ standalone: true }" (ngModelChange)="patchEscalation(i, { reassignToUserId: $event })">
                            <option [ngValue]="null">—</option>
                            @for (a of agentOptions(); track a.value) {
                              <option [ngValue]="a.value">{{ a.label }}</option>
                            }
                          </select>
                        </sd-form-field>
                        <sd-form-field label="…or to team" optional [error]="errors()['escalations[' + i + '].reassignToTeamId']">
                          <select sdInput [ngModel]="e.reassignToTeamId" [ngModelOptions]="{ standalone: true }" (ngModelChange)="patchEscalation(i, { reassignToTeamId: $event })">
                            <option [ngValue]="null">—</option>
                            @for (t of teamOptions(); track t.value) {
                              <option [ngValue]="t.value">{{ t.label }}</option>
                            }
                          </select>
                        </sd-form-field>
                      </div>
                    }
                    @if (e.action !== 'Reassign' && e.action !== 'RaisePriority') {
                      <div class="notify">
                        <sd-checkbox [value]="e.notifyAssignee" (valueChange)="patchEscalation(i, { notifyAssignee: $event })">Notify assignee</sd-checkbox>
                        <sd-checkbox [value]="e.notifySupervisors" (valueChange)="patchEscalation(i, { notifySupervisors: $event })">Notify supervisors</sd-checkbox>
                        <sd-combobox multiple size="sm" [options]="agentOptions()" [value]="e.notifyUserIds" (valueChange)="patchEscalation(i, { notifyUserIds: $any($event) ?? [] })"
                          placeholder="Also notify…" ariaLabel="Also notify agents" />
                      </div>
                    }
                    <button sdButton size="sm" variant="ghost" icon="trash" type="button" class="remove" [attr.aria-label]="'Remove escalation ' + (i + 1)" (click)="removeEscalation(i)">
                      Remove
                    </button>
                  </div>
                }
              </div>
            </sd-card>
          </fieldset>

          <sd-card title="Simulate against a ticket" subtitle="Uses the unsaved form above. Shows which policy would apply and the resulting due dates.">
            <form class="sim-form" (ngSubmit)="simulate()">
              <sd-form-field label="Ticket number">
                <input sdInput name="ticket" type="number" min="1" [(ngModel)]="ticketNumber" placeholder="e.g. 1042" />
              </sd-form-field>
              <button sdButton type="submit" icon="play" [loading]="simulating()" [disabled]="!ticketNumber">Simulate</button>
            </form>
            @if (simError()) {
              <p class="error" role="alert">{{ simError() }}</p>
            }
            @if (simulation(); as s) {
              <div class="sim" aria-live="polite">
                <p>
                  <a [routerLink]="['/tickets', s.number]">#{{ s.number }}</a> {{ s.subject }} · {{ s.priority }} · {{ s.status }}
                </p>
                <p>
                  @if (s.selectedPolicyName) {
                    <sd-badge tone="primary">{{ s.selectedPolicyName }}</sd-badge>
                  } @else {
                    <sd-badge>No SLA</sd-badge>
                  }
                  <span class="small"> {{ s.selectionReason }} · calendar: {{ s.calendarName ?? s.calendarSource }}</span>
                  @if (s.wouldChange) {
                    <sd-badge tone="warning" size="sm">Differs from the current SLA ({{ s.currentPolicyName ?? 'none' }})</sd-badge>
                  }
                </p>
                <ul class="eval">
                  @for (e of s.evaluated; track $index) {
                    <li [class.selected]="e.selected">
                      <sd-badge size="sm" [tone]="e.selected ? 'success' : e.matched ? 'info' : 'neutral'">{{ e.selected ? 'Selected' : e.matched ? 'Matches' : 'Skipped' }}</sd-badge>
                      {{ e.name }} <span class="xs muted">— {{ e.note }}</span>
                    </li>
                  }
                </ul>
                @if (s.milestones.length > 0) {
                  <div class="dt-wrap">
                    <table class="dt">
                      <caption class="sd-visually-hidden">Simulated milestones</caption>
                      <thead>
                        <tr>
                          <th scope="col">Milestone</th>
                          <th scope="col">Target</th>
                          <th scope="col">Starts</th>
                          <th scope="col">Due</th>
                          <th scope="col">Current</th>
                        </tr>
                      </thead>
                      <tbody>
                        @for (m of s.milestones; track m.milestone) {
                          <tr>
                            <td>{{ milestoneLabel(m.milestone) }}</td>
                            <td>{{ minutes(m.targetMinutes) }} {{ m.businessHours ? 'business' : '24x7' }}</td>
                            <td class="nowrap">{{ m.startAt | sdDateTime: 'datetime' : 'short' }}</td>
                            <td class="nowrap"><strong>{{ m.dueAt | sdDateTime: 'datetime' : 'short' }}</strong></td>
                            <td class="small nowrap">{{ m.currentDueAt ? (m.currentDueAt | sdDateTime: 'datetime' : 'short') : '—' }} {{ m.currentStatus ?? '' }}</td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  </div>
                }
              </div>
            }
          </sd-card>
        }
      </div>
    </div>
  `,
  styles: [
    TABLE_STYLES,
    SECTION_STYLES,
    PAGE_STYLES,
    FORM_STYLES,
    `
      .actions { display: flex; gap: var(--sd-space-2); flex-wrap: wrap; }
      .editor { border: 0; padding: 0; margin: 0 0 var(--sd-space-4); display: grid; gap: var(--sd-space-4); min-width: 0; }
      .targets td { vertical-align: middle; }
      .targets tr.off { opacity: 0.6; }
      .duration { display: inline-flex; gap: 4px; align-items: center; }
      .duration input { width: 76px; }
      .duration input.pct { width: 64px; }
      .duration select { width: 64px; }
      .esc-list { display: grid; gap: var(--sd-space-3); }
      .esc { border: 1px solid var(--sd-divider); border-radius: var(--sd-radius-md); padding: var(--sd-space-3); display: grid; gap: var(--sd-space-2); }
      /* Milestone · level · when · percent/minutes · action: the selects get room for their longest option. */
      .esc-grid {
        display: grid;
        gap: var(--sd-space-2);
        grid-template-columns: minmax(160px, 1.2fr) minmax(80px, 0.6fr) minmax(200px, 1.3fr) minmax(120px, 0.9fr) minmax(190px, 1.3fr);
        align-items: start;
      }
      @media (max-width: 1180px) {
        .esc-grid { grid-template-columns: repeat(auto-fill, minmax(min(100%, 200px), 1fr)); }
      }
      .notify { display: flex; flex-wrap: wrap; gap: var(--sd-space-3); align-items: center; }
      .notify sd-combobox { min-width: 220px; flex: 1; }
      .remove { justify-self: end; }
      .sim-form { display: flex; gap: var(--sd-space-2); align-items: end; flex-wrap: wrap; }
      .sim { margin-top: var(--sd-space-3); display: grid; gap: var(--sd-space-2); }
      .eval { list-style: none; padding: 0; margin: 0; display: grid; gap: 4px; font-size: var(--sd-text-sm); }
      .eval li.selected { font-weight: var(--sd-weight-semibold); }
      a { color: var(--sd-primary); }
    `,
  ],
})
export class PolicyEditorPage {
  private readonly api = inject(SlaAdminApi);
  private readonly meta = inject(MetaApi);
  private readonly workbench = inject(WorkbenchApi);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(SdDialogService);
  /** Route param (absent on /sla/policies/new). */
  readonly id = input<string | null>(null);
  protected readonly isNew = computed(() => !this.id());
  protected readonly canManage = inject(MeStore).can('admin.sla');
  protected readonly tabs = SLA_TABS;
  protected readonly crumbs = [{ label: 'SLA', link: '/sla' }, { label: 'Policies', link: '/sla/policies' }];
  protected readonly milestones = SLA_MILESTONES;
  protected readonly actions = ESCALATION_ACTIONS;
  protected readonly durationKeys = ['firstResponse', 'nextResponse', 'resolution'] as const;

  protected readonly policy = signal<SlaPolicyDto | null>(null);
  protected readonly calendars = signal<BusinessCalendarListItemDto[]>([]);
  private readonly catalog = signal<ConditionFieldDto[]>([]);
  protected readonly fields = computed(() => slaConditionFields(this.catalog(), this.meta.ticketForm()));
  protected readonly statusOptions = computed<SdOption[]>(() => (this.meta.ticketForm()?.statuses ?? []).map((s) => ({ value: s.id, label: s.name, color: s.color })));
  protected readonly agentOptions = computed<SdOption[]>(() =>
    (this.meta.ticketForm()?.agents ?? []).filter((a) => !a.isLightAgent).map((a) => ({ value: a.userId, label: a.name })),
  );
  protected readonly teamOptions = computed<SdOption[]>(() => (this.meta.ticketForm()?.teams ?? []).map((t) => ({ value: t.id, label: t.name })));

  protected name = '';
  protected description = '';
  protected calendarId: string | null = null;
  protected readonly isActive = signal(true);
  protected readonly isDefault = signal(false);
  protected readonly useContractCalendar = signal(true);
  protected readonly pauseStatusIds = signal<string[]>([]);
  protected readonly conditions = signal<FilterGroup>(emptyFilterGroup());
  protected readonly targets = signal<TargetRow[]>([]);
  protected readonly escalations = signal<EscalationRow[]>([]);

  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected ticketNumber: number | null = null;
  protected readonly simulating = signal(false);
  protected readonly simulation = signal<SlaSimulationDto | null>(null);
  protected readonly simError = signal<string | null>(null);

  constructor() {
    this.meta.load();
    effect(() => {
      this.id();
      untracked(() => this.load());
    });
  }

  protected load(): void {
    this.loading.set(true);
    this.loadError.set(null);
    const id = this.id();
    forkJoin({
      policy: id ? this.api.policy(id) : of(null),
      calendars: this.api.calendars().pipe(catchError(() => of([] as BusinessCalendarListItemDto[]))),
      catalog: this.workbench.conditionFields().pipe(catchError(() => of({ fields: [] as ConditionFieldDto[], operators: [] as string[] }))),
    }).subscribe({
      next: (r) => {
        this.calendars.set(r.calendars);
        this.catalog.set(r.catalog.fields);
        this.fill(r.policy);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loadError.set(errorText(err));
        this.loading.set(false);
      },
    });
  }

  private fill(p: SlaPolicyDto | null): void {
    this.policy.set(p);
    this.name = p?.name ?? '';
    this.description = p?.description ?? '';
    this.calendarId = p?.calendarId ?? null;
    this.isActive.set(p?.isActive ?? true);
    this.isDefault.set(p?.isDefault ?? false);
    this.useContractCalendar.set(p?.useContractCalendar ?? true);
    this.pauseStatusIds.set(p?.pauseStatusIds ?? []);
    this.conditions.set(toFilterGroup(p?.conditions ?? null));
    this.targets.set(
      PRIORITY_ORDER.map((priority) => {
        const t = p?.targets.find((x) => x.priority === priority);
        return {
          priority,
          enabled: !!t || !p,
          firstResponse: splitMinutes(t ? t.firstResponseMinutes : defaults(priority).fr),
          nextResponse: splitMinutes(t ? t.nextResponseMinutes : defaults(priority).nr),
          resolution: splitMinutes(t ? t.resolutionMinutes : defaults(priority).res),
          businessHours: t?.businessHours ?? priority !== 'Critical',
          warningPercent: t?.warningPercent ?? 75,
        };
      }),
    );
    this.escalations.set((p?.escalations ?? []).map(toRow));
  }

  protected keyLabel(key: string): string {
    return key === 'firstResponse' ? 'first response' : key === 'nextResponse' ? 'next response' : 'resolution';
  }

  protected milestoneLabel(m: string): string {
    return MILESTONE_LABELS[m] ?? m;
  }

  protected actionLabel(a: string): string {
    return ACTION_LABELS[a] ?? a;
  }

  protected minutes(m: number): string {
    return formatMinutes(m);
  }

  protected patchTarget(i: number, patch: Partial<TargetRow>): void {
    this.targets.update((rows) => rows.map((r, x) => (x === i ? { ...r, ...patch } : r)));
  }

  protected patchDuration(i: number, key: 'firstResponse' | 'nextResponse' | 'resolution', patch: Partial<DurationField>): void {
    this.targets.update((rows) => rows.map((r, x) => (x === i ? { ...r, [key]: { ...r[key], ...patch } } : r)));
  }

  protected addEscalation(): void {
    const used = this.escalations().filter((e) => e.milestone === 'Resolution').map((e) => e.level);
    this.escalations.update((rows) => [
      ...rows,
      {
        milestone: 'Resolution', level: used.length ? Math.max(...used) + 1 : 1, trigger: 'percent', percent: 100, minutes: 0, action: 'Notify',
        reassignToUserId: null, reassignToTeamId: null, notifyAssignee: true, notifySupervisors: true, notifyUserIds: [],
      },
    ]);
  }

  protected removeEscalation(i: number): void {
    this.escalations.update((rows) => rows.filter((_, x) => x !== i));
  }

  protected patchEscalation(i: number, patch: Partial<EscalationRow>): void {
    this.escalations.update((rows) => rows.map((r, x) => (x === i ? { ...r, ...patch } : r)));
  }

  private request(): SaveSlaPolicyRequest {
    return {
      name: this.name.trim(),
      description: this.description.trim() || null,
      isActive: this.isActive() || this.isDefault(),
      isDefault: this.isDefault(),
      conditions: this.isDefault() ? null : (this.conditions() as unknown as JsonValue),
      calendarId: this.calendarId,
      useContractCalendar: this.useContractCalendar(),
      pauseStatusIds: this.pauseStatusIds(),
      targets: this.targets()
        .filter((t) => t.enabled)
        .map((t) => ({
          priority: t.priority,
          firstResponseMinutes: joinMinutes(t.firstResponse.value, t.firstResponse.unit),
          nextResponseMinutes: joinMinutes(t.nextResponse.value, t.nextResponse.unit),
          resolutionMinutes: joinMinutes(t.resolution.value, t.resolution.unit),
          businessHours: t.businessHours,
          warningPercent: Number(t.warningPercent) || 75,
        })),
      escalations: this.escalations().map(toDto),
      rowVersion: this.policy()?.rowVersion ?? null,
    };
  }

  protected save(): void {
    this.saving.set(true);
    this.formError.set(null);
    this.errors.set({});
    this.api.savePolicy(this.policy()?.id ?? null, this.request()).subscribe({
      next: (r) => {
        this.saving.set(false);
        this.toast.success('SLA policy saved', r.retargetQueued > 0 ? `Recalculating ${r.retargetQueued} running SLAs.` : undefined);
        if (this.isNew()) void this.router.navigate(['/sla/policies', r.policy.id], { replaceUrl: true });
        else this.fill(r.policy);
      },
      error: (err: unknown) => {
        this.saving.set(false);
        this.errors.set(validationMessages(err));
        this.formError.set(saveErrorMessage(err));
      },
    });
  }

  protected async remove(): Promise<void> {
    const p = this.policy();
    if (!p) return;
    const ok = await this.dialogs.confirm({
      title: `Delete ${p.name}?`,
      message: 'Policies that tickets have used are kept for reproducibility — deactivate those instead.',
      danger: true,
      confirmLabel: 'Delete',
    });
    if (!ok) return;
    this.api.deletePolicy(p.id).subscribe({
      next: () => {
        this.toast.success('Policy deleted', p.name);
        void this.router.navigate(['/sla/policies']);
      },
      error: (err: unknown) => this.toast.error('Could not delete', saveErrorMessage(err)),
    });
  }

  protected simulate(): void {
    if (!this.ticketNumber) return;
    this.simulating.set(true);
    this.simError.set(null);
    this.api.simulate({ ticketId: null, ticketNumber: Number(this.ticketNumber), policyId: this.policy()?.id ?? null, draft: this.request() }).subscribe({
      next: (r) => {
        this.simulation.set(r);
        this.simulating.set(false);
      },
      error: (err: unknown) => {
        this.simError.set(saveErrorMessage(err));
        this.simulating.set(false);
      },
    });
  }
}

function defaults(priority: string): { fr: number; nr: number; res: number } {
  switch (priority) {
    case 'Critical':
      return { fr: 15, nr: 60, res: 240 };
    case 'High':
      return { fr: 60, nr: 240, res: 480 };
    case 'Medium':
      return { fr: 240, nr: 480, res: 1440 };
    default:
      return { fr: 480, nr: 960, res: 2880 };
  }
}

function toFilterGroup(raw: JsonValue | null): FilterGroup {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return emptyFilterGroup();
  const g = raw as unknown as Partial<FilterGroup>;
  return {
    logic: g.logic === 'or' ? 'or' : 'and',
    conditions: Array.isArray(g.conditions) ? g.conditions : [],
    groups: Array.isArray(g.groups) ? g.groups.map((x) => toFilterGroup(x as unknown as JsonValue)) : [],
  };
}

function toRow(e: SlaEscalationDto): EscalationRow {
  const n = (e.notify ?? {}) as SlaEscalationNotify;
  return {
    milestone: e.milestone,
    level: e.level,
    trigger: e.atPercent !== null && e.atPercent !== undefined ? 'percent' : 'minutes',
    percent: e.atPercent ?? 100,
    minutes: e.minutesAfterBreach,
    action: e.action,
    reassignToUserId: e.reassignToUserId,
    reassignToTeamId: e.reassignToTeamId,
    notifyAssignee: !!n.notifyAssignee,
    notifySupervisors: !!n.notifySupervisors,
    notifyUserIds: Array.isArray(n.userIds) ? n.userIds : [],
  };
}

function toDto(r: EscalationRow): SlaEscalationDto {
  const notify: SlaEscalationNotify = { notifyAssignee: r.notifyAssignee, notifySupervisors: r.notifySupervisors };
  if (r.notifyUserIds.length) notify.userIds = r.notifyUserIds;
  return {
    milestone: r.milestone,
    level: Number(r.level),
    minutesAfterBreach: r.trigger === 'minutes' ? Number(r.minutes) || 0 : 0,
    atPercent: r.trigger === 'percent' ? Number(r.percent) || 100 : null,
    action: r.action,
    notify,
    reassignToUserId: r.reassignToUserId,
    reassignToTeamId: r.reassignToTeamId,
  };
}
