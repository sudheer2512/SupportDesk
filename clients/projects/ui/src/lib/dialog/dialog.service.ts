import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { ComponentType } from '@angular/cdk/portal';
import { ChangeDetectionStrategy, Component, Injectable, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { SdButton } from '../button/button.component';
import { SdDialog } from './dialog.component';

export type SdDialogSize = 'sm' | 'md' | 'lg' | 'xl' | 'full';

export interface SdDialogOptions<D> {
  data?: D;
  size?: SdDialogSize;
  /** Prevent closing by Escape / backdrop click (forms with unsaved changes). */
  disableClose?: boolean;
  ariaLabel?: string;
}

const WIDTHS: Record<SdDialogSize, string> = {
  sm: '420px',
  md: '560px',
  lg: '760px',
  xl: '1040px',
  full: 'calc(100vw - 32px)',
};

export interface SdConfirmOptions {
  title: string;
  message?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  /** Destructive action styling. */
  danger?: boolean;
  /** Require typing this text to enable the confirm button (e.g. a ticket number before delete). */
  typeToConfirm?: string;
}

/** Built-in confirmation dialog used by SdDialogService.confirm(). */
@Component({
  selector: 'sd-confirm-dialog',
  imports: [SdDialog, SdButton, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="data.title" [description]="data.message ?? null">
      @if (data.typeToConfirm) {
        <label class="confirm-label">
          Type <strong>{{ data.typeToConfirm }}</strong> to confirm
          <input class="sd-input" [ngModel]="typed()" (ngModelChange)="typed.set($event)" autocomplete="off" />
        </label>
      }
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close(false)">{{ data.cancelLabel ?? 'Cancel' }}</button>
        <button
          sdButton
          type="button"
          [variant]="data.danger ? 'danger' : 'primary'"
          [disabled]="!!data.typeToConfirm && typed() !== data.typeToConfirm"
          (click)="ref.close(true)"
        >
          {{ data.confirmLabel ?? 'Confirm' }}
        </button>
      </div>
    </sd-dialog>
  `,
  styles: `
    .confirm-label {
      display: grid;
      gap: 6px;
      margin-top: var(--sd-space-2);
      color: var(--sd-text-2);
    }
  `,
})
export class SdConfirmDialog {
  protected readonly data = inject<SdConfirmOptions>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  protected readonly typed = signal('');
}

/**
 * Opens modal dialogs (CDK Dialog: focus trap, focus restore, Escape, aria-modal) with SupportDesk styling.
 * `open(MyDialog, { data, size })` → DialogRef (`ref.closed` emits the result);
 * `confirm({ title, message, danger })` → Promise<boolean>.
 */
@Injectable({ providedIn: 'root' })
export class SdDialogService {
  private readonly dialog = inject(Dialog);

  open<R = unknown, D = unknown, C = unknown>(component: ComponentType<C>, options: SdDialogOptions<D> = {}): DialogRef<R, C> {
    const size = options.size ?? 'md';
    return this.dialog.open<R, D, C>(component, {
      data: options.data,
      width: WIDTHS[size],
      maxWidth: 'calc(100vw - 32px)',
      panelClass: ['sd-dialog-panel', `sd-dialog-panel--${size}`],
      backdropClass: 'sd-backdrop',
      hasBackdrop: true,
      disableClose: options.disableClose ?? false,
      ariaLabel: options.ariaLabel ?? null,
      ariaModal: true,
      autoFocus: 'first-tabbable',
      restoreFocus: true,
    });
  }

  async confirm(options: SdConfirmOptions): Promise<boolean> {
    const ref = this.open<boolean, SdConfirmOptions, SdConfirmDialog>(SdConfirmDialog, { data: options, size: 'sm' });
    const result = await firstValueFrom(ref.closed);
    return result === true;
  }

  closeAll(): void {
    this.dialog.closeAll();
  }
}
