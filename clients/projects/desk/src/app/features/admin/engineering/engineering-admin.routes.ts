import { Routes } from '@angular/router';

/** /admin/engineering (admin.integrations; guarded in admin.routes.ts). */
export const ROUTES: Routes = [
  { path: '', title: 'Engineering integration', loadComponent: () => import('./engineering-admin.page').then((m) => m.EngineeringAdminPage) },
];
