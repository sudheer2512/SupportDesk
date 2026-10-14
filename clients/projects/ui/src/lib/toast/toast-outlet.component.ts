import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { SdIcon } from '../icon/icon.component';
import { SdToast, ToastService } from './toast.service';

const ICONS = { info: 'info', success: 'circle-check', warning: 'triangle-alert', danger: 'circle-alert' } as const;

/**
 * `<sd-toast-outlet />` — renders ToastService toasts bottom-right (bottom-centre on phones).
 * Polite live region for info/success; danger/warning toasts use role="alert". Hover/focus pauses.
 */
@Component({
  selector: 'sd-toast-outlet',
  imports: [SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="region" aria-live="polite" aria-relevant="additions">
      @for (toast of toasts.toasts(); track toast.id) {
        <div
          class="toast"
          [class]="'toast toast--' + toast.kind"
          [attr.role]="toast.kind === 'danger' || toast.kind === 'warning' ? 'alert' : 'status'"
          (mouseenter)="toasts.pause(toast.id)"
          (mouseleave)="toasts.resume(toast.id)"
          (focusin)="toasts.pause(toast.id)"
          (focusout)="toasts.resume(toast.id)"
        >
          <sd-icon class="kind-icon" [name]="icon(toast)" [size]="18" />
          <div class="text">
            <p class="title">{{ toast.title }}</p>
            @if (toast.message) {
              <p class="message">{{ toast.message }}</p>
            }
          </div>
          @if (toast.action; as action) {
            <button type="button" class="action" (click)="action.run(); toasts.dismiss(toast.id)">{{ action.label }}</button>
          }
          <button type="button" class="close" aria-label="Dismiss notification" (click)="toasts.dismiss(toast.id)">
            <sd-icon name="x" [size]="14" />
          </button>
        </div>
      }
    </div>
  `,
  styles: `
    .region {
      position: fixed;
      right: var(--sd-space-4);
      bottom: var(--sd-space-4);
      z-index: var(--sd-z-toast);
      display: flex;
      flex-direction: column;
      gap: var(--sd-space-2);
      width: min(380px, calc(100vw - 32px));
      pointer-events: none;
    }
    @media (max-width: 640px) {
      .region {
        right: 50%;
        transform: translateX(50%);
        bottom: calc(var(--sd-space-3) + env(safe-area-inset-bottom, 0px));
      }
    }
    .toast {
      --_c: var(--sd-info);
      display: flex;
      align-items: flex-start;
      gap: var(--sd-space-3);
      padding: 12px 12px 12px 14px;
      border: 1px solid var(--sd-border);
      border-left: 3px solid var(--_c);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      color: var(--sd-text);
      box-shadow: var(--sd-shadow-lg);
      pointer-events: auto;
      animation: sd-toast-in var(--sd-dur-slow) var(--sd-ease);
    }
    @keyframes sd-toast-in {
      from {
        opacity: 0;
        transform: translateY(8px);
      }
    }
    .toast--success {
      --_c: var(--sd-success);
    }
    .toast--warning {
      --_c: var(--sd-warning);
    }
    .toast--danger {
      --_c: var(--sd-danger);
    }
    .kind-icon {
      color: var(--_c);
      margin-top: 1px;
    }
    .text {
      flex: 1;
      min-width: 0;
    }
    .title {
      font-weight: var(--sd-weight-semibold);
    }
    .message {
      margin-top: 2px;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
      overflow-wrap: anywhere;
    }
    .action {
      border: 0;
      background: transparent;
      color: var(--sd-primary);
      font-weight: var(--sd-weight-semibold);
      cursor: pointer;
      padding: 2px 4px;
    }
    .close {
      display: inline-flex;
      border: 0;
      background: transparent;
      color: var(--sd-text-3);
      cursor: pointer;
      padding: 2px;
      border-radius: var(--sd-radius-xs);
    }
    .close:hover {
      color: var(--sd-text);
      background: var(--sd-surface-3);
    }
  `,
})
export class SdToastOutlet {
  protected readonly toasts = inject(ToastService);

  protected icon(toast: SdToast): string {
    return ICONS[toast.kind];
  }
}
