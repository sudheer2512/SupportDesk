import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { SdButton, SdDialog, SdDialogService, SdFormField, SdInput } from '@sd/ui';
import { firstValueFrom } from 'rxjs';

export interface KbPromptOptions {
  title: string;
  message?: string;
  label: string;
  placeholder?: string;
  hint?: string;
  /** The confirm button stays disabled until text is entered. */
  required?: boolean;
  multiline?: boolean;
  confirmLabel?: string;
  danger?: boolean;
  initial?: string;
  maxLength?: number;
}

/** Asks for one line/paragraph of text (reject note, moderation reason, change note). */
@Component({
  selector: 'desk-kb-prompt-dialog',
  imports: [FormsModule, SdDialog, SdButton, SdFormField, SdInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="data.title" [description]="data.message ?? null">
      <form id="kb-prompt-form" (ngSubmit)="submit()" novalidate>
        <sd-form-field [label]="data.label" [hint]="data.hint ?? null" [required]="!!data.required" [optional]="!data.required" [error]="error()">
          @if (data.multiline) {
            <textarea sdInput name="text" rows="4" [placeholder]="data.placeholder ?? ''" [attr.maxlength]="data.maxLength ?? 2000" [(ngModel)]="text"></textarea>
          } @else {
            <input sdInput name="text" [placeholder]="data.placeholder ?? ''" [attr.maxlength]="data.maxLength ?? 500" [(ngModel)]="text" />
          }
        </sd-form-field>
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close(null)">Cancel</button>
        <button sdButton type="submit" form="kb-prompt-form" [variant]="data.danger ? 'danger' : 'primary'" [disabled]="!valid()">
          {{ data.confirmLabel ?? 'OK' }}
        </button>
      </div>
    </sd-dialog>
  `,
})
export class KbPromptDialog {
  protected readonly data = inject<KbPromptOptions>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<string | null>>(DialogRef);
  protected readonly text = signal(this.data.initial ?? '');
  protected readonly touched = signal(false);
  protected readonly valid = computed(() => !this.data.required || this.text().trim().length > 0);
  protected readonly error = computed(() => (this.touched() && !this.valid() ? `${this.data.label} is required.` : null));

  protected submit(): void {
    this.touched.set(true);
    if (!this.valid()) return;
    this.ref.close(this.text().trim());
  }
}

/** Opens {@link KbPromptDialog}; resolves to the entered text, or null when cancelled. */
export async function promptText(dialogs: SdDialogService, options: KbPromptOptions): Promise<string | null> {
  const ref = dialogs.open<string | null, KbPromptOptions, KbPromptDialog>(KbPromptDialog, { data: options, size: 'sm' });
  const result = await firstValueFrom(ref.closed);
  return typeof result === 'string' ? result : null;
}
