import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ApiError } from '@sd/core';
import { SdButton, SdDialog, SdFormField, SdInput } from '@sd/ui';
import { Observable } from 'rxjs';

export interface NameDescriptionData<T> {
  title: string;
  description?: string;
  name?: string | null;
  text?: string | null;
  nameLabel?: string;
  confirmLabel?: string;
  /** Saves the values; server field errors (`name`, `description`) are shown on the fields. */
  save: (name: string, description: string | null) => Observable<T>;
}

/**
 * Create / rename dialog for simple named records (customer groups, product families): name + optional description,
 * saved through the given callback. Closes with the saved record.
 */
@Component({
  selector: 'desk-name-description-dialog',
  imports: [SdDialog, SdFormField, SdInput, SdButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="data.title" [description]="data.description ?? null">
      <form id="name-description" class="form" (submit)="$event.preventDefault(); submit()" novalidate>
        <sd-form-field [label]="data.nameLabel ?? 'Name'" required [error]="errors()['name']">
          <input sdInput name="name" maxlength="200" autocomplete="off" [value]="name()" (input)="name.set($any($event.target).value)" />
        </sd-form-field>
        <sd-form-field label="Description" optional [error]="errors()['description']">
          <textarea sdInput name="description" rows="3" maxlength="2000" [value]="text()" (input)="text.set($any($event.target).value)"></textarea>
        </sd-form-field>
        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="name-description" [disabled]="!name().trim()" [loading]="saving()">
          {{ data.confirmLabel ?? 'Save' }}
        </button>
      </div>
    </sd-dialog>
  `,
  styles: `
    .form {
      display: grid;
      gap: var(--sd-space-3);
    }
    .error {
      color: var(--sd-danger);
    }
  `,
})
export class NameDescriptionDialog<T> {
  protected readonly data = inject<NameDescriptionData<T>>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<T>>(DialogRef);
  protected readonly name = signal(this.data.name ?? '');
  protected readonly text = signal(this.data.text ?? '');
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly errors = signal<Record<string, string>>({});

  protected submit(): void {
    const name = this.name().trim();
    if (!name || this.saving()) return;
    this.saving.set(true);
    this.error.set(null);
    this.errors.set({});
    this.data.save(name, this.text().trim() || null).subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.ref.close(saved);
      },
      error: (err: unknown) => {
        this.saving.set(false);
        const e = ApiError.from(err);
        const fields = { name: e.fieldError('name') ?? '', description: e.fieldError('description') ?? '' };
        this.errors.set(fields);
        if (!fields.name && !fields.description) this.error.set(e.userMessage);
      },
    });
  }
}
