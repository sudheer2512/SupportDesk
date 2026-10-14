import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet, Routes } from '@angular/router';
import { SdPageHeader } from '@sd/ui';
import { AdminSection, AdminSectionsNav, confirmAdminLeave } from '../admin-shared';

const SECTIONS: AdminSection[] = [
  { path: 'defaults', label: 'Delivery defaults', icon: 'bell' },
  { path: 'templates', label: 'E-mail templates', icon: 'mail' },
];

/** Notification administration shell (admin.organisation or admin.channels). */
@Component({
  selector: 'desk-admin-notifications-layout',
  imports: [RouterOutlet, SdPageHeader, AdminSectionsNav],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Notifications & e-mail templates" icon="bell" [breadcrumbs]="crumbs"
        subtitle="How agents are notified by default, and the wording of automatic e-mails to customers and agents." />
      <desk-admin-sections [sections]="sections" ariaLabel="Notification settings sections" />
      <div class="desk-page__body"><router-outlet /></div>
    </div>
  `,
})
export class NotificationsLayoutPage {
  protected readonly sections = SECTIONS;
  protected readonly crumbs = [{ label: 'Administration', link: '/admin' }, { label: 'Notifications & e-mail templates' }];
}

/** Notification defaults and e-mail templates (/admin/notifications…). */
export const ROUTES: Routes = [
  {
    path: '',
    component: NotificationsLayoutPage,
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'defaults' },
      {
        path: 'defaults',
        title: 'Notification defaults',
        canDeactivate: [confirmAdminLeave],
        loadComponent: () => import('./notification-defaults.page').then((m) => m.NotificationDefaultsPage),
      },
      { path: 'templates', title: 'E-mail templates', loadComponent: () => import('./templates.page').then((m) => m.TemplatesPage) },
      {
        path: 'templates/:key',
        title: 'E-mail template',
        canDeactivate: [confirmAdminLeave],
        loadComponent: () => import('./template-editor.page').then((m) => m.TemplateEditorPage),
      },
    ],
  },
];
