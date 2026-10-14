import { Routes } from '@angular/router';
import { SubscriptionHomePage } from './subscription-home.page';

/** Subscription module routes (lazy-loaded from app.routes.ts; permission-guarded there). */
export const ROUTES: Routes = [{ path: '', title: 'Subscription', component: SubscriptionHomePage }];
