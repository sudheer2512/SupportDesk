import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, linkedSignal, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Title } from '@angular/platform-browser';
import { Router, RouterLink } from '@angular/router';
import {
  ApiError,
  ArticleAttachmentDto,
  ArticleDetailDto,
  ArticleTransitionAction,
  KnowledgeApi,
  MeStore,
  ToastBridge,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdDialogService,
  SdDropdownMenu,
  SdEmptyState,
  SdFormField,
  SdIcon,
  SdImageUploader,
  SdInput,
  SdMenuItem,
  SdRichTextEditor,
  SdSkeleton,
  SdTab,
  SdTabs,
  isHtmlEmpty,
} from '@sd/ui';
import { Subscription, firstValueFrom, map, tap } from 'rxjs';
import { KbStatusBadge } from '../components/kb-badges.component';
import { promptText } from '../components/kb-prompt.dialog';
import { KB_BASE, KB_STYLES, KbLeaveGuarded, errorMessage, registerKbIcons, toastedGlobally } from '../kb-common';
import { KbStore } from '../kb.store';
import { ArticleAttachments } from './article-attachments.component';
import { ArticleDraft, ArticleErrors, draftFromArticle, draftKey, emptyDraft, fieldErrorsFrom, ruleIsEmpty, toSaveRequest } from './article-draft';
import { ArticleFeedback } from './article-feedback.component';
import { ArticleProperties } from './article-properties.component';
import { ArticleTranslations } from './article-translations.component';
import { ArticleVersions } from './article-versions.component';

const ACTION_DONE: Record<ArticleTransitionAction, string> = {
  submit: 'Submitted for review',
  approve: 'Approved',
  reject: 'Sent back to the author',
  publish: 'Published',
  archive: 'Archived',
  reopen: 'Reopened as a draft',
};

const ACTION_VERB: Record<ArticleTransitionAction, string> = {
  submit: 'submit the article',
  approve: 'approve the article',
  reject: 'reject the article',
  publish: 'publish the article',
  archive: 'archive the article',
  reopen: 'reopen the article',
};

const TABS = ['content', 'versions', 'translations', 'feedback'];

/**
 * /kb/articles/new (optionally ?fromTicket=<id> or ?title=…) and /kb/articles/:id — the article editor:
 * content, properties, attachments, workflow action bar, versions, translations and feedback.
 */
@Component({
  selector: 'desk-kb-article-editor',
  imports: [
    FormsModule,
    NgTemplateOutlet,
    RouterLink,
    SdButton,
    SdBadge,
    SdIcon,
    SdInput,
    SdFormField,
    SdRichTextEditor,
    SdTabs,
    SdTab,
    SdSkeleton,
    SdEmptyState,
    SdDropdownMenu,
    KbStatusBadge,
    ArticleProperties,
    ArticleAttachments,
    ArticleVersions,
    ArticleTranslations,
    ArticleFeedback,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '(window:beforeunload)': 'onBeforeUnload($event)',
    '(window:keydown)': 'onKeydown($event)',
  },
  template: `
    <a class="back" [routerLink]="base"><sd-icon name="arrow-left" [size]="14" /> All articles</a>

    @if (creatingFromTicket()) {
      <div class="center" role="status" aria-live="polite">
        <sd-empty-state icon="sparkles" title="Creating a draft from the ticket…" message="The conversation is being turned into an article draft." />
      </div>
    } @else if (fromTicketError(); as err) {
      <sd-empty-state icon="circle-alert" title="Could not create an article from this ticket" [message]="err">
        <button sdButton type="button" icon="refresh" (click)="retryFromTicket()">Try again</button>
        <a sdButton variant="ghost" [routerLink]="[base, 'articles', 'new']">Start a blank article</a>
      </sd-empty-state>
    } @else if (loading() && !article()) {
      <div class="grid" aria-busy="true" aria-label="Loading article">
        <div class="main skeleton">
          <sd-skeleton width="55%" height="28px" />
          <sd-skeleton height="38px" />
          <sd-skeleton height="64px" />
          <sd-skeleton height="320px" radius="var(--sd-radius-md)" />
        </div>
        <div class="side skeleton">
          <sd-skeleton height="140px" radius="var(--sd-radius-lg)" />
          <sd-skeleton height="240px" radius="var(--sd-radius-lg)" />
        </div>
      </div>
    } @else if (loadError(); as err) {
      <sd-empty-state [icon]="notFound() ? 'search-x' : 'circle-alert'" [title]="notFound() ? 'Article not found' : 'Could not load the article'" [message]="notFound() ? 'It may have been deleted, or you do not have access to it.' : err">
        @if (!notFound()) {
          <button sdButton type="button" icon="refresh" (click)="reload()">Retry</button>
        }
        <a sdButton variant="ghost" [routerLink]="base">Back to articles</a>
      </sd-empty-state>
    } @else {
      <header class="head">
        <div class="titles">
          <h2 class="sd-truncate" [attr.title]="heading()">{{ heading() }}</h2>
          <div class="badges">
            @if (article(); as a) {
              <desk-kb-status [status]="a.status" size="sm" />
              @if (a.isLive) {
                <sd-badge tone="success" size="sm" icon="globe">Live</sd-badge>
              }
              @if (a.hasPendingChanges) {
                <sd-badge tone="warning" size="sm" variant="outline">Unpublished changes</sd-badge>
              }
              <span class="sd-xs sd-muted">v{{ a.currentVersionNumber }}</span>
            } @else {
              <sd-badge size="sm" dot>New draft</sd-badge>
            }
            @if (dirty()) {
              <sd-badge tone="warning" size="sm" icon="pencil">Unsaved changes</sd-badge>
            }
          </div>
        </div>
        <div class="actions" role="toolbar" aria-label="Article actions">
          @if (!readonly()) {
            <button sdButton variant="primary" icon="save" type="button" [loading]="saving()" [disabled]="!!busyAction()" (click)="save()" title="Save (Ctrl+S)">
              {{ isNew() ? 'Create draft' : 'Save' }}
            </button>
          }
          @if (actions(); as act) {
            @if (act.submit) {
              <button sdButton icon="send" type="button" [loading]="busyAction() === 'submit'" [disabled]="busy()" (click)="transition('submit')">Submit for review</button>
            }
            @if (act.approve) {
              <button sdButton icon="circle-check" type="button" [loading]="busyAction() === 'approve'" [disabled]="busy()" (click)="transition('approve')">Approve</button>
            }
            @if (act.reject) {
              <button sdButton icon="circle-x" type="button" [loading]="busyAction() === 'reject'" [disabled]="busy()" (click)="transition('reject')">Reject</button>
            }
            @if (act.publish) {
              <button sdButton [variant]="readonly() ? 'primary' : 'subtle'" icon="globe" type="button" [loading]="busyAction() === 'publish'" [disabled]="busy()" (click)="transition('publish')">
                Publish
              </button>
            }
            @if (act.reopen) {
              <button sdButton icon="archive-restore" type="button" [loading]="busyAction() === 'reopen'" [disabled]="busy()" (click)="transition('reopen')">Reopen as draft</button>
            }
          }
          @if (moreItems().length) {
            <sd-dropdown-menu icon="ellipsis" iconOnly [caret]="false" ariaLabel="More article actions" [items]="moreItems()" align="end" [disabled]="busy()" />
          }
        </div>
      </header>

      @if (article()?.lastReviewNote; as note) {
        <div class="kb-note review-note" role="note">
          <sd-icon name="messages" [size]="16" />
          <div>
            <strong>Reviewer note</strong>
            <p>{{ note }}</p>
          </div>
        </div>
      }
      @if (readonly() && article()) {
        <p class="readonly-note" role="note"><sd-icon name="lock" [size]="14" /> You can read this article but not edit it in its current state.</p>
      }

      <div class="grid">
        <div class="main">
          @if (isNew()) {
            <ng-container [ngTemplateOutlet]="contentTpl" />
          } @else if (article(); as a) {
            <sd-tabs [(selected)]="selectedTab" ariaLabel="Article sections">
              <sd-tab id="content" label="Content" icon="file-text">
                <ng-container [ngTemplateOutlet]="contentTpl" />
              </sd-tab>
              <sd-tab id="versions" label="Versions" icon="history" [badge]="a.currentVersionNumber">
                <desk-kb-article-versions [articleId]="a.id" [revision]="revision()" [canRestore]="!readonly()" [dirty]="dirty()" (restored)="onRestored($event)" />
              </sd-tab>
              <sd-tab id="translations" label="Translations" icon="languages" [badge]="translationCount()">
                <desk-kb-article-translations
                  [articleId]="a.id"
                  [primaryLocale]="a.primaryLocale"
                  [revision]="revision()"
                  [canManage]="a.actions.manageTranslations"
                  [canPublish]="canPublish()"
                  [sourceTitle]="a.title"
                  [sourceSummary]="a.summary"
                  [sourceBody]="a.bodyHtml"
                  [uploadImage]="uploadImage"
                  (countChange)="translationCount.set($event)"
                />
              </sd-tab>
              <sd-tab id="feedback" label="Feedback" icon="thumbs-up" [badge]="a.helpfulCount + a.notHelpfulCount">
                <desk-kb-article-feedback [article]="a" [canReview]="canReviewFeedback()" />
              </sd-tab>
            </sd-tabs>
          }
        </div>
        <aside class="side" aria-label="Article properties">
          <desk-kb-article-properties [(draft)]="draft" [article]="article()" [errors]="errors()" [disabled]="readonly()" />
        </aside>
      </div>
    }

    <ng-template #contentTpl>
      <div class="content">
        <sd-form-field label="Title" required [error]="errors().title">
          <input
            sdInput
            class="title-input"
            maxlength="250"
            placeholder="e.g. How to reset your password"
            [disabled]="readonly()"
            [ngModel]="draft().title"
            (ngModelChange)="patch({ title: $event })"
          />
        </sd-form-field>
        <sd-form-field label="Summary" optional hint="Shown in search results and article lists." [error]="errors().summary">
          <textarea sdInput rows="2" maxlength="1000" [disabled]="readonly()" [ngModel]="draft().summary" (ngModelChange)="patch({ summary: $event })"></textarea>
        </sd-form-field>
        <sd-form-field label="Body" required [error]="errors().bodyHtml">
          <sd-rich-text-editor
            ariaLabel="Article body"
            placeholder="Write the article — paste or drop images to upload them."
            minHeight="340px"
            maxHeight="70vh"
            [uploadImage]="uploadImage"
            [disabled]="readonly()"
            [value]="draft().bodyHtml"
            (valueChange)="patch({ bodyHtml: $event })"
          />
        </sd-form-field>
        <sd-form-field label="Search keywords" optional hint="Synonyms readers might search for, separated by commas." [error]="errors().keywords">
          <input sdInput maxlength="500" [disabled]="readonly()" [ngModel]="draft().keywords" (ngModelChange)="patch({ keywords: $event })" />
        </sd-form-field>
        @if (!readonly()) {
          <sd-form-field label="Change note" optional hint="Stored with the version this save creates." [error]="errors().changeNote">
            <input sdInput maxlength="500" placeholder="What changed and why?" [ngModel]="changeNote()" (ngModelChange)="changeNote.set($event)" />
          </sd-form-field>
        }
        @if (article(); as a) {
          <desk-kb-article-attachments [articleId]="a.id" [(attachments)]="attachments" [readonly]="readonly()" />
        } @else {
          <p class="sd-small sd-muted attach-hint"><sd-icon name="paperclip" [size]="14" /> Create the draft first to attach downloadable files.</p>
        }
      </div>
    </ng-template>
  `,
  styles: [
    KB_STYLES,
    `
      :host {
        display: block;
      }
      .back {
        display: inline-flex;
        align-items: center;
        gap: 4px;
        margin-bottom: var(--sd-space-3);
        color: var(--sd-text-2);
        font-size: var(--sd-text-sm);
      }
      .center {
        padding: var(--sd-space-6) 0;
      }
      .head {
        position: sticky;
        top: 0;
        z-index: var(--sd-z-sticky);
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sd-space-3);
        margin: 0 calc(var(--sd-space-2) * -1) var(--sd-space-3);
        padding: var(--sd-space-2);
        border-bottom: 1px solid var(--sd-divider);
        background: var(--sd-bg);
      }
      .titles {
        flex: 1 1 280px;
        min-width: 0;
      }
      .titles h2 {
        font-size: var(--sd-text-xl);
        font-weight: var(--sd-weight-semibold);
      }
      .badges {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 6px;
        margin-top: 4px;
      }
      .actions {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sd-space-2);
      }
      .review-note {
        margin-bottom: var(--sd-space-3);
      }
      .review-note p {
        margin-top: 2px;
        white-space: pre-line;
      }
      .readonly-note {
        display: flex;
        align-items: center;
        gap: 6px;
        margin-bottom: var(--sd-space-3);
        color: var(--sd-text-2);
        font-size: var(--sd-text-sm);
      }
      .grid {
        display: grid;
        grid-template-columns: minmax(0, 1fr) 340px;
        gap: var(--sd-space-5);
        align-items: start;
      }
      @media (max-width: 1100px) {
        .grid {
          grid-template-columns: minmax(0, 1fr);
        }
      }
      .main,
      .side {
        min-width: 0;
      }
      .skeleton {
        display: grid;
        gap: var(--sd-space-3);
      }
      .content {
        display: grid;
        gap: var(--sd-space-4);
      }
      .title-input {
        font-size: var(--sd-text-lg);
        font-weight: var(--sd-weight-semibold);
        height: auto;
        min-height: 42px;
      }
      .attach-hint {
        display: flex;
        align-items: center;
        gap: 6px;
      }
    `,
  ],
})
export class ArticleEditorPage implements KbLeaveGuarded {
  /** Route param (absent on /articles/new). */
  readonly id = input<string>();
  /** ?fromTicket=<ticketId> on /articles/new. */
  readonly fromTicket = input<string>();
  /** ?title=… prefill on /articles/new (e.g. from a "search without results"). */
  readonly title = input<string>();
  /** ?tab=content|versions|translations|feedback */
  readonly tab = input<string>();

  private readonly api = inject(KnowledgeApi);
  private readonly store = inject(KbStore);
  private readonly me = inject(MeStore);
  private readonly router = inject(Router);
  private readonly toasts = inject(ToastBridge);
  private readonly dialogs = inject(SdDialogService);
  private readonly titleService = inject(Title);

  protected readonly base = KB_BASE;
  protected readonly article = signal<ArticleDetailDto | null>(null);
  protected readonly draft = signal<ArticleDraft>(emptyDraft());
  private readonly baseline = signal(draftKey(emptyDraft()));
  protected readonly changeNote = signal('');
  protected readonly attachments = signal<ArticleAttachmentDto[]>([]);
  protected readonly translationCount = signal(0);
  protected readonly errors = signal<ArticleErrors>({});
  protected readonly loading = signal(false);
  protected readonly loadError = signal<string | null>(null);
  protected readonly notFound = signal(false);
  protected readonly creatingFromTicket = signal(false);
  protected readonly fromTicketError = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly busyAction = signal<ArticleTransitionAction | 'delete' | null>(null);
  protected readonly selectedTab = linkedSignal<string | null>(() => {
    const t = this.tab();
    return t && TABS.includes(t) ? t : 'content';
  });

  protected readonly isNew = computed(() => !this.id());
  protected readonly actions = computed(() => this.article()?.actions ?? null);
  protected readonly readonly = computed(() => (this.isNew() ? !this.me.can('kb.author') : !this.actions()?.edit));
  protected readonly dirty = computed(() => !this.readonly() && draftKey(this.draft()) !== this.baseline());
  protected readonly busy = computed(() => this.saving() || this.busyAction() !== null);
  protected readonly canPublish = computed(() => this.me.can('kb.publish'));
  protected readonly canReviewFeedback = computed(() => this.me.canAny(['kb.author', 'kb.review', 'kb.publish']));
  protected readonly heading = computed(() => this.draft().title.trim() || (this.isNew() ? 'New article' : 'Untitled article'));
  /** Bumps when the server copy changes so the versions/translations tabs refresh. */
  protected readonly revision = computed(() => {
    const a = this.article();
    return a ? `${a.currentVersionNumber}:${a.status}:${a.updatedAt}` : '';
  });

  protected readonly moreItems = computed<SdMenuItem[]>(() => {
    const act = this.actions();
    const a = this.article();
    if (!act || !a) return [];
    const items: SdMenuItem[] = [];
    if (act.archive) items.push({ id: 'archive', label: 'Archive', icon: 'archive', run: () => void this.transition('archive') });
    if (a.sourceTicketId) items.push({ id: 'ticket', label: 'Open source ticket', icon: 'ticket', run: () => void this.router.navigate(['/tickets', a.sourceTicketId]) });
    if (act.delete) {
      if (items.length) items.push({ id: 'd1', divider: true });
      items.push({ id: 'delete', label: 'Delete article…', icon: 'trash', danger: true, run: () => void this.remove() });
    }
    return items;
  });

  protected readonly uploadImage: SdImageUploader = (file: File) =>
    this.api.uploadImage(file).pipe(
      map((a) => a.url),
      tap({ error: (err: unknown) => this.toasts.error('Image upload failed', errorMessage(err)) }),
    );

  private loadSub: Subscription | null = null;
  private leaving = false;

  constructor() {
    registerKbIcons();
    this.store.ensureOptions();
    this.store.ensureCategories();
    effect(() => {
      const id = this.id();
      const ticketId = this.fromTicket();
      untracked(() => {
        if (id) this.load(id);
        else if (ticketId) void this.createFromTicket(ticketId);
        else this.startNew();
      });
    });
    inject(DestroyRef).onDestroy(() => this.loadSub?.unsubscribe());
  }

  // ───── Loading ─────

  private startNew(): void {
    this.loadSub?.unsubscribe();
    this.article.set(null);
    this.loadError.set(null);
    const d = emptyDraft({ title: this.title()?.trim() ?? '' });
    this.draft.set(d);
    this.baseline.set(draftKey(emptyDraft()));
    this.changeNote.set('');
    this.attachments.set([]);
    this.errors.set({});
  }

  private load(id: string): void {
    this.loadSub?.unsubscribe();
    if (this.article()?.id !== id) this.article.set(null);
    this.loading.set(true);
    this.loadError.set(null);
    this.notFound.set(false);
    this.loadSub = this.api.article(id).subscribe({
      next: (a) => {
        this.loading.set(false);
        this.applyArticle(a);
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
    const id = this.id();
    if (id) this.load(id);
  }

  private async createFromTicket(ticketId: string): Promise<void> {
    this.creatingFromTicket.set(true);
    this.fromTicketError.set(null);
    try {
      const created = await firstValueFrom(this.api.fromTicket(ticketId, { includeConversation: true }));
      this.toasts.success('Draft created from the ticket', 'Review and edit it before submitting.');
      this.leaving = true;
      await this.router.navigate([KB_BASE, 'articles', created.id], { replaceUrl: true });
    } catch (err) {
      this.fromTicketError.set(errorMessage(err));
    } finally {
      this.creatingFromTicket.set(false);
    }
  }

  protected retryFromTicket(): void {
    const ticketId = this.fromTicket();
    if (ticketId) void this.createFromTicket(ticketId);
  }

  private applyArticle(a: ArticleDetailDto): void {
    this.article.set(a);
    const d = draftFromArticle(a);
    this.draft.set(d);
    this.baseline.set(draftKey(d));
    this.attachments.set([...a.attachments]);
    this.translationCount.set(a.translations.length);
    this.errors.set({});
    this.titleService.setTitle(`${a.title} · Knowledge Base · SupportDesk`);
  }

  // ───── Editing ─────

  protected patch(changes: Partial<ArticleDraft>): void {
    this.draft.update((d) => ({ ...d, ...changes }));
    const keys = Object.keys(changes) as (keyof ArticleDraft)[];
    if (keys.some((k) => this.errors()[k as keyof ArticleErrors])) {
      this.errors.update((e) => {
        const next = { ...e };
        for (const k of keys) delete next[k as keyof ArticleErrors];
        return next;
      });
    }
  }

  private validate(): boolean {
    const d = this.draft();
    const errors: ArticleErrors = {};
    if (!d.title.trim()) errors.title = 'Give the article a title.';
    if (isHtmlEmpty(d.bodyHtml)) errors.bodyHtml = 'Write the article body.';
    if (d.visibility === 'Restricted' && d.rules.filter((r) => !ruleIsEmpty(r)).length === 0) {
      errors.accessRules = 'Restricted articles need at least one access rule with a condition.';
    }
    if (d.reviewDue && d.expires && d.expires < d.reviewDue) errors.expiresAt = 'The expiry date is before the review date.';
    this.errors.set(errors);
    return Object.keys(errors).length === 0;
  }

  /** Creates or updates the article; resolves true when saved. */
  async save(): Promise<boolean> {
    if (this.readonly() || this.saving()) return false;
    if (!this.validate()) {
      this.toasts.warning('Check the highlighted fields');
      return false;
    }
    const current = this.article();
    const request = toSaveRequest(this.draft(), this.changeNote(), current?.rowVersion ?? null);
    this.saving.set(true);
    try {
      const saved = await firstValueFrom(current ? this.api.update(current.id, request) : this.api.create(request));
      this.applyArticle(saved);
      this.changeNote.set('');
      if (!current) {
        this.toasts.success('Draft created', `Version ${saved.currentVersionNumber} saved.`);
        this.leaving = true;
        await this.router.navigate([KB_BASE, 'articles', saved.id], { replaceUrl: true });
      } else {
        this.toasts.success('Article saved', `Version ${saved.currentVersionNumber}.`);
      }
      return true;
    } catch (err) {
      await this.handleSaveError(err);
      return false;
    } finally {
      this.saving.set(false);
    }
  }

  private async handleSaveError(err: unknown): Promise<void> {
    const e = ApiError.from(err);
    if (e.is('concurrency_conflict')) {
      const reload = await this.dialogs.confirm({
        title: 'Someone else changed this article',
        message:
          'A newer version was saved after you opened it, so your changes were not saved. Reload to get the latest version (your edits will be discarded), or keep editing to copy your changes first.',
        confirmLabel: 'Reload latest',
        cancelLabel: 'Keep editing',
      });
      if (reload) this.reload();
      return;
    }
    const fieldErrors = fieldErrorsFrom(e);
    this.errors.set(fieldErrors);
    const hasFieldErrors = Object.values(fieldErrors).some(Boolean);
    this.toasts.error(hasFieldErrors ? 'Check the highlighted fields' : 'Could not save the article', hasFieldErrors ? undefined : e.userMessage);
  }

  // ───── Workflow ─────

  protected async transition(action: ArticleTransitionAction): Promise<void> {
    const a = this.article();
    if (!a || this.busy()) return;
    let note: string | null = null;
    if (action === 'reject') {
      note = await promptText(this.dialogs, {
        title: 'Reject this article?',
        message: 'It goes back to the author as a draft with your note.',
        label: 'What needs to change?',
        required: true,
        multiline: true,
        confirmLabel: 'Reject',
        danger: true,
      });
      if (note === null) return;
    } else if (action === 'archive') {
      const ok = await this.dialogs.confirm({
        title: 'Archive this article?',
        message: 'It is removed from the help center and agent suggestions. You can reopen it later.',
        confirmLabel: 'Archive',
        danger: true,
      });
      if (!ok) return;
    }
    // Workflow steps act on the saved version: save pending edits first (nothing is lost).
    if (this.dirty()) {
      const saved = await this.save();
      if (!saved) return;
    }
    this.busyAction.set(action);
    try {
      const updated = await firstValueFrom(this.api.transition(a.id, { action, note }));
      this.applyArticle(updated);
      this.toasts.success(ACTION_DONE[action]);
    } catch (err) {
      this.toasts.error(`Could not ${ACTION_VERB[action]}`, errorMessage(err));
    } finally {
      this.busyAction.set(null);
    }
  }

  private async remove(): Promise<void> {
    const a = this.article();
    if (!a) return;
    const ok = await this.dialogs.confirm({
      title: `Delete “${a.title}”?`,
      message: 'The article, its versions, translations, attachments and feedback are permanently deleted.',
      confirmLabel: 'Delete article',
      danger: true,
    });
    if (!ok) return;
    this.busyAction.set('delete');
    try {
      await firstValueFrom(this.api.delete(a.id));
      this.leaving = true;
      this.toasts.success('Article deleted');
      await this.router.navigate([KB_BASE]);
    } catch (err) {
      if (!toastedGlobally(err)) this.toasts.error('Could not delete the article', errorMessage(err));
    } finally {
      this.busyAction.set(null);
    }
  }

  protected onRestored(article: ArticleDetailDto): void {
    this.applyArticle(article);
    this.changeNote.set('');
    this.selectedTab.set('content');
  }

  // ───── Leaving & shortcuts ─────

  canDeactivate(): boolean | Promise<boolean> {
    if (this.leaving || !this.dirty()) {
      this.leaving = false;
      return true;
    }
    return this.dialogs.confirm({
      title: 'Discard unsaved changes?',
      message: 'You have edits to this article that have not been saved.',
      confirmLabel: 'Discard changes',
      cancelLabel: 'Keep editing',
      danger: true,
    });
  }

  protected onBeforeUnload(event: BeforeUnloadEvent): void {
    if (this.dirty()) {
      event.preventDefault();
      event.returnValue = '';
    }
  }

  protected onKeydown(event: KeyboardEvent): void {
    if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 's') {
      event.preventDefault();
      if (!this.readonly() && !this.busy()) void this.save();
    }
  }
}
