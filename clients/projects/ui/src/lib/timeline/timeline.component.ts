import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  Directive,
  TemplateRef,
  booleanAttribute,
  computed,
  contentChild,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { formatDateTime } from '@sd/core';
import { SdAvatar } from '../avatar/avatar.component';
import { SdIcon } from '../icon/icon.component';
import { SdRelativeTimePipe } from '../pipes/time.pipes';

/** Visual kind of a timeline entry. `internal` = private note (distinct styling, never shown to customers). */
export type SdTimelineKind = 'public' | 'customer' | 'internal' | 'event' | 'system';

export interface SdTimelineItem {
  id: string;
  kind: SdTimelineKind;
  at: string | Date;
  author?: string | null;
  /** Avatar name override (defaults to author). Null hides the avatar (events). */
  avatarName?: string | null;
  avatarUrl?: string | null;
  icon?: string | null;
  /** One-line heading, e.g. "replied by e-mail" or "changed priority Low → High". */
  title?: string | null;
  /** Rich body (sanitised by Angular when bound). */
  bodyHtml?: string | null;
  bodyText?: string | null;
  /** Small right-aligned labels (channel, delivery status…). */
  meta?: string[];
  /** Start collapsed (long quoted e-mails, bot messages…). */
  collapsed?: boolean;
  /** Arbitrary payload for custom templates. */
  data?: unknown;
}

export interface SdTimelineItemContext {
  $implicit: SdTimelineItem;
}

/** Optional custom body: `<ng-template sdTimelineBody let-item>…</ng-template>` inside `<sd-timeline>`. */
@Directive({ selector: 'ng-template[sdTimelineBody]' })
export class SdTimelineBodyDef {
  readonly template = inject<TemplateRef<SdTimelineItemContext>>(TemplateRef);
  static ngTemplateContextGuard(_dir: SdTimelineBodyDef, ctx: unknown): ctx is SdTimelineItemContext {
    return true;
  }
}

/**
 * Chronological conversation + activity feed.
 * `<sd-timeline [items]="items" [timeZone]="me.timeZone()" (itemToggled)="…" />`
 * Conversation entries are cards with author avatar, relative time (absolute on hover) and a collapsible
 * body; internal notes get the amber "Internal note" treatment; events render as compact one-liners.
 * The list is an ARIA feed (`role="feed"`, each entry an `article`).
 */
@Component({
  selector: 'sd-timeline',
  imports: [NgTemplateOutlet, SdAvatar, SdIcon, SdRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ol class="feed" role="feed" [attr.aria-busy]="busy() || null" [attr.aria-label]="ariaLabel()">
      @for (item of ordered(); track item.id; let i = $index) {
        <li
          role="article"
          class="item"
          [class]="'item item--' + item.kind"
          [attr.aria-posinset]="i + 1"
          [attr.aria-setsize]="ordered().length"
          [attr.aria-label]="labelFor(item)"
        >
          @if (item.kind === 'event' || item.kind === 'system') {
            <div class="event">
              <span class="event-icon" aria-hidden="true"><sd-icon [name]="item.icon || 'activity'" [size]="13" /></span>
              <span class="event-text">
                @if (item.author) {
                  <strong>{{ item.author }}</strong>
                }
                {{ item.title }}
              </span>
              <time class="time" [attr.datetime]="iso(item.at)" [title]="absolute(item.at)">{{ item.at | sdRelativeTime }}</time>
            </div>
          } @else {
            <div class="gutter" aria-hidden="true">
              @if (item.avatarName !== null) {
                <sd-avatar [name]="item.avatarName ?? item.author ?? '?'" [src]="item.avatarUrl ?? null" size="md" />
              }
            </div>
            <div class="card">
              <header class="head">
                @if (item.kind === 'internal') {
                  <span class="note-flag"><sd-icon name="lock" [size]="12" />Internal note</span>
                } @else if (item.icon) {
                  <sd-icon class="kind-icon" [name]="item.icon" [size]="14" />
                }
                <span class="author">{{ item.author || 'Unknown' }}</span>
                @if (item.title) {
                  <span class="title">{{ item.title }}</span>
                }
                <span class="sd-spacer"></span>
                @for (m of item.meta ?? []; track m) {
                  <span class="meta">{{ m }}</span>
                }
                <time class="time" [attr.datetime]="iso(item.at)" [title]="absolute(item.at)">{{ item.at | sdRelativeTime }}</time>
                @if (collapsible()) {
                  <button
                    type="button"
                    class="toggle"
                    [attr.aria-expanded]="!isCollapsed(item)"
                    [attr.aria-label]="isCollapsed(item) ? 'Expand message' : 'Collapse message'"
                    (click)="toggle(item)"
                  >
                    <sd-icon [name]="isCollapsed(item) ? 'chevron-down' : 'chevron-up'" [size]="14" />
                  </button>
                }
              </header>
              @if (!isCollapsed(item)) {
                <div class="body">
                  @if (bodyTemplate(); as tpl) {
                    <ng-container [ngTemplateOutlet]="tpl.template" [ngTemplateOutletContext]="{ $implicit: item }" />
                  } @else if (item.bodyHtml) {
                    <div class="sd-prose" [innerHTML]="item.bodyHtml"></div>
                  } @else if (item.bodyText) {
                    <p class="plain">{{ item.bodyText }}</p>
                  }
                </div>
              } @else {
                <p class="preview sd-truncate">{{ preview(item) }}</p>
              }
            </div>
          }
        </li>
      } @empty {
        <li class="empty">No activity yet.</li>
      }
    </ol>
  `,
  styles: `
    .feed {
      position: relative;
      margin: 0;
      padding: 0;
      list-style: none;
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
    .item {
      display: flex;
      gap: 12px;
      min-width: 0;
    }
    .gutter {
      flex: none;
      width: 32px;
    }
    .card {
      flex: 1;
      min-width: 0;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
      box-shadow: var(--sd-shadow-xs);
      overflow: hidden;
    }
    .item--customer .card {
      border-left: 3px solid var(--sd-info);
    }
    .item--public .card {
      border-left: 3px solid var(--sd-primary);
    }
    .item--internal .card {
      background: var(--sd-note-bg);
      border-color: var(--sd-note-border);
      border-style: dashed;
    }
    .head {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: 6px;
      padding: 8px 10px 8px 12px;
      font-size: var(--sd-text-sm);
    }
    .author {
      font-weight: var(--sd-weight-semibold);
    }
    .title {
      color: var(--sd-text-2);
    }
    .kind-icon {
      color: var(--sd-text-3);
    }
    .note-flag {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      padding: 1px 6px;
      border-radius: var(--sd-radius-pill);
      background: color-mix(in oklab, var(--sd-note-fg) 14%, transparent);
      color: var(--sd-note-fg);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
    }
    .meta {
      padding: 0 6px;
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface-3);
      color: var(--sd-text-2);
      font-size: 11px;
      line-height: 18px;
    }
    .time {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      white-space: nowrap;
    }
    .toggle {
      display: inline-flex;
      padding: 3px;
      border: 0;
      border-radius: var(--sd-radius-xs);
      background: transparent;
      color: var(--sd-text-3);
      cursor: pointer;
    }
    .toggle:hover {
      background: var(--sd-surface-3);
      color: var(--sd-text);
    }
    .body {
      padding: 0 12px 12px;
    }
    .plain {
      white-space: pre-wrap;
    }
    .preview {
      padding: 0 12px 10px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
    .event {
      display: flex;
      align-items: center;
      gap: 8px;
      width: 100%;
      padding: 2px 0 2px 8px;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .event-icon {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 24px;
      height: 24px;
      flex: none;
      border-radius: 50%;
      background: var(--sd-surface-3);
      color: var(--sd-text-3);
    }
    .event-text {
      flex: 1;
      min-width: 0;
    }
    .event-text strong {
      color: var(--sd-text);
      font-weight: var(--sd-weight-medium);
    }
    .empty {
      padding: 16px;
      color: var(--sd-text-3);
      text-align: center;
    }
  `,
})
export class SdTimeline {
  readonly items = input.required<SdTimelineItem[]>();
  /** Newest first instead of oldest first. */
  readonly newestFirst = input(false, { transform: booleanAttribute });
  readonly collapsible = input(true, { transform: booleanAttribute });
  readonly busy = input(false, { transform: booleanAttribute });
  readonly timeZone = input<string | null>(null);
  readonly ariaLabel = input('Ticket timeline');
  readonly itemToggled = output<{ item: SdTimelineItem; collapsed: boolean }>();

  protected readonly bodyTemplate = contentChild(SdTimelineBodyDef);
  private readonly overrides = signal<ReadonlyMap<string, boolean>>(new Map());

  protected readonly ordered = computed(() => {
    const items = [...this.items()].sort((a, b) => new Date(a.at).getTime() - new Date(b.at).getTime());
    return this.newestFirst() ? items.reverse() : items;
  });

  protected isCollapsed(item: SdTimelineItem): boolean {
    return this.overrides().get(item.id) ?? !!item.collapsed;
  }

  protected toggle(item: SdTimelineItem): void {
    const collapsed = !this.isCollapsed(item);
    this.overrides.update((m) => new Map(m).set(item.id, collapsed));
    this.itemToggled.emit({ item, collapsed });
  }

  /** Expands or collapses every conversation entry. */
  setAllCollapsed(collapsed: boolean): void {
    this.overrides.set(new Map(this.items().map((i) => [i.id, collapsed])));
  }

  protected iso(at: string | Date): string {
    return new Date(at).toISOString();
  }

  protected absolute(at: string | Date): string {
    return formatDateTime(at, { timeZone: this.timeZone() ?? undefined, style: 'medium' });
  }

  protected preview(item: SdTimelineItem): string {
    const text = item.bodyText ?? (item.bodyHtml ? item.bodyHtml.replace(/<[^>]+>/g, ' ') : '');
    return text.replace(/\s+/g, ' ').trim().slice(0, 160);
  }

  protected labelFor(item: SdTimelineItem): string {
    const who = item.author ?? 'System';
    const what = item.kind === 'internal' ? 'internal note' : item.kind === 'event' || item.kind === 'system' ? 'activity' : 'message';
    return `${what} by ${who}, ${this.absolute(item.at)}`;
  }
}
