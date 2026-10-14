import { Routes } from '@angular/router';
import { permissionGuard } from '@sd/core';
import { AdminHomePage } from './admin-home.page';

/** Administration module routes (lazy-loaded from app.routes.ts; permission-guarded there). */
export const ROUTES: Routes = [
  { path: '', title: 'Administration', component: AdminHomePage },
  {
    path: 'webhooks',
    canActivate: [permissionGuard('admin.integrations')],
    loadChildren: () => import('./webhooks/webhooks.routes').then((m) => m.ROUTES),
  },
];
