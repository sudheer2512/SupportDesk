import { Routes } from '@angular/router';

/** SLA module routes (lazy-loaded from app.routes.ts; module guarded by admin.sla or supervisor.monitor). */
export const ROUTES: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'policies' },
  { path: 'policies', title: 'SLA policies', loadComponent: () => import('./policies.page').then((m) => m.PoliciesPage) },
  { path: 'policies/new', title: 'New SLA policy', loadComponent: () => import('./policy-editor.page').then((m) => m.PolicyEditorPage) },
  { path: 'policies/:id', title: 'SLA policy', loadComponent: () => import('./policy-editor.page').then((m) => m.PolicyEditorPage) },
  { path: 'calendars', title: 'Calendars & holidays', loadComponent: () => import('./calendars.page').then((m) => m.CalendarsPage) },
  { path: 'breaches', title: 'Live breaches', loadComponent: () => import('./breaches.page').then((m) => m.BreachesPage) },
];
