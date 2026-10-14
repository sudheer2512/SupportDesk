import { Routes } from '@angular/router';
import { confirmAdminLeave } from '../admin-shared';

/** Brands & portals (/admin/brands…), guarded by admin.organisation in admin.routes.ts. */
export const ROUTES: Routes = [
  { path: '', title: 'Brands & portals', loadComponent: () => import('./brands-list.page').then((m) => m.BrandsListPage) },
  { path: 'new', title: 'New brand', canDeactivate: [confirmAdminLeave], loadComponent: () => import('./brand-editor.page').then((m) => m.BrandEditorPage) },
  { path: ':id', title: 'Brand', canDeactivate: [confirmAdminLeave], loadComponent: () => import('./brand-editor.page').then((m) => m.BrandEditorPage) },
];
