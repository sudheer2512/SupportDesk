import { Routes } from '@angular/router';
import { AdminHomePage } from './admin-home.page';

/** Administration module routes (lazy-loaded from app.routes.ts; permission-guarded there). */
export const ROUTES: Routes = [{ path: '', title: 'Administration', component: AdminHomePage }];
