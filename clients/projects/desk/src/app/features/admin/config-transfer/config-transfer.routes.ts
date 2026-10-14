import { Routes } from '@angular/router';

/** Configuration transfer (lazy child of /admin; guarded by admin.config_transfer in admin.routes.ts). */
export const ROUTES: Routes = [
  { path: '', title: 'Configuration transfer', loadComponent: () => import('./config-transfer.page').then((m) => m.ConfigTransferPage) },
];
