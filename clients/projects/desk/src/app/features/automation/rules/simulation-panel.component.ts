import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AutomationApi, RuleDefinition, SimulationResultDto, TicketsApi } from '@sd/core';
import { SdBadge, SdButton, SdFormField, SdIcon, SdInput, SdToggle } from '@sd/ui';
import { AutomationContext } from '../shared/automation-context.service';
import { actionTone, errorMessage } from '../shared/automation-ui';

/**
 * Dry run of a rule against one ticket (by number) or the last N tickets: per-condition trace and the actions that
 * would run. Nothing is changed on tickets (the server records a "Simulated" execution).
 */
@Component({
  selector: 'desk-simulation-panel',
  imports: [FormsModule, RouterLink, SdButton, SdBadge, SdFormField, SdInput, SdIcon, SdToggle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form class="form" (ngSubmit)="run()">
      <div class="mode" role="radiogroup" aria-label="Tickets to simulate on">
        <label><input type="radio" name="mode" value="ticket" [(ngModel)]="mode" /> One ticket</label>
        <label><input type="radio" name="mode" value="recent" [(ngModel)]="mode" /> Most recent</label>
      </div>
      @if (mode() === 'ticket') {
        <sd-form-field label="Ticket number">
          <input sdInput name="number" inputmode="numeric" placeholder="e.g. 1042" [(ngModel)]="ticketNumber" />
        </sd-form-field>
      } @else {
        <sd-form-field label="Number of tickets" hint="Newest first, at most 50.">
          <input sdInput name="count" type="number" min="1" max="50" [(ngModel)]="lastN" />
        </sd-form-field>
      }
      @if (hasDraft()) {
        <sd-toggle name="useDraft" [(ngModel)]="useEditor">Use the unsaved changes in the editor</sd-toggle>
      }
      <button sdButton type="submit" variant="primary" icon="play" [loading]="running()">Run simulation</button>
      @if (error()) {
        <p class="err" role="alert">{{ error() }}</p>
      }
    </form>

    @if (result(); as r) {
      <div class="summary" aria-live="polite">
        <strong>{{ r.matched }}</strong> of {{ r.evaluated }} ticket{{ r.evaluated === 1 ? '' : 's' }} matched
        @if (r.versionNumber) {
          <span class="sd-muted">· version {{ r.versionNumber }}</span>
        }
      </div>
      <ul class="results">
        @for (t of r.results; track t.ticketId) {
          <li class="result" [class.matched]="t.matched">
            <div class="head">
              <sd-badge size="sm" [tone]="t.matched ? 'success' : 'neutral'" [icon]="t.matched ? 'check' : 'minus'">{{ t.matched ? 'Matches' : 'No match' }}</sd-badge>
              <a [routerLink]="['/automation/tickets', t.ticketId]" class="ticket">#{{ t.number }}</a>
              <span class="sd-truncate subject">{{ t.subject }}</span>
            </div>
            @if (t.note) {
              <p class="note"><sd-icon name="info" [size]="12" /> {{ t.note }}</p>
            }
            @if (t.trace.length) {
              <table class="trace">
                <caption class="sd-visually-hidden">Condition trace for #{{ t.number }}</caption>
                <thead>
                  <tr><th scope="col">Field</th><th scope="col">Check</th><th scope="col">Ticket value</th><th scope="col"><span class="sd-visually-hidden">Result</span></th></tr>
                </thead>
                <tbody>
                  @for (c of t.trace; track $index) {
                    <tr>
                      <td>{{ ctx.fieldLabel(c.field) }}</td>
                      <td>
                        {{ ctx.describeCheck(c.op, c.expected) }}
                      </td>
                      <td class="actual">{{ ctx.describeValue(c.actual) }}</td>
                      <td>
                        <sd-icon [name]="c.result ? 'circle-check' : 'circle-x'" [size]="14" [class]="c.result ? 'ok' : 'no'" />
                        <span class="sd-visually-hidden">{{ c.result ? 'true' : 'false' }}</span>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            }
            @if (t.matched) {
              <ol class="actions">
                @for (a of t.actions; track a.index) {
                  <li>
                    <sd-badge size="sm" [tone]="actionTone(a.wouldRun ? 'would_run' : 'skipped')">{{ a.wouldRun ? 'would run' : 'skipped' }}</sd-badge>
                    {{ a.description }}
                    @if (a.note) {
                      <span class="sd-muted"> — {{ a.note }}</span>
                    }
                  </li>
                }
              </ol>
            }
          </li>
        }
      </ul>
    }
  `,
  styles: `
    .form {
      display: flex;
      flex-direction: column;
      gap: 10px;
    }
    .mode {
      display: flex;
      gap: 14px;
      font-size: var(--sd-text-sm);
    }
    .mode label {
      display: inline-flex;
      align-items: center;
      gap: 6px;
    }
    .err {
      color: var(--sd-danger);
      font-size: var(--sd-text-sm);
    }
    .summary {
      margin: 14px 0 8px;
      font-size: var(--sd-text-sm);
    }
    .results {
      display: flex;
      flex-direction: column;
      gap: 10px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .result {
      padding: 10px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
    }
    .result.matched {
      border-color: var(--sd-success-border);
    }
    .head {
      display: flex;
      align-items: center;
      gap: 8px;
      min-width: 0;
    }
    .ticket {
      color: var(--sd-primary);
      font-weight: var(--sd-weight-semibold);
      text-decoration: none;
    }
    .subject {
      font-size: var(--sd-text-sm);
      color: var(--sd-text-2);
    }
    .note {
      margin: 6px 0 0;
      font-size: var(--sd-text-xs);
      color: var(--sd-text-2);
    }
    .trace {
      width: 100%;
      margin-top: 8px;
      border-collapse: collapse;
      font-size: var(--sd-text-xs);
    }
    .trace th {
      text-align: left;
      color: var(--sd-text-3);
      font-weight: var(--sd-weight-medium);
      padding: 2px 4px;
    }
    .trace td {
      padding: 3px 4px;
      border-top: 1px solid var(--sd-divider);
      vertical-align: top;
      word-break: break-word;
    }
    .trace td:first-child {
      white-space: nowrap;
      word-break: normal;
    }
    .op {
      font-family: var(--sd-font-mono);
      color: var(--sd-text-2);
    }
    .actual {
      color: var(--sd-text-2);
    }
    .ok {
      color: var(--sd-success);
    }
    .no {
      color: var(--sd-danger);
    }
    .actions {
      display: flex;
      flex-direction: column;
      gap: 4px;
      margin: 8px 0 0;
      padding-left: 18px;
      font-size: var(--sd-text-sm);
    }
  `,
})
export class SimulationPanel {
  protected readonly ctx = inject(AutomationContext);
  private readonly api = inject(AutomationApi);
  private readonly tickets = inject(TicketsApi);

  readonly ruleId = input<string | null>(null);
  readonly versionId = input<string | null>(null);
  readonly trigger = input<string>('ticket.created');
  /** The editor's current (possibly unsaved) definition. */
  readonly draft = input<RuleDefinition | null>(null);
  readonly hasDraft = input(false);

  protected readonly mode = signal<'ticket' | 'recent'>('recent');
  protected readonly ticketNumber = signal('');
  protected readonly lastN = signal(10);
  protected readonly useEditor = signal(true);
  protected readonly running = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly result = signal<SimulationResultDto | null>(null);
  protected readonly actionTone = actionTone;

  protected run(): void {
    this.error.set(null);
    if (this.mode() === 'ticket') {
      const number = this.ticketNumber().replace(/[^0-9]/g, '');
      if (!number) {
        this.error.set('Enter a ticket number.');
        return;
      }
      this.running.set(true);
      this.tickets.getByNumber(number).subscribe({
        next: (t) => this.simulate(t.id),
        error: (err: unknown) => {
          this.running.set(false);
          this.error.set(errorMessage(err, 'Ticket not found.'));
        },
      });
    } else {
      this.running.set(true);
      this.simulate(null);
    }
  }

  private simulate(ticketId: string | null): void {
    const useDraft = (this.hasDraft() && this.useEditor()) || !this.ruleId();
    this.api
      .simulate(this.ruleId(), {
        ticketId,
        lastTickets: ticketId ? null : Math.min(50, Math.max(1, Number(this.lastN()) || 10)),
        definition: useDraft ? this.draft() : null,
        versionId: useDraft ? null : this.versionId(),
        trigger: this.trigger(),
      })
      .subscribe({
        next: (r) => {
          this.result.set(r);
          this.running.set(false);
        },
        error: (err: unknown) => {
          this.running.set(false);
          this.error.set(errorMessage(err));
        },
      });
  }
}
