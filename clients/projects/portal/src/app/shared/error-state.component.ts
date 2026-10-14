import { ChangeDetectionStrategy, Component, booleanAttribute, computed, input, output } from '@angular/core';
import { SdButton, SdEmptyState } from '@sd/ui';
import { errorMessage, isNotFound } from './portal-utils';

/**
 * Friendly failure state with a retry button; 404/403 render as "not found or no access".
 * `<portal-error-state [error]="res.error()" notFoundTitle="Article not found" (retry)="res.reload()" />`
 */
@Component({
  selector: 'portal-error-state',
  imports: [SdEmptyState, SdButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-empty-state [icon]="notFound() ? 'search' : 'circle-alert'" [title]="title()" [message]="message()" [compact]="compact()">
      @if (!notFound()) {
        <button sdButton type="button" icon="refresh" (click)="retry.emit()">Try again</button>
      }
      <ng-content />
    </sd-empty-state>
  `,
})
export class ErrorStateComponent {
  readonly error = input<unknown>(null);
  readonly notFoundTitle = input('Not found');
  readonly notFoundMessage = input<string | null>('It may have been moved or removed, or you may not have access to it.');
  readonly errorTitle = input("This couldn't be loaded");
  readonly compact = input(false, { transform: booleanAttribute });
  readonly retry = output<void>();

  protected readonly notFound = computed(() => isNotFound(this.error()));
  protected readonly title = computed(() => (this.notFound() ? this.notFoundTitle() : this.errorTitle()));
  protected readonly message = computed(() => (this.notFound() ? this.notFoundMessage() : errorMessage(this.error())));
}
