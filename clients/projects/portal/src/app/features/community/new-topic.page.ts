import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ApiError, PortalContentApi } from '@sd/core';
import {
  SdButton,
  SdChipsInput,
  SdFormField,
  SdIcon,
  SdInput,
  SdRadio,
  SdRadioGroup,
  SdRichTextEditor,
  SdSkeleton,
  ToastService,
  isHtmlEmpty,
} from '@sd/ui';
import { catchError, firstValueFrom, of } from 'rxjs';
import { BrandService } from '../../brand.service';
import { BreadcrumbComponent } from '../../shared/breadcrumb.component';
import { DeflectionComponent } from '../../shared/deflection.component';
import { errorMessage, htmlToText } from '../../shared/portal-utils';

/** Start a community topic: title (with article suggestions), question vs discussion, body and tags. */
@Component({
  selector: 'portal-new-topic-page',
  imports: [
    FormsModule,
    RouterLink,
    SdButton,
    SdIcon,
    SdFormField,
    SdInput,
    SdRadioGroup,
    SdRadio,
    SdChipsInput,
    SdRichTextEditor,
    SdSkeleton,
    BreadcrumbComponent,
    DeflectionComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="portal-container portal-page narrow">
      <portal-breadcrumb [items]="[{ label: 'Home', link: brand.base() }, { label: 'Community', link: brand.base() + '/community' }, { label: 'New topic' }]" />
      <header class="portal-page-head">
        <div>
          <h1>Start a topic</h1>
          <p class="portal-sub">Ask a question or start a discussion with other customers and our team.</p>
        </div>
      </header>

      <form class="form" (ngSubmit)="submit()" novalidate>
        @if (error()) {
          <p class="portal-callout portal-callout--danger" role="alert"><sd-icon name="circle-alert" [size]="15" />{{ error() }}</p>
        }
        <sd-form-field label="What kind of topic is it?">
          <sd-radio-group [(value)]="kind" direction="row">
            <sd-radio value="question" description="You need an answer or a solution.">Question</sd-radio>
            <sd-radio value="discussion" description="Share an idea, tip or feedback.">Discussion</sd-radio>
          </sd-radio-group>
        </sd-form-field>

        <sd-form-field label="Title" required [error]="titleError()" hint="Summarise it in one sentence.">
          <input sdInput name="title" maxlength="200" autocomplete="off" [ngModel]="title()" (ngModelChange)="title.set($event)" />
        </sd-form-field>

        <portal-deflection [query]="title()" (solved)="solvedByArticle()" />

        <sd-form-field label="Details" required [error]="bodyError()">
          @defer (on idle) {
            <sd-rich-text-editor
              [(value)]="body"
              toolbar="compact"
              minHeight="180px"
              ariaLabel="Topic details"
              [placeholder]="kind() === 'question' ? 'Describe what you are trying to do, what you tried and what happened…' : 'Share your thoughts…'"
            />
          } @placeholder {
            <sd-skeleton height="220px" radius="var(--sd-radius-md)" />
          }
        </sd-form-field>

        <sd-form-field label="Tags" optional hint="Press Enter after each tag.">
          <sd-chips-input [(value)]="tags" [suggestions]="knownTags()" placeholder="e.g. billing, mobile-app" lowercase ariaLabel="Tags" />
        </sd-form-field>

        <div class="actions">
          <a sdButton variant="ghost" [routerLink]="brand.base() + '/community'">Cancel</a>
          <button sdButton variant="primary" type="submit" icon="send" [loading]="busy()">Post topic</button>
        </div>
      </form>
    </div>
  `,
  styles: `
    .narrow {
      max-width: 820px;
    }
    .form {
      display: grid;
      gap: 18px;
      padding: clamp(16px, 4vw, 24px);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-xl);
      background: var(--sd-surface);
    }
    .actions {
      display: flex;
      justify-content: flex-end;
      gap: 10px;
    }
  `,
})
export class NewTopicPage {
  protected readonly brand = inject(BrandService);
  private readonly api = inject(PortalContentApi);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  protected readonly kind = signal<string | null>('question');
  protected readonly title = signal('');
  protected readonly body = signal('');
  protected readonly tags = signal<string[]>([]);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly titleError = signal<string | null>(null);
  protected readonly bodyError = signal<string | null>(null);

  private readonly tagsRes = rxResource({
    params: () => this.brand.slug() ?? undefined,
    stream: ({ params }) => this.api.communityTags(params).pipe(catchError(() => of<string[]>([]))),
  });
  protected readonly knownTags = computed(() => (this.tagsRes.hasValue() ? this.tagsRes.value() : []));

  protected solvedByArticle(): void {
    this.toast.success('Glad you found an answer!', 'Your topic was not posted.');
    void this.router.navigate([this.brand.base(), 'community']);
  }

  protected async submit(): Promise<void> {
    const slug = this.brand.slug();
    if (!slug || this.busy()) return;
    const title = this.title().trim();
    this.titleError.set(title.length < 5 ? 'Enter a title of at least 5 characters.' : null);
    this.bodyError.set(isHtmlEmpty(this.body()) || htmlToText(this.body()).length < 10 ? 'Add a few more details (at least 10 characters).' : null);
    this.error.set(null);
    if (this.titleError() || this.bodyError()) return;
    this.busy.set(true);
    try {
      const topic = await firstValueFrom(this.api.createTopic(slug, { title, bodyHtml: this.body(), isQuestion: this.kind() !== 'discussion', tags: this.tags() }));
      this.toast.success('Topic posted', 'We will let you know when someone replies.');
      void this.router.navigate([this.brand.base(), 'community', topic.id]);
    } catch (err) {
      const e = ApiError.from(err);
      this.titleError.set(e.fieldError('title'));
      this.bodyError.set(e.fieldError('bodyHtml'));
      if (!this.titleError() && !this.bodyError()) this.error.set(errorMessage(err, 'Your topic could not be posted. Please try again.'));
    } finally {
      this.busy.set(false);
    }
  }
}

