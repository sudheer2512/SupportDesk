import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, catchError, finalize, of, shareReplay, tap, throwError } from 'rxjs';
import { MeStore } from '../auth/me.store';
import { ApiClient } from '../http/api-client';
import { Guid } from '../models/common';
import { AgentRefDto, StatusDto, TicketFormMetaDto } from '../models/meta';

/**
 * Ticket form/filter metadata (GET /meta/ticket-form), cached per tenant for the session. Read it as an
 * Observable (`ticketForm$()`) or as signals (`ticketForm`, `statusById`, `agentById`) after `load()`.
 * Call `invalidate()` after admins change statuses, fields, layouts, etc.
 */
@Injectable({ providedIn: 'root' })
export class MetaApi {
  private readonly api = inject(ApiClient);
  private readonly me = inject(MeStore);
  private cached$: Observable<TicketFormMetaDto> | null = null;
  private cachedTenant: Guid | null = null;
  private readonly _ticketForm = signal<TicketFormMetaDto | null>(null);

  readonly ticketForm = this._ticketForm.asReadonly();
  readonly loading = signal(false);
  readonly statusById = computed(() => new Map<Guid, StatusDto>((this._ticketForm()?.statuses ?? []).map((s) => [s.id, s])));
  readonly agentById = computed(() => new Map<Guid, AgentRefDto>((this._ticketForm()?.agents ?? []).map((a) => [a.userId, a])));

  /** Cached metadata; `force` refetches. */
  ticketForm$(force = false): Observable<TicketFormMetaDto> {
    const tenant = this.me.user()?.tenantId ?? null;
    if (force || tenant !== this.cachedTenant) this.cached$ = null;
    if (!this.cached$) {
      this.cachedTenant = tenant;
      this.loading.set(true);
      this.cached$ = this.api.get<TicketFormMetaDto>('meta/ticket-form').pipe(
        tap((meta) => this._ticketForm.set(meta)),
        catchError((err: unknown) => {
          this.cached$ = null;
          return throwError(() => err);
        }),
        finalize(() => this.loading.set(false)),
        shareReplay({ bufferSize: 1, refCount: false }),
      );
    }
    return this.cached$;
  }

  /** Fire-and-forget load that fills the signals (errors are toasted by the interceptor). */
  load(): void {
    this.ticketForm$()
      .pipe(catchError(() => of(null)))
      .subscribe();
  }

  invalidate(): void {
    this.cached$ = null;
    this._ticketForm.set(null);
  }

  /**
   * Re-fetches metadata that was already loaded, keeping the current value until the new one arrives (after an
   * administrator changes statuses, fields, layouts…; also triggered by the realtime `config.changed` event).
   */
  refresh(): void {
    if (!this.cached$ && !this._ticketForm()) return;
    this.ticketForm$(true)
      .pipe(catchError(() => of(null)))
      .subscribe();
  }
}
