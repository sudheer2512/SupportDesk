import { Routes } from '@angular/router';
import { confirmAdminLeave } from '../admin-shared';

/** Roles & permissions (/admin/roles…), guarded by admin.roles in admin.routes.ts. */
export const ROUTES: Routes = [
  { path: '', title: 'Roles & permissions', loadComponent: () => import('./roles-list.page').then((m) => m.RolesListPage) },
  { path: 'new', title: 'New role', canDeactivate: [confirmAdminLeave], loadComponent: () => import('./role-editor.page').then((m) => m.RoleEditorPage) },
  { path: ':id', title: 'Role', canDeactivate: [confirmAdminLeave], loadComponent: () => import('./role-editor.page').then((m) => m.RoleEditorPage) },
];
