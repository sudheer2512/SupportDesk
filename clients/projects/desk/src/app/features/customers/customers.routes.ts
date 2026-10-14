import { Routes } from '@angular/router';
import { CustomersHomePage } from './customers-home.page';

/** Customers module routes (lazy-loaded from app.routes.ts; permission-guarded there). */
export const ROUTES: Routes = [{ path: '', title: 'Customers', component: CustomersHomePage }];
