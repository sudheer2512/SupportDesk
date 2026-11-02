import { CurrencyPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError, Guid, MeStore, TicketsApi, TimeEntryDto } from '@sd/core';
import { SdButton, SdCheckbox, SdDateInput, SdDateTimePipe, SdDurationPipe, SdEmptyState, SdFormField, SdIcon, SdInput, SdSkeleton, ToastService } from '@sd/ui';
import { toastApiError } from '../../shared/ticket-ui';
import { TicketContext } from '../ticket-context.service';

/** Time tracking: log time (billable or not), list entries; costs only for users allowed to see them. */
@Component({
  selector: 'desk-time-panel',
  imports: [FormsModule, CurrencyPipe, SdButton, SdCheckbox, SdDateInput, SdDateTimePipe, SdDurationPipe, SdEmptyState, SdFormField, SdIcon, SdInput, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="totals">
      <div><span class="k">Total</span><strong>{{ totalMinutes() | sdDuration }}</strong></div>
      <div><span class="k">Billable</span><strong>{{ billableMinutes() | sdDuration }}</strong></div>
      @if (canViewCosts()) {
        <div><span class="k">Cost</span><strong>{{ totalCost() | currency: undefined : 'symbol' : '1.0-2' }}</strong></div>
        <div><span class="k">Billable value</span><strong>{{ totalBillable() | currency: undefined : 'symbol' : '1.0-2' }}</strong></div>
      }
    </div>

    @if (ctx.can()?.logTime) {
      <form class="log" (ngSubmit)="log()" novalidate>
        <div class="row">
          <sd-form-field label="Hours">
            <input sdInput type="number" min="0" max="24" name="h" [ngModel]="hours()" (ngModelChange)="hours.set(wholeOrNull($event))" />
          </sd-form-field>
          <sd-form-field label="Minutes">
            <input sdInput type="number" min="0" max="59" name="m" [ngModel]="minutes()" (ngModelChange)="minutes.set(wholeOrNull($event))" />
          </sd-form-field>
          <sd-form-field label="Started" optional>
            <sd-date-input type="datetime" [value]="startedAt()" (valueChange)="startedAt.set($event)" />
          </sd-form-field>
        </div>
        <sd-form-field label="Activity" optional>
          <input sdInput name="activity" list="time-activities" maxlength="60" [ngModel]="activity()" (ngModelChange)="activity.set($event)" placeholder="Investigation, call, configuration…" />
          <datalist id="time-activities">
            <option value="Investigation"></option>
            <option value="Customer call"></option>
            <option value="Configuration"></option>
            <option value="Remote session"></option>
            <option value="On-site visit"></option>
            <option value="Documentation"></option>
          </datalist>
        </sd-form-field>
        <sd-form-field label="Notes" optional>
          <input sdInput name="notes" maxlength="500" [ngModel]="notes()" (ngModelChange)="notes.set($event)" />
        </sd-form-field>
        <div class="row-end">
          <sd-checkbox [value]="billable()" (valueChange)="billable.set($event)">Billable</sd-checkbox>
          <span class="sd-spacer"></span>
          <button sdButton size="sm" variant="primary" icon="clock" type="submit" [disabled]="totalToLog() <= 0" [loading]="saving()">Log time</button>
        </div>
      </form>
    }

    @if (loading() && entries().length === 0) {
      <sd-skeleton [lines]="3" />
    } @else if (error()) {
      <p class="error" role="alert">{{ error() }} <button sdButton variant="link" size="sm" type="button" (click)="load()">Retry</button></p>
    } @else {
      <ul class="entries">
        @for (e of entries(); track e.id) {
          <li>
            <div class="line">
              <strong>{{ e.minutes | sdDuration }}</strong>
              @if (e.isBillable) {
                <span class="bill">billable</span>
              }
              <span class="sd-muted sd-small sd-truncate">{{ e.activityType ?? 'Work' }}</span>
              <span class="sd-spacer"></span>
              @if (e.userId === me.user()?.id) {
                <button type="button" class="icon" [attr.aria-label]="'Delete time entry of ' + e.minutes + ' minutes'" (click)="remove(e)"><sd-icon name="trash" [size]="14" /></button>
              }
            </div>
            <div class="sd-xs sd-muted">
              {{ e.userName }} · {{ e.startedAt | sdDateTime }}
              @if (e.contractNumber) {
                · {{ e.contractNumber }}
              }
              @if (canViewCosts() && e.costAmount !== null) {
                · cost {{ e.costAmount | currency: undefined : 'symbol' : '1.0-2' }}
              }
            </div>
            @if (e.notes) {
              <p class="notes">{{ e.notes }}</p>
            }
          </li>
        } @empty {
          <sd-empty-state icon="clock" title="No time logged" message="Log the time you spend so contracts and costs stay accurate." compact />
        }
      </ul>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--sd-space-3);
    }
    .totals {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(110px, 1fr));
      gap: 8px;
    }
    .totals div {
      display: grid;
      padding: 8px 10px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface-2);
    }
    .k {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .log {
      display: grid;
      gap: 6px;
      padding: var(--sd-space-3);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
    }
    .row {
      display: grid;
      grid-template-columns: 70px 80px 1fr;
      gap: 6px;
    }
    @media (max-width: 420px) {
      .row {
        grid-template-columns: 1fr 1fr;
      }
    }
    .row-end {
      display: flex;
      align-items: center;
      gap: 6px;
    }
    .entries {
      display: grid;
      gap: 8px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .entries li {
      padding: 8px 10px;
      border: 1px solid var(--sd-divider);
      border-radius: var(--sd-radius-md);
    }
    .line {
      display: flex;
      align-items: center;
      gap: 8px;
      min-width: 0;
    }
    .bill {
      padding: 0 6px;
      border-radius: var(--sd-radius-pill);
      background: var(--sd-success-bg);
      color: var(--sd-success);
      font-size: var(--sd-text-xs);
    }
    .notes {
      margin-top: 4px;
      font-size: var(--sd-text-sm);
      color: var(--sd-text-2);
    }
    .icon {
      display: inline-flex;
      padding: 4px;
      border: 0;
      border-radius: var(--sd-radius-xs);
      background: transparent;
      color: var(--sd-text-3);
      cursor: pointer;
    }
    .icon:hover {
      color: var(--sd-danger);
    }
    .error {
      color: var(--sd-danger);
    }
  `,
})
export class TimePanelComponent {
  readonly ticketId = input.required<Guid>();
  protected readonly ctx = inject(TicketContext);
  protected readonly me = inject(MeStore);
  private readonly tickets = inject(TicketsApi);
  private readonly toast = inject(ToastService);

  protected readonly entries = signal<TimeEntryDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly saving = signal(false);
  /** Null while a box is empty: writing 0 back would turn "30" typed into a cleared box into "030". */
  protected readonly hours = signal<number | null>(0);
  protected readonly minutes = signal<number | null>(15);
  protected readonly totalToLog = computed(() => (this.hours() ?? 0) * 60 + (this.minutes() ?? 0));
  protected readonly startedAt = signal<string | null>(null);
  protected readonly activity = signal('');
  protected readonly notes = signal('');
  protected readonly billable = signal(true);

  protected readonly canViewCosts = computed(() => !!this.ctx.can()?.viewCosts);
  protected readonly totalMinutes = computed(() => this.entries().reduce((s, e) => s + e.minutes, 0));
  protected readonly billableMinutes = computed(() => this.entries().reduce((s, e) => s + (e.isBillable ? e.minutes : 0), 0));
  protected readonly totalCost = computed(() => this.entries().reduce((s, e) => s + (e.costAmount ?? 0), 0));
  protected readonly totalBillable = computed(() => this.entries().reduce((s, e) => s + (e.billableAmount ?? 0), 0));

  constructor() {
    effect(() => {
      this.ticketId();
      untracked(() => this.load());
    });
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.tickets.timeEntries(this.ticketId()).subscribe({
      next: (list) => {
        this.entries.set(list);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.error.set(ApiError.from(err).userMessage);
      },
    });
  }

  protected wholeOrNull(value: unknown): number | null {
    if (value === null || value === undefined || value === '') return null;
    const n = Math.trunc(Number(value));
    return Number.isFinite(n) && n >= 0 ? n : null;
  }

  protected log(): void {
    const minutes = this.totalToLog();
    if (minutes <= 0) return;
    this.saving.set(true);
    this.tickets
      .logTime(this.ticketId(), {
        minutes,
        startedAt: this.startedAt(),
        isBillable: this.billable(),
        activityType: this.activity().trim() || null,
        notes: this.notes().trim() || null,
      })
      .subscribe({
        next: () => {
          this.saving.set(false);
          this.notes.set('');
          this.toast.success('Time logged');
          this.load();
          this.ctx.refresh();
        },
        error: (err: unknown) => {
          this.saving.set(false);
          toastApiError(this.toast, err, 'Could not log time');
        },
      });
  }

  protected remove(e: TimeEntryDto): void {
    this.tickets.deleteTimeEntry(e.id).subscribe({
      next: () => {
        this.load();
        this.ctx.refresh();
      },
      error: (err: unknown) => toastApiError(this.toast, err, 'Could not delete the entry'),
    });
  }
}
