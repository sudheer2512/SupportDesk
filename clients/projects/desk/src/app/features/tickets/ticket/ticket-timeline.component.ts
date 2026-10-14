import { ChangeDetectionStrategy, Component, computed, inject, input, signal, viewChild } from '@angular/core';
import { AttachmentDto, FilesApi, MeStore, ThreadDto, TicketEventDto, TimelineItemDto, safeStorage } from '@sd/core';
import { SdBadge, SdButton, SdFileSizePipe, SdIcon, SdSkeleton, SdTimeline, SdTimelineBodyDef, SdTimelineItem, SdToggle } from '@sd/ui';
import { channelIcon, channelLabel } from '../shared/ticket-ui';

const ACTIVITY_KEY = 'sd.tickets.showActivity';

const EVENT_ICONS: [RegExp, string][] = [
  [/status|resolv|reopen|clos/i, 'circle-dot'],
  [/assign|transfer|route/i, 'user-check'],
  [/priorit|severit/i, 'flag'],
  [/sla|breach|due/i, 'timer'],
  [/escalat/i, 'arrow-up-right'],
  [/merge/i, 'merge'],
  [/split|clone/i, 'split'],
  [/link/i, 'link'],
  [/tag/i, 'tag'],
  [/watch|collab/i, 'eye'],
  [/approv/i, 'shield-check'],
  [/time/i, 'clock'],
  [/task/i, 'list-checks'],
  [/entitle|contract/i, 'scale'],
  [/automat|rule|workflow|macro/i, 'workflow'],
  [/ai/i, 'sparkles'],
];

function eventIcon(type: string): string {
  return EVENT_ICONS.find(([re]) => re.test(type))?.[1] ?? 'activity';
}

function humanise(type: string): string {
  const t = type.replace(/^ticket\./, '').replace(/[._]/g, ' ');
  return t.charAt(0).toUpperCase() + t.slice(1);
}

function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/\n/g, '<br>');
}

/**
 * Unified ticket timeline: public replies, customer messages, internal/handover notes (amber, never shown to
 * customers) and activity events, with channel icons, delivery status, e-mail recipients and downloadable
 * attachments. Activity can be hidden; long older messages start collapsed.
 */
@Component({
  selector: 'desk-ticket-timeline',
  imports: [SdTimeline, SdTimelineBodyDef, SdIcon, SdButton, SdBadge, SdSkeleton, SdToggle, SdFileSizePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="head">
      <h2 class="title">Conversation <span class="sd-muted">({{ messageCount() }})</span></h2>
      <span class="sd-spacer"></span>
      <sd-toggle [value]="showActivity()" (valueChange)="setShowActivity($event)">Activity</sd-toggle>
      <button sdButton size="sm" variant="ghost" type="button" (click)="timeline()?.setAllCollapsed(false)">Expand all</button>
      <button sdButton size="sm" variant="ghost" type="button" (click)="timeline()?.setAllCollapsed(true)">Collapse all</button>
    </div>
    @if (loading() && items().length === 0) {
      <div class="pad" aria-busy="true"><sd-skeleton [lines]="4" /><br /><sd-skeleton [lines]="3" /></div>
    } @else if (error()) {
      <p class="pad error" role="alert">{{ error() }}</p>
    } @else if (mapped().length === 0) {
      <p class="pad sd-muted">No messages yet.</p>
    } @else {
      <sd-timeline [items]="mapped()" [timeZone]="me.timeZone()" [busy]="loading()" ariaLabel="Ticket conversation and activity">
        <ng-template sdTimelineBody let-item>
          @if (asThread(item); as t) {
            @if (t.to.length || t.cc.length || (t.fromAddress && t.channel === 'Email')) {
              <p class="addr">
                @if (t.fromAddress) {
                  <span><span class="k">From</span> {{ t.fromAddress }}</span>
                }
                @if (t.to.length) {
                  <span><span class="k">To</span> {{ t.to.join(', ') }}</span>
                }
                @if (t.cc.length) {
                  <span><span class="k">Cc</span> {{ t.cc.join(', ') }}</span>
                }
              </p>
            }
            <div class="sd-prose body" [innerHTML]="bodyOf(t)"></div>
            @if (t.attachments.length) {
              <ul class="files" aria-label="Attachments">
                @for (a of t.attachments; track a.id) {
                  <li>
                    <button type="button" class="file" (click)="download(a)" [disabled]="a.scanStatus === 'Infected'" [attr.aria-label]="'Download ' + a.fileName">
                      <sd-icon name="paperclip" [size]="14" />
                      <span class="sd-truncate">{{ a.fileName }}</span>
                      <span class="sd-muted sd-xs">{{ a.sizeBytes | sdFileSize }}</span>
                      @if (a.scanStatus === 'Infected') {
                        <sd-badge tone="danger" size="sm">Blocked</sd-badge>
                      } @else if (a.scanStatus === 'Pending') {
                        <sd-badge tone="warning" size="sm">Scanning</sd-badge>
                      } @else {
                        <sd-icon name="download" [size]="14" />
                      }
                    </button>
                  </li>
                }
              </ul>
            }
          }
        </ng-template>
      </sd-timeline>
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .head {
      position: sticky;
      top: 0;
      z-index: 1;
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: 6px;
      padding: 8px 0;
      background: var(--sd-bg);
    }
    .title {
      font-size: var(--sd-text-md);
      font-weight: var(--sd-weight-semibold);
    }
    .pad {
      padding: var(--sd-space-4) 0;
    }
    .error {
      color: var(--sd-danger);
    }
    .addr {
      display: flex;
      flex-wrap: wrap;
      gap: 4px 14px;
      margin-bottom: 8px;
      color: var(--sd-text-2);
      font-size: var(--sd-text-xs);
    }
    .k {
      color: var(--sd-text-3);
      font-weight: var(--sd-weight-semibold);
      text-transform: uppercase;
      letter-spacing: 0.04em;
      margin-right: 2px;
    }
    .body {
      overflow-wrap: anywhere;
    }
    .body :is(img, table) {
      max-width: 100%;
    }
    .files {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      margin: 10px 0 0;
      padding: 0;
      list-style: none;
    }
    .file {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      max-width: 280px;
      padding: 5px 9px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface-2);
      color: var(--sd-text);
      font: inherit;
      font-size: var(--sd-text-sm);
      cursor: pointer;
    }
    .file:hover:not(:disabled) {
      border-color: var(--sd-primary-border);
      background: var(--sd-primary-soft);
    }
    .file:disabled {
      cursor: not-allowed;
      opacity: 0.7;
    }
  `,
})
export class TicketTimelineComponent {
  readonly items = input.required<TimelineItemDto[]>();
  readonly loading = input(false);
  readonly error = input<string | null>(null);

  protected readonly me = inject(MeStore);
  private readonly files = inject(FilesApi);
  protected readonly timeline = viewChild(SdTimeline);
  protected readonly showActivity = signal(safeStorage.get(ACTIVITY_KEY) !== '0');

  protected readonly messageCount = computed(() => this.items().filter((i) => i.itemType === 'thread').length);

  protected readonly mapped = computed<SdTimelineItem[]>(() => {
    const items = this.items();
    const threads = items.filter((i) => i.itemType === 'thread' && i.thread);
    const recent = new Set(threads.slice(-4).map((i) => i.thread!.id));
    const result: SdTimelineItem[] = [];
    for (const item of items) {
      if (item.itemType === 'thread' && item.thread) result.push(this.fromThread(item.thread, recent.has(item.thread.id)));
      else if (item.itemType === 'event' && item.event && this.showActivity()) result.push(this.fromEvent(item.event));
    }
    return result;
  });

  protected setShowActivity(on: boolean): void {
    this.showActivity.set(on);
    safeStorage.set(ACTIVITY_KEY, on ? '1' : '0');
  }

  protected asThread(item: SdTimelineItem): ThreadDto | null {
    return item.data && typeof item.data === 'object' && 'seq' in (item.data as object) ? (item.data as ThreadDto) : null;
  }

  protected bodyOf(t: ThreadDto): string {
    return t.bodyHtml ?? (t.bodyText ? escapeHtml(t.bodyText) : '');
  }

  protected download(a: AttachmentDto): void {
    this.files.save(a.id, a.fileName);
  }

  private fromThread(t: ThreadDto, recent: boolean): SdTimelineItem {
    const internal = t.visibility === 'Internal';
    const inbound = t.direction === 'Inbound' || t.authorType === 'Contact';
    const system = t.kind === 'SystemMessage';
    const title = internal
      ? t.kind === 'HandoverNote'
        ? 'handover note'
        : ''
      : system
        ? 'system message'
        : inbound
          ? `wrote via ${channelLabel(t.channel)}`
          : t.kind === 'Forward'
            ? `forwarded via ${channelLabel(t.channel)}`
            : `replied via ${channelLabel(t.channel)}`;
    const meta: string[] = [];
    if (t.deliveryStatus && !internal) meta.push(t.deliveryStatus);
    if (t.editedAt) meta.push('edited');
    const length = (t.bodyHtml ?? t.bodyText ?? '').length;
    return {
      id: t.id,
      kind: system ? 'system' : internal ? 'internal' : inbound ? 'customer' : 'public',
      at: t.createdAt,
      author: t.authorName ?? t.fromAddress ?? (inbound ? 'Customer' : 'Agent'),
      icon: internal ? 'lock' : channelIcon(t.channel),
      title,
      meta,
      collapsed: !recent && length > 700,
      data: t,
    };
  }

  private fromEvent(e: TicketEventDto): SdTimelineItem {
    const changes = e.changes
      .filter((c) => c.field !== 'mentions')
      .map((c) => `${c.field}: ${c.from ?? '—'} → ${c.to ?? '—'}`)
      .join('; ');
    return {
      id: e.id,
      kind: 'event',
      at: e.occurredAt,
      author: e.actorName,
      avatarName: null,
      icon: eventIcon(e.type),
      title: e.summary ?? (changes ? `${humanise(e.type)} — ${changes}` : humanise(e.type)),
    };
  }
}
