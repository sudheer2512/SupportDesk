import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  inject,
  input,
  linkedSignal,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { PortalContentApi, safeStorage } from '@sd/core';
import { SdButton, SdFormField, SdIcon, SdInput } from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { BrandService } from '../../brand.service';
import { errorMessage } from '../../shared/portal-utils';
import { StarRatingComponent } from '../../shared/star-rating.component';

type Step = 'ask' | 'details' | 'thanks';
const STORAGE_PREFIX = 'sd.portal.feedback.';

/**
 * "Was this helpful?" → Yes/No → optional 1–5 rating and comment → thank-you. Remembers (per browser)
 * that the article was already rated so the question is not asked again.
 */
@Component({
  selector: 'portal-article-feedback',
  imports: [FormsModule, RouterLink, SdButton, SdIcon, SdFormField, SdInput, StarRatingComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="feedback" aria-labelledby="fb-title">
      @switch (step()) {
        @case ('ask') {
          <h2 id="fb-title">Was this article helpful?</h2>
          <div class="choices">
            <button sdButton type="button" icon="thumbs-up" (click)="choose(true)">Yes</button>
            <button sdButton type="button" icon="thumbs-down" (click)="choose(false)">No</button>
          </div>
        }
        @case ('details') {
          <h2 id="fb-title" #detailsHeading tabindex="-1">{{ helpful() ? 'Glad it helped! Anything to add?' : 'Sorry about that. How can we improve it?' }}</h2>
          <form class="form" (ngSubmit)="send(true)" novalidate>
            <portal-star-rating [(value)]="rating" label="Rate this article (optional)" />
            <sd-form-field [label]="helpful() ? 'Comment (optional)' : 'What were you looking for? (optional)'" hint="Please don't include personal information.">
              <textarea sdInput name="comment" rows="3" maxlength="1000" [(ngModel)]="comment"></textarea>
            </sd-form-field>
            @if (error()) {
              <p class="portal-callout portal-callout--danger" role="alert"><sd-icon name="circle-alert" [size]="15" />{{ error() }}</p>
            }
            <div class="choices">
              <button sdButton variant="primary" type="submit" [loading]="busy()">Send feedback</button>
              <button sdButton variant="ghost" type="button" [disabled]="busy()" (click)="send(false)">Skip</button>
            </div>
          </form>
        }
        @case ('thanks') {
          <div class="thanks" role="status">
            <span class="ok" aria-hidden="true"><sd-icon name="circle-check" [size]="20" /></span>
            <div>
              <h2 id="fb-title">Thanks for your feedback!</h2>
              @if (helpful() === false) {
                <p>Still need help? <a [routerLink]="brand.base() + '/submit'">Submit a ticket</a> and our team will get back to you.</p>
              } @else {
                <p>It helps us make our articles better.</p>
              }
            </div>
          </div>
        }
      }
    </section>
  `,
  styles: `
    .feedback {
      padding: 20px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface-2);
    }
    h2 {
      font-size: var(--sd-text-lg);
      outline: none;
    }
    .choices {
      display: flex;
      flex-wrap: wrap;
      gap: 10px;
      margin-top: 12px;
    }
    .form {
      display: grid;
      gap: 14px;
      margin-top: 12px;
      max-width: 560px;
    }
    .form .choices {
      margin-top: 0;
    }
    .thanks {
      display: flex;
      gap: 12px;
      align-items: flex-start;
    }
    .thanks p {
      margin: 4px 0 0;
      color: var(--sd-text-2);
    }
    .ok {
      display: inline-flex;
      color: var(--sd-success);
      margin-top: 2px;
    }
  `,
})
export class ArticleFeedbackComponent {
  readonly articleId = input.required<string>();
  readonly locale = input<string | null>(null);

  protected readonly brand = inject(BrandService);
  private readonly api = inject(PortalContentApi);
  private readonly injector = inject(Injector);
  private readonly heading = viewChild<ElementRef<HTMLElement>>('detailsHeading');

  protected readonly step = linkedSignal<Step>(() => (safeStorage.get(STORAGE_PREFIX + this.articleId()) ? 'thanks' : 'ask'));
  protected readonly helpful = linkedSignal<boolean | null>(() => {
    const stored = safeStorage.get(STORAGE_PREFIX + this.articleId());
    return stored === 'yes' ? true : stored === 'no' ? false : null;
  });
  protected readonly rating = signal<number | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected comment = '';

  protected choose(helpful: boolean): void {
    this.helpful.set(helpful);
    this.step.set('details');
    afterNextRender(() => this.heading()?.nativeElement.focus(), { injector: this.injector });
  }

  protected async send(withDetails: boolean): Promise<void> {
    const slug = this.brand.slug();
    const helpful = this.helpful();
    if (!slug || helpful === null) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      await firstValueFrom(
        this.api.feedback(slug, this.articleId(), {
          helpful,
          rating: withDetails ? this.rating() : null,
          comment: withDetails && this.comment.trim() ? this.comment.trim() : null,
          locale: this.locale(),
        }),
      );
      safeStorage.set(STORAGE_PREFIX + this.articleId(), helpful ? 'yes' : 'no');
      this.step.set('thanks');
    } catch (err) {
      this.error.set(errorMessage(err, 'Your feedback could not be sent. Please try again.'));
    } finally {
      this.busy.set(false);
    }
  }
}
