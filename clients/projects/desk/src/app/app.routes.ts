import { inject, isDevMode } from '@angular/core';
import { Routes } from '@angular/router';
import { MeStore, authGuard, guestGuard, permissionGuard } from '@sd/core';
import { ForbiddenPage } from './pages/forbidden.page';
import { NotFoundPage } from './pages/not-found.page';
import { DESK_MODULES, moduleByPath } from './nav';
import { ShellComponent } from './shell/shell.component';

/** Permission list for a module route (kept in sync with the nav rail via DESK_MODULES). */
const perms = (path: string) => permissionGuard(...(moduleByPath(path)?.permissions ?? []));

/** Lands on the first module the user may open (or the profile page when none). */
function firstAllowedModule(): string {
  const me = inject(MeStore);
  return DESK_MODULES.find((m) => me.canAny(m.permissions))?.path ?? 'me';
}

export const routes: Routes = [
  {
    path: 'login',
    canActivate: [guestGuard],
    title: 'Sign in',
    loadComponent: () => import('./auth/login.page').then((m) => m.LoginPage),
  },
  {
    // Developer reference of @sd/ui components; only routable in dev builds.
    path: 'design-system',
    canMatch: [() => isDevMode()],
    title: 'Design system',
    loadComponent: () => import('./dev/design-system.page').then((m) => m.DesignSystemPage),
  },
  {
    path: '',
    component: ShellComponent,
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: () => firstAllowedModule() },
      { path: 'command', canActivate: [perms('command')], loadChildren: () => import('./features/command/command.routes').then((m) => m.ROUTES) },
      { path: 'tickets', canActivate: [perms('tickets')], loadChildren: () => import('./features/tickets/tickets.routes').then((m) => m.ROUTES) },
      { path: 'inbox', canActivate: [perms('inbox')], loadChildren: () => import('./features/inbox/inbox.routes').then((m) => m.ROUTES) },
      { path: 'customers', canActivate: [perms('customers')], loadChildren: () => import('./features/customers/customers.routes').then((m) => m.ROUTES) },
      { path: 'products', canActivate: [perms('products')], loadChildren: () => import('./features/products/products.routes').then((m) => m.ROUTES) },
      { path: 'teams', canActivate: [perms('teams')], loadChildren: () => import('./features/teams/teams.routes').then((m) => m.ROUTES) },
      { path: 'sla', canActivate: [perms('sla')], loadChildren: () => import('./features/sla/sla.routes').then((m) => m.ROUTES) },
      { path: 'automation', canActivate: [perms('automation')], loadChildren: () => import('./features/automation/automation.routes').then((m) => m.ROUTES) },
      { path: 'kb', canActivate: [perms('kb')], loadChildren: () => import('./features/kb/kb.routes').then((m) => m.ROUTES) },
      { path: 'analytics', canActivate: [perms('analytics')], loadChildren: () => import('./features/analytics/analytics.routes').then((m) => m.ROUTES) },
      { path: 'ai', canActivate: [perms('ai')], loadChildren: () => import('./features/ai/ai.routes').then((m) => m.ROUTES) },
      { path: 'admin', canActivate: [perms('admin')], loadChildren: () => import('./features/admin/admin.routes').then((m) => m.ROUTES) },
      {
        path: 'subscription',
        canActivate: [perms('subscription')],
        loadChildren: () => import('./features/subscription/subscription.routes').then((m) => m.ROUTES),
      },
      { path: 'me', title: 'My profile', loadComponent: () => import('./profile/profile.page').then((m) => m.ProfilePage) },
      { path: 'forbidden', title: 'Access denied', component: ForbiddenPage },
      { path: '**', title: 'Not found', component: NotFoundPage },
    ],
  },
];
