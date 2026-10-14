import { Routes } from '@angular/router';
import { AutomationHomePage } from './automation-home.page';

/** Automation module routes (lazy-loaded from app.routes.ts; permission-guarded there). */
export const ROUTES: Routes = [{ path: '', title: 'Automation', component: AutomationHomePage }];
