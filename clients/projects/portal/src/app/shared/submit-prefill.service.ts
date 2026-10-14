import { Injectable } from '@angular/core';

/** Values handed to the submit-a-ticket form by another screen (troubleshooting flow, assistant, article). */
export interface TicketPrefill {
  subject?: string | null;
  descriptionHtml?: string | null;
  sourceFlowId?: string | null;
}

/**
 * One-shot hand-over of pre-filled ticket values: the source screen calls `set()` and navigates to
 * /:brand/submit; the form `take()`s them once (they survive a sign-in redirect because the service is
 * an in-memory singleton). Nothing is persisted.
 */
@Injectable({ providedIn: 'root' })
export class SubmitPrefillService {
  private pending: TicketPrefill | null = null;

  set(prefill: TicketPrefill): void {
    this.pending = prefill;
  }

  take(): TicketPrefill | null {
    const value = this.pending;
    this.pending = null;
    return value;
  }
}
