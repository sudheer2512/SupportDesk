import { Routes } from '@angular/router';

/** The signed-in contact's tickets (own / location / organisation scope) and ticket detail. */
export const ROUTES: Routes = [
  { path: '', title: 'My tickets', loadComponent: () => import('./tickets-list.page').then((m) => m.TicketsListPage) },
  { path: ':id', title: 'Ticket', loadComponent: () => import('./ticket-detail.page').then((m) => m.TicketDetailPage) },
];
