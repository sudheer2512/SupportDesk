import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { SdButton, SdDialog, SdFormField, SdInput } from '@sd/ui';

export interface TextPromptData {
  title: string;
  label: string;
  value?: string;
  hint?: string;
  confirmLabel?: string;
  multiline?: boolean;
}

/** Small dialog asking for one text value (view names, department names…). Closes with the trimmed text. */
@Component({
  selector: 'desk-text-prompt-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="data.title">
      <form id="text-prompt" (ngSubmit)="submit()">
        <sd-form-field [label]="data.label" [hint]="data.hint ?? null" required>
          @if (data.multiline) {
            <textarea sdInput name="value" rows="4" [(ngModel)]="value" required></textarea>
          } @else {
            <input sdInput name="value" [(ngModel)]="value" required maxlength="200" />
          }
        </sd-form-field>
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="text-prompt" [disabled]="!value.trim()">{{ data.confirmLabel ?? 'Save' }}</button>
      </div>
    </sd-dialog>
  `,
})
export class TextPromptDialog {
  protected readonly data = inject<TextPromptData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<string>>(DialogRef);
  protected value = this.data.value ?? '';

  protected submit(): void {
    if (this.value.trim()) this.ref.close(this.value.trim());
  }
}
