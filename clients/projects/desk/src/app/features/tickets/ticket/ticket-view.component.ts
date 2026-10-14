import { NgComponentOutlet, NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Type,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { Title } from '@angular/platform-browser';
import { Router } from '@angular/router';
import { ApiError, MeStore, RealtimeService, TicketsApi, safeStorage } from '@sd/core';
import { SdButton, SdDrawer, SdEmptyState, SdSkeleton, SdTab, SdTabs, ToastService } from '@sd/ui';
import { debounceTime, filter, merge } from 'rxjs';
import { TICKET_PANELS, TicketPanelDef } from '../extensions/ticket-panels';
import { TicketWorkbenchStore } from '../workspace/workbench.store';
import { ComposerComponent } from './composer.component';
import { FollowUpsPanelComponent, WatchersPanelComponent } from './panels/people-panel.component';
import { PropertiesPanelComponent } from './panels/properties-panel.component';
import { RelatedPanelComponent } from './panels/related-panel.component';
import { TasksPanelComponent } from './panels/tasks-panel.component';
import { TimePanelComponent } from './panels/time-panel.component';
import { TicketActions } from './ticket-actions.service';
import { TicketContext } from './ticket-context.service';
import { TicketHeaderComponent } from './ticket-header.component';
import { TicketTimelineComponent } from './ticket-timeline.component';

const SIDE_TAB_KEY = 'sd.tickets.sideTab';
const NARROW_PX = 1000;

/**
 * The ticket open in the workspace (route /tickets/:id): header with quick editors, the unified timeline,
 * the composer and the side tabs (details, tasks, time, people, follow-ups, related + contributed panels).
 * Joins the ticket's realtime group for presence and live updates; reacts to workspace shortcuts.
 */
@Component({
  selector: 'desk-ticket-view',
  imports: [
    NgTemplateOutlet,
    NgComponentOutlet,
    SdButton,
    SdDrawer,
    SdEmptyState,
    SdSkeleton,
    SdTabs,
    SdTab,
    TicketHeaderComponent,
    TicketTimelineComponent,
    ComposerComponent,
    PropertiesPanelComponent,
    TasksPanelComponent,
    TimePanelComponent,
    WatchersPanelComponent,
    FollowUpsPanelComponent,
    RelatedPanelComponent,
  ],
  providers: [TicketContext, TicketActions],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class.is-narrow]': 'narrow()' },
  template: `
    @if (ctx.error(); as e) {
      <div class="state">
        <sd-empty-state
          [icon]="e.status === 404 ? 'search' : 'circle-alert'"
          [title]="e.status === 404 ? 'Ticket not found' : 'Could not open the ticket'"
          [message]="e.status === 404 ? 'It may have been deleted or merged, or it is outside what you can see.' : e.userMessage"
        >
          <div class="state-actions">
            <button sdButton type="button" (click)="closeTab()">Close tab</button>
            <button sdButton variant="primary" type="button" (click)="retry()">Try again</button>
          </div>
        </sd-empty-state>
      </div>
    } @else if (!ctx.ticket()) {
      <div class="loading" aria-busy="true" aria-label="Loading ticket">
        <sd-skeleton height="28px" width="60%" />
        <sd-skeleton [lines]="2" />
        <br />
        <sd-skeleton height="120px" />
        <sd-skeleton height="120px" />
      </div>
    } @else {
      <desk-ticket-header (openDetails)="detailsOpen.set(true)" />
      <div class="body">
        <div class="conversation">
          <div class="scroll" #scroller>
            @if (showDescription()) {
              <article class="description" aria-label="Description">
                <h2 class="sd-visually-hidden">Description</h2>
                <div class="sd-prose" [innerHTML]="ctx.ticket()!.descriptionHtml"></div>
              </article>
            }
            <desk-ticket-timeline
              [items]="ctx.timeline()"
              [loading]="ctx.timelineLoading()"
              [error]="ctx.timelineError()?.userMessage ?? null"
            />
          </div>
          <desk-composer [ticketId]="id()" />
        </div>
        @if (!narrow()) {
          <aside class="side" aria-label="Ticket details">
            <ng-container [ngTemplateOutlet]="sideTabs" />
          </aside>
        }
      </div>
      @if (narrow()) {
        <sd-drawer [(open)]="detailsOpen" title="Ticket details" [side]="phone() ? 'bottom' : 'right'" width="440px">
          @if (detailsOpen()) {
            <ng-container [ngTemplateOutlet]="sideTabs" />
          }
        </sd-drawer>
      }
    }

    <ng-template #sideTabs>
      <sd-tabs [selected]="sideTab()" (selectedChange)="selectSideTab($event)" ariaLabel="Ticket details">
        <sd-tab id="details" label="Details">
          <desk-properties-panel />
        </sd-tab>
        <sd-tab id="tasks" label="Tasks">
          <desk-tasks-panel [ticketId]="id()" />
        </sd-tab>
        <sd-tab id="time" label="Time" [badge]="timeBadge()">
          <desk-time-panel [ticketId]="id()" />
        </sd-tab>
        <sd-tab id="people" label="People" [badge]="ctx.ticket()?.watchers?.length || null">
          <desk-watchers-panel />
        </sd-tab>
        <sd-tab id="followups" label="Follow-ups">
          <desk-followups-panel [ticketId]="id()" />
        </sd-tab>
        <sd-tab id="related" label="Related" [badge]="relatedCount() || null">
          <desk-related-panel />
        </sd-tab>
        @for (p of extensionPanels(); track p.key) {
          <sd-tab [id]="p.key" [label]="p.label">
            @if (loadedPanels()[p.key]; as cmp) {
              <ng-container [ngComponentOutlet]="cmp" [ngComponentOutletInputs]="{ ticketId: id(), ticket: ctx.ticket() }" />
            } @else if (panelErrors()[p.key]) {
              <p class="error" role="alert">{{ panelErrors()[p.key] }}</p>
            } @else {
              <sd-skeleton [lines]="3" />
            }
          </sd-tab>
        }
      </sd-tabs>
    </ng-template>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 0;
      container-type: inline-size;
    }
    .loading,
    .state {
      padding: 24px;
      display: grid;
      gap: 10px;
    }
    .state-actions {
      display: flex;
      gap: 8px;
      justify-content: center;
    }
    .body {
      display: flex;
      flex: 1;
      min-height: 0;
    }
    .conversation {
      display: flex;
      flex-direction: column;
      flex: 1;
      min-width: 0;
      min-height: 0;
    }
    .scroll {
      flex: 1;
      min-height: 0;
      overflow-y: auto;
      padding: 0 16px 16px;
      overscroll-behavior: contain;
    }
    .description {
      margin: 12px 0 4px;
      padding: 12px 14px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    .side {
      width: 380px;
      flex: none;
      min-height: 0;
      overflow-y: auto;
      padding: 4px 14px 16px;
      border-left: 1px solid var(--sd-border);
      background: var(--sd-surface);
    }
    .error {
      color: var(--sd-danger);
    }
    @media (max-width: 767px) {
      /* Phones: one scrolling column (header → conversation), composer pinned to the bottom. */
      :host {
        display: block;
        overflow-y: auto;
      }
      .loading,
      .state {
        padding: 16px;
      }
      .body,
      .conversation {
        display: block;
      }
      .scroll {
        overflow: visible;
        padding: 0 10px 12px;
      }
      desk-composer {
        position: sticky;
        bottom: 0;
        z-index: 2;
      }
    }
  `,
})
export class TicketViewComponent {
  /** Route param (component input binding). */
  readonly id = input.required<string>();

  protected readonly ctx = inject(TicketContext);
  private readonly actions = inject(TicketActions);
  private readonly store = inject(TicketWorkbenchStore);
  private readonly realtime = inject(RealtimeService);
  private readonly me = inject(MeStore);
  private readonly router = inject(Router);
  private readonly title = inject(Title);
  private readonly toast = inject(ToastService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly scroller = viewChild<ElementRef<HTMLElement>>('scroller');
  private readonly composer = viewChild(ComposerComponent);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly narrow = signal(false);
  protected readonly phone = signal(false);
  protected readonly detailsOpen = signal(false);
  protected readonly sideTab = signal(safeStorage.get(SIDE_TAB_KEY) ?? 'details');
  protected readonly loadedPanels = signal<Record<string, Type<unknown>>>({});
  protected readonly panelErrors = signal<Record<string, string>>({});

  protected readonly extensionPanels = computed(() =>
    TICKET_PANELS.filter(
      (p) =>
        (!p.permission || this.me.canAny(typeof p.permission === 'string' ? [p.permission] : p.permission)) &&
        (!p.feature || this.me.hasFeature(p.feature)),
    ).sort((a, b) => (a.order ?? 100) - (b.order ?? 100)),
  );
  protected readonly relatedCount = computed(() => {
    const t = this.ctx.ticket();
    return t ? t.links.length + t.children.length + t.externalReferences.length + (t.parentTicketId ? 1 : 0) : 0;
  });
  protected readonly timeBadge = computed(() => {
    const m = this.ctx.ticket()?.timeSpentMinutes ?? 0;
    return m > 0 ? `${Math.floor(m / 60)}h${m % 60 ? ` ${m % 60}m` : ''}` : null;
  });
  /** Show the stored description only when the conversation does not already start with it. */
  protected readonly showDescription = computed(() => {
    const t = this.ctx.ticket();
    if (!t?.descriptionHtml) return false;
    return !this.ctx.timeline().some((i) => i.itemType === 'thread');
  });

  private lastTimelineLength = 0;
  private joined: string | null = null;
  private readonly ticketsApi = inject(TicketsApi);

  /** /tickets/<number> → /tickets/<id> without a history entry; a missing number shows the normal error state. */
  private openByNumber(number: string): void {
    // The workspace only shows the ticket pane while a ticket is active: without this a number that does not resolve
    // (deleted, or outside the agent's scope) left the queue list on screen with the "not found" state hidden.
    this.store.activeTicketId.set(this.id());
    this.ctx.ticket.set(null);
    this.ctx.error.set(null);
    this.ctx.loading.set(true);
    this.ticketsApi.getByNumber(number).subscribe({
      next: (t) => void this.router.navigate(['/tickets', t.id], { replaceUrl: true, queryParamsHandling: 'preserve' }),
      error: (err: unknown) => {
        this.ctx.loading.set(false);
        this.ctx.error.set(ApiError.from(err));
      },
    });
  }

  protected retry(): void {
    const number = /^#?(\d{1,18})$/.exec(this.id())?.[1];
    if (number) this.openByNumber(number);
    else this.ctx.load(this.id());
  }

  constructor() {
    // Load + join the realtime group whenever the routed ticket changes (the component is reused across ids).
    effect(() => {
      const id = this.id();
      // Shared links may carry the ticket number (/tickets/1011 or /tickets/%231011): resolve it to the id route.
      const number = /^#?(\d{1,18})$/.exec(id)?.[1];
      if (number) {
        untracked(() => this.openByNumber(number));
        return;
      }
      untracked(() => {
        if (this.joined && this.joined !== id) this.realtime.leaveTicket(this.joined);
        this.joined = id;
        this.lastTimelineLength = 0;
        this.store.activeTicketId.set(id);
        this.store.activeTicket.set(null);
        this.ctx.ticket.set(null);
        this.ctx.timeline.set([]);
        this.ctx.load(id);
        this.realtime.joinTicket(id);
        this.ctx.meta.load();
      });
    });

    effect(() => {
      const t = this.ctx.ticket();
      if (t) untracked(() => this.title.setTitle(`${t.displayNumber} · ${t.subject} · SupportDesk`));
    });

    // Keep the newest message in view: on first load, and when new entries arrive while near the bottom.
    effect(() => {
      const length = this.ctx.timeline().length;
      const scroller = this.scroller()?.nativeElement;
      const phone = this.phone();
      untracked(() => {
        // On phones the whole view scrolls; elsewhere only the conversation does.
        const el = phone ? this.host.nativeElement : scroller;
        if (!el || !scroller || length === 0) return;
        const wasEmpty = this.lastTimelineLength === 0;
        this.lastTimelineLength = length;
        const nearBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 160;
        const own = this.ctx.scrollToEnd();
        if (own) this.ctx.scrollToEnd.set(false);
        if (wasEmpty || nearBottom || own) setTimeout(() => el.scrollTo({ top: el.scrollHeight }), 0);
      });
    });

    // Lazy-load contributed panels when their tab is selected.
    effect(() => {
      const key = this.sideTab();
      untracked(() => this.loadPanel(this.extensionPanels().find((p) => p.key === key)));
    });

    const changed = this.realtime.ticketChanged$
      .pipe(
        filter((e) => e.ticketId === this.id()),
        debounceTime(600),
      )
      .subscribe(() => {
        this.ctx.refresh();
        this.ctx.loadTimeline();
      });
    const thread = this.realtime.ticketThread$
      .pipe(
        filter((e) => e.ticketId === this.id()),
        debounceTime(400),
      )
      .subscribe(() => this.ctx.loadTimeline());
    const sla = this.realtime.slaAlert$.pipe(filter((e) => e.ticketId === this.id())).subscribe((e) => {
      this.toast.show({
        kind: e.kind === 'breach' ? 'danger' : 'warning',
        title: e.kind === 'breach' ? `SLA breached on #${e.number}` : `SLA at risk on #${e.number}`,
        message: `${e.milestone} target`,
        key: `sla:${e.ticketId}:${e.milestone}:${e.kind}`,
      });
      this.ctx.refresh();
    });
    const reconnect = this.realtime.reconnected$.subscribe(() => {
      this.ctx.refresh();
      this.ctx.loadTimeline();
    });
    const commands = merge(this.store.commands).subscribe((c) => {
      switch (c.type) {
        case 'reply':
          this.composer()?.open('reply');
          break;
        case 'note':
          this.composer()?.open('note');
          break;
        case 'handover':
          this.composer()?.open('handover');
          break;
        case 'assignToMe':
          void this.ctx.assignToMe();
          break;
        case 'escalate':
          this.actions.escalate();
          break;
        case 'status':
          if (c.statusId) void this.ctx.changeStatus(c.statusId);
          else this.actions.status();
          break;
        case 'resolve':
          this.actions.resolve();
          break;
        case 'reload':
          this.ctx.refresh();
          this.ctx.loadTimeline();
          break;
      }
    });

    afterNextRender(() => {
      const observer = new ResizeObserver((entries) => {
        const width = entries[0]?.contentRect.width ?? this.host.nativeElement.clientWidth;
        this.narrow.set(width < NARROW_PX);
        this.phone.set(window.innerWidth < 768);
      });
      observer.observe(this.host.nativeElement);
      this.destroyRef.onDestroy(() => observer.disconnect());
    });

    this.destroyRef.onDestroy(() => {
      changed.unsubscribe();
      thread.unsubscribe();
      sla.unsubscribe();
      reconnect.unsubscribe();
      commands.unsubscribe();
      if (this.joined) this.realtime.leaveTicket(this.joined);
      if (this.store.activeTicketId() === this.joined) {
        this.store.activeTicketId.set(null);
        this.store.activeTicket.set(null);
      }
    });
  }

  protected selectSideTab(id: string | null): void {
    if (!id) return;
    this.sideTab.set(id);
    safeStorage.set(SIDE_TAB_KEY, id);
  }

  protected closeTab(): void {
    const next = this.store.closeTab(this.id());
    void this.router.navigate(next ? ['/tickets', next] : ['/tickets']);
  }

  private loadPanel(def: TicketPanelDef | undefined): void {
    if (!def || this.loadedPanels()[def.key]) return;
    def.loadComponent().then(
      (cmp) => this.loadedPanels.update((m) => ({ ...m, [def.key]: cmp })),
      () => this.panelErrors.update((m) => ({ ...m, [def.key]: `The “${def.label}” panel could not be loaded.` })),
    );
  }
}
