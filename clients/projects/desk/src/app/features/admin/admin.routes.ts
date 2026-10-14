import { Routes } from '@angular/router';
import { permissionGuard } from '@sd/core';
import { AdminHomePage } from './admin-home.page';

/** Administration module routes (lazy-loaded from app.routes.ts; permission-guarded there). */
export const ROUTES: Routes = [
  { path: '', title: 'Administration', component: AdminHomePage },
  { path: 'channels', canActivate: [permissionGuard('admin.channels')], loadChildren: () => import('./channels/channels.routes').then((m) => m.ROUTES) },
  {
    path: 'webhooks',
    canActivate: [permissionGuard('admin.integrations')],
    loadChildren: () => import('./webhooks/webhooks.routes').then((m) => m.ROUTES),
  },
  // AI administration (owned by the AI module): provider, budget, features, prompts, agents, usage, execution log.
  { path: 'ai', canActivate: [permissionGuard('admin.ai')], loadChildren: () => import('./ai/admin-ai.routes').then((m) => m.ROUTES) },
];
