import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Routes } from '@angular/router';
import { PortalAuthService } from '@sd/core';
import { PortalPlaceholderComponent } from '../../shared/portal-placeholder.component';

/** Account — shows the signed-in contact (from the session) until profile editing is built. */
@Component({
  selector: 'portal-account-home',
  imports: [PortalPlaceholderComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <portal-placeholder title="Account" icon="user" subtitle="Your contact details and preferences." [planned]="planned">
      @if (auth.contact(); as c) {
        <dl class="facts">
          <dt>Name</dt>
          <dd>{{ c.fullName }}</dd>
          <dt>E-mail</dt>
          <dd>{{ c.email || '—' }}</dd>
          <dt>Organisation</dt>
          <dd>{{ c.customerName || '—' }}</dd>
          <dt>Location</dt>
          <dd>{{ c.locationName || '—' }}</dd>
          <dt>Language</dt>
          <dd>{{ c.preferredLanguage }}</dd>
        </dl>
      }
    </portal-placeholder>
  `,
  styles: `
    .facts {
      display: grid;
      grid-template-columns: max-content 1fr;
      gap: 6px 16px;
      margin: 18px 0 0;
    }
    dt {
      color: var(--sd-text-3);
    }
    dd {
      margin: 0;
    }
  `,
})
export class AccountHomePage {
  protected readonly auth = inject(PortalAuthService);
  protected readonly planned = ['Edit your name, phone and language', 'Change password', 'Notification preferences'];
}

/** Self-registration — placeholder until the sign-up form is built. */
@Component({
  selector: 'portal-register-page',
  imports: [PortalPlaceholderComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<portal-placeholder title="Create an account" icon="user-plus" subtitle="Sign up to submit and follow your tickets." />`,
})
export class RegisterPage {}

export const ROUTES: Routes = [{ path: '', title: 'Account', component: AccountHomePage }];
export const REGISTER_ROUTES: Routes = [{ path: '', title: 'Create account', component: RegisterPage }];
