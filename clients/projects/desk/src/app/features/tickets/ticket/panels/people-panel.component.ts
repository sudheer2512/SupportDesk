import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError, FollowUpDto, Guid, MeStore, TicketsApi, WatcherDto } from '@sd/core';
import {
  SdAvatar,
  SdBadge,
  SdButton,
  SdCombobox,
  SdDateInput,
  SdDateTimePipe,
  SdEmptyState,
  SdFormField,
  SdIcon,
  SdInput,
  SdRelativeTimePipe,
  SdSkeleton,
  ToastService,
} from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { agentOptions, toastApiError } from '../../shared/ticket-ui';
import { TicketContext } from '../ticket-context.service';

/** Watchers, collaborators and temporary specialists (POST/DELETE /tickets/{id}/watchers). */
@Component({
  selector: 'desk-watchers-panel',
  imports: [FormsModule, SdAvatar, SdBadge, SdButton, SdCombobox, SdDateInput, SdEmptyState, SdFormField, SdIcon, SdRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ul class="people">
      @for (w of watchers(); track w.userId) {
        <li>
          <sd-avatar [name]="w.name" size="sm" />
          <span class="who">
            <span class="sd-truncate">{{ w.name }}</span>
            <span class="sd-xs sd-muted">
              added {{ w.addedAt | sdRelativeTime }}
              @if (w.expiresAt) {
                · until {{ w.expiresAt | sdRelativeTime }}
              }
            </span>
          </span>
          <sd-badge size="sm" [tone]="w.kind === 'Specialist' ? 'accent' : w.kind === 'Collaborator' ? 'info' : 'neutral'">{{ w.kind }}</sd-badge>
          @if (canRemove(w)) {
            <button type="button" class="icon" [attr.aria-label]="'Remove ' + w.name" (click)="remove(w)"><sd-icon name="x" [size]="14" /></button>
          }
        </li>
      } @empty {
        <sd-empty-state icon="eye" title="Nobody is watching" message="Watchers get notified about every update." compact />
      }
    </ul>

    <form class="add" (ngSubmit)="add()">
      <sd-form-field label="Add a person">
        <sd-combobox [options]="agents()" [value]="userId()" (valueChange)="userId.set($any($event))" placeholder="Agent or specialist" />
      </sd-form-field>
      <div class="row">
        <sd-form-field label="Role">
          <select sdInput name="kind" [ngModel]="kind()" (ngModelChange)="kind.set($event)">
            <option value="Watcher">Watcher</option>
            <option value="Collaborator">Collaborator</option>
            <option value="Specialist">Temporary specialist</option>
          </select>
        </sd-form-field>
        @if (kind() === 'Specialist') {
          <sd-form-field label="Access until" required>
            <sd-date-input type="datetime" [value]="expiresAt()" (valueChange)="expiresAt.set($event)" />
          </sd-form-field>
        }
      </div>
      <button sdButton size="sm" variant="primary" icon="user-plus" type="submit" [disabled]="!userId() || (kind() === 'Specialist' && !expiresAt())" [loading]="saving()">Add</button>
    </form>
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--sd-space-3);
    }
    .people {
      display: grid;
      gap: 4px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .people li {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 6px 4px;
      min-width: 0;
    }
    .who {
      display: grid;
      flex: 1;
      min-width: 0;
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
    .add {
      display: grid;
      gap: 6px;
      padding-top: var(--sd-space-3);
      border-top: 1px solid var(--sd-divider);
    }
    .add > button {
      justify-self: end;
    }
    .row {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
      gap: 6px;
    }
  `,
})
export class WatchersPanelComponent {
  private readonly ctx = inject(TicketContext);
  private readonly tickets = inject(TicketsApi);
  private readonly toast = inject(ToastService);
  private readonly me = inject(MeStore);

  protected readonly watchers = computed(() => this.ctx.ticket()?.watchers ?? []);
  protected readonly agents = computed(() => {
    const present = new Set(this.watchers().map((w) => w.userId));
    return agentOptions(this.ctx.meta.ticketForm(), null, true).filter((o) => !present.has(o.value));
  });
  protected readonly userId = signal<string | null>(null);
  protected readonly kind = signal('Watcher');
  protected readonly expiresAt = signal<string | null>(null);
  protected readonly saving = signal(false);

  protected canRemove(w: WatcherDto): boolean {
    return w.userId === this.me.user()?.id || !!this.ctx.can()?.edit;
  }

  protected async add(): Promise<void> {
    const id = this.ctx.id();
    const userId = this.userId();
    if (!id || !userId) return;
    this.saving.set(true);
    try {
      await firstValueFrom(this.tickets.addWatcher(id, { userId, kind: this.kind(), expiresAt: this.kind() === 'Specialist' ? this.expiresAt() : null }));
      this.userId.set(null);
      this.toast.success('Added to the ticket');
      this.ctx.refresh();
    } catch (err) {
      toastApiError(this.toast, err, 'Could not add the person');
    } finally {
      this.saving.set(false);
    }
  }

  protected async remove(w: WatcherDto): Promise<void> {
    const id = this.ctx.id();
    if (!id) return;
    try {
      await firstValueFrom(this.tickets.removeWatcher(id, w.userId));
      this.ctx.refresh();
    } catch (err) {
      toastApiError(this.toast, err, 'Could not remove the person');
    }
  }
}

/** Follow-up reminders on the ticket (schedule / cancel). */
@Component({
  selector: 'desk-followups-panel',
  imports: [FormsModule, SdButton, SdCombobox, SdDateInput, SdDateTimePipe, SdEmptyState, SdFormField, SdIcon, SdInput, SdSkeleton, SdBadge],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading() && items().length === 0) {
      <sd-skeleton [lines]="2" />
    } @else if (error()) {
      <p class="error" role="alert">{{ error() }} <button sdButton variant="link" size="sm" type="button" (click)="load()">Retry</button></p>
    } @else {
      <ul class="items">
        @for (f of items(); track f.id) {
          <li>
            <sd-icon name="alarm-clock" [size]="15" />
            <span class="body">
              <strong>{{ f.remindAt | sdDateTime }}</strong>
              <span class="sd-xs sd-muted">for {{ f.userName }}</span>
              @if (f.note) {
                <span class="sd-small">{{ f.note }}</span>
              }
            </span>
            <sd-badge size="sm" [tone]="f.status === 'Pending' ? 'info' : 'neutral'">{{ f.status }}</sd-badge>
            @if (f.status === 'Pending') {
              <button sdButton size="sm" variant="ghost" type="button" (click)="cancel(f)">Cancel</button>
            }
          </li>
        } @empty {
          <sd-empty-state icon="alarm-clock" title="No follow-ups" message="Schedule a reminder to check back on this ticket." compact />
        }
      </ul>
    }
    <form class="add" (ngSubmit)="schedule()">
      <sd-form-field label="Remind at" required>
        <sd-date-input type="datetime" [value]="remindAt()" (valueChange)="remindAt.set($event)" [min]="now" />
      </sd-form-field>
      <sd-form-field label="Remind" optional>
        <sd-combobox [options]="agents()" [value]="userId()" (valueChange)="userId.set($any($event))" placeholder="Me" />
      </sd-form-field>
      <sd-form-field label="Note" optional>
        <input sdInput name="note" maxlength="500" [ngModel]="note()" (ngModelChange)="note.set($event)" />
      </sd-form-field>
      <button sdButton size="sm" variant="primary" icon="alarm-clock" type="submit" [disabled]="!remindAt()" [loading]="saving()">Schedule</button>
    </form>
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--sd-space-3);
    }
    .items {
      display: grid;
      gap: 6px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .items li {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 6px 8px;
      border: 1px solid var(--sd-divider);
      border-radius: var(--sd-radius-md);
    }
    .body {
      display: grid;
      flex: 1;
      min-width: 0;
    }
    .add {
      display: grid;
      gap: 6px;
      padding-top: var(--sd-space-3);
      border-top: 1px solid var(--sd-divider);
    }
    .add > button {
      justify-self: end;
    }
    .error {
      color: var(--sd-danger);
    }
  `,
})
export class FollowUpsPanelComponent {
  readonly ticketId = input.required<Guid>();
  private readonly ctx = inject(TicketContext);
  private readonly tickets = inject(TicketsApi);
  private readonly toast = inject(ToastService);

  protected readonly now = new Date().toISOString();
  protected readonly items = signal<FollowUpDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly remindAt = signal<string | null>(null);
  protected readonly userId = signal<string | null>(null);
  protected readonly note = signal('');
  protected readonly agents = computed(() => agentOptions(this.ctx.meta.ticketForm(), null, true));

  constructor() {
    effect(() => {
      this.ticketId();
      untracked(() => this.load());
    });
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.tickets.followUps(this.ticketId()).subscribe({
      next: (list) => {
        this.items.set(list);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.error.set(ApiError.from(err).userMessage);
      },
    });
  }

  protected schedule(): void {
    const at = this.remindAt();
    if (!at) return;
    this.saving.set(true);
    this.tickets.createFollowUp(this.ticketId(), { remindAt: at, note: this.note().trim() || null, userId: this.userId() }).subscribe({
      next: () => {
        this.saving.set(false);
        this.remindAt.set(null);
        this.note.set('');
        this.toast.success('Follow-up scheduled');
        this.load();
      },
      error: (err: unknown) => {
        this.saving.set(false);
        toastApiError(this.toast, err, 'Could not schedule the follow-up');
      },
    });
  }

  protected cancel(f: FollowUpDto): void {
    this.tickets.deleteFollowUp(f.id).subscribe({
      next: () => this.load(),
      error: (err: unknown) => toastApiError(this.toast, err, 'Could not cancel the follow-up'),
    });
  }
}
