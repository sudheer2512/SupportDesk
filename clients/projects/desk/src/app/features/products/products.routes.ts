import { inject } from '@angular/core';
import { CanActivateFn, Router, Routes } from '@angular/router';
import { MeStore, permissionGuard } from '@sd/core';

/** Users with contracts.view but not products.view land on the contracts list. */
const catalogGuard: CanActivateFn = () => (inject(MeStore).can('products.view') ? true : inject(Router).parseUrl('/products/contracts'));

/** Products & Assets module routes (lazy-loaded from app.routes.ts; module guarded by products.view or contracts.view). */
export const ROUTES: Routes = [
  { path: '', title: 'Products & Assets', canActivate: [catalogGuard], loadComponent: () => import('./catalog.page').then((m) => m.CatalogPage) },
  {
    path: 'installations',
    title: 'Installations',
    canActivate: [permissionGuard('products.view')],
    loadComponent: () => import('./installations.page').then((m) => m.InstallationsPage),
  },
  {
    path: 'licences',
    title: 'Licences',
    canActivate: [permissionGuard('products.view')],
    loadComponent: () => import('./licences.page').then((m) => m.LicencesPage),
  },
  {
    path: 'assets',
    title: 'Assets',
    canActivate: [permissionGuard('products.view')],
    loadComponent: () => import('./licences.page').then((m) => m.AssetsPage),
  },
  {
    path: 'install-base',
    title: 'Install base',
    canActivate: [permissionGuard('products.view')],
    loadComponent: () => import('./install-base.page').then((m) => m.InstallBasePage),
  },
  {
    path: 'contracts',
    title: 'Contracts',
    canActivate: [permissionGuard('contracts.view')],
    loadComponent: () => import('./contracts.page').then((m) => m.ContractsPage),
  },
  {
    path: 'contracts/:id',
    title: 'Contract',
    canActivate: [permissionGuard('contracts.view')],
    loadComponent: () => import('./contract-detail.page').then((m) => m.ContractDetailPage),
  },
  {
    path: 'renewals',
    title: 'Renewals',
    canActivate: [permissionGuard('contracts.view')],
    loadComponent: () => import('./contracts.page').then((m) => m.RenewalsPage),
  },
  {
    path: 'erp',
    title: 'ERP integration',
    canActivate: [permissionGuard('admin.integrations')],
    loadComponent: () => import('./erp-integration.page').then((m) => m.ErpIntegrationPage),
  },
];
