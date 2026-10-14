import { ChangeDetectionStrategy, Component, computed, inject, input, linkedSignal, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { ApiError, CsatApi, CsatSurveyDto } from '@sd/core';
import { SdButton, SdDateTimePipe, SdFormField, SdIcon, SdInput, SdSkeleton } from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { BrandService } from '../../brand.service';
import { errorMessage } from '../../shared/portal-utils';
import { StarRatingComponent } from '../../shared/star-rating.component';

/**
 * Satisfaction survey opened from the e-mailed link (`/{brand}/csat/{token}?rating=n`). No sign-in: the token is the
 * credential. The star chosen in the e-mail is pre-selected; the customer confirms it and may add a comment.
 */
@Component({
  selector: 'portal-csat-page',
  imports: [RouterLink, SdButton, SdDateTimePipe, SdFormField, SdIcon, SdInput, SdSkeleton, StarRatingComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="portal-container wrap">
      @if (survey.isLoading() && !current()) {
        <div class="card"><sd-skeleton [lines]="4" /></div>
      } @else if (loadError(); as err) {
        <div class="card state" role="alert">
          <span class="art" aria-hidden="true"><sd-icon name="circle-x" [size]="28" /></span>
          <h1>{{ err.title }}</h1>
          <p class="lead">{{ err.detail }}</p>
          <a sdButton variant="primary" icon="home" [routerLink]="brand.base()">Help center home</a>
        </div>
      } @else if (current(); as s) {
        <section class="card" aria-labelledby="csat-title">
          <p class="eyebrow">{{ s.brandName || brand.name() }} · Ticket {{ s.ticketNumber }}</p>
          <h1 id="csat-title">How did we do?</h1>
          <p class="lead">“{{ s.subject }}”@if (s.agentName) { — handled by {{ s.agentName }}}</p>

          @if (s.status === 'answered') {
            <div class="done" role="status">
              <span class="art" aria-hidden="true"><sd-icon name="circle-check" [size]="28" /></span>
              <h2>Thank you for your feedback</h2>
              <p class="stars"><span class="sd-visually-hidden">{{ s.rating }} out of 5 stars</span>
                @for (n of [1, 2, 3, 4, 5]; track n) {
                  <sd-icon name="star" [size]="20" [class.on]="n <= (s.rating ?? 0)" />
                }
              </p>
              @if (s.comment) { <p class="muted">“{{ s.comment }}”</p> }
            </div>
          } @else if (s.status === 'expired') {
            <p class="portal-callout" role="status">This survey closed on {{ s.expiresAt | sdDateTime }}. Thank you all the same.</p>
          } @else {
            <form class="form" (submit)="$event.preventDefault(); submit()" novalidate>
              <portal-star-rating [(value)]="rating" label="Rate the support you received" [size]="32" />
              <sd-form-field label="Anything you'd like to tell us?" optional>
                <textarea sdInput rows="4" maxlength="2000" [value]="comment()" (input)="comment.set($any($event.target).value)"></textarea>
              </sd-form-field>
              @if (submitError()) {
                <p class="portal-callout portal-callout--danger" role="alert">{{ submitError() }}</p>
              }
              <button sdButton variant="primary" type="submit" [loading]="busy()" [disabled]="!rating()">Send feedback</button>
              <p class="muted small">You can answer until {{ s.expiresAt | sdDateTime }}.</p>
            </form>
          }
        </section>
      }
    </div>
  `,
  styles: `
    .wrap { max-width: 640px; padding-block: clamp(24px, 6vw, 56px); }
    .card { padding: clamp(20px, 4vw, 32px); }
    .eyebrow { margin: 0 0 4px; color: var(--sd-text-3); font-size: var(--sd-text-sm); }
    h1 { margin: 0 0 6px; font-size: var(--sd-text-2xl); }
    .lead { margin: 0 0 var(--sd-space-5); color: var(--sd-text-2); overflow-wrap: anywhere; }
    .form { display: grid; gap: var(--sd-space-4); justify-items: start; }
    .form sd-form-field { width: 100%; }
    .state, .done { display: grid; justify-items: center; gap: var(--sd-space-2); text-align: center; }
    .art { display: inline-flex; padding: 12px; border-radius: 50%; background: var(--sd-primary-soft); color: var(--sd-primary); }
    .stars { display: inline-flex; gap: 2px; margin: 0; color: var(--sd-text-3); }
    .stars .on { color: var(--sd-warning); }
    .muted { color: var(--sd-text-2); }
    .small { font-size: var(--sd-text-sm); }
  `,
})
export class CsatPage {
  /** Route param (component input binding). */
  readonly token = input.required<string>();
  /** The star clicked in the e-mail (`?rating=4`). */
  readonly ratingParam = input<string | undefined>(undefined, { alias: 'rating' });

  protected readonly brand = inject(BrandService);
  private readonly api = inject(CsatApi);

  protected readonly survey = rxResource({ params: () => this.token(), stream: ({ params }) => this.api.survey(params) });
  private readonly submitted = signal<CsatSurveyDto | null>(null);
  protected readonly current = computed(() => this.submitted() ?? (this.survey.hasValue() ? this.survey.value() : null));

  /** Pre-selected from the e-mail link; the customer can change it. */
  protected readonly rating = linkedSignal<number | null>(() => {
    const n = Number(this.ratingParam());
    return Number.isInteger(n) && n >= 1 && n <= 5 ? n : null;
  });
  protected readonly comment = signal('');
  protected readonly busy = signal(false);
  protected readonly submitError = signal<string | null>(null);

  protected readonly loadError = computed(() => {
    const err = this.survey.error();
    if (!err) return null;
    const e = ApiError.from(err);
    if (e.status === 404) return { title: 'Survey not found', detail: 'This feedback link is not valid. It may have been copied incompletely.' };
    if (e.status === 410) return { title: 'This survey has closed', detail: 'Feedback can no longer be given for this ticket. Thank you all the same.' };
    return { title: 'We could not open the survey', detail: errorMessage(err) };
  });

  protected async submit(): Promise<void> {
    const rating = this.rating();
    if (!rating || this.busy()) return;
    this.busy.set(true);
    this.submitError.set(null);
    try {
      this.submitted.set(await firstValueFrom(this.api.submit(this.token(), { rating, comment: this.comment().trim() || null })));
    } catch (err) {
      this.submitError.set(errorMessage(err, 'Your feedback could not be sent. Please try again.'));
    } finally {
      this.busy.set(false);
    }
  }
}
