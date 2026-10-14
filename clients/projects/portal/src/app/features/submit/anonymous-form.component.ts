import { ChangeDetectionStrategy, Component, computed, inject, output, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiError, PortalContentApi, PortalSubmissionResultDto, PortalTicketFormDto } from '@sd/core';
import { SdButton, SdFormField, SdIcon, SdInput, ToastService } from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { BrandService } from '../../brand.service';
import { DeflectionComponent } from '../../shared/deflection.component';
import { errorMessage, htmlToText, textToHtml } from '../../shared/portal-utils';
import { SubmitPrefillService } from '../../shared/submit-prefill.service';

/**
 * Ticket form for visitors who are not signed in (brands with anonymous tickets): name, e-mail, phone,
 * subject and description, with article suggestions. Spam protection: a hidden honeypot field named
 * `website` and the time the form was shown (`formStartedAt`).
 */
@Component({
  selector: 'portal-anonymous-form',
  imports: [RouterLink, SdButton, SdIcon, SdFormField, SdInput, DeflectionComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form class="form" (submit)="$event.preventDefault(); submit()" novalidate>
      <p class="portal-callout portal-callout--info" role="note">
        <sd-icon name="info" [size]="16" />
        <span>
          Have an account?
          <a [routerLink]="brand.base() + '/login'" [queryParams]="{ returnUrl: brand.base() + '/submit' }">Sign in</a>
          to attach files and follow your tickets online.
        </span>
      </p>
      @if (error()) {
        <p class="portal-callout portal-callout--danger" role="alert"><sd-icon name="circle-alert" [size]="15" />{{ error() }}</p>
      }
      <div class="grid">
        <sd-form-field label="Your name" required [error]="errors()['name']">
          <input sdInput name="name" autocomplete="name" maxlength="200" [value]="name()" (input)="name.set($any($event.target).value)" />
        </sd-form-field>
        <sd-form-field label="E-mail" required [error]="errors()['email']" hint="We'll send updates to this address.">
          <input sdInput type="email" name="email" autocomplete="email" maxlength="254" [value]="email()" (input)="email.set($any($event.target).value)" />
        </sd-form-field>
        <sd-form-field label="Phone" optional [error]="errors()['phone']">
          <input sdInput type="tel" name="phone" autocomplete="tel" maxlength="40" [value]="phone()" (input)="phone.set($any($event.target).value)" />
        </sd-form-field>
      </div>
      @if (options(); as o) {
        <div class="grid">
          @if (o.departments.length > 1) {
            <sd-form-field label="Department" optional>
              <select sdInput name="departmentId" [value]="departmentId()" (change)="setDepartment($any($event.target).value)">
                <option value="">Not sure</option>
                @for (d of o.departments; track d.id) {
                  <option [value]="d.id">{{ d.name }}</option>
                }
              </select>
            </sd-form-field>
          }
          @if (categories().length > 0) {
            <sd-form-field label="Category" optional>
              <select sdInput name="categoryId" [value]="categoryId()" (change)="categoryId.set($any($event.target).value)">
                <option value="">Not sure</option>
                @for (c of categories(); track c.id) {
                  <option [value]="c.id">{{ c.name }}</option>
                }
              </select>
            </sd-form-field>
          }
          @if (o.products.length > 0) {
            <sd-form-field label="Product" optional>
              <select sdInput name="productId" [value]="productId()" (change)="productId.set($any($event.target).value)">
                <option value="">Not sure</option>
                @for (p of o.products; track p.id) {
                  <option [value]="p.id">{{ p.name }}</option>
                }
              </select>
            </sd-form-field>
          }
        </div>
      }
      <sd-form-field label="Subject" required [error]="errors()['subject']">
        <input sdInput name="subject" maxlength="250" autocomplete="off" [value]="subject()" (input)="subject.set($any($event.target).value)" />
      </sd-form-field>
      <sd-form-field label="Description" required [error]="errors()['description']" hint="What happened? Include steps, error messages and when it started.">
        <textarea sdInput name="description" rows="7" maxlength="20000" [value]="description()" (input)="description.set($any($event.target).value)"></textarea>
      </sd-form-field>

      <portal-deflection [query]="subject().trim().length >= 3 ? subject() : description()" (solved)="solvedByArticle()" />

      <!-- Honeypot: invisible to people, tempting to bots. Leave it empty. -->
      <div class="hp" aria-hidden="true">
        <label for="portal-website">Website</label>
        <input id="portal-website" type="text" name="website" tabindex="-1" autocomplete="off" [value]="website()" (input)="website.set($any($event.target).value)" />
      </div>

      <div class="actions">
        <span class="hint">By submitting you agree that we may contact you about this request.</span>
        <button sdButton variant="primary" size="lg" type="submit" icon="send" [loading]="busy()">Submit ticket</button>
      </div>
    </form>
  `,
  styles: `
    .form {
      display: grid;
      gap: 18px;
    }
    .grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 220px), 1fr));
      gap: 14px 16px;
    }
    .hp {
      position: absolute;
      left: -10000px;
      top: auto;
      width: 1px;
      height: 1px;
      overflow: hidden;
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
    }
    .hint {
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
    @media (max-width: 640px) {
      .actions button {
        width: 100%;
      }
    }
  `,
})
export class AnonymousFormComponent {
  protected readonly brand = inject(BrandService);
  private readonly api = inject(PortalContentApi);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  readonly submitted = output<PortalSubmissionResultDto>();

  protected readonly name = signal('');
  protected readonly email = signal('');
  protected readonly phone = signal('');
  protected readonly subject = signal('');
  protected readonly description = signal('');
  protected readonly website = signal('');
  protected readonly options = signal<PortalTicketFormDto | null>(null);
  protected readonly departmentId = signal('');
  protected readonly categoryId = signal('');
  protected readonly productId = signal('');
  /** Top-level categories of the chosen department (or brand-wide ones). */
  protected readonly categories = computed(() => {
    const dept = this.departmentId();
    return (this.options()?.categories ?? []).filter((c) => !c.parentId && (!dept || !c.departmentId || c.departmentId === dept));
  });
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly errors = signal<Record<string, string | null>>({});
  private readonly formStartedAt = Date.now();

  constructor() {
    const slug = this.brand.slug();
    if (slug) this.api.submitForm(slug).subscribe({ next: (o) => this.options.set(o), error: () => this.options.set(null) });
    const prefill = inject(SubmitPrefillService).take();
    if (prefill) {
      this.subject.set(prefill.subject ?? '');
      this.description.set(htmlToText(prefill.descriptionHtml));
    }
  }

  protected setDepartment(id: string): void {
    this.departmentId.set(id);
    if (this.categoryId() && !this.categories().some((c) => c.id === this.categoryId())) this.categoryId.set('');
  }

  protected solvedByArticle(): void {
    this.toast.success("Great, you're all set!", 'No ticket was created.');
    void this.router.navigate([this.brand.base()]);
  }

  protected async submit(): Promise<void> {
    const slug = this.brand.slug();
    if (!slug || this.busy()) return;
    const errors: Record<string, string | null> = {
      name: this.name().trim() ? null : 'Enter your name.',
      email: /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(this.email().trim()) ? null : 'Enter a valid e-mail address.',
      subject: this.subject().trim().length >= 5 ? null : 'Enter a subject of at least 5 characters.',
      description: this.description().trim().length >= 10 ? null : 'Describe the problem (at least 10 characters).',
    };
    this.errors.set(errors);
    this.error.set(null);
    if (Object.values(errors).some(Boolean)) {
      this.error.set('Please check the highlighted fields.');
      return;
    }
    this.busy.set(true);
    try {
      const result = await firstValueFrom(
        this.api.submitAnonymous(slug, {
          name: this.name().trim(),
          email: this.email().trim(),
          phone: this.phone().trim() || null,
          subject: this.subject().trim(),
          descriptionHtml: textToHtml(this.description()),
          departmentId: this.departmentId() || null,
          categoryId: this.categoryId() || null,
          productId: this.productId() || null,
          website: this.website() || null,
          formStartedAt: this.formStartedAt,
        }),
      );
      if (result.accepted) this.submitted.emit(result);
      else this.error.set(result.message || 'Your request could not be submitted.');
    } catch (err) {
      const e = ApiError.from(err);
      const fieldErrors: Record<string, string | null> = {
        name: e.fieldError('name'),
        email: e.fieldError('email'),
        phone: e.fieldError('phone'),
        subject: e.fieldError('subject'),
        description: e.fieldError('descriptionHtml'),
      };
      this.errors.set(fieldErrors);
      this.error.set(
        Object.values(fieldErrors).some(Boolean) ? 'Please check the highlighted fields.' : errorMessage(err, 'Your request could not be submitted. Please try again.'),
      );
    } finally {
      this.busy.set(false);
    }
  }
}
