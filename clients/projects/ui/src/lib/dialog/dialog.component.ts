import { DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, ElementRef, afterNextRender, booleanAttribute, inject, input } from '@angular/core';
import { uniqueId } from '@sd/core';
import { SdIcon } from '../icon/icon.component';

/**
 * Layout shell for dialog content components:
 * `<sd-dialog title="Merge tickets" description="…">body…<div sdDialogFooter>buttons</div></sd-dialog>`.
 * Labels the surrounding CDK dialog (aria-labelledby/-describedby) and renders a close button.
 */
@Component({
  selector: 'sd-dialog',
  imports: [SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="head">
      <div class="titles">
        <h2 [id]="titleId">{{ title() }}</h2>
        @if (description()) {
          <p class="description" [id]="descriptionId">{{ description() }}</p>
        }
      </div>
      @if (closable()) {
        <button type="button" class="close" aria-label="Close dialog" (click)="close()">
          <sd-icon name="x" [size]="16" />
        </button>
      }
    </header>
    <div class="body"><ng-content /></div>
    <footer class="foot"><ng-content select="[sdDialogFooter]" /></footer>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      min-height: 0;
      max-height: inherit;
      width: 100%;
    }
    .head {
      display: flex;
      align-items: flex-start;
      gap: var(--sd-space-3);
      padding: var(--sd-space-5) var(--sd-space-5) var(--sd-space-3);
    }
    .titles {
      flex: 1;
      min-width: 0;
    }
    h2 {
      font-size: var(--sd-text-lg);
      font-weight: var(--sd-weight-semibold);
    }
    .description {
      margin-top: 4px;
      color: var(--sd-text-2);
    }
    .close {
      display: inline-flex;
      padding: 6px;
      border: 0;
      border-radius: var(--sd-radius-sm);
      background: transparent;
      color: var(--sd-text-3);
      cursor: pointer;
    }
    .close:hover {
      background: var(--sd-surface-3);
      color: var(--sd-text);
    }
    .body {
      flex: 1;
      min-height: 0;
      overflow: auto;
      padding: 0 var(--sd-space-5) var(--sd-space-4);
    }
    .foot {
      display: flex;
      justify-content: flex-end;
      gap: var(--sd-space-2);
      padding: var(--sd-space-3) var(--sd-space-5);
      border-top: 1px solid var(--sd-divider);
      background: var(--sd-surface-2);
    }
    .foot:empty {
      display: none;
    }
  `,
})
export class SdDialog {
  readonly title = input.required<string>();
  readonly description = input<string | null>(null);
  readonly closable = input(true, { transform: booleanAttribute });

  protected readonly titleId = uniqueId('sd-dlg-title');
  protected readonly descriptionId = uniqueId('sd-dlg-desc');
  private readonly ref = inject(DialogRef, { optional: true });
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  constructor() {
    afterNextRender(() => {
      const container = this.host.nativeElement.closest('.cdk-dialog-container');
      if (!container) return;
      if (!container.getAttribute('aria-labelledby') && !container.getAttribute('aria-label')) {
        container.setAttribute('aria-labelledby', this.titleId);
      }
      if (this.description() && !container.getAttribute('aria-describedby')) {
        container.setAttribute('aria-describedby', this.descriptionId);
      }
    });
  }

  close(result?: unknown): void {
    this.ref?.close(result);
  }
}
