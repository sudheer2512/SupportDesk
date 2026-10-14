import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ApiError,
  BlueprintTransitionDto,
  BlueprintsApi,
  JsonValue,
  MeStore,
  MetaApi,
  PRIORITIES,
  TicketBlueprintDto,
  TicketFormMetaDto,
} from '@sd/core';
import { SdBadge, SdButton, SdCombobox, SdFormField, SdIcon, SdInput, SdOption, SdRelativeTimePipe, SdSkeleton, SdToggle, ToastService } from '@sd/ui';

type FieldKind = 'select' | 'combo' | 'number' | 'date' | 'bool' | 'text';

interface FieldEditor {
  field: string;
  label: string;
  kind: FieldKind;
  options: SdOption[];
}

/**
 * Ticket blueprint panel for the agent workbench: current state, the transitions the agent may run (mandatory
 * fields that are still missing are asked for inline, plus the comment when required), execution with
 * approval-pending handling (HTTP 202). Uses only agent-level APIs (`/tickets/{id}/blueprint`, `/meta/ticket-form`).
 * `<desk-blueprint-panel [ticketId]="ticket.id" (changed)="reloadTicket()" />` — renders nothing when the
 * ticket does not follow a blueprint.
 */
@Component({
  selector: 'desk-blueprint-panel',
  imports: [FormsModule, SdBadge, SdButton, SdCombobox, SdFormField, SdIcon, SdInput, SdSkeleton, SdToggle, SdRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading() && !blueprint()) {
      <sd-skeleton [lines]="3" />
    } @else if (blueprint(); as bp) {
      <section class="panel" [attr.aria-label]="'Blueprint ' + bp.workflowName">
        <header class="head">
          <sd-icon name="workflow" [size]="15" />
          <strong class="sd-truncate">{{ bp.workflowName }}</strong>
          @if (bp.versionNumber) {
            <span class="sd-muted small">v{{ bp.versionNumber }}</span>
          }
          <span class="sd-spacer"></span>
          <button sdButton size="sm" variant="ghost" icon="refresh" iconOnly aria-label="Refresh blueprint" (click)="reload()"></button>
        </header>

        <ol class="track" aria-label="Blueprint states">
          @for (s of bp.states; track s.key) {
            <li [class.current]="s.key === bp.currentState.key" [class.final]="s.isFinal" [attr.aria-current]="s.key === bp.currentState.key ? 'step' : null">
              {{ s.name }}
            </li>
          }
        </ol>

        <div class="current">
          <span class="sd-muted small">Current state</span>
          <div class="state">
            <strong>{{ bp.currentState.name }}</strong>
            @if (bp.currentState.statusName) {
              <sd-badge size="sm" variant="outline">{{ bp.currentState.statusName }}{{ bp.currentState.substatus ? ' · ' + bp.currentState.substatus : '' }}</sd-badge>
            }
            @if (bp.currentState.isFinal) {
              <sd-badge size="sm" tone="success" icon="check">Completed</sd-badge>
            }
          </div>
        </div>

        @if (bp.pendingApproval; as p) {
          <div class="pending" role="status">
            <sd-icon name="hourglass" [size]="14" />
            <span>
              <strong>{{ p.transitionName }}</strong> is waiting for approval
              — requested by {{ p.requestedByName ?? 'an agent' }} {{ p.requestedAt | sdRelativeTime }}.
            </span>
          </div>
        }

        @if (bp.transitions.length) {
          <ul class="transitions">
            @for (t of bp.transitions; track t.key) {
              <li class="transition" [class.open]="openKey() === t.key">
                <button
                  type="button"
                  class="tbtn"
                  [disabled]="!t.canExecute"
                  [attr.aria-expanded]="openKey() === t.key"
                  (click)="toggle(t)"
                >
                  <span class="tname">{{ t.name }}</span>
                  <span class="sd-muted small">→ {{ t.toName }}</span>
                  <span class="sd-spacer"></span>
                  @if (t.requiresApproval) {
                    <sd-badge size="sm" tone="accent" icon="shield-check">approval</sd-badge>
                  }
                  @if (t.missingFields.length) {
                    <sd-badge size="sm" tone="warning">{{ t.missingFields.length }} field{{ t.missingFields.length === 1 ? '' : 's' }}</sd-badge>
                  }
                </button>
                @if (t.blockedReason) {
                  <p class="blocked">{{ t.blockedReason }}</p>
                }
                @if (openKey() === t.key) {
                  <form class="form" (ngSubmit)="execute(t)">
                    @for (f of editorsFor(t); track f.field) {
                      <sd-form-field [label]="f.label" required [error]="fieldErrors()[f.field]">
                        @switch (f.kind) {
                          @case ('select') {
                            <select sdInput [name]="f.field" [ngModel]="valueOf(f.field) ?? ''" (ngModelChange)="setValue(f.field, $event || null)">
                              <option value="">Choose…</option>
                              @for (o of f.options; track o.value) {
                                <option [value]="o.value">{{ o.label }}</option>
                              }
                            </select>
                          }
                          @case ('combo') {
                            <sd-combobox [options]="f.options" [value]="$any(valueOf(f.field))" (valueChange)="setValue(f.field, $any($event))" />
                          }
                          @case ('number') {
                            <input sdInput type="number" [name]="f.field" [ngModel]="valueOf(f.field)" (ngModelChange)="setValue(f.field, $event === '' ? null : +$event)" />
                          }
                          @case ('date') {
                            <input sdInput type="date" [name]="f.field" [ngModel]="valueOf(f.field)" (ngModelChange)="setValue(f.field, $event || null)" />
                          }
                          @case ('bool') {
                            <sd-toggle [value]="valueOf(f.field) === true" (valueChange)="setValue(f.field, $event)" [ariaLabel]="f.label" />
                          }
                          @default {
                            <input sdInput type="text" [name]="f.field" [ngModel]="valueOf(f.field) ?? ''" (ngModelChange)="setValue(f.field, $event || null)" />
                          }
                        }
                      </sd-form-field>
                    }
                    <sd-form-field label="Comment" [required]="t.requiresComment" [optional]="!t.requiresComment" [error]="fieldErrors()['comment']">
                      <textarea sdInput name="comment" rows="2" [ngModel]="comment()" (ngModelChange)="comment.set($event)"></textarea>
                    </sd-form-field>
                    @if (error()) {
                      <p class="err" role="alert">{{ error() }}</p>
                    }
                    <div class="buttons">
                      <button sdButton type="submit" variant="primary" size="sm" [icon]="t.requiresApproval ? 'send' : 'check'" [loading]="running()">
                        {{ t.requiresApproval ? 'Request approval' : 'Move to ' + t.toName }}
                      </button>
                      <button sdButton type="button" size="sm" variant="ghost" (click)="openKey.set(null)">Cancel</button>
                    </div>
                  </form>
                }
              </li>
            }
          </ul>
        } @else if (!bp.currentState.isFinal) {
          <p class="sd-muted small">No transitions are available to you from this state.</p>
        }

        @if (me.can('admin.automation')) {
          <button sdButton size="sm" variant="ghost" icon="x" class="remove" (click)="removeFromBlueprint()">Remove ticket from blueprint</button>
        }
      </section>
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .panel {
      display: flex;
      flex-direction: column;
      gap: 10px;
      padding: 12px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      font-size: var(--sd-text-sm);
    }
    .head {
      display: flex;
      align-items: center;
      gap: 6px;
      min-width: 0;
      color: var(--sd-text);
    }
    .small {
      font-size: var(--sd-text-xs);
    }
    .track {
      display: flex;
      flex-wrap: wrap;
      gap: 4px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .track li {
      padding: 2px 8px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-pill);
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .track li.current {
      border-color: var(--sd-primary);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
      font-weight: var(--sd-weight-semibold);
    }
    .current .state {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
      margin-top: 2px;
    }
    .pending {
      display: flex;
      gap: 8px;
      align-items: flex-start;
      padding: 8px 10px;
      border: 1px solid var(--sd-warning-border);
      border-radius: var(--sd-radius-sm);
      background: var(--sd-warning-bg);
    }
    .transitions {
      display: flex;
      flex-direction: column;
      gap: 6px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .transition {
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-sm);
    }
    .transition.open {
      border-color: var(--sd-primary-border);
    }
    .tbtn {
      display: flex;
      align-items: center;
      gap: 6px;
      width: 100%;
      padding: 7px 9px;
      border: 0;
      background: transparent;
      color: var(--sd-text);
      text-align: left;
      cursor: pointer;
      border-radius: var(--sd-radius-sm);
    }
    .tbtn:hover:not(:disabled) {
      background: var(--sd-row-hover);
    }
    .tbtn:disabled {
      color: var(--sd-text-disabled);
      cursor: not-allowed;
    }
    .tbtn:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .tname {
      font-weight: var(--sd-weight-semibold);
    }
    .blocked {
      margin: 0;
      padding: 0 9px 7px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .form {
      display: flex;
      flex-direction: column;
      gap: 8px;
      padding: 4px 9px 10px;
    }
    .buttons {
      display: flex;
      gap: 6px;
    }
    .err {
      margin: 0;
      color: var(--sd-danger);
    }
    .remove {
      align-self: flex-start;
    }
  `,
})
export class BlueprintPanelComponent {
  private readonly api = inject(BlueprintsApi);
  private readonly metaApi = inject(MetaApi);
  private readonly toast = inject(ToastService);
  protected readonly me = inject(MeStore);

  readonly ticketId = input.required<string>();
  /** Emits after a transition completed or was sent for approval (reload the ticket). */
  readonly changed = output<void>();

  protected readonly blueprint = signal<TicketBlueprintDto | null>(null);
  protected readonly loading = signal(false);
  protected readonly running = signal(false);
  protected readonly openKey = signal<string | null>(null);
  protected readonly comment = signal('');
  protected readonly values = signal<Record<string, JsonValue>>({});
  protected readonly error = signal<string | null>(null);
  protected readonly fieldErrors = signal<Record<string, string>>({});
  private readonly meta = signal<TicketFormMetaDto | null>(null);

  constructor() {
    effect(() => {
      const id = this.ticketId();
      untracked(() => {
        this.blueprint.set(null);
        this.openKey.set(null);
        if (id) this.reload();
      });
    });
    this.metaApi.ticketForm$().subscribe({ next: (m) => this.meta.set(m), error: () => undefined });
  }

  reload(): void {
    this.loading.set(true);
    this.api.forTicket(this.ticketId()).subscribe({
      next: (bp) => {
        this.blueprint.set(bp);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  protected toggle(t: BlueprintTransitionDto): void {
    if (this.openKey() === t.key) {
      this.openKey.set(null);
      return;
    }
    this.openKey.set(t.key);
    this.comment.set('');
    this.values.set({});
    this.error.set(null);
    this.fieldErrors.set({});
  }

  protected readonly editorsFor = (t: BlueprintTransitionDto): FieldEditor[] =>
    t.requiredFields.filter((f) => f.missing).map((f) => this.editor(f.field, f.label));

  private editor(field: string, label: string): FieldEditor {
    const m = this.meta();
    const opt = (values: readonly string[]): SdOption[] => values.map((v) => ({ value: v, label: v }));
    const lookups = (type: string): SdOption[] => (m?.lookups ?? []).filter((l) => l.type === type).map((l) => ({ value: l.value, label: l.label }));
    if (field.startsWith('cf:')) {
      const def = m?.customFields.find((c) => c.key === field.slice(3));
      switch (def?.fieldType) {
        case 'Picklist': {
          const options = Array.isArray(def.options)
            ? def.options.map((o) => (o && typeof o === 'object' && !Array.isArray(o) ? { value: String(o['value']), label: String(o['label'] ?? o['value']) } : { value: String(o), label: String(o) }))
            : [];
          return { field, label, kind: 'select', options };
        }
        case 'Number':
        case 'Decimal':
        case 'Currency':
          return { field, label, kind: 'number', options: [] };
        case 'Date':
          return { field, label, kind: 'date', options: [] };
        case 'Checkbox':
          return { field, label, kind: 'bool', options: [] };
        default:
          return { field, label, kind: 'text', options: [] };
      }
    }
    switch (field) {
      case 'severity':
        return { field, label, kind: 'select', options: opt(m?.severities ?? ['Cosmetic', 'Minor', 'Major', 'Critical']) };
      case 'priority':
        return { field, label, kind: 'select', options: opt(PRIORITIES) };
      case 'resolutionCode':
        return { field, label, kind: 'select', options: lookups('resolution_code') };
      case 'rootCause':
        return { field, label, kind: 'select', options: lookups('root_cause') };
      case 'categoryId':
        return { field, label, kind: 'combo', options: (m?.categories ?? []).filter((c) => !c.parentId).map((c) => ({ value: c.id, label: c.name })) };
      case 'subcategoryId':
        return { field, label, kind: 'combo', options: (m?.categories ?? []).filter((c) => c.parentId).map((c) => ({ value: c.id, label: c.name })) };
      case 'issueTypeId':
        return { field, label, kind: 'combo', options: (m?.issueTypes ?? []).map((i) => ({ value: i.id, label: i.name })) };
      case 'productId':
        return { field, label, kind: 'combo', options: (m?.products ?? []).map((p) => ({ value: p.id, label: p.name })) };
      case 'moduleId':
        return { field, label, kind: 'combo', options: (m?.products ?? []).flatMap((p) => p.modules.map((x) => ({ value: x.id, label: `${p.name} › ${x.name}` }))) };
      case 'versionId':
        return { field, label, kind: 'combo', options: (m?.products ?? []).flatMap((p) => p.versions.map((x) => ({ value: x.id, label: `${p.name} ${x.name}` }))) };
      case 'assigneeId':
        return { field, label, kind: 'combo', options: (m?.agents ?? []).filter((a) => !a.isLightAgent).map((a) => ({ value: a.userId, label: a.name, avatar: a.name })) };
      case 'teamId':
        return { field, label, kind: 'combo', options: (m?.teams ?? []).map((x) => ({ value: x.id, label: x.name })) };
      default:
        return { field, label, kind: 'text', options: [] };
    }
  }

  protected valueOf(field: string): JsonValue | null {
    return this.values()[field] ?? null;
  }

  protected setValue(field: string, value: JsonValue | null): void {
    this.values.update((v) => {
      const next = { ...v };
      if (value === null || value === '') delete next[field];
      else next[field] = value;
      return next;
    });
  }

  protected execute(t: BlueprintTransitionDto): void {
    const errors: Record<string, string> = {};
    for (const f of t.requiredFields.filter((x) => x.missing)) if (this.values()[f.field] === undefined) errors[f.field] = `${f.label} is required.`;
    if (t.requiresComment && !this.comment().trim()) errors['comment'] = 'A comment is required for this transition.';
    this.fieldErrors.set(errors);
    if (Object.keys(errors).length) return;
    this.running.set(true);
    this.error.set(null);
    this.api.transition(this.ticketId(), t.key, { comment: this.comment().trim() || null, fields: this.values() }).subscribe({
      next: (r) => {
        this.running.set(false);
        this.openKey.set(null);
        if (r.status === 'pending_approval') this.toast.info('Sent for approval', `“${t.name}” runs once it is approved.`);
        else {
          const failed = r.actionResults.filter((a) => a.status === 'failed').length;
          if (failed) this.toast.warning(`Moved to ${r.stateName}`, `${failed} follow-up action(s) failed — see the ticket history.`);
          else this.toast.success(`Moved to ${r.stateName}`);
        }
        this.reload();
        this.changed.emit();
      },
      error: (err: unknown) => {
        this.running.set(false);
        if (err instanceof ApiError) {
          const fe: Record<string, string> = {};
          for (const [k, v] of Object.entries(err.errors as Record<string, unknown>)) fe[k.replace(/^fields\./, '')] = Array.isArray(v) ? String(v[0]) : String(v);
          this.fieldErrors.set(fe);
          this.error.set(err.userMessage);
          if (err.status === 409) this.reload();
        } else this.error.set('The transition could not be completed.');
      },
    });
  }

  protected removeFromBlueprint(): void {
    this.api.removeFromTicket(this.ticketId()).subscribe(() => {
      this.toast.success('Removed from the blueprint');
      this.blueprint.set(null);
      this.changed.emit();
    });
  }

  /** True when the ticket follows a blueprint (for hosts that hide an empty panel). */
  readonly hasBlueprint = computed(() => this.blueprint() !== null);
}
