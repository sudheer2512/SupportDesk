import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, signal, viewChild } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  ActionSheetController,
  IonBackButton,
  IonBadge,
  IonButton,
  IonButtons,
  IonChip,
  IonContent,
  IonFooter,
  IonHeader,
  IonIcon,
  IonItem,
  IonLabel,
  IonList,
  IonListHeader,
  IonNote,
  IonRefresher,
  IonRefresherContent,
  IonSegment,
  IonSegmentButton,
  IonTitle,
  IonToolbar,
} from '@ionic/angular/standalone';
import {
  ApiError,
  AttachmentDto,
  ClockService,
  FilesApi,
  MeStore,
  MetaApi,
  Priority,
  RealtimeService,
  TicketContextDto,
  TicketDetailDto,
  TicketsApi,
  TimelineItemDto,
  formatDateTime,
  formatRelative,
} from '@sd/core';
import { Subscription, debounceTime, firstValueFrom, merge } from 'rxjs';
import { cachedResource } from '../core/cached-resource';
import { OutboxEntry, OutboxService } from '../core/outbox.service';
import { SessionCoordinator } from '../core/session-coordinator';
import { SessionSnapshotStore } from '../core/session-snapshot';
import { MobileToasts } from '../core/ui-services';
import { OfflineBanner } from '../shared/offline-banner.component';
import { OutboxItem } from '../shared/outbox-item.component';
import { SlaChip } from '../shared/sla-chip.component';
import { StateView } from '../shared/state-view.component';
import { fileSize, initials, priorityColor, statusColor } from '../shared/format';
import { AgentComposer } from './agent-composer.component';

type Tab = 'conversation' | 'customer';

/**
 * Agent ticket screen: header with live SLA countdowns and quick edits (status, priority, assignee), the conversation
 * timeline, unsent outbox entries, the reply / note composer and a customer card. Joins the ticket's SignalR group for
 * live updates and presence; cached for offline reading.
 */
@Component({
  selector: 'm-agent-ticket',
  imports: [
    RouterLink,
    IonHeader,
    IonToolbar,
    IonTitle,
    IonButtons,
    IonBackButton,
    IonButton,
    IonIcon,
    IonContent,
    IonFooter,
    IonRefresher,
    IonRefresherContent,
    IonSegment,
    IonSegmentButton,
    IonLabel,
    IonBadge,
    IonChip,
    IonList,
    IonListHeader,
    IonItem,
    IonNote,
    AgentComposer,
    OfflineBanner,
    OutboxItem,
    SlaChip,
    StateView,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ion-header>
      <ion-toolbar>
        <ion-buttons slot="start"><ion-back-button defaultHref="/agent/tickets" text="" aria-label="Back to tickets" /></ion-buttons>
        <ion-title>{{ t()?.displayNumber ?? 'Ticket' }}</ion-title>
        <ion-buttons slot="end">
          <ion-button aria-label="Refresh" (click)="loadAll()"><ion-icon slot="icon-only" name="refresh-outline" aria-hidden="true" /></ion-button>
        </ion-buttons>
      </ion-toolbar>
      <m-offline-banner [staleSince]="detail.staleSince()" />
    </ion-header>

    <ion-content>
      <ion-refresher slot="fixed" (ionRefresh)="refresh($event)"><ion-refresher-content /></ion-refresher>

      @if (t(); as t) {
        <section class="head">
          <h1>{{ t.subject }}</h1>
          <div class="quick">
            <ion-chip [disabled]="readOnly() || !t.can.edit" (click)="pickStatus(t)" [attr.aria-label]="'Status ' + t.status + '. Change'">
              <span class="swatch" [style.background]="t.statusColor" aria-hidden="true"></span>
              <ion-label>{{ t.status }}</ion-label>
              <ion-icon name="chevron-down" aria-hidden="true" />
            </ion-chip>
            <ion-chip [color]="priorityColor(t.priority)" [disabled]="readOnly() || !t.can.edit" (click)="pickPriority(t)" [attr.aria-label]="'Priority ' + t.priority + '. Change'">
              <ion-label>{{ t.priority }}</ion-label>
              <ion-icon name="chevron-down" aria-hidden="true" />
            </ion-chip>
            <ion-chip [disabled]="readOnly() || !(t.can.assign || t.can.edit)" (click)="pickAssignee(t)" [attr.aria-label]="'Assignee ' + (t.assigneeName || 'nobody') + '. Change'">
              <ion-icon name="person-circle-outline" aria-hidden="true" />
              <ion-label>{{ t.assigneeName || 'Unassigned' }}</ion-label>
              <ion-icon name="chevron-down" aria-hidden="true" />
            </ion-chip>
          </div>
          <div class="sla">
            @if (!t.firstResponseAt && t.firstResponseDueAt) {
              <m-sla-chip label="First response" [dueAt]="t.firstResponseDueAt" [state]="t.slaState" />
            }
            @if (t.firstResponseAt && t.nextResponseDueAt) {
              <m-sla-chip label="Next response" [dueAt]="t.nextResponseDueAt" [state]="t.slaState" />
            }
            @if (t.dueAt) {
              <m-sla-chip label="Resolve" [dueAt]="t.dueAt" [state]="t.slaState" />
            }
            @if (t.isEscalated) {
              <ion-badge color="danger">Escalated L{{ t.escalationLevel }}</ion-badge>
            }
          </div>
          <p class="meta">
            {{ t.departmentName }}{{ t.teamName ? ' · ' + t.teamName : '' }} · {{ t.channel }} · opened {{ rel(t.createdAt) }}
          </p>
          @if (viewers().length) {
            <p class="viewers" aria-live="polite">
              <ion-icon name="eye-outline" aria-hidden="true" />
              @for (v of viewers(); track v.userId) {
                <span>{{ v.name }} {{ v.activity === 'replying' ? 'is replying' : v.activity === 'noting' ? 'is writing a note' : 'is viewing' }}</span>
              }
            </p>
          }
        </section>

        <ion-segment [value]="tab()" (ionChange)="tab.set($any($event).detail.value)" aria-label="Ticket sections">
          <ion-segment-button value="conversation"><ion-label>Conversation</ion-label></ion-segment-button>
          <ion-segment-button value="customer"><ion-label>Customer</ion-label></ion-segment-button>
        </ion-segment>

        @if (tab() === 'conversation') {
          @if (timeline.value(); as items) {
            <ol class="timeline" aria-label="Conversation">
              @for (item of items; track itemKey(item)) {
                @if (item.thread; as th) {
                  <li class="msg" [attr.data-kind]="th.visibility === 'Internal' ? 'note' : th.authorType === 'Customer' ? 'customer' : 'agent'">
                    <div class="who">
                      <span class="avatar" aria-hidden="true">{{ initials(th.authorName) }}</span>
                      <strong>{{ th.authorName || th.fromAddress || 'Unknown' }}</strong>
                      @if (th.visibility === 'Internal') {
                        <ion-badge color="warning">{{ th.kind === 'HandoverNote' ? 'Handover' : 'Internal' }}</ion-badge>
                      }
                      <time [attr.datetime]="th.createdAt" [title]="dateTime(th.createdAt)">{{ rel(th.createdAt) }}</time>
                    </div>
                    <div class="body" [innerHTML]="th.bodyHtml || th.bodyText || ''"></div>
                    @if (th.attachments.length) {
                      <ul class="atts">
                        @for (a of th.attachments; track a.id) {
                          <li>
                            <ion-button fill="clear" size="small" [disabled]="readOnly()" (click)="download(a)">
                              <ion-icon slot="start" name="attach-outline" aria-hidden="true" />{{ a.fileName }} ({{ size(a.sizeBytes) }})
                            </ion-button>
                          </li>
                        }
                      </ul>
                    }
                  </li>
                } @else if (item.event; as ev) {
                  <li class="event">
                    <ion-icon name="ellipse" aria-hidden="true" />
                    <span>{{ ev.actorName || 'System' }} — {{ ev.summary || ev.type }}</span>
                    <time [attr.datetime]="ev.occurredAt">{{ rel(ev.occurredAt) }}</time>
                  </li>
                }
              } @empty {
                <m-state kind="empty" title="No messages yet" message="Start the conversation below." />
              }
            </ol>
          } @else if (timeline.loading()) {
            <m-state kind="loading" [lines]="4" title="Loading conversation" />
          } @else if (timeline.error()) {
            <m-state [kind]="readOnly() ? 'offline' : 'error'" [message]="readOnly() ? 'The conversation is not saved on this device.' : timeline.error()!.userMessage" actionLabel="Try again" (action)="timeline.load()" />
          }
          @for (e of pending(); track e.id) {
            <m-outbox-item [entry]="e" (edit)="editEntry($event)" />
          }
        } @else {
          @if (context.value(); as c) {
            <ion-list lines="full">
              @if (c.contact; as ct) {
                <ion-list-header><ion-label>Contact</ion-label></ion-list-header>
                <ion-item>
                  <ion-label>
                    <h2>{{ ct.fullName }} @if (ct.isVip) {<ion-badge color="warning">VIP</ion-badge>}</h2>
                    <p>{{ ct.jobTitle || '' }}{{ ct.locationName ? ' · ' + ct.locationName : '' }}</p>
                    <p>{{ ct.openTickets }} open ticket{{ ct.openTickets === 1 ? '' : 's' }} · prefers {{ ct.preferredChannel || 'e-mail' }}</p>
                  </ion-label>
                </ion-item>
                @if (ct.email) {
                  <ion-item [href]="'mailto:' + ct.email" detail="false">
                    <ion-icon slot="start" name="mail-outline" aria-hidden="true" /><ion-label>{{ ct.email }}</ion-label>
                  </ion-item>
                }
                @if (ct.phone) {
                  <ion-item [href]="'tel:' + ct.phone" detail="false">
                    <ion-icon slot="start" name="call-outline" aria-hidden="true" /><ion-label>{{ ct.phone }}</ion-label>
                  </ion-item>
                }
              }
              @if (c.customer; as cu) {
                <ion-list-header><ion-label>Customer</ion-label></ion-list-header>
                <ion-item>
                  <ion-label>
                    <h2>{{ cu.name }} <ion-badge color="medium">{{ cu.tier }}</ion-badge></h2>
                    <p>{{ cu.code || '' }}{{ cu.industry ? ' · ' + cu.industry : '' }}{{ cu.serviceRegion ? ' · ' + cu.serviceRegion : '' }}</p>
                    <p>Account manager: {{ cu.accountManagerName || '—' }}</p>
                  </ion-label>
                </ion-item>
                <div class="stats">
                  <div><strong>{{ cu.openTickets }}</strong><span>open</span></div>
                  <div><strong>{{ cu.ticketsLast90Days }}</strong><span>tickets 90d</span></div>
                  <div><strong>{{ cu.csatAverage !== null ? cu.csatAverage.toFixed(1) : '—' }}</strong><span>CSAT</span></div>
                  <div><strong>{{ cu.slaBreachesLast90Days }}</strong><span>breaches 90d</span></div>
                </div>
              }
              @if (!c.contact && !c.customer) {
                <m-state kind="empty" title="No customer linked" message="This ticket has no contact or customer yet." />
              }
              @if (c.contracts.length) {
                <ion-list-header><ion-label>Contracts</ion-label></ion-list-header>
                @for (k of c.contracts; track k.id) {
                  <ion-item>
                    <ion-label>
                      <h3>{{ k.name }}</h3>
                      <p>{{ k.number }} · {{ k.type }} · until {{ k.endDate }}</p>
                    </ion-label>
                    <ion-note slot="end" [color]="k.coversTicket ? 'success' : 'medium'">{{ k.coversTicket ? 'Covers' : k.status }}</ion-note>
                  </ion-item>
                }
              }
              @if (c.previousTickets.length) {
                <ion-list-header><ion-label>Previous tickets</ion-label></ion-list-header>
                @for (p of c.previousTickets; track p.id) {
                  <ion-item [routerLink]="['/agent/tickets', p.id]" [button]="true" detail="false">
                    <ion-label>
                      <h3>{{ p.displayNumber }} · {{ p.subject }}</h3>
                      <p>{{ p.status }} · {{ rel(p.lastActivityAt) }}</p>
                    </ion-label>
                  </ion-item>
                }
              }
            </ion-list>
            <ion-list lines="full">
              <ion-list-header><ion-label>Details</ion-label></ion-list-header>
              <ion-item><ion-label>Product</ion-label><ion-note slot="end">{{ t.productName || '—' }}</ion-note></ion-item>
              <ion-item><ion-label>Category</ion-label><ion-note slot="end">{{ t.categoryName || '—' }}</ion-note></ion-item>
              <ion-item><ion-label>Entitlement</ion-label><ion-note slot="end">{{ t.entitlementStatus }}</ion-note></ion-item>
              <ion-item><ion-label>Created</ion-label><ion-note slot="end">{{ dateTime(t.createdAt) }}</ion-note></ion-item>
            </ion-list>
          } @else if (context.loading()) {
            <m-state kind="loading" [lines]="3" title="Loading customer" />
          } @else if (context.error()) {
            <m-state [kind]="readOnly() ? 'offline' : 'error'" [message]="context.error()!.userMessage" actionLabel="Try again" (action)="context.load()" />
          }
        }
      } @else if (detail.loading()) {
        <m-state kind="loading" title="Loading ticket" />
      } @else if (detail.error(); as err) {
        <m-state [kind]="readOnly() ? 'offline' : 'error'" [title]="err.status === 404 ? 'Ticket not found' : ''"
          [message]="err.status === 404 ? 'It may have been deleted or moved out of your queues.' : readOnly() ? 'This ticket is not saved on this device yet.' : err.userMessage"
          actionLabel="Try again" (action)="loadAll()" />
      }
    </ion-content>

    @if (t(); as t) {
      @if ((t.can.reply || t.can.note) && tab() === 'conversation') {
        <ion-footer>
          <m-agent-composer [ticket]="t" [seenSeq]="seenSeq()" (sent)="onSent()" (activity)="setActivity($event)" />
        </ion-footer>
      }
    }
  `,
  styles: `
    .head {
      padding: 12px 16px 4px;
    }
    h1 {
      margin: 0 0 8px;
      font-size: 1.15rem;
      line-height: 1.3;
    }
    .quick,
    .sla {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      align-items: center;
    }
    .quick ion-chip {
      margin: 0;
      min-height: 36px;
    }
    .swatch {
      width: 10px;
      height: 10px;
      border-radius: 50%;
      margin-right: 6px;
    }
    .sla {
      margin-top: 8px;
    }
    .meta,
    .viewers {
      margin: 8px 0 4px;
      font-size: 0.8rem;
      color: var(--sd-text-2);
    }
    .viewers {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      align-items: center;
      color: var(--ion-color-primary);
    }
    ion-segment {
      margin: 8px 16px;
      width: auto;
    }
    .timeline {
      list-style: none;
      margin: 0;
      padding: 4px 12px 12px;
    }
    .msg {
      margin: 8px 0;
      padding: 10px 12px;
      border-radius: 12px;
      border: 1px solid var(--sd-border);
      background: var(--sd-surface);
    }
    .msg[data-kind='customer'] {
      background: var(--sd-surface-2);
    }
    .msg[data-kind='note'] {
      background: var(--sd-note-bg);
      border-color: var(--sd-note-border);
    }
    .who {
      display: flex;
      align-items: center;
      gap: 8px;
      font-size: 0.85rem;
    }
    .who time {
      margin-left: auto;
      color: var(--sd-text-2);
      font-size: 0.75rem;
    }
    .avatar {
      display: inline-grid;
      place-items: center;
      width: 28px;
      height: 28px;
      border-radius: 50%;
      font-size: 0.7rem;
      font-weight: 700;
      background: var(--sd-surface-3);
    }
    .body {
      margin-top: 6px;
      overflow-wrap: anywhere;
      line-height: 1.45;
    }
    .body :is(p, ul, ol, blockquote) {
      margin: 0 0 8px;
    }
    .body img {
      max-width: 100%;
      height: auto;
    }
    .atts {
      list-style: none;
      margin: 4px 0 0;
      padding: 0;
    }
    .event {
      display: flex;
      gap: 8px;
      align-items: center;
      margin: 4px 4px;
      font-size: 0.78rem;
      color: var(--sd-text-2);
    }
    .event ion-icon {
      font-size: 6px;
    }
    .event time {
      margin-left: auto;
      white-space: nowrap;
    }
    .stats {
      display: grid;
      grid-template-columns: repeat(4, 1fr);
      gap: 4px;
      padding: 8px 16px 12px;
      text-align: center;
    }
    .stats strong {
      display: block;
      font-size: 1.1rem;
    }
    .stats span {
      font-size: 0.7rem;
      color: var(--sd-text-2);
    }
  `,
})
export class AgentTicketDetailPage implements OnInit {
  /** Route param. */
  readonly id = input.required<string>();

  private readonly api = inject(TicketsApi);
  private readonly meta = inject(MetaApi);
  private readonly me = inject(MeStore);
  private readonly snapshot = inject(SessionSnapshotStore);
  private readonly realtime = inject(RealtimeService);
  private readonly files = inject(FilesApi);
  private readonly outbox = inject(OutboxService);
  private readonly session = inject(SessionCoordinator);
  private readonly clock = inject(ClockService);
  private readonly sheets = inject(ActionSheetController);
  private readonly toasts = inject(MobileToasts);
  private readonly destroyRef = inject(DestroyRef);
  private readonly composer = viewChild(AgentComposer);

  protected readonly tab = signal<Tab>('conversation');
  protected readonly readOnly = this.session.readOnly;
  protected readonly priorityColor = priorityColor;
  protected readonly statusColor = statusColor;
  protected readonly initials = initials;

  protected readonly detail = cachedResource<TicketDetailDto>({ key: () => `ticket:${this.id()}`, kind: 'ticket', fetch: () => this.api.get(this.id()) });
  protected readonly timeline = cachedResource<TimelineItemDto[]>({
    key: () => `timeline:${this.id()}`,
    kind: 'ticket',
    fetch: () => this.api.timeline(this.id()),
  });
  protected readonly context = cachedResource<TicketContextDto>({
    key: () => `context:${this.id()}`,
    kind: 'ticket',
    fetch: () => this.api.context(this.id()),
  });

  protected readonly t = this.detail.value;
  protected readonly pending = computed(() => this.outbox.entries().filter((e) => e.ticketId === this.id() && e.kind.startsWith('agent-')));
  protected readonly seenSeq = computed(() => Math.max(0, ...(this.timeline.value() ?? []).map((i) => i.thread?.seq ?? 0)));
  protected readonly viewers = computed(() => {
    const me = this.me.user()?.id ?? this.snapshot.userId();
    return (this.realtime.presence().get(this.id()) ?? []).filter((v) => v.userId !== me);
  });

  private live: Subscription | null = null;

  ngOnInit(): void {
    void this.loadAll();
    // Statuses, priorities and agents for the quick-edit pickers (cached for the session).
    if (this.session.live()) this.meta.load();
    this.realtime.joinTicket(this.id());
    const events = this.realtime.ticketEvents(this.id());
    this.live = merge(
      events.changed$.pipe(debounceTime(400)),
      this.realtime.reconnected$,
      this.session.refresh$,
    ).subscribe(() => void this.detail.load());
    const threads = merge(events.thread$, this.realtime.reconnected$, this.session.refresh$)
      .pipe(debounceTime(300))
      .subscribe(() => void this.timeline.load());
    const sent = this.outbox.sent$.subscribe((s) => {
      if (s.ticketId === this.id()) {
        void this.timeline.load();
        void this.detail.load();
      }
    });
    this.destroyRef.onDestroy(() => {
      this.live?.unsubscribe();
      threads.unsubscribe();
      sent.unsubscribe();
      this.realtime.leaveTicket(this.id());
    });
  }

  async loadAll(): Promise<void> {
    await Promise.all([this.detail.load(), this.timeline.load(), this.context.load()]);
  }

  protected async refresh(event: Event): Promise<void> {
    await this.loadAll();
    await (event.target as HTMLIonRefresherElement).complete();
  }

  protected onSent(): void {
    void this.timeline.load();
  }

  protected setActivity(activity: 'replying' | 'noting' | 'viewing'): void {
    this.realtime.setActivity(this.id(), activity);
  }

  protected editEntry(entry: OutboxEntry): void {
    void this.outbox.takeBack(entry.id).then((e) => {
      if (e) this.composer()?.restore(e);
    });
  }

  // ───── Quick edits ─────

  protected async pickStatus(t: TicketDetailDto): Promise<void> {
    const form = await this.metaForm();
    if (!form) return;
    const current = form.statuses.find((s) => s.id === t.statusId);
    const allowed = form.statuses
      .filter((s) => s.isActive && s.id !== t.statusId)
      .filter((s) => !current?.allowedNext?.length || current.allowedNext.includes(s.id))
      .sort((a, b) => a.sortOrder - b.sortOrder);
    await this.choose(
      'Change status',
      allowed.map((s) => ({ text: s.name, run: () => this.api.changeStatus(t.id, { statusId: s.id, rowVersion: t.rowVersion }) })),
      `Status: ${t.status}`,
    );
  }

  protected async pickPriority(t: TicketDetailDto): Promise<void> {
    const form = await this.metaForm();
    if (!form) return;
    await this.choose(
      'Change priority',
      form.priorities
        .filter((p) => p !== t.priority)
        .map((p) => ({ text: p, run: () => this.api.update(t.id, { rowVersion: t.rowVersion, priority: p as Priority }) })),
      `Priority: ${t.priority}`,
    );
  }

  protected async pickAssignee(t: TicketDetailDto): Promise<void> {
    const me = this.me.user()?.id ?? this.snapshot.userId();
    const options: { text: string; run: () => ReturnType<TicketsApi['assign']> }[] = [];
    if (me && t.assigneeId !== me) options.push({ text: 'Assign to me', run: () => this.api.assign(t.id, { assigneeId: me, teamId: null }) });
    if (t.can.assign) {
      const form = await this.metaForm();
      const agents = (form?.agents ?? [])
        .filter((a) => !a.isLightAgent && a.userId !== me && a.userId !== t.assigneeId && a.departmentIds.includes(t.departmentId))
        .sort((a, b) => a.name.localeCompare(b.name))
        .slice(0, 30);
      for (const a of agents)
        options.push({ text: `${a.name} · ${a.availability} · ${a.openTickets}/${a.capacity}`, run: () => this.api.assign(t.id, { assigneeId: a.userId, teamId: null }) });
      if (t.assigneeId) options.push({ text: 'Unassign', run: () => this.api.assign(t.id, { assigneeId: null, teamId: null }) });
    }
    await this.choose('Assign to', options, t.assigneeName ? `Currently ${t.assigneeName}` : 'Unassigned');
  }

  private async choose(header: string, options: { text: string; run: () => ReturnType<TicketsApi['get']> }[], subHeader: string): Promise<void> {
    if (!options.length) {
      void this.toasts.show('Nothing else to choose here.');
      return;
    }
    const sheet = await this.sheets.create({
      header,
      subHeader,
      buttons: [...options.map((o) => ({ text: o.text, handler: () => void this.apply(o.run) })), { text: 'Cancel', role: 'cancel' }],
    });
    await sheet.present();
  }

  private async apply(run: () => ReturnType<TicketsApi['get']>): Promise<void> {
    try {
      const updated = await firstValueFrom(run());
      this.detail.set(updated);
      void this.toasts.success('Ticket updated.');
      void this.timeline.load();
    } catch (e) {
      const err = ApiError.from(e);
      if (err.currentTicket) {
        this.detail.set(err.currentTicket);
        void this.toasts.show('Someone else changed this ticket — showing the latest version. Try again.', 'warning', 5000);
      } else {
        void this.toasts.error(err.fieldError('statusId') ?? err.userMessage);
      }
    }
  }

  private async metaForm() {
    try {
      return await firstValueFrom(this.meta.ticketForm$());
    } catch (e) {
      void this.toasts.error(ApiError.from(e).userMessage);
      return null;
    }
  }

  // ───── Helpers ─────

  protected download(a: AttachmentDto): void {
    this.files.save(a.id, a.fileName);
  }

  protected itemKey(item: TimelineItemDto): string {
    return item.thread?.id ?? item.event?.id ?? item.at;
  }

  protected rel(value: string): string {
    return formatRelative(value, this.clock.coarseNow());
  }

  protected dateTime(value: string): string {
    return formatDateTime(value);
  }

  protected size(bytes: number): string {
    return fileSize(bytes);
  }
}
