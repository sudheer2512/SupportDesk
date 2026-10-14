import { Routes } from '@angular/router';

/** Compliance (lazy child of /admin; guarded by admin.compliance in admin.routes.ts). */
export const ROUTES: Routes = [{ path: '', title: 'Compliance', loadComponent: () => import('./compliance.page').then((m) => m.CompliancePage) }];
