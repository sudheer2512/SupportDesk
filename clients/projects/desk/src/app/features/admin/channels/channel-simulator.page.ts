import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import {
  ApiError,
  CallEventType,
  ChannelAccountDto,
  ChannelsApi,
  InboundResultDto,
  OutboundMessageDto,
  SimulatedAttachment,
  SimulateStatusRequest,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCard,
  SdDateTimePipe,
  SdEmptyState,
  SdFormField,
  SdIcon,
  SdInput,
  SdPageHeader,
  SdSkeleton,
  ToastService,
} from '@sd/ui';
import { Observable } from 'rxjs';
import { deliveryTone } from '../../../shared/channel-ui';
import { TYPE_META } from './provider-fields';

interface LogEntry {
  at: Date;
  kind: string;
  summary: string;
  result: InboundResultDto | null;
  error: string | null;
}

/**
 * /admin/channels/simulator — drives the real inbound pipeline without real providers: inbound e-mail (composed as
 * MIME), WhatsApp/SMS/Messenger messages, call events (started → answered → ended, missed, voicemail) and delivery
 * receipts; lists the outbound messages of the account with their delivery status. For demos and testing.
 */
@Component({
  selector: 'desk-channel-simulator-page',
  imports: [FormsModule, RouterLink, SdPageHeader, SdCard, SdFormField, SdInput, SdButton, SdBadge, SdIcon, SdEmptyState, SdSkeleton, SdDateTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Channel simulator" subtitle="Inject inbound traffic through the same pipeline real providers use, and inspect what was sent." icon="play" [breadcrumbs]="crumbs" />
      <div class="desk-page__body">
        @if (!loaded()) {
          <sd-card><sd-skeleton [lines]="4" /></sd-card>
        } @else if (channels().length === 0) {
          <sd-empty-state icon="radio-tower" title="No active channels" message="Create a channel first (a Simulator WhatsApp/SMS channel needs no credentials).">
            <a sdButton variant="primary" routerLink="/admin/channels/new">New channel</a>
          </sd-empty-state>
        } @else {
          <div class="layout">
            <div class="col">
              <sd-card padding="lg">
                <sd-form-field label="Channel">
                  <select sdInput name="channel" [ngModel]="selectedId()" (ngModelChange)="selectChannel($event)">
                    @for (c of channels(); track c.id) {
                      <option [value]="c.id">{{ typeLabel(c.type) }} · {{ c.name }} ({{ c.address }})</option>
                    }
                  </select>
                </sd-form-field>

                @if (selected(); as c) {
                  @switch (c.type) {
                    @case ('Email') {
                      <form class="form" (ngSubmit)="sendEmail(c)">
                        <h2>Inbound e-mail to {{ c.address }}</h2>
                        <div class="two">
                          <sd-form-field label="From address" required>
                            <input sdInput type="email" name="from" [(ngModel)]="email.from" placeholder="customer@example.com" />
                          </sd-form-field>
                          <sd-form-field label="From name">
                            <input sdInput name="fromName" [(ngModel)]="email.fromName" />
                          </sd-form-field>
                        </div>
                        <sd-form-field label="Subject" required>
                          <input sdInput name="subject" [(ngModel)]="email.subject" placeholder="Invoice total is wrong" />
                        </sd-form-field>
                        <sd-form-field label="Body (plain text)">
                          <textarea sdInput rows="5" name="text" [(ngModel)]="email.text"></textarea>
                        </sd-form-field>
                        <div class="two">
                          <sd-form-field label="In-Reply-To" hint="A Message-ID from the conversation threads the mail into its ticket.">
                            <input sdInput name="inReplyTo" [(ngModel)]="email.inReplyTo" placeholder="abc@mail.example.com" />
                          </sd-form-field>
                          <sd-form-field label="Message-ID" hint="Reuse one to simulate a duplicate delivery.">
                            <input sdInput name="messageId" [(ngModel)]="email.messageId" placeholder="(generated)" />
                          </sd-form-field>
                        </div>
                        <sd-form-field label="Attachment" optional>
                          <input sdInput type="file" name="attachment" (change)="pickFile($event)" />
                        </sd-form-field>
                        <div class="actions"><button sdButton variant="primary" type="submit" icon="send" [loading]="busy()">Deliver e-mail</button></div>
                      </form>
                    }
                    @case ('Voice') {
                      <form class="form" (ngSubmit)="sendCall(c)">
                        <h2>Call event on {{ c.address }}</h2>
                        <div class="two">
                          <sd-form-field label="Event" required>
                            <select sdInput name="event" [(ngModel)]="call.event">
                              <option value="started">Started (ringing)</option>
                              <option value="answered">Answered</option>
                              <option value="ended">Ended</option>
                              <option value="missed">Missed</option>
                              <option value="voicemail">Voicemail</option>
                            </select>
                          </sd-form-field>
                          <sd-form-field label="Call id" hint="Keep it for the next event of the same call.">
                            <div class="inline">
                              <input sdInput name="callId" [(ngModel)]="call.callId" />
                              <button sdButton type="button" size="sm" variant="ghost" (click)="newCall()">New call</button>
                            </div>
                          </sd-form-field>
                          <sd-form-field label="Caller number" required>
                            <input sdInput name="callFrom" [(ngModel)]="call.from" placeholder="+91 98 1002 0002" />
                          </sd-form-field>
                          <sd-form-field label="Direction">
                            <select sdInput name="direction" [(ngModel)]="call.direction">
                              <option value="inbound">Inbound</option>
                              <option value="outbound">Outbound</option>
                            </select>
                          </sd-form-field>
                          <sd-form-field label="Agent e-mail" hint="Who answered.">
                            <input sdInput type="email" name="agentEmail" [(ngModel)]="call.agentEmail" />
                          </sd-form-field>
                          <sd-form-field label="Duration (seconds)">
                            <input sdInput type="number" name="duration" [(ngModel)]="call.durationSeconds" min="0" />
                          </sd-form-field>
                        </div>
                        <sd-form-field label="Recording URL">
                          <input sdInput type="url" name="recordingUrl" [(ngModel)]="call.recordingUrl" placeholder="https://…/recording.mp3" />
                        </sd-form-field>
                        <sd-form-field label="Notes">
                          <input sdInput name="notes" [(ngModel)]="call.notes" />
                        </sd-form-field>
                        <div class="actions"><button sdButton variant="primary" type="submit" icon="phone" [loading]="busy()">Send call event</button></div>
                      </form>
                    }
                    @case ('Chat') {
                      <p class="sd-secondary">
                        Live chat is simulated with the real widget: open the
                        <a [href]="'/widget/demo.html?widgetKey=' + c.address" target="_blank" rel="noopener">demo page</a>
                        and chat as a visitor, then answer from <a routerLink="/inbox/chat">Live chat</a>.
                      </p>
                    }
                    @default {
                      <form class="form" (ngSubmit)="sendMessage(c)">
                        <h2>Inbound {{ typeLabel(c.type) }} message to {{ c.address }}</h2>
                        <div class="two">
                          <sd-form-field [label]="c.type === 'Facebook' || c.type === 'Instagram' ? 'Sender id (PSID)' : 'Sender number'" required>
                            <input sdInput name="msgFrom" [(ngModel)]="message.from" [placeholder]="c.type === 'Facebook' || c.type === 'Instagram' ? '6543210987654321' : '+91 98 1001 0003'" />
                          </sd-form-field>
                          <sd-form-field label="Sender name">
                            <input sdInput name="msgFromName" [(ngModel)]="message.fromName" />
                          </sd-form-field>
                        </div>
                        <sd-form-field label="Text">
                          <textarea sdInput rows="3" name="msgText" [(ngModel)]="message.text"></textarea>
                        </sd-form-field>
                        <div class="two">
                          <sd-form-field label="Provider message id" hint="Reuse one to simulate a duplicate webhook.">
                            <input sdInput name="externalId" [(ngModel)]="message.externalId" placeholder="(generated)" />
                          </sd-form-field>
                          <sd-form-field label="Media" optional>
                            <input sdInput type="file" name="media" (change)="pickFile($event)" />
                          </sd-form-field>
                        </div>
                        <div class="actions"><button sdButton variant="primary" type="submit" icon="send" [loading]="busy()">Deliver message</button></div>
                      </form>
                    }
                  }
                }
              </sd-card>

              <sd-card title="Results" padding="md">
                @for (e of log(); track $index) {
                  <div class="log-entry">
                    <sd-badge size="sm" [tone]="e.error ? 'danger' : outcomeTone(e.result?.outcome)">{{ e.error ? 'error' : e.result?.outcome }}</sd-badge>
                    <span class="sd-small">{{ e.kind }} — {{ e.summary }}</span>
                    <span class="sd-xs sd-muted">{{ e.at | sdDateTime }}</span>
                    @if (e.error) {
                      <span class="sd-small err">{{ e.error }}</span>
                    } @else if (e.result; as r) {
                      <span class="links sd-small">
                        @if (r.ticketId) {
                          <a [routerLink]="['/tickets', r.ticketId]">Ticket{{ r.ticketNumber ? ' #' + r.ticketNumber : '' }}</a>
                        }
                        @if (r.conversationId) {
                          <a routerLink="/inbox" [queryParams]="{ c: r.conversationId }">Open in inbox</a>
                        }
                        @if (r.detail) {
                          <span class="sd-muted">{{ r.detail }}</span>
                        }
                      </span>
                    }
                  </div>
                } @empty {
                  <p class="sd-small sd-muted">Nothing sent yet.</p>
                }
              </sd-card>
            </div>

            <sd-card class="col" title="Outbound messages" subtitle="What SupportDesk sent through this channel (newest first)." padding="md">
              <div class="out-actions">
                <button sdButton size="sm" variant="ghost" icon="refresh" [loading]="loadingOutbound()" (click)="loadOutbound()">Refresh</button>
              </div>
              @for (m of outbound(); track m.id) {
                <article class="out">
                  <div class="out-head">
                    <sd-badge size="sm" [tone]="tone(m.status)">{{ m.status }}</sd-badge>
                    <span class="sd-small sd-truncate">to {{ m.to }}</span>
                    <span class="sd-xs sd-muted">{{ m.occurredAt | sdDateTime }}</span>
                  </div>
                  @if (m.subject) {
                    <strong class="sd-small">{{ m.subject }}</strong>
                  }
                  <p class="sd-small body">{{ m.bodyText }}</p>
                  @for (d of m.deliveries; track d.id) {
                    <div class="sd-xs sd-muted">
                      {{ d.attempts }} attempt(s){{ d.providerMessageId ? ' · ' + d.providerMessageId : '' }}
                      @if (d.error) {
                        <span class="err"> · {{ d.error }}</span>
                      }
                    </div>
                    @if (canReceipt(d.providerMessageId, d.status)) {
                      <div class="receipts">
                        <span class="sd-xs sd-muted">Simulate receipt:</span>
                        @for (s of receipts; track s) {
                          <button sdButton type="button" size="sm" variant="ghost" (click)="receipt(d.providerMessageId!, s)">{{ s }}</button>
                        }
                      </div>
                    }
                  }
                  <div class="links sd-xs">
                    @if (m.ticketId) {
                      <a [routerLink]="['/tickets', m.ticketId]"><sd-icon name="ticket" [size]="11" /> Ticket</a>
                    }
                  </div>
                </article>
              } @empty {
                <p class="sd-small sd-muted">No outbound messages yet. Reply to a ticket from this channel to see one here.</p>
              }
            </sd-card>
          </div>
        }
      </div>
    </div>
  `,
  styles: `
    .layout {
      display: grid;
      grid-template-columns: minmax(0, 1.2fr) minmax(0, 1fr);
      gap: var(--sd-space-5);
      align-items: start;
    }
    @media (max-width: 1100px) {
      .layout {
        grid-template-columns: 1fr;
      }
    }
    .col {
      display: flex;
      flex-direction: column;
      gap: var(--sd-space-4);
      min-width: 0;
    }
    h2 {
      margin: var(--sd-space-4) 0 var(--sd-space-2);
      font-size: var(--sd-text-md);
      font-weight: var(--sd-weight-semibold);
    }
    .form {
      display: flex;
      flex-direction: column;
      gap: var(--sd-space-3);
    }
    .two {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
      gap: var(--sd-space-3);
    }
    .inline {
      display: flex;
      gap: var(--sd-space-2);
    }
    .inline input {
      flex: 1;
    }
    .actions {
      display: flex;
      justify-content: flex-end;
    }
    .log-entry {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--sd-space-2);
      padding: var(--sd-space-2) 0;
      border-bottom: 1px solid var(--sd-divider);
    }
    .links {
      display: flex;
      flex-wrap: wrap;
      gap: var(--sd-space-3);
    }
    .err {
      color: var(--sd-danger);
    }
    .out-actions {
      display: flex;
      justify-content: flex-end;
      margin-top: calc(-1 * var(--sd-space-2));
    }
    .out {
      padding: var(--sd-space-2) 0;
      border-bottom: 1px solid var(--sd-divider);
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    .out-head {
      display: flex;
      align-items: center;
      gap: var(--sd-space-2);
      min-width: 0;
    }
    .body {
      margin: 0;
      white-space: pre-wrap;
      max-height: 6.5em;
      overflow: hidden;
      color: var(--sd-text-2);
    }
    .receipts {
      display: flex;
      align-items: center;
      gap: 4px;
      flex-wrap: wrap;
    }
  `,
})
export class ChannelSimulatorPage {
  private readonly api = inject(ChannelsApi);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  /** Preselected channel (`?channel=`). */
  readonly channel = input<string | undefined>();

  protected readonly crumbs = [
    { label: 'Administration', link: '/admin' },
    { label: 'Channels', link: '/admin/channels' },
    { label: 'Simulator' },
  ];
  protected readonly receipts: SimulateStatusRequest['status'][] = ['Delivered', 'Read', 'Failed'];
  protected readonly channels = signal<ChannelAccountDto[]>([]);
  protected readonly loaded = signal(false);
  protected readonly selectedId = signal<string | null>(null);
  protected readonly selected = computed(() => this.channels().find((c) => c.id === this.selectedId()) ?? null);
  protected readonly busy = signal(false);
  protected readonly log = signal<LogEntry[]>([]);
  protected readonly outbound = signal<OutboundMessageDto[]>([]);
  protected readonly loadingOutbound = signal(false);
  protected readonly tone = deliveryTone;

  protected email = { from: 'customer@example.com', fromName: 'Demo Customer', subject: '', text: '', inReplyTo: '', messageId: '' };
  protected message = { from: '+91 98 1001 0003', fromName: 'Demo Customer', text: '', externalId: '' };
  protected call: { event: CallEventType; callId: string; from: string; direction: 'inbound' | 'outbound'; agentEmail: string; durationSeconds: number | null; recordingUrl: string; notes: string } = {
    event: 'started',
    callId: newCallId(),
    from: '+91 98 1002 0002',
    direction: 'inbound',
    agentEmail: '',
    durationSeconds: null,
    recordingUrl: '',
    notes: '',
  };
  private file: SimulatedAttachment | null = null;

  constructor() {
    this.api.list().subscribe({
      next: (list) => {
        this.channels.set(list.filter((c) => c.isActive));
        this.loaded.set(true);
        const wanted = untracked(() => this.channel());
        this.selectChannel(this.channels().find((c) => c.id === wanted)?.id ?? this.channels()[0]?.id ?? null, false);
      },
      error: () => this.loaded.set(true),
    });
    effect(() => {
      const wanted = this.channel();
      untracked(() => {
        if (wanted && wanted !== this.selectedId() && this.channels().some((c) => c.id === wanted)) this.selectChannel(wanted, false);
      });
    });
  }

  protected typeLabel(t: string): string {
    return TYPE_META[t]?.label ?? t;
  }

  protected selectChannel(id: string | null, navigate = true): void {
    this.selectedId.set(id);
    this.file = null;
    this.outbound.set([]);
    if (id) this.loadOutbound();
    if (navigate && id) void this.router.navigate([], { queryParams: { channel: id }, queryParamsHandling: 'merge', replaceUrl: true });
  }

  protected loadOutbound(): void {
    const id = this.selectedId();
    if (!id) return;
    this.loadingOutbound.set(true);
    this.api.outbound(id, 30).subscribe({
      next: (list) => {
        this.outbound.set(list);
        this.loadingOutbound.set(false);
      },
      error: () => this.loadingOutbound.set(false),
    });
  }

  protected pickFile(event: Event): void {
    const input = event.target as HTMLInputElement;
    const f = input.files?.[0];
    if (!f) {
      this.file = null;
      return;
    }
    if (f.size > 5 * 1024 * 1024) {
      this.toast.warning('File too large', 'Use a file under 5 MB for simulations.');
      input.value = '';
      return;
    }
    const reader = new FileReader();
    reader.onload = () => {
      const data = String(reader.result ?? '');
      this.file = { fileName: f.name, contentType: f.type || 'application/octet-stream', base64: data.slice(data.indexOf(',') + 1) };
    };
    reader.readAsDataURL(f);
  }

  protected newCall(): void {
    this.call.callId = newCallId();
    this.call.event = 'started';
  }

  protected sendEmail(c: ChannelAccountDto): void {
    if (!this.email.from.trim() || !this.email.subject.trim()) {
      this.toast.warning('From and subject are required');
      return;
    }
    this.run('E-mail', `${this.email.from} → ${c.address}: ${this.email.subject}`, this.api.simulateEmail(c.id, {
      from: this.email.from.trim(),
      fromName: this.email.fromName.trim() || null,
      subject: this.email.subject.trim(),
      text: this.email.text || ' ',
      inReplyTo: this.email.inReplyTo.trim() || null,
      references: this.email.inReplyTo.trim() ? [this.email.inReplyTo.trim()] : null,
      messageId: this.email.messageId.trim() || null,
      attachments: this.file ? [this.file] : null,
    }));
  }

  protected sendMessage(c: ChannelAccountDto): void {
    if (!this.message.from.trim() || (!this.message.text.trim() && !this.file)) {
      this.toast.warning('Sender and text (or media) are required');
      return;
    }
    this.run(this.typeLabel(c.type), `${this.message.from}: ${this.message.text || this.file?.fileName}`, this.api.simulateMessage(c.id, {
      from: this.message.from.trim(),
      fromName: this.message.fromName.trim() || null,
      text: this.message.text.trim() || null,
      externalId: this.message.externalId.trim() || null,
      media: this.file,
    }));
  }

  protected sendCall(c: ChannelAccountDto): void {
    if (!this.call.from.trim()) {
      this.toast.warning('The caller number is required');
      return;
    }
    this.run('Call', `${this.call.event} · ${this.call.from} (${this.call.callId})`, this.api.simulateCall(c.id, {
      event: this.call.event,
      callId: this.call.callId,
      from: this.call.from.trim(),
      direction: this.call.direction,
      agentEmail: this.call.agentEmail.trim() || null,
      durationSeconds: this.call.durationSeconds,
      recordingUrl: this.call.recordingUrl.trim() || null,
      notes: this.call.notes.trim() || null,
    }));
    // Natural next step of the same call.
    const next: Partial<Record<CallEventType, CallEventType>> = { started: 'answered', answered: 'ended' };
    this.call.event = next[this.call.event] ?? this.call.event;
  }

  protected canReceipt(providerId: string | null, status: string): boolean {
    return !!providerId && this.selected()?.type !== 'Email' && status !== 'Failed';
  }

  protected receipt(providerMessageId: string, status: SimulateStatusRequest['status']): void {
    const c = this.selected();
    if (!c) return;
    this.run('Receipt', `${status} for ${providerMessageId}`, this.api.simulateStatus(c.id, {
      providerMessageId,
      status,
      error: status === 'Failed' ? 'Simulated provider failure' : null,
    }));
  }

  protected outcomeTone(outcome: string | undefined): 'success' | 'info' | 'warning' | 'neutral' {
    switch (outcome) {
      case 'created':
        return 'success';
      case 'threaded':
      case 'status':
      case 'call':
        return 'info';
      case 'duplicate':
      case 'ignored':
      case 'rejected':
        return 'warning';
      default:
        return 'neutral';
    }
  }

  private run(kind: string, summary: string, request$: Observable<InboundResultDto>): void {
    this.busy.set(true);
    request$.subscribe({
      next: (result) => {
        this.busy.set(false);
        this.log.update((l) => [{ at: new Date(), kind, summary, result, error: null }, ...l].slice(0, 50));
        this.loadOutbound();
      },
      error: (e: unknown) => {
        this.busy.set(false);
        this.log.update((l) => [{ at: new Date(), kind, summary, result: null, error: ApiError.from(e).userMessage }, ...l].slice(0, 50));
      },
    });
  }
}

function newCallId(): string {
  return `sim-call-${Math.random().toString(36).slice(2, 10)}`;
}
