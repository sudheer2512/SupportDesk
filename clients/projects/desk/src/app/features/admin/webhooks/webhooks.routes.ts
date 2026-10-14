import { Routes } from '@angular/router';

/** Webhook subscriptions (lazy child of /admin; guarded by admin.integrations in admin.routes.ts). */
export const ROUTES: Routes = [{ path: '', title: 'Webhooks', loadComponent: () => import('./webhooks.page').then((m) => m.WebhooksPage) }];
