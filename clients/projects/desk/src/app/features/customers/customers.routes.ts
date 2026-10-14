import { Routes } from '@angular/router';
import { CustomersListPage } from './customers-list.page';

/** Customers module routes (lazy-loaded from app.routes.ts; permission-guarded there by customers.view). */
export const ROUTES: Routes = [
  { path: '', title: 'Customers', component: CustomersListPage },
  { path: 'contacts', title: 'Contacts', loadComponent: () => import('./contacts-list.page').then((m) => m.ContactsListPage) },
  { path: 'contacts/:id', title: 'Contact', loadComponent: () => import('./contact-detail.page').then((m) => m.ContactDetailPage) },
  { path: 'duplicates', title: 'Duplicate contacts', loadComponent: () => import('./duplicates.page').then((m) => m.DuplicatesPage) },
  { path: 'activities', title: 'Service activities', loadComponent: () => import('./activities.page').then((m) => m.ActivitiesPage) },
  { path: ':id', title: 'Customer 360', loadComponent: () => import('./customer-360.page').then((m) => m.Customer360Page) },
];
