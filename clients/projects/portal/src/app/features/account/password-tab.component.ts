import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiError, PortalTicketsApi } from '@sd/core';
import { SdButton, SdFormField, SdIcon, SdInput, ToastService } from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { BrandService } from '../../brand.service';
import { errorMessage } from '../../shared/portal-utils';

/** Change the portal password (current + new + confirmation). */
@Component({
  selector: 'portal-password-tab',
  imports: [RouterLink, SdButton, SdIcon, SdFormField, SdInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form class="form" (submit)="$event.preventDefault(); save()" novalidate>
      <p class="portal-callout portal-callout--info" role="note">
        <sd-icon name="info" [size]="16" />
        <span>
          Use at least 8 characters — a few unrelated words make a strong password. If you always sign in with an e-mail code and have no password
          yet, ask our team via <a [routerLink]="brand.base() + '/help'">Help &amp; contact</a>.
        </span>
      </p>
      @if (error()) {
        <p class="portal-callout portal-callout--danger" role="alert"><sd-icon name="circle-alert" [size]="15" />{{ error() }}</p>
      }
      <!-- Lets password managers associate the new password with the account. -->
      <input type="text" name="username" autocomplete="username" class="sd-visually-hidden" tabindex="-1" aria-hidden="true" [value]="''" />
      <sd-form-field label="Current password" required [error]="errors()['currentPassword']">
        <input sdInput type="password" name="current" autocomplete="current-password" [value]="current()" (input)="current.set($any($event.target).value)" />
      </sd-form-field>
      <sd-form-field label="New password" required [error]="errors()['newPassword']" hint="At least 8 characters.">
        <input sdInput type="password" name="new" autocomplete="new-password" [value]="next()" (input)="next.set($any($event.target).value)" />
      </sd-form-field>
      <sd-form-field label="Confirm new password" required [error]="errors()['confirm']">
        <input sdInput type="password" name="confirm" autocomplete="new-password" [value]="confirm()" (input)="confirm.set($any($event.target).value)" />
      </sd-form-field>
      <div class="actions">
        <button sdButton variant="primary" type="submit" icon="key" [loading]="busy()">Change password</button>
      </div>
    </form>
  `,
  styles: `
    .form {
      display: grid;
      gap: 16px;
      max-width: 480px;
    }
    .actions {
      display: flex;
      justify-content: flex-start;
    }
  `,
})
export class PasswordTabComponent {
  protected readonly brand = inject(BrandService);
  private readonly api = inject(PortalTicketsApi);
  private readonly toast = inject(ToastService);

  protected readonly current = signal('');
  protected readonly next = signal('');
  protected readonly confirm = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly errors = signal<Record<string, string | null>>({});

  protected async save(): Promise<void> {
    const slug = this.brand.slug();
    if (!slug || this.busy()) return;
    const errors: Record<string, string | null> = {
      currentPassword: this.current() ? null : 'Enter your current password.',
      newPassword: this.next().length < 8 ? 'Use at least 8 characters.' : this.next() === this.current() ? 'Choose a password you have not used here.' : null,
      confirm: this.confirm() === this.next() ? null : "The passwords don't match.",
    };
    this.errors.set(errors);
    this.error.set(null);
    if (Object.values(errors).some(Boolean)) return;
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.changePassword(slug, { currentPassword: this.current(), newPassword: this.next() }));
      this.current.set('');
      this.next.set('');
      this.confirm.set('');
      this.toast.success('Password changed', 'Use your new password next time you sign in.');
    } catch (err) {
      const e = ApiError.from(err);
      const fields = { currentPassword: e.fieldError('currentPassword'), newPassword: e.fieldError('newPassword'), confirm: null };
      if (!fields.currentPassword && (e.status === 401 || e.is('validation_failed')) && !fields.newPassword) {
        fields.currentPassword = e.status === 401 ? 'Your current password is not correct.' : null;
      }
      this.errors.set(fields);
      if (!fields.currentPassword && !fields.newPassword) this.error.set(errorMessage(err, 'Your password could not be changed.'));
    } finally {
      this.busy.set(false);
    }
  }
}
