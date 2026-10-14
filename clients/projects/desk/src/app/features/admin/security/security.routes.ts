import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet, Routes } from '@angular/router';
import { SdPageHeader } from '@sd/ui';
import { AdminSection, AdminSectionsNav, confirmAdminLeave } from '../admin-shared';

const SECTIONS: AdminSection[] = [
  { path: 'policies', label: 'Policies', icon: 'shield-check' },
  { path: 'sso', label: 'Single sign-on', icon: 'log-in' },
  { path: 'sessions', label: 'Active sessions', icon: 'devices' },
];

/** Security administration shell (admin.security). */
@Component({
  selector: 'desk-admin-security-layout',
  imports: [RouterOutlet, SdPageHeader, AdminSectionsNav],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Security & sign-in" icon="lock" [breadcrumbs]="crumbs" subtitle="Multi-factor authentication, password and session rules, network restrictions and single sign-on." />
      <desk-admin-sections [sections]="sections" ariaLabel="Security sections" />
      <div class="desk-page__body"><router-outlet /></div>
    </div>
  `,
})
export class SecurityLayoutPage {
  protected readonly sections = SECTIONS;
  protected readonly crumbs = [{ label: 'Administration', link: '/admin' }, { label: 'Security & sign-in' }];
}

/** Security policies, SSO and sessions (/admin/security…), guarded by admin.security in admin.routes.ts. */
export const ROUTES: Routes = [
  {
    path: '',
    component: SecurityLayoutPage,
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'policies' },
      {
        path: 'policies',
        title: 'Security policies',
        canDeactivate: [confirmAdminLeave],
        loadComponent: () => import('./security-policies.page').then((m) => m.SecurityPoliciesPage),
      },
      { path: 'sso', title: 'Single sign-on', canDeactivate: [confirmAdminLeave], loadComponent: () => import('./sso.page').then((m) => m.SsoPage) },
      { path: 'sessions', title: 'Active sessions', loadComponent: () => import('./sessions.page').then((m) => m.SessionsPage) },
    ],
  },
];
