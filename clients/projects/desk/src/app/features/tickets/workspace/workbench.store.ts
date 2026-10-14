import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import {
  ApiError,
  Guid,
  MeStore,
  QueueCountsDto,
  QueueKey,
  RealtimeService,
  SavedViewDto,
  TicketDetailDto,
  TicketQuery,
  ViewsApi,
  WorkbenchApi,
  safeStorage,
} from '@sd/core';
import { Subject, debounceTime, merge } from 'rxjs';

/** What the workspace shows: a standard queue, a saved view, or the recycle bin (deleted tickets, tickets.delete). */
export type QueueSelection = { kind: 'queue'; key: QueueKey } | { kind: 'view'; id: Guid } | { kind: 'trash' };

/** `?queue=deleted` opens the recycle bin. */
export const TRASH_QUEUE_PARAM = 'deleted';

export function sameSelection(a: QueueSelection, b: QueueSelection): boolean {
  if (a.kind !== b.kind) return false;
  if (a.kind === 'queue' && b.kind === 'queue') return a.key === b.key;
  if (a.kind === 'view' && b.kind === 'view') return a.id === b.id;
  return true;
}

/** Query params that name a selection in the URL (`?queue=<key>`, `?queue=deleted`, `?view=<id>`). */
export function selectionQueryParams(s: QueueSelection): Record<string, string> {
  return s.kind === 'queue' ? { queue: s.key } : s.kind === 'view' ? { view: s.id } : { queue: TRASH_QUEUE_PARAM };
}

/** Table-prefs key of the list for a selection (columns are remembered per queue / view). */
export function selectionStorageKey(s: QueueSelection): string {
  return s.kind === 'queue' ? `tickets.queue.${s.key}` : s.kind === 'view' ? `tickets.view.${s.id}` : 'tickets.trash';
}

export interface QueueDef {
  key: QueueKey;
  label: string;
  icon: string;
  /** GET /tickets filters for this queue (mirrors GET /tickets/queue-counts). */
  query: TicketQuery;
}

export const STANDARD_QUEUES: readonly QueueDef[] = [
  { key: 'mine', label: 'My open tickets', icon: 'user-check', query: { mine: true, statusCategory: 'open' } },
  { key: 'unassigned', label: 'Unassigned', icon: 'user-minus', query: { unassigned: true, statusCategory: 'open' } },
  { key: 'open', label: 'All open', icon: 'inbox', query: { statusCategory: 'open' } },
  { key: 'overdue', label: 'Overdue', icon: 'alarm-clock', query: { overdue: true } },
  { key: 'atRisk', label: 'SLA at risk', icon: 'triangle-alert', query: { slaState: 'AtRisk', statusCategory: 'open' } },
  { key: 'escalated', label: 'Escalated', icon: 'arrow-up-right', query: { escalated: true, statusCategory: 'open' } },
  { key: 'watching', label: 'Watching', icon: 'eye', query: { watching: true, statusCategory: 'open' } },
  { key: 'waiting', label: 'Waiting', icon: 'hourglass', query: { statusCategory: 'Waiting' } },
];

export function queueDef(key: QueueKey): QueueDef {
  return STANDARD_QUEUES.find((q) => q.key === key) ?? STANDARD_QUEUES[0];
}

/** One ticket open as a tab in the workspace. */
export interface OpenTicketTab {
  id: Guid;
  displayNumber: string;
  subject: string;
}

/** Commands sent from shortcuts / the palette to the ticket currently open. */
export type TicketCommand =
  | { type: 'reply' }
  | { type: 'note' }
  | { type: 'handover' }
  | { type: 'assignToMe' }
  | { type: 'escalate' }
  | { type: 'status'; statusId?: Guid }
  | { type: 'resolve' }
  | { type: 'reload' };

const MAX_TABS = 12;
const tabsKey = (userId: Guid) => `sd.tickets.tabs.${userId}`;
const selectionKey = (userId: Guid) => `sd.tickets.queue.${userId}`;

/**
 * Workspace state shared by the queue panel, list, tabs, ticket view, context panel, shortcuts and palette:
 * live queue counts and saved views (refreshed from realtime ticket changes), the current queue/view, the
 * open-ticket tabs (persisted per user in localStorage) and the active ticket.
 *
 * Which list opens (`resolveSelection`), highest precedence first:
 * 1. the URL — `/tickets?queue=<key>` (`?queue=deleted` = recycle bin) or `/tickets?view=<id>`;
 * 2. on a fresh load (first time the workspace opens for this user in this tab), the user's default saved view —
 *    their own personal default, else the shared default ("Open this view by default" in the view editor);
 * 3. the last queue/view this user picked on this browser (localStorage);
 * 4. "My open tickets".
 * Within a session, returning to `/tickets` without query params keeps the current selection.
 */
@Injectable({ providedIn: 'root' })
export class TicketWorkbenchStore {
  private readonly workbench = inject(WorkbenchApi);
  private readonly viewsApi = inject(ViewsApi);
  private readonly me = inject(MeStore);
  private readonly realtime = inject(RealtimeService);

  readonly counts = signal<QueueCountsDto>({});
  readonly countsLoaded = signal(false);
  readonly views = signal<SavedViewDto[]>([]);
  readonly viewsLoading = signal(false);
  readonly viewsError = signal<string | null>(null);
  readonly selection = signal<QueueSelection>({ kind: 'queue', key: 'mine' }, { equal: sameSelection });
  /** False until the initial selection is settled (URL / default view / last); the list waits for it. */
  readonly selectionReady = signal(false);
  readonly tabs = signal<OpenTicketTab[]>([]);
  readonly activeTicketId = signal<Guid | null>(null);
  /** Detail of the ticket open in the main panel (set by the ticket view; feeds the context panel and palette). */
  readonly activeTicket = signal<TicketDetailDto | null>(null);
  /** Ticket ids in the order the list currently shows them (j / k navigation). */
  readonly listIds = signal<Guid[]>([]);
  /** Commands for the open ticket (shortcuts, palette). */
  readonly commands = new Subject<TicketCommand>();
  /** Ask the list to reload (after bulk updates, ticket creation, view edits). */
  readonly listReload = new Subject<void>();

  readonly myViews = computed(() => this.views().filter((v) => v.isMine));
  readonly sharedViews = computed(() => this.views().filter((v) => !v.isMine));
  readonly selectedView = computed(() => {
    const s = this.selection();
    return s.kind === 'view' ? (this.views().find((v) => v.id === s.id) ?? null) : null;
  });
  readonly selectionLabel = computed(() => {
    const s = this.selection();
    return s.kind === 'queue' ? queueDef(s.key).label : s.kind === 'view' ? (this.selectedView()?.name ?? 'Saved view') : 'Deleted tickets';
  });
  /** The view that opens on a fresh load: the user's personal default, else the shared default. */
  readonly defaultView = computed(() => this.myViews().find((v) => v.isDefault) ?? this.sharedViews().find((v) => v.isDefault) ?? null);

  private userId: Guid | null = null;
  private countsInFlight = false;
  /** The first selection of this user's session has been settled (URL or default view). */
  private initialResolved = false;
  /** Waiting for the saved views to arrive before applying the default view. */
  private pendingDefault = false;
  private viewsLoadedOnce = false;

  constructor() {
    // Initialise for the current user right away (init() may be called before the effect below first runs)…
    this.onUser(this.me.user()?.id ?? null);
    // …and switch state when the signed-in user changes (sign-out / different agent on the same browser).
    effect(() => {
      const uid = this.me.user()?.id ?? null;
      untracked(() => this.onUser(uid));
    });
    merge(this.realtime.ticketChanged$, this.realtime.ticketThread$)
      .pipe(debounceTime(1500))
      .subscribe(() => this.refreshCounts());
    // Saved-view counts are more expensive: refresh them at most every 20 s while tickets change.
    this.realtime.ticketChanged$.pipe(debounceTime(20_000)).subscribe(() => this.loadViews(true));
    this.realtime.reconnected$.subscribe(() => {
      this.refreshCounts();
      this.loadViews(true);
    });
  }

  /** Called by the workspace when it opens: loads counts and views on first use. */
  init(): void {
    if (!this.countsLoaded()) this.refreshCounts();
    if (this.views().length === 0 && !this.viewsLoading()) this.loadViews();
  }

  refreshCounts(): void {
    if (!this.userId || this.countsInFlight) return;
    this.countsInFlight = true;
    this.workbench.queueCounts().subscribe({
      next: (c) => {
        this.counts.set(c);
        this.countsLoaded.set(true);
        this.countsInFlight = false;
      },
      error: () => {
        // Counts are decorative; the list itself reports errors. Keep the last known numbers.
        this.countsInFlight = false;
      },
    });
  }

  loadViews(quiet = false): void {
    if (!this.userId) return;
    if (!quiet) this.viewsLoading.set(true);
    const uid = this.userId;
    this.viewsApi.list('tickets', true).subscribe({
      next: (views) => {
        if (uid !== this.userId) return;
        this.views.set(views);
        this.viewsLoading.set(false);
        this.viewsError.set(null);
        this.viewsLoadedOnce = true;
        if (this.pendingDefault) {
          this.applyDefaultView();
          return;
        }
        // A selected view that was deleted elsewhere falls back to "My open".
        const s = this.selection();
        if (s.kind === 'view' && !views.some((v) => v.id === s.id)) this.select({ kind: 'queue', key: 'mine' });
      },
      error: (err: unknown) => {
        if (uid !== this.userId) return;
        this.viewsLoading.set(false);
        this.viewsLoadedOnce = true;
        if (!quiet) this.viewsError.set(ApiError.from(err).userMessage);
        // Without views the default view cannot apply: keep the last selection.
        if (this.pendingDefault) {
          this.pendingDefault = false;
          this.selectionReady.set(true);
        }
      },
    });
  }

  /**
   * Settles which list the workspace shows (see the class comment for the precedence). Called by the workspace with the
   * selection named in the URL (or null) whenever its query params change.
   */
  resolveSelection(fromUrl: QueueSelection | null): void {
    if (fromUrl) {
      this.initialResolved = true;
      this.pendingDefault = false;
      this.select(fromUrl);
      this.selectionReady.set(true);
      return;
    }
    if (this.initialResolved) {
      if (!this.pendingDefault) this.selectionReady.set(true);
      return;
    }
    this.initialResolved = true;
    this.pendingDefault = true;
    if (this.viewsLoadedOnce) this.applyDefaultView();
    else if (!this.viewsLoading()) this.loadViews();
  }

  private applyDefaultView(): void {
    this.pendingDefault = false;
    const view = this.defaultView();
    if (view) this.select({ kind: 'view', id: view.id });
    else {
      // A remembered view that no longer exists falls back to "My open".
      const s = this.selection();
      if (s.kind === 'view' && !this.views().some((v) => v.id === s.id)) this.select({ kind: 'queue', key: 'mine' });
    }
    this.selectionReady.set(true);
  }

  upsertView(view: SavedViewDto): void {
    this.views.update((list) => {
      const i = list.findIndex((v) => v.id === view.id);
      if (i < 0) return [...list, view];
      const copy = list.slice();
      copy[i] = { ...view, count: view.count ?? list[i].count };
      return copy;
    });
  }

  removeView(id: Guid): void {
    this.views.update((list) => list.filter((v) => v.id !== id));
    const s = this.selection();
    if (s.kind === 'view' && s.id === id) this.select({ kind: 'queue', key: 'mine' });
  }

  select(selection: QueueSelection): void {
    this.selection.set(selection);
    // The recycle bin is never remembered: a fresh load opens a working queue.
    if (this.userId && selection.kind !== 'trash') safeStorage.setJson(selectionKey(this.userId), selection);
  }

  /** Adds (or refreshes) a tab; keeps at most MAX_TABS, dropping the oldest. */
  openTab(tab: OpenTicketTab): void {
    this.tabs.update((tabs) => {
      const i = tabs.findIndex((t) => t.id === tab.id);
      if (i >= 0) {
        if (tabs[i].subject === tab.subject && tabs[i].displayNumber === tab.displayNumber) return tabs;
        const copy = tabs.slice();
        copy[i] = tab;
        return copy;
      }
      const next = [...tabs, tab];
      return next.length > MAX_TABS ? next.slice(next.length - MAX_TABS) : next;
    });
    this.persistTabs();
  }

  /** Closes a tab and returns the id of the tab to show instead (null → back to the list). */
  closeTab(id: Guid): Guid | null {
    const tabs = this.tabs();
    const i = tabs.findIndex((t) => t.id === id);
    if (i < 0) return null;
    const next = tabs.filter((t) => t.id !== id);
    this.tabs.set(next);
    this.persistTabs();
    if (this.activeTicketId() !== id) return this.activeTicketId();
    return next[i]?.id ?? next[i - 1]?.id ?? null;
  }

  closeAllTabs(): void {
    this.tabs.set([]);
    this.persistTabs();
  }

  /** Neighbour of the active ticket in the current list (j = +1, k = -1). */
  neighbour(delta: 1 | -1): Guid | null {
    const ids = this.listIds();
    const current = this.activeTicketId();
    if (ids.length === 0) return null;
    const i = current ? ids.indexOf(current) : -1;
    if (i < 0) return delta === 1 ? ids[0] : null;
    return ids[i + delta] ?? null;
  }

  private onUser(uid: Guid | null): void {
    if (uid === this.userId) return;
    this.userId = uid;
    this.counts.set({});
    this.countsLoaded.set(false);
    this.views.set([]);
    this.viewsLoading.set(false);
    this.viewsLoadedOnce = false;
    this.initialResolved = false;
    this.pendingDefault = false;
    this.selectionReady.set(false);
    this.activeTicket.set(null);
    this.activeTicketId.set(null);
    if (!uid) {
      this.tabs.set([]);
      return;
    }
    const tabs = safeStorage.getJson<OpenTicketTab[]>(tabsKey(uid));
    this.tabs.set(Array.isArray(tabs) ? tabs.filter((t) => t && typeof t.id === 'string').slice(-MAX_TABS) : []);
    const saved = safeStorage.getJson<QueueSelection>(selectionKey(uid));
    const valid =
      saved &&
      ((saved.kind === 'queue' && STANDARD_QUEUES.some((q) => q.key === saved.key)) || (saved.kind === 'view' && typeof saved.id === 'string'));
    this.selection.set(valid ? saved : { kind: 'queue', key: 'mine' });
  }

  private persistTabs(): void {
    if (this.userId) safeStorage.setJson(tabsKey(this.userId), this.tabs());
  }
}
