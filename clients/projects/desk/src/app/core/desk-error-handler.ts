import { ErrorHandler, Injectable, inject } from '@angular/core';
import { ApiError, ToastBridge } from '@sd/core';

/**
 * Last-resort handler for uncaught errors (including unhandled promise rejections via
 * provideBrowserGlobalErrorListeners). Network/5xx/403/429 API errors were already toasted by the HTTP
 * interceptor; other API errors that nobody handled get a short toast; programming errors are logged and
 * reported once with a generic message.
 */
@Injectable()
export class DeskErrorHandler implements ErrorHandler {
  private readonly toasts = inject(ToastBridge);

  handleError(error: unknown): void {
    const cause = unwrap(error);
    if (cause instanceof ApiError) {
      const alreadyToasted = cause.status === 0 || cause.status >= 500 || cause.status === 403 || cause.status === 429 || cause.status === 401;
      if (!alreadyToasted) {
        this.toasts.show({ kind: 'warning', title: cause.title, message: cause.detail, key: `api:${cause.code ?? cause.status}` });
      }
      return;
    }
    console.error(error);
    this.toasts.show({
      kind: 'danger',
      title: 'Something went wrong',
      message: 'An unexpected error occurred. If it keeps happening, reload the page.',
      key: 'unexpected',
    });
  }
}

function unwrap(error: unknown): unknown {
  // Errors from promises may arrive wrapped as { rejection } (zone-style) or { reason }.
  if (error && typeof error === 'object') {
    const e = error as { rejection?: unknown; reason?: unknown };
    if (e.rejection !== undefined) return e.rejection;
    if (e.reason !== undefined) return e.reason;
  }
  return error;
}
