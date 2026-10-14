import { Routes } from '@angular/router';
import { SlaHomePage } from './sla-home.page';

/** SLA module routes (lazy-loaded from app.routes.ts; permission-guarded there). */
export const ROUTES: Routes = [{ path: '', title: 'SLA', component: SlaHomePage }];
