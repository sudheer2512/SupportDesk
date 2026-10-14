import { Routes } from '@angular/router';
import { platformGuard } from '@sd/core';

/**
 * Platform operator console (/platform). It lives outside the tenant shell and nav rail on purpose: platform operators
 * are not tenant agents (their token has no tenant and is refused by every tenant endpoint), so the console has its own
 * sign-in, guard and layout. Tenant users never see an entry for it.
 */
export const ROUTES: Routes = [
  { path: 'login', title: 'Platform sign in', loadComponent: () => import('./platform-login.page').then((m) => m.PlatformLoginPage) },
  {
    path: '',
    canActivate: [platformGuard],
    loadComponent: () => import('./platform-shell.component').then((m) => m.PlatformShellComponent),
    children: [
      { path: '', pathMatch: 'full', title: 'Platform overview', loadComponent: () => import('./platform-overview.page').then((m) => m.PlatformOverviewPage) },
      { path: 'tenants', title: 'Tenants', loadComponent: () => import('./platform-tenants.page').then((m) => m.PlatformTenantsPage) },
      { path: 'tenants/:id', title: 'Tenant', loadComponent: () => import('./platform-tenant-detail.page').then((m) => m.PlatformTenantDetailPage) },
      { path: 'plans', title: 'Plans', loadComponent: () => import('./platform-plans.page').then((m) => m.PlatformPlansPage) },
      { path: 'invoices', title: 'Invoices', loadComponent: () => import('./platform-invoices.page').then((m) => m.PlatformInvoicesPage) },
      { path: 'support', title: 'Support access', loadComponent: () => import('./platform-support.page').then((m) => m.PlatformSupportPage) },
      { path: 'audit', title: 'Platform audit', loadComponent: () => import('./platform-audit.page').then((m) => m.PlatformAuditPage) },
    ],
  },
];
