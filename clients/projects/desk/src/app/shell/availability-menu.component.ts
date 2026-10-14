import { CdkMenu, CdkMenuItemRadio, CdkMenuTrigger } from '@angular/cdk/menu';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { AVAILABILITIES, ApiError, Availability, MeApi, MeStore, ToastBridge } from '@sd/core';
import { SdIcon } from '@sd/ui';

const HINTS: Record<Availability, string> = {
  Online: 'Receive new assignments',
  Away: 'Temporarily away',
  Busy: 'Do not route new work',
  Offline: 'Not working',
};

/** Availability selector (PUT /me/availability) — the coloured dot shows the current status. */
@Component({
  selector: 'desk-availability-menu',
  imports: [CdkMenuTrigger, CdkMenu, CdkMenuItemRadio, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button type="button" class="trigger" [cdkMenuTriggerFor]="menu" [attr.aria-label]="'Availability: ' + me.availability()" [disabled]="saving()">
      <span class="dot" [class]="'dot dot--' + me.availability().toLowerCase()" aria-hidden="true"></span>
      <span class="label">{{ me.availability() }}</span>
      <sd-icon name="chevron-down" [size]="14" />
    </button>
    <ng-template #menu>
      <div class="sd-menu" cdkMenu aria-label="Set availability">
        <div class="sd-menu-label" role="presentation">Availability</div>
        @for (a of options; track a) {
          <button
            type="button"
            class="sd-menu-item"
            cdkMenuItemRadio
            [cdkMenuItemChecked]="me.availability() === a"
            (cdkMenuItemTriggered)="set(a)"
          >
            <span class="dot" [class]="'dot dot--' + a.toLowerCase()" aria-hidden="true"></span>
            <span class="opt">
              <span>{{ a }}</span>
              <span class="hint">{{ hints[a] }}</span>
            </span>
            @if (me.availability() === a) {
              <sd-icon name="check" [size]="14" class="tick" />
            }
          </button>
        }
      </div>
    </ng-template>
  `,
  styles: `
    .trigger {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      height: 32px;
      padding: 0 8px 0 10px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface);
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-medium);
      cursor: pointer;
    }
    .trigger:hover {
      border-color: var(--sd-border-strong);
      color: var(--sd-text);
    }
    .dot {
      width: 9px;
      height: 9px;
      border-radius: 50%;
      background: var(--sd-text-disabled);
      flex: none;
    }
    .dot--online {
      background: var(--sd-success);
    }
    .dot--away {
      background: var(--sd-warning);
    }
    .dot--busy {
      background: var(--sd-danger);
    }
    .opt {
      display: flex;
      flex-direction: column;
      flex: 1;
    }
    .hint {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .tick {
      color: var(--sd-primary);
    }
  `,
})
export class AvailabilityMenuComponent {
  protected readonly me = inject(MeStore);
  private readonly api = inject(MeApi);
  private readonly toasts = inject(ToastBridge);
  protected readonly options = AVAILABILITIES;
  protected readonly hints = HINTS;
  protected readonly saving = signal(false);

  protected set(availability: Availability): void {
    if (availability === this.me.availability()) return;
    this.saving.set(true);
    this.api.setAvailability(availability).subscribe({
      next: () => this.saving.set(false),
      error: (err: unknown) => {
        this.saving.set(false);
        const e = ApiError.from(err);
        if (e.status > 0 && e.status < 500 && e.status !== 403) this.toasts.error('Could not change availability', e.userMessage);
      },
    });
  }
}
