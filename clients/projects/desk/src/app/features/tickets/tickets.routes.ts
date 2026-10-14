import { Routes } from '@angular/router';
import { TicketsWorkspacePage } from './workspace/tickets-workspace.page';

/**
 * Tickets module routes (lazy-loaded from app.routes.ts; permission-guarded there).
 * The workspace stays mounted while tickets open and close in its outlet (tabs keep their state);
 * the new-ticket form is a page of its own so it also works on phones and from the top bar.
 */
export const ROUTES: Routes = [
  { path: 'new', title: 'New ticket', loadComponent: () => import('./new/new-ticket.page').then((m) => m.NewTicketPage) },
  {
    path: '',
    component: TicketsWorkspacePage,
    children: [
      { path: '', title: 'Tickets', children: [] },
      { path: ':id', title: 'Ticket', loadComponent: () => import('./ticket/ticket-view.component').then((m) => m.TicketViewComponent) },
    ],
  },
];
