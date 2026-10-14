import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { SdBadge } from '@sd/ui';
import { statusTone } from './portal-utils';

/** Customer-facing ticket status, coloured by its category: `<portal-status-badge [status]="t.status" [category]="t.statusCategory" />` */
@Component({
  selector: 'portal-status-badge',
  imports: [SdBadge],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<sd-badge [tone]="tone()" dot>{{ status() }}</sd-badge>`,
})
export class StatusBadgeComponent {
  readonly status = input.required<string>();
  readonly category = input<string | null>(null);
  protected readonly tone = computed(() => statusTone(this.category()));
}
