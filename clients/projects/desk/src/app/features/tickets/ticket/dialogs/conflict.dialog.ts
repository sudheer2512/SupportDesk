import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { TicketDetailDto } from '@sd/core';
import { SdButton, SdDialog, SdIcon, SdRelativeTimePipe } from '@sd/ui';

export interface ConflictDialogData {
  /** The ticket as it is now on the server (from the 409 problem's `current`). */
  current: TicketDetailDto | null;
  /** field → the value the agent tried to save. */
  labels: Record<string, string>;
  message: string;
}

const FIELD_LABELS: Record<string, string> = {
  subject: 'Subject',
  priority: 'Priority',
  severity: 'Severity',
  status: 'Status',
  productId: 'Product',
  moduleId: 'Module',
  versionId: 'Version',
  installationId: 'Installation',
  categoryId: 'Category',
  subcategoryId: 'Subcategory',
  issueTypeId: 'Issue type',
  language: 'Language',
  customFields: 'Custom fields',
  descriptionHtml: 'Description',
};

/** 409 concurrency_conflict: shows the server's current values next to the agent's change; re-apply or keep theirs. */
@Component({
  selector: 'desk-conflict-dialog',
  imports: [SdDialog, SdButton, SdIcon, SdRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Someone else changed this ticket" [description]="data.message">
      <p class="lead">
        <sd-icon name="info" [size]="16" />
        The ticket now shows the latest saved values
        @if (data.current; as c) {
          (updated {{ c.updatedAt | sdRelativeTime }})
        }. Compare, then re-apply your change or keep theirs.
      </p>
      <table class="cmp">
        <thead>
          <tr><th scope="col">Field</th><th scope="col">Now saved</th><th scope="col">Your change</th></tr>
        </thead>
        <tbody>
          @for (row of rows(); track row.field) {
            <tr>
              <th scope="row">{{ row.label }}</th>
              <td>{{ row.current || '—' }}</td>
              <td class="mine">{{ row.mine || '—' }}</td>
            </tr>
          }
        </tbody>
      </table>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close('discard')">Keep theirs</button>
        <button sdButton variant="primary" type="button" (click)="ref.close('reapply')">Re-apply my change</button>
      </div>
    </sd-dialog>
  `,
  styles: `
    .lead {
      display: flex;
      gap: 8px;
      align-items: flex-start;
      color: var(--sd-text-2);
      margin-bottom: var(--sd-space-3);
    }
    .cmp {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--sd-text-sm);
    }
    .cmp th,
    .cmp td {
      padding: 6px 8px;
      border-bottom: 1px solid var(--sd-divider);
      text-align: left;
      vertical-align: top;
    }
    thead th {
      color: var(--sd-text-3);
      font-weight: var(--sd-weight-medium);
    }
    .mine {
      color: var(--sd-primary);
      font-weight: var(--sd-weight-medium);
    }
  `,
})
export class ConflictDialog {
  protected readonly data = inject<ConflictDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<'reapply' | 'discard'>>(DialogRef);

  protected readonly rows = computed(() => {
    const c = this.data.current;
    return Object.entries(this.data.labels).map(([field, mine]) => ({
      field,
      label: FIELD_LABELS[field] ?? field,
      mine,
      current: c ? currentValue(c, field) : '',
    }));
  });
}

function currentValue(t: TicketDetailDto, field: string): string {
  switch (field) {
    case 'status':
      return t.status;
    case 'productId':
      return t.productName ?? '';
    case 'moduleId':
      return t.moduleName ?? '';
    case 'versionId':
      return t.versionName ?? '';
    case 'installationId':
      return t.installationName ?? '';
    case 'categoryId':
      return t.categoryName ?? '';
    case 'subcategoryId':
      return t.subcategoryName ?? '';
    case 'issueTypeId':
      return t.issueTypeName ?? '';
    case 'customFields':
      return Object.entries(t.customFields ?? {})
        .map(([k, v]) => `${k}: ${v ?? ''}`)
        .join(', ');
    default: {
      const v = (t as unknown as Record<string, unknown>)[field];
      return v === null || v === undefined ? '' : String(v);
    }
  }
}
