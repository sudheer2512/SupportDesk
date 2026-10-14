import { inject } from '@angular/core';
import { CanActivateFn, Router, Routes } from '@angular/router';
import { ModeService } from './core/mobile-session';
import { SessionCoordinator } from './core/session-coordinator';

/** Agent screens need an agent session — live, or an offline session restored from this device's saved data. */
const agentGuard: CanActivateFn = async () => {
  const ok = await inject(SessionCoordinator).canEnter('agent');
  return ok ? true : inject(Router).parseUrl('/login');
};

/** Customer screens need a portal session for the remembered help center (live or offline). */
const customerGuard: CanActivateFn = async () => {
  const ok = await inject(SessionCoordinator).canEnter('customer');
  return ok ? true : inject(Router).parseUrl('/login');
};

/** Opening the app with a saved session skips the sign-in screen. */
const loginGuard: CanActivateFn = async () => {
  const mode = inject(ModeService).mode();
  const router = inject(Router);
  const signedIn = await inject(SessionCoordinator).canEnter(mode);
  return signedIn ? router.parseUrl(mode === 'agent' ? '/agent/home' : '/customer/tickets') : true;
};

const profile = () => import('./pages/profile.page').then((m) => m.ProfilePage);

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'login' },
  { path: 'login', canActivate: [loginGuard], loadComponent: () => import('./pages/login.page').then((m) => m.LoginPage), title: 'Sign in' },
  {
    path: 'agent',
    canActivate: [agentGuard],
    loadComponent: () => import('./tabs/agent-tabs.page').then((m) => m.AgentTabsPage),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'home' },
      { path: 'home', loadComponent: () => import('./agent/home.page').then((m) => m.AgentHomePage), title: 'Home' },
      { path: 'tickets', loadComponent: () => import('./agent/tickets.page').then((m) => m.AgentTicketsPage), title: 'Tickets' },
      {
        path: 'tickets/number/:number',
        loadComponent: () => import('./agent/ticket-by-number.page').then((m) => m.TicketByNumberPage),
        title: 'Ticket',
      },
      { path: 'tickets/:id', loadComponent: () => import('./agent/ticket-detail.page').then((m) => m.AgentTicketDetailPage), title: 'Ticket' },
      {
        path: 'notifications',
        loadComponent: () => import('./agent/notifications.page').then((m) => m.AgentNotificationsPage),
        title: 'Notifications',
      },
      { path: 'approvals', loadComponent: () => import('./agent/approvals.page').then((m) => m.AgentApprovalsPage), title: 'Approvals' },
      { path: 'profile', loadComponent: profile, title: 'Me' },
    ],
  },
  {
    path: 'customer',
    canActivate: [customerGuard],
    loadComponent: () => import('./tabs/customer-tabs.page').then((m) => m.CustomerTabsPage),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'tickets' },
      { path: 'tickets', loadComponent: () => import('./customer/customer-tickets.page').then((m) => m.CustomerTicketsPage), title: 'My tickets' },
      { path: 'tickets/:id', loadComponent: () => import('./customer/customer-ticket.page').then((m) => m.CustomerTicketPage), title: 'Request' },
      { path: 'new', loadComponent: () => import('./customer/new-ticket.page').then((m) => m.NewTicketPage), title: 'New request' },
      { path: 'help', loadComponent: () => import('./customer/help.page').then((m) => m.HelpPage), title: 'Help' },
      { path: 'help/articles/:slug', loadComponent: () => import('./customer/article.page').then((m) => m.ArticlePage), title: 'Article' },
      { path: 'profile', loadComponent: profile, title: 'Me' },
    ],
  },
  { path: '**', redirectTo: 'login' },
];
