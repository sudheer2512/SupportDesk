import { inject } from '@angular/core';
import { CanActivateFn, Router, Routes } from '@angular/router';
import { AuthService, PortalAuthService } from '@sd/core';
import { ModeService } from './core/mobile-session';

/** Agent tabs need a restored/active agent session. */
const agentGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (inject(ModeService).mode() !== 'agent') return router.parseUrl('/login');
  await auth.ready();
  return auth.isAuthenticated() ? true : router.parseUrl('/login');
};

/** Customer tabs need a portal session for the remembered brand. */
const customerGuard: CanActivateFn = async () => {
  const mode = inject(ModeService);
  const auth = inject(PortalAuthService);
  const router = inject(Router);
  if (mode.mode() !== 'customer' || !mode.brand()) return router.parseUrl('/login');
  await auth.ready(mode.brand());
  return auth.isAuthenticated() ? true : router.parseUrl('/login');
};

const placeholder = () => import('./pages/placeholder.page').then((m) => m.PlaceholderPage);
const profile = () => import('./pages/profile.page').then((m) => m.ProfilePage);

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'login' },
  { path: 'login', loadComponent: () => import('./pages/login.page').then((m) => m.LoginPage) },
  {
    path: 'agent',
    canActivate: [agentGuard],
    loadComponent: () => import('./tabs/agent-tabs.page').then((m) => m.AgentTabsPage),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'queue' },
      {
        path: 'queue',
        loadComponent: placeholder,
        data: { title: 'Queue', text: 'Your assigned and team tickets with SLA timers, swipe actions and quick replies.' },
      },
      {
        path: 'notifications',
        loadComponent: placeholder,
        data: { title: 'Notifications', text: 'Mentions, assignments and SLA alerts with push notifications.' },
      },
      { path: 'profile', loadComponent: profile },
    ],
  },
  {
    path: 'customer',
    canActivate: [customerGuard],
    loadComponent: () => import('./tabs/customer-tabs.page').then((m) => m.CustomerTabsPage),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'tickets' },
      {
        path: 'tickets',
        loadComponent: placeholder,
        data: { title: 'My tickets', text: 'Your requests, their status and replies — with photo attachments.' },
      },
      {
        path: 'help',
        loadComponent: placeholder,
        data: { title: 'Help', text: 'Search the knowledge base and start a new request.' },
      },
      { path: 'profile', loadComponent: profile },
    ],
  },
  { path: '**', redirectTo: 'login' },
];
