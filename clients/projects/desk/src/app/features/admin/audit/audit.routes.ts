import { Routes } from '@angular/router';

/** Audit viewer (lazy child of /admin; guarded by admin.audit in admin.routes.ts). */
export const ROUTES: Routes = [{ path: '', title: 'Audit log', loadComponent: () => import('./audit.page').then((m) => m.AuditPage) }];
