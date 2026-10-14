import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiError, CommunityApi, PostDto, ToastBridge, TopicDetailDto } from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdDateTimePipe,
  SdDialogService,
  SdEmptyState,
  SdIcon,
  SdRelativeTimePipe,
  SdRichTextEditor,
  SdSkeleton,
  isHtmlEmpty,
} from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { KbTopicStatusBadge } from '../components/kb-badges.component';
import { promptText } from '../components/kb-prompt.dialog';
import { KB_BASE, KB_STYLES, errorMessage, registerKbIcons, toastedGlobally } from '../kb-common';
import { PostEditDialog, TopicEditDialog } from './topic-dialogs';

type TopicModeration = 'hide' | 'unhide' | 'close' | 'reopen' | 'pin' | 'unpin';

const MODERATION_DONE: Record<TopicModeration, string> = {
  hide: 'Topic hidden from the community',
  unhide: 'Topic visible again',
  close: 'Topic closed',
  reopen: 'Topic reopened',
  pin: 'Topic pinned',
  unpin: 'Topic unpinned',
};

/** /kb/community/:id — a topic with its replies: agent answers, accept answer, moderation, edit, convert to ticket. */
@Component({
  selector: 'desk-kb-topic-detail',
  imports: [RouterLink, SdButton, SdBadge, SdIcon, SdSkeleton, SdEmptyState, SdRichTextEditor, SdRelativeTimePipe, SdDateTimePipe, KbTopicStatusBadge],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <a class="back" [routerLink]="[base, 'community']"><sd-icon name="arrow-left" [size]="14" /> All topics</a>

    @if (loading() && !topic()) {
      <div class="skeleton" aria-busy="true" aria-label="Loading topic">
        <sd-skeleton width="60%" height="28px" />
        <sd-skeleton [lines]="4" />
        <sd-skeleton height="120px" radius="var(--sd-radius-md)" />
      </div>
    } @else if (loadError(); as err) {
      <sd-empty-state [icon]="notFound() ? 'search-x' : 'circle-alert'" [title]="notFound() ? 'Topic not found' : 'Could not load the topic'" [message]="notFound() ? 'It may have been deleted.' : err">
        @if (!notFound()) {
          <button sdButton type="button" icon="refresh" (click)="reload()">Retry</button>
        }
        <a sdButton variant="ghost" [routerLink]="[base, 'community']">Back to topics</a>
      </sd-empty-state>
    } @else if (topic(); as t) {
      <header class="head">
        <div class="titles">
          <h2>
            @if (t.isPinned) {
              <sd-icon name="pin" [size]="16" label="Pinned" class="pin" />
            }
            {{ t.title }}
          </h2>
          <div class="kb-meta">
            <desk-kb-topic-status [status]="t.status" />
            @if (t.isQuestion) {
              <sd-badge size="sm" variant="outline" icon="help">Question</sd-badge>
            }
            @if (t.acceptedPostId) {
              <sd-badge size="sm" tone="success" icon="circle-check">Has accepted answer</sd-badge>
            }
            <span>
              <sd-icon name="user" [size]="12" /> {{ t.authorName }}
              @if (t.authorIsAgent) {
                <sd-badge size="sm" tone="primary">Agent</sd-badge>
              }
            </span>
            <span [attr.title]="t.createdAt | sdDateTime">Asked {{ t.createdAt | sdRelativeTime }}</span>
            <span><sd-icon name="eye" [size]="12" /> {{ t.viewCount }} views</span>
            <span><sd-icon name="thumbs-up" [size]="12" /> {{ t.voteScore }} votes</span>
          </div>
        </div>
        <div class="actions" role="toolbar" aria-label="Topic actions">
          @if (t.actions.edit) {
            <button sdButton size="sm" icon="pencil" type="button" [disabled]="!!busy()" (click)="editTopic()">Edit</button>
          }
          @if (t.actions.moderate) {
            <button sdButton size="sm" [icon]="t.isPinned ? 'pin-off' : 'pin'" type="button" [loading]="busy() === (t.isPinned ? 'unpin' : 'pin')" [disabled]="!!busy()" (click)="moderate(t.isPinned ? 'unpin' : 'pin')">
              {{ t.isPinned ? 'Unpin' : 'Pin' }}
            </button>
            @if (t.status === 'Closed') {
              <button sdButton size="sm" icon="lock-open" type="button" [loading]="busy() === 'reopen'" [disabled]="!!busy()" (click)="moderate('reopen')">Reopen</button>
            } @else if (t.status !== 'Hidden') {
              <button sdButton size="sm" icon="lock" type="button" [loading]="busy() === 'close'" [disabled]="!!busy()" (click)="moderate('close')">Close</button>
            }
            @if (t.status === 'Hidden') {
              <button sdButton size="sm" icon="eye" type="button" [loading]="busy() === 'unhide'" [disabled]="!!busy()" (click)="moderate('unhide')">Unhide</button>
            } @else {
              <button sdButton size="sm" icon="eye-off" type="button" [loading]="busy() === 'hide'" [disabled]="!!busy()" (click)="moderate('hide')">Hide</button>
            }
          }
          @if (t.convertedTicketId) {
            <a sdButton size="sm" variant="subtle" icon="ticket" [routerLink]="['/tickets', t.convertedTicketId]">Ticket {{ t.convertedTicketNumber ?? '' }}</a>
          } @else if (t.actions.convert) {
            <button sdButton size="sm" variant="subtle" icon="ticket" type="button" [loading]="busy() === 'convert'" [disabled]="!!busy()" (click)="convert()">Convert to ticket</button>
          }
        </div>
      </header>

      @if (t.status === 'Hidden') {
        <p class="kb-note" role="note"><sd-icon name="eye-off" [size]="16" /> This topic is hidden — customers cannot see it.</p>
      }

      <article class="post topic-body" aria-label="Original post">
        <div class="sd-prose" [innerHTML]="t.bodyHtml"></div>
        @if (t.tags.length) {
          <div class="kb-chips tags">
            @for (tag of t.tags; track tag) {
              <a [routerLink]="[base, 'community']" [queryParams]="{ tag: tag }"><sd-badge size="sm" variant="outline" icon="tag">{{ tag }}</sd-badge></a>
            }
          </div>
        }
      </article>

      <h3 class="replies-title" id="kb-replies">{{ t.posts.length }} {{ t.posts.length === 1 ? 'reply' : 'replies' }}</h3>
      @if (t.posts.length) {
        <ol class="posts" aria-labelledby="kb-replies">
          @for (p of sortedPosts(); track p.id) {
            <li class="post" [class.is-accepted]="p.isAcceptedAnswer" [class.is-hidden]="p.isHidden" [class.is-agent]="p.isAgentResponse">
              <div class="post-head">
                <strong>{{ p.authorName }}</strong>
                @if (p.isAgentResponse) {
                  <sd-badge size="sm" tone="primary" icon="headphones">Agent</sd-badge>
                }
                @if (p.isAcceptedAnswer) {
                  <sd-badge size="sm" tone="success" icon="circle-check">Accepted answer</sd-badge>
                }
                @if (p.isHidden) {
                  <sd-badge size="sm" tone="danger" icon="eye-off">Hidden</sd-badge>
                }
                <span class="kb-meta">
                  <span [attr.title]="p.createdAt | sdDateTime">{{ p.createdAt | sdRelativeTime }}</span>
                  @if (p.updatedAt !== p.createdAt) {
                    <span [attr.title]="p.updatedAt | sdDateTime">(edited)</span>
                  }
                  <span><sd-icon name="thumbs-up" [size]="12" /> {{ p.voteScore }}</span>
                </span>
              </div>
              <div class="sd-prose" [innerHTML]="p.bodyHtml"></div>
              <div class="post-actions">
                @if (t.isQuestion && t.actions.accept && !p.isAcceptedAnswer && !p.isHidden) {
                  <button sdButton size="sm" variant="ghost" icon="circle-check" type="button" [loading]="busy() === 'accept:' + p.id" [disabled]="!!busy()" (click)="accept(p)">
                    Accept answer
                  </button>
                }
                @if (t.actions.moderate || p.isMine) {
                  <button sdButton size="sm" variant="ghost" icon="pencil" type="button" [disabled]="!!busy()" (click)="editPost(p)">Edit</button>
                }
                @if (t.actions.moderate) {
                  <button
                    sdButton
                    size="sm"
                    variant="ghost"
                    [icon]="p.isHidden ? 'eye' : 'eye-off'"
                    type="button"
                    [loading]="busy() === 'post:' + p.id"
                    [disabled]="!!busy()"
                    (click)="moderatePost(p)"
                  >
                    {{ p.isHidden ? 'Unhide' : 'Hide' }}
                  </button>
                }
              </div>
            </li>
          }
        </ol>
      } @else {
        <p class="sd-muted sd-small">No replies yet{{ t.isQuestion ? ' — be the first to answer.' : '.' }}</p>
      }

      @if (t.actions.reply) {
        <section class="reply" aria-labelledby="kb-reply-title">
          <h3 id="kb-reply-title">Reply as an agent</h3>
          <sd-rich-text-editor
            ariaLabel="Your reply"
            toolbar="compact"
            minHeight="140px"
            placeholder="Write an answer… (Ctrl+Enter to post)"
            [(value)]="replyBody"
            (submitted)="postReply()"
          />
          <div class="reply-actions">
            <span class="sd-xs sd-muted">Agent replies are badged and can be accepted as the answer.</span>
            <span class="sd-spacer"></span>
            <button sdButton variant="primary" icon="reply" type="button" [loading]="busy() === 'reply'" [disabled]="replyEmpty() || (!!busy() && busy() !== 'reply')" (click)="postReply()">
              Post reply
            </button>
          </div>
        </section>
      } @else if (t.status === 'Closed') {
        <p class="sd-muted sd-small closed"><sd-icon name="lock" [size]="14" /> This topic is closed to new replies.</p>
      }
    }
  `,
  styles: [
    KB_STYLES,
    `
      :host {
        display: block;
        max-width: 980px;
      }
      .back {
        display: inline-flex;
        align-items: center;
        gap: 4px;
        margin-bottom: var(--sd-space-3);
        color: var(--sd-text-2);
        font-size: var(--sd-text-sm);
      }
      .skeleton {
        display: grid;
        gap: var(--sd-space-3);
      }
      .head {
        display: flex;
        flex-wrap: wrap;
        align-items: flex-start;
        gap: var(--sd-space-3);
        margin-bottom: var(--sd-space-3);
      }
      .titles {
        display: grid;
        gap: 6px;
        flex: 1 1 320px;
        min-width: 0;
      }
      h2 {
        display: flex;
        align-items: center;
        gap: 6px;
        font-size: var(--sd-text-xl);
        font-weight: var(--sd-weight-semibold);
      }
      .pin {
        color: var(--sd-accent);
      }
      .actions {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sd-space-2);
      }
      .kb-note {
        margin-bottom: var(--sd-space-3);
      }
      .post {
        display: grid;
        gap: var(--sd-space-2);
        padding: var(--sd-space-4);
        border: 1px solid var(--sd-border);
        border-radius: var(--sd-radius-md);
        background: var(--sd-surface);
      }
      .topic-body {
        margin-bottom: var(--sd-space-5);
        box-shadow: var(--sd-shadow-xs);
      }
      .tags a {
        text-decoration: none;
      }
      .replies-title {
        margin-bottom: var(--sd-space-2);
        font-size: var(--sd-text-md);
      }
      .posts {
        display: grid;
        gap: var(--sd-space-2);
        margin: 0 0 var(--sd-space-5);
        padding: 0;
        list-style: none;
      }
      .post.is-agent {
        border-left: 3px solid var(--sd-primary);
      }
      .post.is-accepted {
        border-color: var(--sd-success-border);
        background: color-mix(in oklab, var(--sd-success-bg) 50%, var(--sd-surface));
      }
      .post.is-hidden {
        border-style: dashed;
        background: var(--sd-surface-2);
      }
      .post.is-hidden .sd-prose {
        opacity: 0.65;
      }
      .post-head {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 6px 8px;
      }
      .post-actions {
        display: flex;
        flex-wrap: wrap;
        gap: 2px;
      }
      .post-actions:empty {
        display: none;
      }
      .reply {
        display: grid;
        gap: var(--sd-space-2);
        padding: var(--sd-space-4);
        border: 1px solid var(--sd-border);
        border-radius: var(--sd-radius-lg);
        background: var(--sd-surface-2);
      }
      .reply h3 {
        font-size: var(--sd-text-md);
      }
      .reply-actions {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sd-space-2);
      }
      .closed {
        display: flex;
        align-items: center;
        gap: 6px;
      }
    `,
  ],
})
export class TopicDetailPage {
  readonly id = input.required<string>();

  private readonly api = inject(CommunityApi);
  private readonly router = inject(Router);
  private readonly dialogs = inject(SdDialogService);
  private readonly toasts = inject(ToastBridge);

  protected readonly base = KB_BASE;
  protected readonly topic = signal<TopicDetailDto | null>(null);
  protected readonly loading = signal(false);
  protected readonly loadError = signal<string | null>(null);
  protected readonly notFound = signal(false);
  protected readonly busy = signal<string | null>(null);
  protected readonly replyBody = signal('');
  protected readonly replyEmpty = computed(() => isHtmlEmpty(this.replyBody()));
  /** Accepted answer first, then chronological. */
  protected readonly sortedPosts = computed(() => {
    const posts = this.topic()?.posts ?? [];
    return [...posts].sort((a, b) => Number(b.isAcceptedAnswer) - Number(a.isAcceptedAnswer) || a.createdAt.localeCompare(b.createdAt));
  });

  constructor() {
    registerKbIcons();
    effect(() => {
      const id = this.id();
      untracked(() => this.load(id));
    });
  }

  private load(id: string): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.notFound.set(false);
    this.api.topic(id).subscribe({
      next: (t) => {
        this.loading.set(false);
        this.topic.set(t);
      },
      error: (err: unknown) => {
        const e = ApiError.from(err);
        this.loading.set(false);
        this.notFound.set(e.status === 404);
        this.loadError.set(e.userMessage);
      },
    });
  }

  protected reload(): void {
    this.load(this.id());
  }

  protected async moderate(action: TopicModeration): Promise<void> {
    const t = this.topic();
    if (!t) return;
    let reason: string | null = null;
    if (action === 'hide' || action === 'close') {
      reason = await promptText(this.dialogs, {
        title: action === 'hide' ? 'Hide this topic?' : 'Close this topic?',
        message: action === 'hide' ? 'Customers will no longer see it. You can unhide it later.' : 'Nobody can reply until it is reopened.',
        label: 'Reason',
        hint: 'Kept in the moderation log.',
        multiline: true,
        confirmLabel: action === 'hide' ? 'Hide topic' : 'Close topic',
        danger: action === 'hide',
      });
      if (reason === null) return;
    }
    await this.run(action, () => this.api.moderateTopic(t.id, { action, reason: reason || null }), MODERATION_DONE[action]);
  }

  protected async editTopic(): Promise<void> {
    const t = this.topic();
    if (!t) return;
    const ref = this.dialogs.open<TopicDetailDto, TopicDetailDto, TopicEditDialog>(TopicEditDialog, { data: t, size: 'lg' });
    const updated = await firstValueFrom(ref.closed);
    if (updated) {
      this.topic.set(updated);
      this.toasts.success('Topic updated');
    }
  }

  protected async editPost(p: PostDto): Promise<void> {
    const ref = this.dialogs.open<PostDto, PostDto, PostEditDialog>(PostEditDialog, { data: p, size: 'lg' });
    const updated = await firstValueFrom(ref.closed);
    if (updated) {
      this.patchPost(updated);
      this.toasts.success('Reply updated');
    }
  }

  protected async moderatePost(p: PostDto): Promise<void> {
    let reason: string | null = null;
    if (!p.isHidden) {
      reason = await promptText(this.dialogs, {
        title: 'Hide this reply?',
        message: 'Customers will no longer see it.',
        label: 'Reason',
        multiline: true,
        confirmLabel: 'Hide reply',
        danger: true,
      });
      if (reason === null) return;
    }
    this.busy.set(`post:${p.id}`);
    try {
      const updated = await firstValueFrom(this.api.moderatePost(p.id, { action: p.isHidden ? 'unhide' : 'hide', reason: reason || null }));
      this.patchPost(updated);
      this.toasts.success(updated.isHidden ? 'Reply hidden' : 'Reply visible again');
    } catch (err) {
      if (!toastedGlobally(err)) this.toasts.error('Could not moderate the reply', errorMessage(err));
    } finally {
      this.busy.set(null);
    }
  }

  protected async accept(p: PostDto): Promise<void> {
    const t = this.topic();
    if (!t) return;
    await this.run(`accept:${p.id}`, () => this.api.accept(t.id, p.id), 'Answer accepted');
  }

  protected async postReply(): Promise<void> {
    const t = this.topic();
    if (!t || this.replyEmpty() || this.busy()) return;
    const ok = await this.run('reply', () => this.api.reply(t.id, { bodyHtml: this.replyBody() }), 'Reply posted');
    if (ok) this.replyBody.set('');
  }

  protected async convert(): Promise<void> {
    const t = this.topic();
    if (!t) return;
    const ok = await this.dialogs.confirm({
      title: 'Convert this topic to a ticket?',
      message: 'A ticket is created for the author with the topic and its replies, so an agent can follow up privately.',
      confirmLabel: 'Create ticket',
    });
    if (!ok) return;
    this.busy.set('convert');
    try {
      const result = await firstValueFrom(this.api.convertToTicket(t.id));
      this.toasts.success(`Ticket ${result.displayNumber} created`);
      await this.router.navigate(['/tickets', result.ticketId]);
    } catch (err) {
      if (!toastedGlobally(err)) this.toasts.error('Could not convert the topic', errorMessage(err));
    } finally {
      this.busy.set(null);
    }
  }

  private async run(key: string, call: () => ReturnType<CommunityApi['topic']>, done: string): Promise<boolean> {
    this.busy.set(key);
    try {
      const updated = await firstValueFrom(call());
      this.topic.set(updated);
      this.toasts.success(done);
      return true;
    } catch (err) {
      if (!toastedGlobally(err)) this.toasts.error('Action failed', errorMessage(err));
      return false;
    } finally {
      this.busy.set(null);
    }
  }

  private patchPost(post: PostDto): void {
    this.topic.update((t) => (t ? { ...t, posts: t.posts.map((p) => (p.id === post.id ? post : p)) } : t));
  }
}
