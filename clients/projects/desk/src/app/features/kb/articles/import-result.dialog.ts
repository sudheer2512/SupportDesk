import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { KbImportResultDto } from '@sd/core';
import { SdBadge, SdButton, SdDialog, SdIcon } from '@sd/ui';

/** Result of a knowledge-base import: counts plus every issue the server reported. Closes with 'open' to jump to the first draft. */
@Component({
  selector: 'desk-kb-import-result-dialog',
  imports: [SdDialog, SdButton, SdBadge, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Import finished" [description]="summary()">
      <dl class="stats">
        <div>
          <dt>Drafts created</dt>
          <dd>{{ data.created }}</dd>
        </div>
        <div>
          <dt>Skipped</dt>
          <dd>{{ data.skipped }}</dd>
        </div>
        <div>
          <dt>Categories created</dt>
          <dd>{{ data.categoriesCreated }}</dd>
        </div>
      </dl>
      @if (data.issues.length) {
        <h3 class="issues-title">Issues ({{ data.issues.length }})</h3>
        <ul class="issues">
          @for (issue of data.issues; track $index) {
            <li>
              <sd-badge [tone]="issue.severity === 'error' ? 'danger' : 'warning'" size="sm" [icon]="issue.severity === 'error' ? 'circle-x' : 'triangle-alert'">
                {{ issue.severity === 'error' ? 'Error' : 'Warning' }}
              </sd-badge>
              <div class="issue-text">
                <strong class="sd-truncate">{{ issue.item }}</strong>
                <span>{{ issue.message }}</span>
              </div>
            </li>
          }
        </ul>
      } @else {
        <p class="ok"><sd-icon name="circle-check" [size]="16" /> No issues were reported.</p>
      }
      <div sdDialogFooter>
        @if (data.articleIds.length) {
          <button sdButton type="button" (click)="ref.close('open')">Open first draft</button>
        }
        <button sdButton variant="primary" type="button" (click)="ref.close()">Done</button>
      </div>
    </sd-dialog>
  `,
  styles: `
    .stats {
      display: grid;
      grid-template-columns: repeat(3, 1fr);
      gap: var(--sd-space-3);
      margin: 0 0 var(--sd-space-4);
    }
    .stats div {
      padding: var(--sd-space-3);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface-2);
    }
    dt {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    dd {
      margin: 2px 0 0;
      font-size: var(--sd-text-xl);
      font-weight: var(--sd-weight-semibold);
      font-variant-numeric: tabular-nums;
    }
    .issues-title {
      margin-bottom: var(--sd-space-2);
      font-size: var(--sd-text-md);
    }
    .issues {
      display: grid;
      gap: 6px;
      max-height: 320px;
      margin: 0;
      padding: 0;
      overflow: auto;
      list-style: none;
    }
    .issues li {
      display: flex;
      align-items: flex-start;
      gap: var(--sd-space-2);
      padding: 8px;
      border: 1px solid var(--sd-divider);
      border-radius: var(--sd-radius-sm);
    }
    .issue-text {
      display: grid;
      gap: 2px;
      min-width: 0;
      font-size: var(--sd-text-sm);
    }
    .ok {
      display: flex;
      align-items: center;
      gap: 6px;
      color: var(--sd-success);
    }
  `,
})
export class ImportResultDialog {
  protected readonly data = inject<KbImportResultDto>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<'open' | undefined>>(DialogRef);
  protected readonly summary = computed(() => {
    const errors = this.data.issues.filter((i) => i.severity === 'error').length;
    const created = `${this.data.created} draft${this.data.created === 1 ? '' : 's'} created`;
    return errors ? `${created} · ${errors} error${errors === 1 ? '' : 's'}` : created;
  });
}
