import { ChangeDetectionStrategy, Component, computed, effect, inject, input, linkedSignal, signal, untracked } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { ApiError, PortalAuthService, PortalContentApi, PostDto, TopicDetailDto } from '@sd/core';
import { SdAvatar, SdBadge, SdButton, SdDateTimePipe, SdIcon, SdRelativeTimePipe, SdRichTextEditor, SdSkeleton, ToastService, isHtmlEmpty } from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { BrandService } from '../../brand.service';
import { BreadcrumbComponent } from '../../shared/breadcrumb.component';
import { ErrorStateComponent } from '../../shared/error-state.component';
import { errorMessage } from '../../shared/portal-utils';
import { ProseLinksDirective } from '../../shared/prose-links.directive';
import { VoteButtonComponent } from './vote-button.component';

/**
 * A community topic: question/discussion body, votes on the topic and every reply (toggle), the accepted
 * answer highlighted (and acceptable by the topic author), agent badges, and a reply box for signed-in
 * contacts. Signed-out visitors can read and are invited to sign in to take part.
 */
@Component({
  selector: 'portal-topic-page',
  imports: [
    RouterLink,
    SdIcon,
    SdBadge,
    SdButton,
    SdAvatar,
    SdSkeleton,
    SdRelativeTimePipe,
    SdDateTimePipe,
    SdRichTextEditor,
    BreadcrumbComponent,
    ErrorStateComponent,
    VoteButtonComponent,
    ProseLinksDirective,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="portal-container portal-page narrow">
      <portal-breadcrumb [items]="[{ label: 'Home', link: brand.base() }, { label: 'Community', link: brand.base() + '/community' }, { label: topic()?.title ?? 'Topic' }]" />
      @if (res.error()) {
        <portal-error-state
          [error]="res.error()"
          notFoundTitle="Topic not found"
          notFoundMessage="This topic was removed or is no longer visible."
          (retry)="res.reload()"
        >
          <a sdButton [routerLink]="brand.base() + '/community'">Back to the community</a>
        </portal-error-state>
      } @else if (topic(); as t) {
        <article class="topic" aria-labelledby="topic-title">
          <div class="vote-col">
            <portal-vote [score]="t.voteScore" [voted]="t.myVote > 0" [busy]="voting() === t.id" [disabled]="!canVote()" (toggle)="voteTopic(t)" />
          </div>
          <div class="topic-main">
            <div class="badges">
              <sd-badge [tone]="t.isQuestion ? 'primary' : 'neutral'" [icon]="t.isQuestion ? 'help' : 'messages'">{{ t.isQuestion ? 'Question' : 'Discussion' }}</sd-badge>
              @if (t.acceptedPostId) {
                <sd-badge tone="success" icon="circle-check">Answered</sd-badge>
              }
              @if (t.status === 'Closed') {
                <sd-badge icon="lock">Closed</sd-badge>
              }
              @if (t.isPinned) {
                <sd-badge tone="accent" icon="pin">Pinned</sd-badge>
              }
            </div>
            <h1 id="topic-title">{{ t.title }}</h1>
            <p class="byline">
              <sd-avatar [name]="t.authorName" size="xs" />
              <span class="author">{{ t.authorName }}</span>
              @if (t.authorIsAgent) {
                <sd-badge size="sm" tone="primary" icon="badge-check">Support team</sd-badge>
              }
              <span class="dot" aria-hidden="true">·</span>
              <time [attr.datetime]="t.createdAt" [attr.title]="t.createdAt | sdDateTime">{{ t.createdAt | sdRelativeTime }}</time>
              <span class="dot" aria-hidden="true">·</span>
              <span>{{ t.viewCount }} views</span>
            </p>
            <div class="sd-prose portal-prose body" [innerHTML]="t.bodyHtml" portalProseLinks></div>
            @if (t.tags.length) {
              <ul class="tags" aria-label="Tags">
                @for (tag of t.tags; track tag) {
                  <li>
                    <a [routerLink]="brand.base() + '/community'" [queryParams]="{ tag }">#{{ tag }}</a>
                  </li>
                }
              </ul>
            }
            @if (t.convertedTicketNumber) {
              <p class="portal-callout portal-callout--info converted" role="note">
                <sd-icon name="ticket" [size]="15" />
                <span>Our support team is following this up as ticket {{ t.convertedTicketNumber }}.</span>
              </p>
            }
          </div>
        </article>

        @if (accepted(); as a) {
          <section class="accepted-summary" aria-labelledby="accepted-title">
            <h2 id="accepted-title"><sd-icon name="circle-check" [size]="17" />Accepted answer</h2>
            <p>
              {{ a.authorName }} answered {{ a.createdAt | sdRelativeTime }} —
              <a [href]="'#post-' + a.id" (click)="jumpTo($event, a.id)">jump to the answer</a>
            </p>
          </section>
        }

        <section class="replies" aria-labelledby="replies-title">
          <h2 id="replies-title" class="replies-title">{{ t.posts.length }} {{ t.posts.length === 1 ? 'reply' : 'replies' }}</h2>
          @if (t.posts.length) {
            <ol class="posts">
              @for (p of orderedPosts(); track p.id) {
                <li class="post" [id]="'post-' + p.id" tabindex="-1" [class.is-accepted]="p.isAcceptedAnswer" [class.is-agent]="p.isAgentResponse">
                  <div class="vote-col">
                    <portal-vote [score]="p.voteScore" [voted]="p.myVote > 0" [busy]="voting() === p.id" [disabled]="!canVote()" (toggle)="votePost(p)" />
                  </div>
                  <div class="post-main">
                    <p class="byline">
                      <sd-avatar [name]="p.authorName" size="xs" />
                      <span class="author">{{ p.authorName }}</span>
                      @if (p.isAgentResponse) {
                        <sd-badge size="sm" tone="primary" icon="badge-check">Support team</sd-badge>
                      }
                      @if (p.isMine) {
                        <sd-badge size="sm">You</sd-badge>
                      }
                      <span class="dot" aria-hidden="true">·</span>
                      <time [attr.datetime]="p.createdAt" [attr.title]="p.createdAt | sdDateTime">{{ p.createdAt | sdRelativeTime }}</time>
                      @if (p.isAcceptedAnswer) {
                        <sd-badge size="sm" tone="success" icon="circle-check" class="accepted-badge">Accepted answer</sd-badge>
                      }
                    </p>
                    <div class="sd-prose portal-prose portal-prose--compact" [innerHTML]="p.bodyHtml" portalProseLinks></div>
                    @if (t.actions.accept && t.isQuestion && !p.isAcceptedAnswer) {
                      <div class="post-actions">
                        <button sdButton size="sm" variant="ghost" icon="circle-check" [loading]="accepting() === p.id" (click)="accept(t, p)">
                          Accept as answer
                        </button>
                      </div>
                    }
                  </div>
                </li>
              }
            </ol>
          } @else {
            <p class="no-replies">No replies yet{{ t.isQuestion ? ' — know the answer?' : '.' }}</p>
          }
        </section>

        <section class="reply" aria-labelledby="reply-title">
          <h2 id="reply-title" class="replies-title">Your reply</h2>
          @if (t.actions.reply) {
            <form (submit)="$event.preventDefault(); sendReply(t)" novalidate>
              @defer (on viewport) {
                <sd-rich-text-editor
                  [(value)]="replyHtml"
                  toolbar="compact"
                  minHeight="120px"
                  placeholder="Share what you know…"
                  ariaLabel="Your reply"
                  (submitted)="sendReply(t)"
                />
              } @placeholder {
                <div class="editor-placeholder"><sd-skeleton height="150px" radius="var(--sd-radius-md)" /></div>
              }
              @if (replyError()) {
                <p class="portal-callout portal-callout--danger" role="alert"><sd-icon name="circle-alert" [size]="15" />{{ replyError() }}</p>
              }
              <div class="reply-actions">
                <span class="hint">Be kind and specific. Don't share passwords or personal data.</span>
                <button sdButton variant="primary" type="submit" icon="send" [loading]="sending()">Post reply</button>
              </div>
            </form>
          } @else if (!auth.isAuthenticated()) {
            <div class="signin">
              <p>Sign in to reply, vote and accept answers.</p>
              <a sdButton variant="primary" icon="log-in" [routerLink]="brand.base() + '/login'" [queryParams]="{ returnUrl: router.url }">Sign in to reply</a>
            </div>
          } @else {
            <p class="no-replies"><sd-icon name="lock" [size]="14" />This topic is closed to new replies.</p>
          }
        </section>
        <p class="sd-visually-hidden" aria-live="polite">{{ announcement() }}</p>
      } @else {
        <div class="loading" aria-busy="true" aria-label="Loading topic">
          <sd-skeleton height="34px" width="70%" />
          <sd-skeleton width="40%" />
          <sd-skeleton [lines]="6" />
          <sd-skeleton height="100px" />
        </div>
      }
    </div>
  `,
  styles: `
    .narrow {
      max-width: 900px;
    }
    .topic {
      display: flex;
      gap: 16px;
      margin-top: 14px;
      padding-bottom: 20px;
      border-bottom: 1px solid var(--sd-border);
    }
    .vote-col {
      flex: none;
    }
    .topic-main,
    .post-main {
      flex: 1;
      min-width: 0;
    }
    .badges {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      margin-bottom: 8px;
    }
    h1 {
      font-size: clamp(1.4rem, 3.4vw, 1.9rem);
      font-weight: var(--sd-weight-bold);
      letter-spacing: -0.02em;
      overflow-wrap: anywhere;
    }
    .byline {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
      margin: 10px 0 14px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
    .author {
      color: var(--sd-text);
      font-weight: var(--sd-weight-medium);
    }
    .tags {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      margin: 16px 0 0;
      padding: 0;
      list-style: none;
    }
    .tags a {
      padding: 2px 10px;
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface-3);
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
      text-decoration: none;
    }
    .tags a:hover {
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    .converted {
      margin: 16px 0 0;
    }
    .accepted-summary {
      margin-top: 18px;
      padding: 12px 16px;
      border: 1px solid var(--sd-success-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-success-bg);
    }
    .accepted-summary h2 {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      color: var(--sd-success);
      font-size: var(--sd-text-md);
    }
    .accepted-summary p {
      margin: 4px 0 0;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .replies {
      margin-top: 24px;
    }
    .replies-title {
      margin-bottom: 12px;
      font-size: var(--sd-text-lg);
    }
    .posts {
      display: grid;
      gap: 12px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .post {
      display: flex;
      gap: 14px;
      padding: 16px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
      outline: none;
      scroll-margin-top: 80px;
    }
    .post:focus-visible {
      box-shadow: var(--sd-focus-ring);
    }
    .post.is-agent {
      border-left: 3px solid var(--sd-primary);
    }
    .post.is-accepted {
      border-color: var(--sd-success-border);
      box-shadow: 0 0 0 1px var(--sd-success-border);
      background: color-mix(in oklab, var(--sd-success-bg) 45%, var(--sd-surface));
    }
    .post .byline {
      margin: 0 0 8px;
    }
    .accepted-badge {
      margin-left: auto;
    }
    .post-actions {
      margin-top: 10px;
    }
    .no-replies {
      display: flex;
      align-items: center;
      gap: 6px;
      color: var(--sd-text-2);
    }
    .reply {
      margin-top: 28px;
    }
    .reply form {
      display: grid;
      gap: 10px;
    }
    .reply-actions {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
    }
    .hint {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .signin {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      padding: 16px;
      border: 1px dashed var(--sd-border-strong);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface-2);
    }
    .signin p {
      margin: 0;
      color: var(--sd-text-2);
    }
    .loading {
      display: grid;
      gap: 14px;
      margin-top: 14px;
    }
    @media (max-width: 560px) {
      .topic,
      .post {
        flex-direction: column-reverse;
        gap: 10px;
      }
      .post {
        padding: 14px;
      }
      .vote-col portal-vote {
        display: inline-block;
      }
    }
  `,
})
export class TopicPage {
  readonly id = input.required<string>();

  protected readonly brand = inject(BrandService);
  protected readonly auth = inject(PortalAuthService);
  protected readonly router = inject(Router);
  private readonly api = inject(PortalContentApi);
  private readonly toast = inject(ToastService);

  protected readonly res = rxResource({
    params: () => {
      const brand = this.brand.slug();
      return brand ? { brand, id: this.id(), signedIn: this.auth.isAuthenticated() } : undefined;
    },
    stream: ({ params }) => this.api.topic(params.brand, params.id),
  });
  /** Local copy patched by votes, replies and accepting an answer. */
  protected readonly topic = linkedSignal<TopicDetailDto | null>(() => (this.res.hasValue() ? this.res.value() : null));
  protected readonly accepted = computed(() => this.topic()?.posts.find((p) => p.isAcceptedAnswer) ?? null);
  /** The accepted answer first, then the rest in order. */
  protected readonly orderedPosts = computed<PostDto[]>(() => {
    const posts = (this.topic()?.posts ?? []).filter((p) => !p.isHidden);
    const accepted = posts.filter((p) => p.isAcceptedAnswer);
    return [...accepted, ...posts.filter((p) => !p.isAcceptedAnswer)];
  });
  protected readonly canVote = computed(() => !this.auth.isAuthenticated() || (this.topic()?.actions.vote ?? false));

  protected readonly voting = signal<string | null>(null);
  protected readonly accepting = signal<string | null>(null);
  protected readonly sending = signal(false);
  protected readonly replyError = signal<string | null>(null);
  protected readonly announcement = signal('');
  protected readonly replyHtml = signal('');

  constructor() {
    effect(() => {
      const t = this.topic();
      if (t) untracked(() => this.brand.setPageTitle(t.title));
    });
  }

  protected async voteTopic(t: TopicDetailDto): Promise<void> {
    if (!this.requireSignIn()) return;
    const slug = this.brand.slug();
    if (!slug || this.voting()) return;
    this.voting.set(t.id);
    try {
      const r = await firstValueFrom(this.api.voteTopic(slug, t.id, t.myVote > 0 ? 0 : 1));
      this.topic.update((cur) => (cur ? { ...cur, voteScore: r.voteScore, myVote: r.myVote } : cur));
      this.announcement.set(r.myVote > 0 ? 'Vote added' : 'Vote removed');
    } catch (err) {
      this.failed(err, 'Your vote could not be saved.');
    } finally {
      this.voting.set(null);
    }
  }

  protected async votePost(p: PostDto): Promise<void> {
    if (!this.requireSignIn()) return;
    const slug = this.brand.slug();
    if (!slug || this.voting()) return;
    this.voting.set(p.id);
    try {
      const r = await firstValueFrom(this.api.votePost(slug, p.id, p.myVote > 0 ? 0 : 1));
      this.topic.update((cur) =>
        cur ? { ...cur, posts: cur.posts.map((x) => (x.id === p.id ? { ...x, voteScore: r.voteScore, myVote: r.myVote } : x)) } : cur,
      );
      this.announcement.set(r.myVote > 0 ? 'Vote added' : 'Vote removed');
    } catch (err) {
      this.failed(err, 'Your vote could not be saved.');
    } finally {
      this.voting.set(null);
    }
  }

  protected async accept(t: TopicDetailDto, p: PostDto): Promise<void> {
    const slug = this.brand.slug();
    if (!slug) return;
    this.accepting.set(p.id);
    try {
      this.topic.set(await firstValueFrom(this.api.accept(slug, t.id, p.id)));
      this.announcement.set(`Reply by ${p.authorName} accepted as the answer`);
      this.toast.success('Answer accepted', 'Thanks — this helps others with the same question.');
    } catch (err) {
      this.failed(err, 'The answer could not be accepted.');
    } finally {
      this.accepting.set(null);
    }
  }

  protected async sendReply(t: TopicDetailDto): Promise<void> {
    const slug = this.brand.slug();
    const body = this.replyHtml();
    if (!slug || this.sending()) return;
    if (isHtmlEmpty(body)) {
      this.replyError.set('Write a reply first.');
      return;
    }
    this.sending.set(true);
    this.replyError.set(null);
    try {
      const updated = await firstValueFrom(this.api.reply(slug, t.id, { bodyHtml: body }));
      const before = new Set(t.posts.map((p) => p.id));
      this.topic.set(updated);
      this.replyHtml.set('');
      this.announcement.set('Your reply was posted');
      const added = updated.posts.find((p) => !before.has(p.id));
      if (added) setTimeout(() => this.focusPost(added.id), 0);
    } catch (err) {
      const e = ApiError.from(err);
      this.replyError.set(e.fieldError('bodyHtml') ?? errorMessage(err, 'Your reply could not be posted. Please try again.'));
    } finally {
      this.sending.set(false);
    }
  }

  protected jumpTo(event: Event, postId: string): void {
    event.preventDefault();
    this.focusPost(postId);
  }

  private focusPost(postId: string): void {
    const el = document.getElementById(`post-${postId}`);
    el?.scrollIntoView({ block: 'start', behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' });
    el?.focus({ preventScroll: true });
  }

  /** Signed-out visitors are sent to sign in (and back here afterwards). */
  private requireSignIn(): boolean {
    if (this.auth.isAuthenticated()) return true;
    void this.router.navigate([this.brand.base(), 'login'], { queryParams: { returnUrl: this.router.url } });
    return false;
  }

  private failed(err: unknown, fallback: string): void {
    const e = ApiError.from(err);
    if (!(e.isNetworkError || e.isServerError || e.status === 403 || e.status === 429)) this.toast.error(fallback, errorMessage(err, ''));
  }
}
