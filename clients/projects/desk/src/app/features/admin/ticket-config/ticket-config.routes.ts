import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet, Routes } from '@angular/router';
import { SdPageHeader } from '@sd/ui';
import { AdminSection, AdminSectionsNav, confirmAdminLeave } from '../admin-shared';

const SECTIONS: AdminSection[] = [
  { path: 'statuses', label: 'Statuses', icon: 'circle-dot' },
  { path: 'categories', label: 'Categories & issue types', icon: 'folder' },
  { path: 'fields', label: 'Custom fields', icon: 'list' },
  { path: 'layouts', label: 'Form layouts', icon: 'layers' },
  { path: 'settings', label: 'Numbering & lifecycle', icon: 'sliders' },
];

/** Ticket configuration shell (admin.ticket_config): breadcrumbs and section navigation. */
@Component({
  selector: 'desk-admin-ticket-config-layout',
  imports: [RouterOutlet, SdPageHeader, AdminSectionsNav],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Ticket configuration" icon="sliders" [breadcrumbs]="crumbs"
        subtitle="Statuses, categories, fields and forms used by every ticket. Changes apply to the next request — open desks refresh automatically." />
      <desk-admin-sections [sections]="sections" ariaLabel="Ticket configuration sections" />
      <div class="desk-page__body"><router-outlet /></div>
    </div>
  `,
})
export class TicketConfigLayoutPage {
  protected readonly sections = SECTIONS;
  protected readonly crumbs = [{ label: 'Administration', link: '/admin' }, { label: 'Ticket configuration' }];
}

/** Ticket configuration (/admin/ticket-config…), guarded by admin.ticket_config in admin.routes.ts. */
export const ROUTES: Routes = [
  {
    path: '',
    component: TicketConfigLayoutPage,
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'statuses' },
      {
        path: 'statuses',
        title: 'Ticket statuses',
        canDeactivate: [confirmAdminLeave],
        loadComponent: () => import('./statuses.page').then((m) => m.StatusesPage),
      },
      {
        path: 'categories',
        title: 'Categories & issue types',
        canDeactivate: [confirmAdminLeave],
        loadComponent: () => import('./categories.page').then((m) => m.CategoriesPage),
      },
      { path: 'fields', title: 'Custom fields', canDeactivate: [confirmAdminLeave], loadComponent: () => import('./fields.page').then((m) => m.FieldsPage) },
      { path: 'layouts', title: 'Form layouts', loadComponent: () => import('./layouts.page').then((m) => m.LayoutsPage) },
      {
        path: 'layouts/:id',
        title: 'Form builder',
        canDeactivate: [confirmAdminLeave],
        loadComponent: () => import('./layout-builder.page').then((m) => m.LayoutBuilderPage),
      },
      {
        path: 'settings',
        title: 'Numbering & lifecycle',
        canDeactivate: [confirmAdminLeave],
        loadComponent: () => import('./ticket-settings.page').then((m) => m.TicketSettingsPage),
      },
    ],
  },
];
