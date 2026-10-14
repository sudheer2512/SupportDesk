import { Routes } from '@angular/router';
import { TicketsHomePage } from './tickets-home.page';

/**
 * Tickets module routes (lazy-loaded from app.routes.ts; permission-guarded there).
 * `new` and `:id` are already routable so the top bar, command palette and notifications can link to
 * them; they render the placeholder until the ticket form and workbench screens land.
 */
export const ROUTES: Routes = [
  { path: '', title: 'Tickets', component: TicketsHomePage, data: { mode: 'list' } },
  { path: 'new', title: 'New ticket', component: TicketsHomePage, data: { mode: 'new' } },
  { path: ':id', title: 'Ticket', component: TicketsHomePage, data: { mode: 'detail' } },
];
