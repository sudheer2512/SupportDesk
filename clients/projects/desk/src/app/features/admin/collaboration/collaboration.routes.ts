import { Routes } from '@angular/router';

/** /admin/collaboration (admin.integrations; guarded in admin.routes.ts). */
export const ROUTES: Routes = [
  { path: '', title: 'Teams & Slack', loadComponent: () => import('./collaboration.page').then((m) => m.CollaborationPage) },
];
