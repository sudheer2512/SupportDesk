import { Routes } from '@angular/router';
import { permissionGuard } from '@sd/core';
import { AdminHomePage } from './admin-home.page';

/** Administration module routes (lazy-loaded from app.routes.ts; permission-guarded there). */
export const ROUTES: Routes = [
  { path: '', title: 'Administration', component: AdminHomePage },
  { path: 'channels', canActivate: [permissionGuard('admin.channels')], loadChildren: () => import('./channels/channels.routes').then((m) => m.ROUTES) },
];
