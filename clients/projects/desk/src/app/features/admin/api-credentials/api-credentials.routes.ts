import { Routes } from '@angular/router';
import { confirmAdminLeave } from '../admin-shared';

/** API keys and OAuth clients (/admin/api-credentials), guarded by admin.integrations in admin.routes.ts. */
export const ROUTES: Routes = [
  {
    path: '',
    title: 'API keys & OAuth clients',
    canDeactivate: [confirmAdminLeave],
    loadComponent: () => import('./api-credentials.page').then((m) => m.ApiCredentialsPage),
  },
];
