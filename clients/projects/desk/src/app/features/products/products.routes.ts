import { Routes } from '@angular/router';
import { ProductsHomePage } from './products-home.page';

/** Products & Assets module routes (lazy-loaded from app.routes.ts; permission-guarded there). */
export const ROUTES: Routes = [{ path: '', title: 'Products & Assets', component: ProductsHomePage }];
