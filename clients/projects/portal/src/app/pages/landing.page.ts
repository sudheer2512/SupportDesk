import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { SdButton, SdFormField, SdInput, SdLogo } from '@sd/ui';

/** /portal/ without a brand: ask for the company's portal name and go to /portal/<brand>. */
@Component({
  selector: 'portal-landing-page',
  imports: [FormsModule, SdButton, SdFormField, SdInput, SdLogo],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="wrap">
      <div class="card">
        <sd-logo [size]="36" />
        <h1>Find your support portal</h1>
        <p class="lead">Enter the portal name your provider gave you — it is the part after <code>/portal/</code> in the address.</p>
        <form (ngSubmit)="go()" class="form" novalidate>
          <sd-form-field label="Portal name" [error]="error()">
            <input sdInput name="brand" autocapitalize="none" spellcheck="false" placeholder="e.g. acme" [(ngModel)]="slug" />
          </sd-form-field>
          <button sdButton variant="primary" type="submit" block>Continue</button>
        </form>
      </div>
    </main>
  `,
  styles: `
    .wrap {
      display: flex;
      align-items: center;
      justify-content: center;
      min-height: 100dvh;
      padding: 24px 16px;
    }
    .card {
      width: min(440px, 100%);
      padding: 32px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-xl);
      background: var(--sd-surface);
      box-shadow: var(--sd-shadow-lg);
    }
    h1 {
      margin-top: 18px;
      font-size: var(--sd-text-2xl);
    }
    .lead {
      margin: 6px 0 20px;
      color: var(--sd-text-2);
    }
    .form {
      display: grid;
      gap: 14px;
    }
  `,
})
export class LandingPage {
  private readonly router = inject(Router);
  protected slug = '';
  protected readonly error = signal<string | null>(null);

  protected go(): void {
    const slug = this.slug.trim().toLowerCase();
    if (!/^[a-z0-9][a-z0-9-]{0,62}$/.test(slug)) {
      this.error.set('Use letters, numbers and dashes only.');
      return;
    }
    void this.router.navigate(['/', slug]);
  }
}
