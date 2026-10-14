import { Routes } from '@angular/router';
import { AnalyticsHomePage } from './analytics-home.page';

/** Analytics module routes (lazy-loaded from app.routes.ts; permission-guarded there). */
export const ROUTES: Routes = [{ path: '', title: 'Analytics', component: AnalyticsHomePage }];
