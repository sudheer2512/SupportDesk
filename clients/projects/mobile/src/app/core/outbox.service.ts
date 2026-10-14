import { DestroyRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import {
  ApiError,
  FilesApi,
  PortalCreateTicketRequest,
  PortalTicketsApi,
  ThreadDto,
  TicketsApi,
  UploadEvent,
  newClientRequestId,
} from '@sd/core';
import { Observable, Subject, filter, firstValueFrom, map } from 'rxjs';
import { NetworkService, isUnreachableStatus } from './network.service';
import { OfflineDb } from './offline-cache.service';
import { SessionSnapshotStore } from './session-snapshot';

export type OutboxKind = 'agent-reply' | 'agent-note' | 'customer-reply' | 'customer-ticket';
/** queued → sending → (removed) | conflict (reply collision) | failed (rejected by the server). */
export type OutboxStatus = 'queued' | 'sending' | 'conflict' | 'failed';

export interface OutboxFile {
  name: string;
  type: string;
  size: number;
  blob: Blob;
  /** Set once uploaded, so a retry never uploads the same file twice. */
  fileId: string | null;
}

export interface OutboxEntry {
  /** Also the idempotency key sent as `clientRequestId`: a retry after a lost response never duplicates. */
  id: string;
  scope: string;
  kind: OutboxKind;
  ticketId: string | null;
  brand: string | null;
  /** Human label: "#1042 · Printer offline" or the new ticket's subject. */
  label: string;
  bodyHtml: string;
  /** Plain text as typed (restored into the composer on "Edit"). */
  bodyText: string;
  basedOnSeq: number | null;
  force: boolean;
  noteKind: 'note' | 'handover';
  /** New customer ticket (attachments travel in `files`). */
  ticket: Omit<PortalCreateTicketRequest, 'attachmentFileIds' | 'clientRequestId'> | null;
  files: OutboxFile[];
  status: OutboxStatus;
  attempts: number;
  nextAttemptAt: number;
  lastError: string | null;
  /** Reply collision: the messages that arrived since the reply was written. */
  conflict: ThreadDto[] | null;
  createdAt: number;
}

export interface OutboxSent {
  entry: OutboxEntry;
  /** Ticket the entry went to (for new tickets: the created one). */
  ticketId: string;
  displayNumber: string | null;
}

export type NewOutboxEntry = Pick<OutboxEntry, 'kind' | 'label' | 'bodyHtml' | 'bodyText'> &
  Partial<Pick<OutboxEntry, 'ticketId' | 'brand' | 'basedOnSeq' | 'noteKind' | 'ticket'>> & { files?: File[] };

/** Retry delays after network failures / 5xx: 5 s, 15 s, 1 min, 5 min, then every 15 min. */
const BACKOFF_MS = [5_000, 15_000, 60_000, 300_000, 900_000];

/**
 * Durable send queue for everything a user writes on the phone: agent replies and notes, customer replies and new
 * tickets. Every message goes through here — online it is sent immediately, offline (or when the server is down) it
 * waits in IndexedDB and is retried with backoff when connectivity returns, the app resumes or the user signs back in.
 * Each entry carries an idempotency key (`clientRequestId`), so retries after a lost response are safe. A reply
 * collision (409 with the newer messages) parks the entry as `conflict` until the user chooses "Send anyway".
 */
@Injectable({ providedIn: 'root' })
export class OutboxService {
  private readonly db = inject(OfflineDb).db;
  private readonly session = inject(SessionSnapshotStore);
  private readonly network = inject(NetworkService);
  private readonly tickets = inject(TicketsApi);
  private readonly files = inject(FilesApi);
  private readonly portal = inject(PortalTicketsApi);

  private readonly _entries = signal<OutboxEntry[]>([]);
  private readonly sentSubject = new Subject<OutboxSent>();
  private running: Promise<void> | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;
  /** Session gate: the app tells the outbox when the API can be called (signed in, not an offline session). */
  private readonly canSend = signal(false);

  /** Entries of the signed-in identity, oldest first. */
  readonly entries = this._entries.asReadonly();
  readonly pendingCount = computed(() => this._entries().length);
  readonly needsAttention = computed(() => this._entries().filter((e) => e.status === 'conflict' || e.status === 'failed').length);
  /** Emits after an entry was delivered (refresh the timeline / list). */
  readonly sent$: Observable<OutboxSent> = this.sentSubject.asObservable();

  constructor() {
    effect(() => {
      const scope = this.session.scope();
      untracked(() => void this.reload(scope));
    });
    const sub = this.network.cameOnline$.subscribe(() => this.kick());
    inject(DestroyRef).onDestroy(() => {
      sub.unsubscribe();
      if (this.timer) clearTimeout(this.timer);
    });
  }

  forTicket(ticketId: string) {
    return computed(() => this._entries().filter((e) => e.ticketId === ticketId && e.kind !== 'customer-ticket'));
  }

  /** Enables sending (signed in with a live session) and flushes anything waiting. */
  setSessionReady(ready: boolean): void {
    this.canSend.set(ready);
    if (ready) this.kick();
  }

  async enqueue(input: NewOutboxEntry): Promise<OutboxEntry> {
    const scope = this.session.scope();
    if (!scope) throw new Error('Not signed in.');
    const entry: OutboxEntry = {
      id: newClientRequestId(),
      scope,
      kind: input.kind,
      ticketId: input.ticketId ?? null,
      brand: input.brand ?? null,
      label: input.label,
      bodyHtml: input.bodyHtml,
      bodyText: input.bodyText,
      basedOnSeq: input.basedOnSeq ?? null,
      force: false,
      noteKind: input.noteKind ?? 'note',
      ticket: input.ticket ?? null,
      files: (input.files ?? []).map((f) => ({ name: f.name, type: f.type || 'application/octet-stream', size: f.size, blob: f, fileId: null })),
      status: 'queued',
      attempts: 0,
      nextAttemptAt: 0,
      lastError: null,
      conflict: null,
      createdAt: Date.now(),
    };
    await this.db.put('outbox', entry);
    this._entries.update((list) => [...list, entry]);
    this.kick();
    return entry;
  }

  /** Re-sends a collided reply on purpose (`force`), keeping the same idempotency key. */
  async sendAnyway(id: string): Promise<void> {
    await this.patch(id, { force: true, status: 'queued', nextAttemptAt: 0, conflict: null, lastError: null });
    this.kick();
  }

  async retry(id: string): Promise<void> {
    await this.patch(id, { status: 'queued', nextAttemptAt: 0, lastError: null });
    this.kick();
  }

  async discard(id: string): Promise<void> {
    await this.db.delete('outbox', id);
    this._entries.update((list) => list.filter((e) => e.id !== id));
  }

  /** Removes the entry and hands it back so its text can be edited in the composer. */
  async takeBack(id: string): Promise<OutboxEntry | null> {
    const entry = this._entries().find((e) => e.id === id) ?? null;
    if (entry && entry.status !== 'sending') await this.discard(id);
    return entry && entry.status !== 'sending' ? entry : null;
  }

  /** Runs the queue now (no-op when offline, signed out or already running). */
  kick(): void {
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }
    void this.process();
  }

  /** Waits for a specific entry to leave the `sending` state (used by composers to show the outcome inline). */
  async settle(id: string): Promise<OutboxEntry | null> {
    await this.process();
    return this._entries().find((e) => e.id === id) ?? null;
  }

  private process(): Promise<void> {
    this.running ??= this.drain().finally(() => {
      this.running = null;
      this.schedule();
    });
    return this.running;
  }

  private async drain(): Promise<void> {
    for (;;) {
      if (!this.canSend() || !this.network.online()) return;
      const now = Date.now();
      const next = this._entries().find((e) => e.status === 'queued' && e.nextAttemptAt <= now);
      if (!next) return;
      const keepGoing = await this.sendOne(next);
      if (!keepGoing) return;
    }
  }

  /** Sends one entry; returns false when the queue should pause (offline / signed out). */
  private async sendOne(entry: OutboxEntry): Promise<boolean> {
    await this.patch(entry.id, { status: 'sending' });
    try {
      const files = await this.upload(entry);
      const fileIds = files.map((f) => f.fileId!).filter(Boolean);
      const sent = await this.deliver(entry, fileIds);
      await this.db.delete('outbox', entry.id);
      this._entries.update((list) => list.filter((e) => e.id !== entry.id));
      this.sentSubject.next({ entry, ...sent });
      return true;
    } catch (e) {
      const err = ApiError.from(e);
      if (isUnreachableStatus(err.status) || err.status === 401 || err.status === 429 || err.status >= 500) {
        const attempts = entry.attempts + 1;
        await this.patch(entry.id, {
          status: 'queued',
          attempts,
          nextAttemptAt: Date.now() + BACKOFF_MS[Math.min(attempts - 1, BACKOFF_MS.length - 1)],
          lastError: isUnreachableStatus(err.status) ? 'Waiting for a connection' : err.userMessage,
        });
        if (isUnreachableStatus(err.status)) this.network.markUnreachable();
        // Offline or signed out: stop; 429/5xx: try the next entry.
        return !(isUnreachableStatus(err.status) || err.status === 401);
      }
      const collision = err.replyCollision;
      if (collision) {
        await this.patch(entry.id, { status: 'conflict', conflict: collision.newEntries, basedOnSeq: collision.currentSeq, lastError: null });
      } else {
        await this.patch(entry.id, { status: 'failed', lastError: err.userMessage });
      }
      return true;
    }
  }

  private async upload(entry: OutboxEntry): Promise<OutboxFile[]> {
    const files = entry.files.slice();
    for (let i = 0; i < files.length; i++) {
      if (files[i].fileId) continue;
      const f = files[i];
      const file = f.blob instanceof File ? f.blob : new File([f.blob], f.name, { type: f.type });
      const events$: Observable<UploadEvent> =
        entry.kind === 'customer-reply' || entry.kind === 'customer-ticket' ? this.portal.upload(entry.brand ?? '', file) : this.files.upload(file, f.name);
      const fileId = await firstValueFrom(
        events$.pipe(
          filter((ev): ev is Extract<UploadEvent, { kind: 'done' }> => ev.kind === 'done'),
          map((ev) => ev.file.fileId),
        ),
      );
      files[i] = { ...f, fileId };
      await this.patch(entry.id, { files });
    }
    return files;
  }

  private async deliver(entry: OutboxEntry, fileIds: string[]): Promise<{ ticketId: string; displayNumber: string | null }> {
    const attachmentFileIds = fileIds.length ? fileIds : null;
    switch (entry.kind) {
      case 'agent-reply':
        await firstValueFrom(
          this.tickets.reply(entry.ticketId!, {
            bodyHtml: entry.bodyHtml,
            attachmentFileIds,
            basedOnSeq: entry.basedOnSeq,
            force: entry.force,
            clientRequestId: entry.id,
          }),
        );
        return { ticketId: entry.ticketId!, displayNumber: null };
      case 'agent-note':
        await firstValueFrom(
          this.tickets.addNote(entry.ticketId!, { bodyHtml: entry.bodyHtml, kind: entry.noteKind, attachmentFileIds, clientRequestId: entry.id }),
        );
        return { ticketId: entry.ticketId!, displayNumber: null };
      case 'customer-reply':
        await firstValueFrom(
          this.portal.reply(entry.brand ?? '', entry.ticketId!, { bodyHtml: entry.bodyHtml, attachmentFileIds, clientRequestId: entry.id }),
        );
        return { ticketId: entry.ticketId!, displayNumber: null };
      case 'customer-ticket': {
        const created = await firstValueFrom(
          this.portal.create(entry.brand ?? '', { ...(entry.ticket ?? { subject: entry.label }), attachmentFileIds, clientRequestId: entry.id }),
        );
        return { ticketId: created.id, displayNumber: created.displayNumber };
      }
    }
  }

  private schedule(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    const queued = this._entries().filter((e) => e.status === 'queued');
    if (!queued.length || !this.canSend()) return;
    const due = Math.min(...queued.map((e) => e.nextAttemptAt));
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.process();
    }, Math.max(1_000, due - Date.now()));
  }

  private async patch(id: string, changes: Partial<OutboxEntry>): Promise<void> {
    let updated: OutboxEntry | null = null;
    this._entries.update((list) =>
      list.map((e) => {
        if (e.id !== id) return e;
        updated = { ...e, ...changes };
        return updated;
      }),
    );
    if (updated) await this.db.put('outbox', updated);
  }

  private async reload(scope: string | null): Promise<void> {
    if (!scope) {
      this._entries.set([]);
      return;
    }
    const rows = await this.db.byScope<OutboxEntry>('outbox', scope);
    if (this.session.scope() !== scope) return;
    // An entry left "sending" by a killed app is re-queued (the idempotency key makes the resend safe).
    const restored = rows
      .map((e) => (e.status === 'sending' ? { ...e, status: 'queued' as const } : e))
      .sort((a, b) => a.createdAt - b.createdAt);
    this._entries.set(restored);
    this.kick();
  }
}
