import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import {
  ApiError,
  PortalMessageDto,
  PortalRealtimeService,
  PortalTicketDetailDto,
  PortalTicketsApi,
  newClientRequestId,
} from '@sd/core';
import {
  SdAvatar,
  SdBadge,
  SdButton,
  SdDateTimePipe,
  SdFormField,
  SdIcon,
  SdInput,
  SdPriorityBadge,
  SdRelativeTimePipe,
  SdRichTextEditor,
  SdSkeleton,
  ToastService,
  isHtmlEmpty,
} from '@sd/ui';
import { Subscription, catchError, debounceTime, firstValueFrom, forkJoin, of } from 'rxjs';
import { BrandService } from '../../brand.service';
import { AttachmentListComponent } from '../../shared/attachment-list.component';
import { AttachmentUploaderComponent } from '../../shared/attachment-uploader.component';
import { BreadcrumbComponent } from '../../shared/breadcrumb.component';
import { ErrorStateComponent } from '../../shared/error-state.component';
import { errorMessage, htmlToText } from '../../shared/portal-utils';
import { ProseLinksDirective } from '../../shared/prose-links.directive';
import { StarRatingComponent } from '../../shared/star-rating.component';
import { StatusBadgeComponent } from '../../shared/status-badge.component';

/**
 * One ticket as the customer sees it: status and facts, custom fields, the original request, the public
 * conversation (agent / customer bubbles with attachments), reply box with attachments, "is your issue
 * resolved?" confirmation, reopen with a reason, CSAT rating, and live refresh over /hubs/portal.
 */
@Component({
  selector: 'portal-ticket-detail-page',
  imports: [
    RouterLink,
    SdIcon,
    SdBadge,
    SdButton,
    SdAvatar,
    SdSkeleton,
    SdPriorityBadge,
    SdFormField,
    SdInput,
    SdRichTextEditor,
    SdRelativeTimePipe,
    SdDateTimePipe,
    BreadcrumbComponent,
    ErrorStateComponent,
    StatusBadgeComponent,
    AttachmentListComponent,
    AttachmentUploaderComponent,
    StarRatingComponent,
    ProseLinksDirective,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="portal-container portal-page">
      <portal-breadcrumb [items]="[{ label: 'Home', link: brand.base() }, { label: 'My tickets', link: brand.base() + '/tickets' }, { label: ticket()?.displayNumber ?? 'Ticket' }]" />

      @if (loadError()) {
        <portal-error-state
          [error]="loadError()"
          notFoundTitle="Ticket not found"
          notFoundMessage="This ticket doesn't exist or isn't shared with your account. Check the link, or look for it in your ticket list."
          (retry)="reload()"
        >
          <a sdButton [routerLink]="brand.base() + '/tickets'">Go to my tickets</a>
        </portal-error-state>
      } @else if (ticket(); as t) {
        <header class="head">
          <div class="head-top">
            <span class="num">{{ t.displayNumber }}</span>
            <portal-status-badge [status]="t.status" [category]="t.statusCategory" />
            <sd-priority-badge [priority]="t.priority" />
            @if (!t.isMine && t.contactName) {
              <sd-badge size="sm" icon="user">Raised by {{ t.contactName }}</sd-badge>
            }
            @if (live()) {
              <span class="live" title="This page updates automatically"><span class="live-dot" aria-hidden="true"></span>Live</span>
            }
          </div>
          <h1>{{ t.subject }}</h1>
          <p class="sub">Opened {{ t.createdAt | sdDateTime }} · last update {{ t.lastActivityAt | sdRelativeTime }}</p>
        </header>

        <p class="sd-visually-hidden" aria-live="polite">{{ announcement() }}</p>

        <div class="layout">
          <div class="main">
            @if (t.awaitingConfirmation && t.actions.confirm) {
              <section class="banner banner--confirm" aria-labelledby="confirm-title">
                <span class="banner-ic" aria-hidden="true"><sd-icon name="circle-check" [size]="20" /></span>
                <div class="banner-body">
                  <h2 id="confirm-title">Is your issue resolved?</h2>
                  <p>Our team believes this request is resolved. Please let us know if that's right.</p>
                  @if (!notResolvedOpen()) {
                    <div class="row">
                      <button sdButton variant="primary" type="button" icon="thumbs-up" [loading]="confirming() === 'yes'" [disabled]="!!confirming()" (click)="confirm(t, true)">
                        Yes, it's resolved
                      </button>
                      <button sdButton type="button" icon="thumbs-down" [disabled]="!!confirming()" (click)="openNotResolved()">No, I still need help</button>
                    </div>
                  } @else {
                    <form class="inline-form" (submit)="$event.preventDefault(); confirm(t, false)" novalidate>
                      <sd-form-field label="What is still not working?" required [error]="confirmError()">
                        <textarea #notResolvedField sdInput rows="3" maxlength="4000" [value]="confirmComment()" (input)="confirmComment.set($any($event.target).value)"></textarea>
                      </sd-form-field>
                      <div class="row">
                        <button sdButton variant="primary" type="submit" [loading]="confirming() === 'no'">Send and keep it open</button>
                        <button sdButton variant="ghost" type="button" (click)="notResolvedOpen.set(false)">Cancel</button>
                      </div>
                    </form>
                  }
                </div>
              </section>
            }

            @if (t.actions.reopen && !t.awaitingConfirmation) {
              <section class="banner banner--reopen" aria-labelledby="reopen-title">
                <span class="banner-ic" aria-hidden="true"><sd-icon name="history" [size]="20" /></span>
                <div class="banner-body">
                  <h2 id="reopen-title">This ticket is {{ t.isOpen ? 'resolved' : 'closed' }}</h2>
                  <p>Still having the problem? You can reopen it@if (t.reopenUntil) { until <strong>{{ t.reopenUntil | sdDateTime }}</strong>}.</p>
                  @if (!reopenOpen()) {
                    <div class="row">
                      <button sdButton type="button" icon="history" (click)="openReopen()">Reopen ticket</button>
                    </div>
                  } @else {
                    <form class="inline-form" (submit)="$event.preventDefault(); reopen(t)" novalidate>
                      <sd-form-field label="Why are you reopening it?" required [error]="reopenError()">
                        <textarea #reopenField sdInput rows="3" maxlength="4000" [value]="reopenReason()" (input)="reopenReason.set($any($event.target).value)"></textarea>
                      </sd-form-field>
                      <div class="row">
                        <button sdButton variant="primary" type="submit" [loading]="reopening()">Reopen ticket</button>
                        <button sdButton variant="ghost" type="button" (click)="reopenOpen.set(false)">Cancel</button>
                      </div>
                    </form>
                  }
                </div>
              </section>
            } @else if (!t.isOpen && !t.actions.reply && !t.actions.reopen) {
              <p class="portal-callout closed-note" role="note">
                <sd-icon name="lock" [size]="16" />
                <span>This ticket is closed. Need more help? <a [routerLink]="brand.base() + '/submit'">Submit a new ticket</a>.</span>
              </p>
            }

            <section class="request" aria-labelledby="request-title">
              <h2 id="request-title" class="section-title">Original request</h2>
              @if (t.descriptionHtml) {
                <div class="sd-prose portal-prose portal-prose--compact" [innerHTML]="t.descriptionHtml" portalProseLinks></div>
              } @else {
                <p class="muted">No description was provided.</p>
              }
              @if (t.attachments.length) {
                <portal-attachments class="files" [attachments]="t.attachments" />
              }
            </section>

            <section class="conversation" aria-labelledby="conv-title">
              <h2 id="conv-title" class="section-title">Conversation</h2>
              @if (messages().length) {
                <ol class="thread">
                  @for (m of messages(); track m.id) {
                    @if (m.kind === 'system') {
                      <li class="system">
                        <span>{{ plain(m.bodyHtml) }}</span>
                        <time [attr.datetime]="m.createdAt">{{ m.createdAt | sdRelativeTime }}</time>
                      </li>
                    } @else {
                      <li
                        class="msg"
                        [id]="'msg-' + m.id"
                        tabindex="-1"
                        [class.is-mine]="m.isMine"
                        [class.is-agent]="m.kind === 'agent'"
                        [class.is-fresh]="fresh().has(m.id)"
                      >
                        <sd-avatar [name]="m.authorName || (m.kind === 'agent' ? 'Support' : 'Customer')" size="sm" class="avatar" />
                        <div class="bubble">
                          <p class="msg-head">
                            <span class="author">{{ m.isMine ? 'You' : m.authorName || (m.kind === 'agent' ? 'Support team' : 'Customer') }}</span>
                            @if (m.kind === 'agent') {
                              <sd-badge size="sm" tone="primary" icon="badge-check">Support</sd-badge>
                            }
                            @if (fresh().has(m.id)) {
                              <sd-badge size="sm" tone="accent">New</sd-badge>
                            }
                            <time [attr.datetime]="m.createdAt" [attr.title]="m.createdAt | sdDateTime">{{ m.createdAt | sdRelativeTime }}</time>
                          </p>
                          <div class="sd-prose portal-prose portal-prose--compact" [innerHTML]="m.bodyHtml" portalProseLinks></div>
                          @if (m.attachments.length) {
                            <portal-attachments class="files" [attachments]="m.attachments" />
                          }
                        </div>
                      </li>
                    }
                  }
                </ol>
              } @else {
                <p class="muted empty-thread"><sd-icon name="message-circle" [size]="16" />No replies yet — we'll notify you as soon as our team responds.</p>
              }
            </section>

            @if (t.actions.reply) {
              <section class="reply" aria-labelledby="reply-title">
                <h2 id="reply-title" class="section-title">Add a reply</h2>
                <form class="reply-form" (submit)="$event.preventDefault(); sendReply(t)" novalidate>
                  @defer (on viewport) {
                    <sd-rich-text-editor
                      [(value)]="replyHtml"
                      toolbar="compact"
                      minHeight="110px"
                      maxHeight="360px"
                      placeholder="Write your reply…"
                      ariaLabel="Your reply"
                      (submitted)="sendReply(t)"
                    />
                  } @placeholder {
                    <sd-skeleton height="150px" radius="var(--sd-radius-md)" />
                  }
                  <portal-attachment-uploader [maxMb]="maxMb()" [(fileIds)]="replyFiles" (busyChange)="uploading.set($event)" />
                  @if (replyError()) {
                    <p class="portal-callout portal-callout--danger" role="alert"><sd-icon name="circle-alert" [size]="15" />{{ replyError() }}</p>
                  }
                  <div class="reply-actions">
                    <span class="hint">Ctrl + Enter to send</span>
                    <button sdButton variant="primary" type="submit" icon="send" [loading]="sending()" [disabled]="uploading()">Send reply</button>
                  </div>
                </form>
              </section>
            }
          </div>

          <aside class="side" aria-label="Ticket details">
            @if (t.csatScore !== null) {
              <section class="card csat" aria-labelledby="csat-title">
                <h2 id="csat-title" class="card-title">Your rating</h2>
                <p class="stars"><span class="sd-visually-hidden">{{ t.csatScore }} out of 5 stars</span>
                  @for (n of [1, 2, 3, 4, 5]; track n) {
                    <sd-icon name="star" [size]="18" [class.on]="n <= (t.csatScore ?? 0)" />
                  }
                </p>
                @if (t.csatComment) {
                  <p class="muted">“{{ t.csatComment }}”</p>
                }
                <p class="muted small">Thank you for your feedback.</p>
              </section>
            } @else if (t.actions.rate) {
              <section class="card csat" aria-labelledby="rate-title">
                <h2 id="rate-title" class="card-title">How did we do?</h2>
                <form class="inline-form" (submit)="$event.preventDefault(); rate(t)" novalidate>
                  <portal-star-rating [(value)]="rating" label="Rate the support you received" [showLabel]="false" [size]="26" />
                  <sd-form-field label="Comment" optional>
                    <textarea sdInput rows="3" maxlength="2000" [value]="rateComment()" (input)="rateComment.set($any($event.target).value)"></textarea>
                  </sd-form-field>
                  @if (rateError()) {
                    <p class="portal-callout portal-callout--danger" role="alert">{{ rateError() }}</p>
                  }
                  <button sdButton variant="primary" type="submit" block [loading]="rateBusy()" [disabled]="!rating()">Submit rating</button>
                </form>
              </section>
            }

            <section class="card" aria-labelledby="facts-title">
              <h2 id="facts-title" class="card-title">Details</h2>
              <dl class="facts">
                <dt>Status</dt>
                <dd><portal-status-badge [status]="t.status" [category]="t.statusCategory" /></dd>
                <dt>Priority</dt>
                <dd>{{ t.priority }}</dd>
                @if (t.contactName) {
                  <dt>Raised by</dt>
                  <dd>{{ t.isMine ? 'You' : t.contactName }}</dd>
                }
                @if (t.customerName) {
                  <dt>Organisation</dt>
                  <dd>{{ t.customerName }}</dd>
                }
                @if (t.locationName) {
                  <dt>Location</dt>
                  <dd>{{ t.locationName }}</dd>
                }
                @if (t.departmentName) {
                  <dt>Department</dt>
                  <dd>{{ t.departmentName }}</dd>
                }
                @if (t.categoryName) {
                  <dt>Category</dt>
                  <dd>{{ t.categoryName }}</dd>
                }
                @if (t.productName) {
                  <dt>Product</dt>
                  <dd>{{ t.productName }}</dd>
                }
                @if (t.installationName) {
                  <dt>Installation</dt>
                  <dd>{{ t.installationName }}</dd>
                }
                <dt>Channel</dt>
                <dd>{{ t.channel }}</dd>
                <dt>Created</dt>
                <dd>{{ t.createdAt | sdDateTime }}</dd>
                @if (t.resolvedAt) {
                  <dt>Resolved</dt>
                  <dd>{{ t.resolvedAt | sdDateTime }}</dd>
                }
                @if (t.closedAt) {
                  <dt>Closed</dt>
                  <dd>{{ t.closedAt | sdDateTime }}</dd>
                }
              </dl>
            </section>

            @if (t.fields.length) {
              <section class="card" aria-labelledby="fields-title">
                <h2 id="fields-title" class="card-title">Additional information</h2>
                <dl class="facts">
                  @for (f of t.fields; track f.key) {
                    <dt>{{ f.label }}</dt>
                    <dd>{{ f.value || '—' }}</dd>
                  }
                </dl>
              </section>
            }
          </aside>
        </div>
      } @else {
        <div class="loading" aria-busy="true" aria-label="Loading ticket">
          <sd-skeleton width="180px" />
          <sd-skeleton height="34px" width="60%" />
          <div class="layout">
            <div class="main">
              <sd-skeleton height="120px" radius="var(--sd-radius-lg)" />
              <sd-skeleton height="90px" radius="var(--sd-radius-lg)" />
              <sd-skeleton height="90px" radius="var(--sd-radius-lg)" />
            </div>
            <sd-skeleton height="260px" radius="var(--sd-radius-lg)" />
          </div>
        </div>
      }
    </div>
  `,
  styles: `
    .head {
      display: grid;
      gap: 8px;
      margin: 12px 0 20px;
    }
    .head-top {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
    }
    .num {
      color: var(--sd-text-3);
      font-weight: var(--sd-weight-semibold);
      font-variant-numeric: tabular-nums;
    }
    .live {
      display: inline-flex;
      align-items: center;
      gap: 5px;
      margin-left: auto;
      color: var(--sd-success);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-medium);
    }
    .live-dot {
      width: 7px;
      height: 7px;
      border-radius: 50%;
      background: currentColor;
      animation: portal-pulse 2s var(--sd-ease-in-out) infinite;
    }
    @keyframes portal-pulse {
      50% {
        opacity: 0.35;
      }
    }
    h1 {
      font-size: clamp(1.4rem, 3.2vw, 1.9rem);
      font-weight: var(--sd-weight-bold);
      letter-spacing: -0.02em;
      overflow-wrap: anywhere;
    }
    .sub {
      margin: 0;
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
    .layout {
      display: grid;
      grid-template-columns: minmax(0, 1fr) 320px;
      gap: 24px;
      align-items: start;
    }
    .main {
      display: grid;
      gap: 20px;
      min-width: 0;
    }
    .section-title {
      margin-bottom: 10px;
      font-size: var(--sd-text-lg);
    }
    .banner {
      display: flex;
      gap: 14px;
      padding: 16px 18px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
    }
    .banner--confirm {
      border-color: var(--sd-success-border);
      background: var(--sd-success-bg);
    }
    .banner--confirm .banner-ic {
      color: var(--sd-success);
    }
    .banner--reopen {
      background: var(--sd-surface-2);
    }
    .banner--reopen .banner-ic {
      color: var(--sd-text-2);
    }
    .banner-ic {
      display: inline-flex;
      margin-top: 2px;
    }
    .banner-body {
      display: grid;
      gap: 8px;
      flex: 1;
      min-width: 0;
    }
    .banner h2 {
      font-size: var(--sd-text-lg);
    }
    .banner p {
      margin: 0;
      color: var(--sd-text-2);
    }
    .row {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
    }
    .inline-form {
      display: grid;
      gap: 10px;
    }
    .closed-note {
      margin: 0;
    }
    .request {
      padding: 16px 18px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    .files {
      display: block;
      margin-top: 12px;
    }
    .muted {
      margin: 0;
      color: var(--sd-text-2);
    }
    .small {
      font-size: var(--sd-text-xs);
    }
    .thread {
      display: grid;
      gap: 14px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .msg {
      display: flex;
      gap: 10px;
      max-width: 92%;
      outline: none;
      scroll-margin-top: 80px;
    }
    .msg.is-mine {
      flex-direction: row-reverse;
      margin-left: auto;
    }
    .avatar {
      flex: none;
      margin-top: 2px;
    }
    .bubble {
      min-width: 0;
      padding: 12px 14px;
      border: 1px solid var(--sd-border);
      border-radius: 4px var(--sd-radius-lg) var(--sd-radius-lg) var(--sd-radius-lg);
      background: var(--sd-surface);
      box-shadow: var(--sd-shadow-xs);
    }
    .is-agent .bubble {
      border-left: 3px solid var(--sd-primary);
    }
    .is-mine .bubble {
      border-color: var(--sd-primary-border);
      border-radius: var(--sd-radius-lg) 4px var(--sd-radius-lg) var(--sd-radius-lg);
      background: var(--sd-primary-soft);
    }
    .is-fresh .bubble {
      box-shadow: 0 0 0 2px color-mix(in oklab, var(--sd-accent) 45%, transparent);
    }
    .msg:focus-visible .bubble {
      box-shadow: var(--sd-focus-ring);
    }
    .msg-head {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
      margin: 0 0 6px;
      font-size: var(--sd-text-sm);
    }
    .msg-head time {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .author {
      font-weight: var(--sd-weight-semibold);
    }
    .system {
      display: flex;
      flex-wrap: wrap;
      justify-content: center;
      gap: 6px;
      padding: 4px 12px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      text-align: center;
    }
    .empty-thread {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 14px;
      border: 1px dashed var(--sd-border-strong);
      border-radius: var(--sd-radius-md);
    }
    .reply {
      padding: 16px 18px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    .reply-form {
      display: grid;
      gap: 10px;
    }
    .reply-actions {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
    }
    .hint {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .side {
      position: sticky;
      top: 76px;
      display: grid;
      gap: 14px;
    }
    .card {
      padding: 16px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    .card-title {
      margin-bottom: 10px;
      font-size: var(--sd-text-md);
    }
    .csat {
      border-color: color-mix(in oklab, var(--sd-warning) 30%, var(--sd-border));
    }
    .stars {
      display: flex;
      gap: 2px;
      margin: 0 0 8px;
      color: var(--sd-border-strong);
    }
    .stars .on {
      color: var(--sd-warning);
    }
    .stars .on ::ng-deep svg {
      fill: currentColor;
    }
    .facts {
      display: grid;
      grid-template-columns: max-content minmax(0, 1fr);
      gap: 8px 14px;
      margin: 0;
      font-size: var(--sd-text-sm);
    }
    dt {
      color: var(--sd-text-3);
    }
    dd {
      margin: 0;
      overflow-wrap: anywhere;
    }
    .loading {
      display: grid;
      gap: 14px;
      margin-top: 12px;
    }
    @media (max-width: 960px) {
      .layout {
        grid-template-columns: minmax(0, 1fr);
      }
      .side {
        position: static;
      }
    }
    @media (max-width: 560px) {
      .msg {
        max-width: 100%;
      }
      .avatar {
        display: none;
      }
    }
    @media (prefers-reduced-motion: reduce) {
      .live-dot {
        animation: none;
      }
    }
  `,
})
export class TicketDetailPage {
  readonly id = input.required<string>();

  protected readonly brand = inject(BrandService);
  private readonly api = inject(PortalTicketsApi);
  private readonly realtime = inject(PortalRealtimeService);
  private readonly toast = inject(ToastService);
  private readonly injector = inject(Injector);

  protected readonly ticket = signal<PortalTicketDetailDto | null>(null);
  protected readonly messages = signal<PortalMessageDto[]>([]);
  protected readonly loadError = signal<unknown>(null);
  protected readonly fresh = signal<ReadonlySet<string>>(new Set());
  protected readonly announcement = signal('');
  protected readonly live = this.realtime.connected;

  // Reply
  protected readonly replyHtml = signal('');
  protected readonly replyFiles = signal<string[]>([]);
  protected readonly uploading = signal(false);
  protected readonly sending = signal(false);
  protected readonly replyError = signal<string | null>(null);
  private replyRequestId = newClientRequestId();
  private readonly uploader = viewChild(AttachmentUploaderComponent);

  // Confirm resolution
  protected readonly notResolvedOpen = signal(false);
  protected readonly confirmComment = signal('');
  protected readonly confirming = signal<'yes' | 'no' | null>(null);
  protected readonly confirmError = signal<string | null>(null);
  private readonly notResolvedField = viewChild<ElementRef<HTMLTextAreaElement>>('notResolvedField');

  // Reopen
  protected readonly reopenOpen = signal(false);
  protected readonly reopenReason = signal('');
  protected readonly reopening = signal(false);
  protected readonly reopenError = signal<string | null>(null);
  private readonly reopenField = viewChild<ElementRef<HTMLTextAreaElement>>('reopenField');

  // CSAT
  protected readonly rating = signal<number | null>(null);
  protected readonly rateComment = signal('');
  protected readonly rateBusy = signal(false);
  protected readonly rateError = signal<string | null>(null);

  /** Upload limit from the ticket form (only fetched when replying is possible). */
  private readonly formRes = rxResource({
    params: () => {
      const slug = this.brand.slug();
      return slug && this.ticket()?.actions.reply ? slug : undefined;
    },
    stream: ({ params }) => this.api.form(params).pipe(catchError(() => of(null))),
  });
  protected readonly maxMb = computed(() => (this.formRes.hasValue() ? (this.formRes.value()?.maxAttachmentMb ?? 20) : 20));

  private loadSub: Subscription | null = null;

  constructor() {
    effect((onCleanup) => {
      const id = this.id();
      const slug = this.brand.slug();
      if (!slug) return;
      untracked(() => this.load(slug, id));
      const events = this.realtime
        .ticketEvents(id)
        .pipe(debounceTime(400))
        .subscribe(() => this.refresh(true));
      const reconnect = this.realtime.reconnected$.subscribe(() => this.refresh(false));
      onCleanup(() => {
        events.unsubscribe();
        reconnect.unsubscribe();
        this.loadSub?.unsubscribe();
      });
    });
    effect(() => {
      const t = this.ticket();
      if (t) untracked(() => this.brand.setPageTitle(`${t.displayNumber} · ${t.subject}`));
    });
  }

  protected plain(html: string): string {
    return htmlToText(html);
  }

  protected reload(): void {
    const slug = this.brand.slug();
    if (slug) this.load(slug, this.id());
  }

  protected openNotResolved(): void {
    this.notResolvedOpen.set(true);
    this.confirmError.set(null);
    afterNextRender(() => this.notResolvedField()?.nativeElement.focus(), { injector: this.injector });
  }

  protected openReopen(): void {
    this.reopenOpen.set(true);
    this.reopenError.set(null);
    afterNextRender(() => this.reopenField()?.nativeElement.focus(), { injector: this.injector });
  }

  protected async confirm(t: PortalTicketDetailDto, resolved: boolean): Promise<void> {
    const slug = this.brand.slug();
    if (!slug || this.confirming()) return;
    const comment = this.confirmComment().trim();
    if (!resolved && comment.length < 3) {
      this.confirmError.set('Tell us briefly what is still not working.');
      return;
    }
    this.confirming.set(resolved ? 'yes' : 'no');
    this.confirmError.set(null);
    try {
      this.ticket.set(await firstValueFrom(this.api.confirm(slug, t.id, resolved, resolved ? null : comment)));
      this.notResolvedOpen.set(false);
      this.confirmComment.set('');
      this.toast.success(resolved ? 'Thanks for confirming!' : 'Thanks — we are on it', resolved ? 'The ticket is now closed.' : 'Your ticket stays open and our team has been notified.');
      this.refresh(false);
    } catch (err) {
      this.confirmError.set(errorMessage(err, 'Your answer could not be sent. Please try again.'));
      if (resolved) this.toast.error('Could not confirm', errorMessage(err));
    } finally {
      this.confirming.set(null);
    }
  }

  protected async reopen(t: PortalTicketDetailDto): Promise<void> {
    const slug = this.brand.slug();
    const reason = this.reopenReason().trim();
    if (!slug || this.reopening()) return;
    if (reason.length < 3) {
      this.reopenError.set('Tell us briefly why you are reopening the ticket.');
      return;
    }
    this.reopening.set(true);
    this.reopenError.set(null);
    try {
      this.ticket.set(await firstValueFrom(this.api.reopen(slug, t.id, reason)));
      this.reopenOpen.set(false);
      this.reopenReason.set('');
      this.toast.success('Ticket reopened', 'Our team will pick it up again.');
      this.refresh(false);
    } catch (err) {
      const e = ApiError.from(err);
      this.reopenError.set(e.fieldError('reason') ?? errorMessage(err, 'The ticket could not be reopened.'));
    } finally {
      this.reopening.set(false);
    }
  }

  protected async rate(t: PortalTicketDetailDto): Promise<void> {
    const slug = this.brand.slug();
    const rating = this.rating();
    if (!slug || !rating || this.rateBusy()) return;
    this.rateBusy.set(true);
    this.rateError.set(null);
    try {
      this.ticket.set(await firstValueFrom(this.api.rate(slug, t.id, rating, this.rateComment().trim() || null)));
      this.announcement.set('Thank you for rating our support');
    } catch (err) {
      this.rateError.set(errorMessage(err, 'Your rating could not be saved.'));
    } finally {
      this.rateBusy.set(false);
    }
  }

  protected async sendReply(t: PortalTicketDetailDto): Promise<void> {
    const slug = this.brand.slug();
    if (!slug || this.sending()) return;
    if (this.uploading()) {
      this.replyError.set('Wait for the attachments to finish uploading.');
      return;
    }
    const body = this.replyHtml();
    const files = this.replyFiles();
    if (isHtmlEmpty(body) && !files.length) {
      this.replyError.set('Write a message or attach a file first.');
      return;
    }
    this.sending.set(true);
    this.replyError.set(null);
    try {
      const message = await firstValueFrom(
        this.api.reply(slug, t.id, {
          bodyHtml: isHtmlEmpty(body) ? '<p>(attachment)</p>' : body,
          attachmentFileIds: files.length ? files : null,
          clientRequestId: this.replyRequestId,
        }),
      );
      this.replyRequestId = newClientRequestId();
      this.messages.update((list) => (list.some((m) => m.id === message.id) ? list : [...list, message].sort((a, b) => a.seq - b.seq)));
      this.replyHtml.set('');
      this.replyFiles.set([]);
      this.uploader()?.clear();
      this.announcement.set('Your reply was sent');
      this.toast.success('Reply sent', 'We will let you know when our team responds.');
      afterNextRender(() => document.getElementById(`msg-${message.id}`)?.scrollIntoView({ block: 'nearest' }), { injector: this.injector });
      this.refresh(false);
    } catch (err) {
      const e = ApiError.from(err);
      this.replyError.set(e.fieldError('bodyHtml') ?? errorMessage(err, 'Your reply could not be sent. Please try again.'));
    } finally {
      this.sending.set(false);
    }
  }

  private load(slug: string, id: string): void {
    this.loadSub?.unsubscribe();
    this.ticket.set(null);
    this.messages.set([]);
    this.loadError.set(null);
    this.fresh.set(new Set());
    this.loadSub = forkJoin({ ticket: this.api.get(slug, id), messages: this.api.messages(slug, id) }).subscribe({
      next: ({ ticket, messages }) => {
        this.ticket.set(ticket);
        this.messages.set([...messages].sort((a, b) => a.seq - b.seq));
      },
      error: (err: unknown) => this.loadError.set(err ?? new Error('Load failed')),
    });
  }

  /** Re-fetches the ticket and thread in the background (realtime events, after actions). */
  private refresh(announce: boolean): void {
    const slug = this.brand.slug();
    const current = this.ticket();
    if (!slug || !current) return;
    forkJoin({ ticket: this.api.get(slug, current.id), messages: this.api.messages(slug, current.id) }).subscribe({
      next: ({ ticket, messages }) => {
        if (this.ticket()?.id !== ticket.id) return;
        const known = new Set(this.messages().map((m) => m.id));
        const added = messages.filter((m) => !known.has(m.id) && !m.isMine && m.kind !== 'system');
        this.ticket.set(ticket);
        this.messages.set([...messages].sort((a, b) => a.seq - b.seq));
        if (announce && added.length) {
          const last = added[added.length - 1];
          this.fresh.update((s) => new Set([...s, ...added.map((m) => m.id)]));
          this.announcement.set(`New reply from ${last.authorName || 'the support team'}`);
        }
      },
      error: () => undefined,
    });
  }
}
